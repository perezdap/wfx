using System.Net;
using Wfx.Core;
using Wfx.Providers;
using Wfx.Tools;

namespace Wfx.Cli;

internal static class Program
{
    private const string Version = "0.1.0";

    public static async Task<int> Main(string[] args)
    {
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
        var provider = new OpenAiCompatibleProvider(httpClient, new OpenAiProviderOptions
        {
            BaseUri = settings.BaseUri,
            ApiKey = settings.ApiKey,
            Headers = settings.Headers,
            Timeout = settings.Timeout
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
            new ConsoleAgentObserver(arguments.Verbose, arguments.Debug),
            new AgentOptions(settings.Model, settings.MaxIterations),
            workspace.Root);
    }

    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }, disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static ValueTask<bool> PromptForApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine($"Denied {request.ToolName}: approval is required but input is redirected.");
            return ValueTask.FromResult(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Console.Error.Write($"Approve {request.ToolName} [{request.Level}]? [y/N] ");
        var answer = Console.ReadLine();
        return ValueTask.FromResult(answer is not null && answer.Equals("y", StringComparison.OrdinalIgnoreCase));
    }

    private static int PrintModels(WfxSettings settings, WorkspaceInfo workspace)
    {
        Console.WriteLine($"Provider: {settings.Provider}");
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
              --provider <name>             openai, openrouter, local, or a custom name
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
