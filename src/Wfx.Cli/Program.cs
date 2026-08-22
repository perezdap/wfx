using System.Net;
using System.Text;
using Wfx.Core;
using Wfx.Providers;
using Wfx.Tools;

namespace Wfx.Cli;

internal static class Program
{
    private const string Version = "0.1.0";

    private const int ApprovalSummaryLength = 400;

    private static bool _unicodeConsole;

    public static async Task<int> Main(string[] args)
    {
        _unicodeConsole = TryEnableUnicodeConsole();
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            var arguments = CliArguments.Parse(args);
            if (arguments.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            if (arguments.ShowVersion)
            {
                Console.WriteLine(Version);
                return 0;
            }

            var workspace = WorkspaceInfo.Discover();
            var settings = WfxConfiguration.Load(workspace.Root, arguments.Settings);
            foreach (var warning in settings.Warnings)
            {
                Console.Error.WriteLine($"wfx: warning: {warning}");
            }

            using var httpClient = CreateHttpClient();
            return arguments.Command switch
            {
                CliCommand.Models => PrintModels(settings, workspace),
                CliCommand.Config => PrintConfig(settings, workspace),
                CliCommand.Run => await RunOnceAsync(arguments.Prompt!, settings, workspace, arguments, httpClient, shutdown.Token)
                    .ConfigureAwait(false),
                _ => await RunInteractiveAsync(settings, workspace, arguments, httpClient, shutdown.Token).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"wfx: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunOnceAsync(
        string prompt,
        WfxSettings settings,
        WorkspaceInfo workspace,
        CliArguments arguments,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        EnsureRunnable(settings);
        Console.Error.WriteLine(settings.Profile is null
            ? $"wfx: {settings.Provider}/{settings.Model}"
            : $"wfx: profile '{settings.Profile}' ({settings.Provider}/{settings.Model})");

        var agent = CreateAgent(settings, workspace, arguments, httpClient);
        var result = await agent.RunAsync(prompt, cancellationToken).ConfigureAwait(false);
        if (!result.FinalResponse.EndsWith('\n'))
        {
            Console.WriteLine();
        }

        if (arguments.Verbose)
        {
            Console.Error.WriteLine($"[wfx] completed in {result.Iterations} model iteration(s)");
        }

        return 0;
    }

    private static async Task<int> RunInteractiveAsync(
        WfxSettings settings,
        WorkspaceInfo workspace,
        CliArguments arguments,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        EnsureRunnable(settings);
        Console.WriteLine("WFX");
        Console.WriteLine();
        if (settings.Profile is not null)
        {
            Console.WriteLine($"Profile: {settings.Profile}");
        }

        Console.WriteLine($"Model: {settings.Provider}/{settings.Model}");
        Console.WriteLine($"Workspace: {workspace.Root}");
        Console.WriteLine();

        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("> ");
            var prompt = Console.ReadLine();
            if (prompt is null || prompt.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
                prompt.Equals("/quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                continue;
            }

            try
            {
                var agent = CreateAgent(settings, workspace, arguments, httpClient);
                var result = await agent.RunAsync(prompt, cancellationToken).ConfigureAwait(false);
                if (!result.FinalResponse.EndsWith('\n'))
                {
                    Console.WriteLine();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.Error.WriteLine($"wfx: {exception.Message}");
            }

            Console.WriteLine();
        }

        return 0;
    }

    private static Agent CreateAgent(
        WfxSettings settings,
        WorkspaceInfo workspace,
        CliArguments arguments,
        HttpClient httpClient)
    {
        var provider = ModelTransports.Create(settings.Protocol, httpClient, new OpenAiProviderOptions
        {
            BaseUri = settings.BaseUri,
            ApiKey = settings.ApiKey,
            Headers = settings.Headers,
            Timeout = settings.Timeout,
            IncludeStreamOptions = settings.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
                || settings.Provider.Equals("openrouter", StringComparison.OrdinalIgnoreCase)
        });
        var tools = BuiltInTools.Create(workspace.Root);
        var context = new CompositeContextProvider([
            new StaticContextProvider($"Workspace root: {workspace.Root}\nWorking directory: {workspace.WorkingDirectory}\nGit repository: {workspace.IsGitRepository}"),
            new AgentInstructionsContextProvider(workspace.Root, workspace.WorkingDirectory)
        ]);
        var approval = new PolicyApprovalService(settings.Approval, PromptForApprovalAsync);
        return new Agent(
            provider,
            tools,
            approval,
            context,
            new ConsoleAgentObserver(arguments.Verbose, arguments.Debug, _unicodeConsole),
            new AgentOptions(settings.Model, settings.MaxIterations),
            workspace.Root);
    }

    private static bool TryEnableUnicodeConsole()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException
            or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }, disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static async ValueTask<bool> PromptForApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var call = ConsoleText.ForConsole(
            ToolCallSummary.Describe(request.ToolName, request.ArgumentsJson, ApprovalSummaryLength),
            _unicodeConsole);
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine($"Denied {call}: approval is required but input is redirected.");
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Console.Error.WriteLine($"Approve {call}");
        Console.Error.Write($"  [{request.Level}] y/N? ");
        var readLine = Task.Run(Console.ReadLine, CancellationToken.None);
        using var promptCompleted = new CancellationTokenSource();
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, promptCompleted.Token);
        var completed = await Task.WhenAny(
            readLine,
            Task.Delay(Timeout.Infinite, waitCancellation.Token)).ConfigureAwait(false);
        if (completed != readLine)
        {
            Console.Error.WriteLine();
            cancellationToken.ThrowIfCancellationRequested();
        }

        promptCompleted.Cancel();
        var answer = await readLine.ConfigureAwait(false);
        return answer is not null && answer.Equals("y", StringComparison.OrdinalIgnoreCase);
    }

    private static int PrintModels(WfxSettings settings, WorkspaceInfo workspace)
    {
        Console.WriteLine($"Provider: {settings.Provider}");
        Console.WriteLine($"Protocol: {settings.Protocol}");
        if (settings.Profile is not null)
        {
            Console.WriteLine($"Profile: {settings.Profile}");
        }

        Console.WriteLine($"Model: {(string.IsNullOrEmpty(settings.Model) ? "(not configured)" : settings.Model)}");
        Console.WriteLine($"Base URL: {settings.BaseUri}");
        Console.WriteLine($"Credentials: {(settings.ApiKey is null ? "not configured" : "configured")}");
        Console.WriteLine($"Workspace: {workspace.Root}");
        return 0;
    }

    private static int PrintConfig(WfxSettings settings, WorkspaceInfo workspace)
    {
        PrintModels(settings, workspace);
        Console.WriteLine($"Approval: {settings.Approval.ToString().ToLowerInvariant()}");
        Console.WriteLine($"Timeout: {settings.Timeout.TotalSeconds:F0}s");
        Console.WriteLine($"Maximum iterations: {settings.MaxIterations}");
        Console.WriteLine($"Project config: {Path.Combine(workspace.Root, ".wfx", "config.json")}");
        Console.WriteLine($"User config: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wfx", "config.json")}");
        return 0;
    }

    private static void EnsureRunnable(WfxSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException("No model is configured. Set WFX_MODEL or pass --model <model>.");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            WFX — Windows-first embeddable AI coding agent

            Usage:
              wfx [options]                 Start interactive mode
              wfx run [options] <prompt>    Run one task
              wfx models [options]          Show provider/model configuration
              wfx config [options]          Inspect effective configuration

            Options:
              --model <model>               Model ID; openrouter/<id> selects OpenRouter
              --profile <name>              Named profile from user/project configuration
              --protocol <name>             chat_completions, responses, or anthropic_messages (reserved)
              --provider <name>             openai, openrouter, anthropic, local, or a custom name
              --base-url <url>              OpenAI-compatible API base URL
              --approval <mode>             always, workspace, or never
              --timeout <seconds>           Provider timeout (1-3600)
              --max-iterations <count>      Agent loop limit (1-100)
              --verbose                     Show timing and progress details
              --debug                       Show tool result diagnostics
              --help                        Show help
              --version                     Show version

            Configuration precedence: CLI > environment > project > user > defaults.
            Prefer WFX_API_KEY for credentials. WFX never prints API keys.
            """);
    }
}
