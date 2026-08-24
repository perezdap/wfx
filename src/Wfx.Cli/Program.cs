using System.Net;
using System.Text;
using System.Text.Json;
using Wfx.Core;
using Wfx.Providers;
using Wfx.Tools;

namespace Wfx.Cli;

internal static class Program
{
    private const string Version = "0.1.0";

    private const int ApprovalSummaryLength = 400;

    private const int RemediationWrapWidth = 80;

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

        using var httpClient = CreateHttpClient();
        return await RunAsync(args, httpClient, new SessionStore(), shutdown.Token).ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(
        string[] args,
        HttpClient httpClient,
        ISessionStore sessionStore,
        CancellationToken cancellationToken,
        string? userProfile = null,
        IConsoleEnvironment? consoleEnvironment = null)
    {
        var console = consoleEnvironment ?? SystemConsoleEnvironment.Instance;
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

            // Session listing needs no model or workspace config, so it runs before settings
            // resolution, which can throw on an unconfigured endpoint.
            if (arguments.Command == CliCommand.Sessions)
            {
                return arguments.Json ? PrintSessionsJson(sessionStore) : PrintSessions(sessionStore);
            }

            var workspace = WorkspaceInfo.Discover();
            var resumeTranscript = arguments.Command == CliCommand.Resume
                ? SessionResume.Inspect(sessionStore, workspace, arguments.SessionId, arguments.Force)
                : null;
            var settingsLayer = arguments.Settings;
            if (resumeTranscript is not null)
            {
                var resolution = SessionResume.ResolveSettings(resumeTranscript, arguments.Settings);
                settingsLayer = resolution.Layer;
                if (resolution.OverridingProfile is not null)
                {
                    Console.Error.WriteLine(
                        $"wfx: profile '{resolution.OverridingProfile}' overrides the recorded endpoint for this resumed session.");
                }
            }

            WfxSettings settings;
            try
            {
                settings = LoadSettings(
                    workspace.Root,
                    settingsLayer,
                    arguments.Settings,
                    resumeTranscript?.LastEndpoint,
                    userProfile);
            }
            catch (InvalidOperationException exception)
                when (arguments.Command is CliCommand.Models or CliCommand.Config)
            {
                // Non-turn commands follow the outer exit-code table only: a configuration
                // error before the result object can be built is exit 2, not a usage error.
                Console.Error.WriteLine($"wfx: {exception.Message}");
                return 2;
            }

            foreach (var warning in settings.Warnings)
            {
                Console.Error.WriteLine($"wfx: warning: {warning}");
            }

            var refusal = StartupApprovalGate.Evaluate(arguments.Command, settings.Approval, console);
            if (refusal is not null)
            {
                Console.Error.WriteLine(refusal.Message);
                return refusal.ExitCode;
            }

            using var resumedSession = resumeTranscript is not null
                ? SessionResume.Open(sessionStore, workspace, resumeTranscript.SessionId, arguments.Force)
                : null;

            return arguments.Command switch
            {
                CliCommand.Models => arguments.Json ? PrintModelsJson(settings) : PrintModels(settings, workspace),
                CliCommand.Config => arguments.Json
                    ? PrintConfigJson(settings)
                    : PrintConfig(settings, workspace, userProfile),
                CliCommand.Run => await RunOnceAsync(
                    arguments.Prompt!,
                    settings,
                    workspace,
                    arguments,
                    httpClient,
                    sessionStore,
                    console,
                    cancellationToken).ConfigureAwait(false),
                CliCommand.Resume => await RunInteractiveAsync(
                    settings,
                    workspace,
                    arguments,
                    httpClient,
                    sessionStore,
                    resumedSession,
                    console,
                    cancellationToken).ConfigureAwait(false),
                _ => await RunInteractiveAsync(
                    settings,
                    workspace,
                    arguments,
                    httpClient,
                    sessionStore,
                    null,
                    console,
                    cancellationToken).ConfigureAwait(false)
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
        ISessionStore sessionStore,
        IConsoleEnvironment console,
        CancellationToken cancellationToken)
    {
        EnsureRunnable(settings);
        Console.Error.WriteLine(settings.Profile is null
            ? $"wfx: {settings.Provider}/{settings.Model}"
            : $"wfx: profile '{settings.Profile}' ({settings.Provider}/{settings.Model})");
        WarnIfYolo(settings);

        using var session = OpenSession(arguments, workspace, Console.Error, "wfx: session ", sessionStore);

        var provider = CreateModelProvider(settings, httpClient);
        var agent = CreateAgent(settings, workspace, arguments, provider, [], session, console);
        var result = await agent.RunAsync(prompt, cancellationToken).ConfigureAwait(false);
        PrintTrailingNewline(result);

        if (result.Status is AgentRunStatus.IterationLimitReached)
        {
            WriteIterationLimitReached(result, "raise --max-iterations to let the run continue");
            return 2;
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
        ISessionStore sessionStore,
        SessionResume? resumedSession,
        IConsoleEnvironment console,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Model) && settings.ConfiguredModels.Count == 0)
        {
            EnsureRunnable(settings);
        }

        Console.WriteLine("WFX");
        Console.WriteLine();
        PrintActiveModel(settings);
        Console.WriteLine($"Workspace: {workspace.Root}");
        WarnIfYolo(settings);
        using var createdSession = resumedSession is null
            ? OpenSession(arguments, workspace, Console.Out, "Session: ", sessionStore)
            : null;
        var session = resumedSession?.Log ?? createdSession;
        var transcript = resumedSession?.Transcript;
        if (resumedSession is not null)
        {
            Console.WriteLine($"Resumed session: {resumedSession.Transcript.SessionId}");
        }

        Console.WriteLine();

        var provider = CreateModelProvider(settings, httpClient);
        IReadOnlyList<ModelMessage> conversation = transcript?.Messages ?? [];
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("> ");
            var prompt = await ReadConsoleLineAsync(cancellationToken).ConfigureAwait(false);
            if (prompt is null || prompt.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
                prompt.Equals("/quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                continue;
            }

            if (prompt.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                PrintInteractiveHelp();
                Console.WriteLine();
                continue;
            }

            if (IsModelCommand(prompt))
            {
                var request = await ReadModelSwitchRequestAsync(prompt, settings, cancellationToken).ConfigureAwait(false);
                if (request is not null)
                {
                    var resolution = ModelSwitchResolver.Resolve(settings, request);
                    if (!resolution.Succeeded)
                    {
                        Console.Error.WriteLine($"wfx: {resolution.Error}");
                    }
                    else
                    {
                        conversation = resolution.MapConversation(conversation);
                        settings = resolution.Settings!;
                        if (resolution.TransportChanged)
                        {
                            provider = CreateModelProvider(settings, httpClient);
                        }

                        foreach (var warning in settings.Warnings)
                        {
                            Console.Error.WriteLine($"wfx: warning: {warning}");
                        }

                        PrintActiveModel(settings);
                    }
                }

                Console.WriteLine();
                continue;
            }

            try
            {
                EnsureRunnable(settings);
                var agent = CreateAgent(settings, workspace, arguments, provider, conversation, session, console);
                var result = await agent.RunAsync(prompt, cancellationToken).ConfigureAwait(false);
                conversation = result.Messages;
                PrintTrailingNewline(result);

                if (result.Status is AgentRunStatus.IterationLimitReached)
                {
                    WriteIterationLimitReached(
                        result,
                        "raise --max-iterations or restate the task to continue");
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

    private static WfxSettings LoadSettings(
        string workspaceRoot,
        WfxSettingsLayer layer,
        WfxSettingsLayer cliOnly,
        EndpointIdentity? recordedEndpoint,
        string? userProfile)
    {
        try
        {
            return WfxConfiguration.Load(workspaceRoot, layer, userProfile: userProfile);
        }
        catch (UndefinedProfileException exception)
            when (recordedEndpoint?.Profile is not null &&
                  cliOnly.Profile is null &&
                  string.Equals(
                      exception.ProfileName,
                      recordedEndpoint.Profile,
                      StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"wfx: recorded profile '{recordedEndpoint.Profile}' is no longer configured; using current settings instead.");
            return WfxConfiguration.Load(workspaceRoot, cliOnly, userProfile: userProfile);
        }
    }

    private static bool IsModelCommand(string prompt) =>
        prompt.Equals("/model", StringComparison.OrdinalIgnoreCase) ||
        (prompt.Length > "/model".Length &&
         prompt.StartsWith("/model", StringComparison.OrdinalIgnoreCase) &&
         char.IsWhiteSpace(prompt["/model".Length]));

    private static async Task<ModelSwitchRequest?> ReadModelSwitchRequestAsync(
        string prompt,
        WfxSettings settings,
        CancellationToken cancellationToken)
    {
        var argument = prompt["/model".Length..].Trim();
        if (argument.Length > 0)
        {
            return ModelSwitchRequest.FreeForm(argument);
        }

        if (settings.ConfiguredModels.Count == 0)
        {
            Console.Error.WriteLine("wfx: No configured models are available. Add a profile with a model key.");
            return null;
        }

        Console.WriteLine("Configured models:");
        for (var index = 0; index < settings.ConfiguredModels.Count; index++)
        {
            var model = settings.ConfiguredModels[index];
            Console.WriteLine($"  {index + 1}. {model.Profile}/{model.Provider}: {model.Model}");
        }

        Console.Write("Select model: ");
        var selection = await ReadConsoleLineAsync(cancellationToken).ConfigureAwait(false);
        return selection is null ? null : ModelSwitchRequest.Picker(selection.Trim());
    }

    private static async Task<string?> ReadConsoleLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var readLine = Task.Run(Console.ReadLine, CancellationToken.None);
        using var readCompleted = new CancellationTokenSource();
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            readCompleted.Token);
        var completed = await Task.WhenAny(
            readLine,
            Task.Delay(Timeout.Infinite, waitCancellation.Token)).ConfigureAwait(false);
        if (completed != readLine)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        readCompleted.Cancel();
        return await readLine.ConfigureAwait(false);
    }

    private static void WarnIfYolo(WfxSettings settings)
    {
        if (settings.Approval == ApprovalMode.AllowAll)
        {
            Console.Error.WriteLine(
                "wfx: warning: approval is yolo; tool prompts are bypassed. Workspace path checks still apply.");
        }
    }

    private static void PrintActiveModel(WfxSettings settings)
    {
        if (settings.Profile is not null)
        {
            Console.WriteLine($"Profile: {settings.Profile}");
        }

        var model = string.IsNullOrWhiteSpace(settings.Model) ? "(not configured)" : settings.Model;
        Console.WriteLine($"Model: {settings.Provider}/{model}");
    }

    private static void PrintInteractiveHelp()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  /model             List configured models and choose one");
        Console.WriteLine("  /model <id>        Use a model ID on the current connection");
        Console.WriteLine("  /help              Show interactive commands");
        Console.WriteLine("  /exit, /quit       End the session");
        Console.WriteLine();
        Console.WriteLine("Resume this session later with 'wfx resume' (or 'wfx resume --id <session-id>').");
    }

    private static void PrintTrailingNewline(AgentRunResult result)
    {
        var text = result.Status is AgentRunStatus.Completed
            ? result.FinalResponse
            : result.AccumulatedText;
        if (!string.IsNullOrEmpty(text) && !text.EndsWith('\n'))
        {
            Console.WriteLine();
        }
    }

    private static void WriteIterationLimitReached(AgentRunResult result, string hint)
    {
        Console.Error.WriteLine(
            $"wfx: {result.Note ?? $"iteration limit reached after {result.Iterations} iteration(s)"}; {hint}");
    }

    private static SessionLog? OpenSession(
        CliArguments arguments,
        WorkspaceInfo workspace,
        TextWriter output,
        string prefix,
        ISessionStore sessionStore)
    {
        if (arguments.NoSession)
        {
            return null;
        }

        SessionLog session;
        try
        {
            session = sessionStore.Create(workspace.Root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"wfx: warning: Could not create session: {exception.Message}. The invocation will continue without a session.");
            return null;
        }

        try
        {
            output.WriteLine($"{prefix}{session.Id}");
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static Agent CreateAgent(
        WfxSettings settings,
        WorkspaceInfo workspace,
        CliArguments arguments,
        IModelProvider provider,
        IReadOnlyList<ModelMessage> conversation,
        SessionLog? session,
        IConsoleEnvironment console)
    {
        var tools = BuiltInTools.Create(workspace.Root);
        var context = new CompositeContextProvider([
            new StaticContextProvider($"Workspace root: {workspace.Root}\nWorking directory: {workspace.WorkingDirectory}\nGit repository: {workspace.IsGitRepository}"),
            new AgentInstructionsContextProvider(workspace.Root, workspace.WorkingDirectory)
        ]);
        var secrets = ProviderSecrets(settings);
        var approval = new PolicyApprovalService(
            settings.Approval,
            (request, token) => PromptForApprovalAsync(request, secrets, console, token));
        IAgentObserver observer = new ConsoleAgentObserver(arguments.Verbose, arguments.Debug, _unicodeConsole, secrets);
        if (session is not null)
        {
            observer = new CompositeAgentObserver(new SessionRecorder(session), observer);
        }

        return new Agent(
            provider,
            tools,
            approval,
            context,
            observer,
            new AgentOptions(
                new EndpointIdentity(settings.Profile, settings.Provider, settings.Protocol, settings.Model),
                settings.MaxIterations),
            workspace.Root,
            conversation);
    }

    private static IModelProvider CreateModelProvider(WfxSettings settings, HttpClient httpClient) =>
        ModelTransports.Create(settings.Protocol, httpClient, new OpenAiProviderOptions
        {
            BaseUri = settings.BaseUri,
            ApiKey = settings.ApiKey,
            Headers = settings.Headers,
            Timeout = settings.Timeout,
            IncludeStreamOptions = settings.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
                || settings.Provider.Equals("openrouter", StringComparison.OrdinalIgnoreCase)
        });

    private static IReadOnlyList<string> ProviderSecrets(WfxSettings settings)
    {
        var secrets = new List<string>();
        if (!string.IsNullOrEmpty(settings.ApiKey))
        {
            secrets.Add(settings.ApiKey);
        }

        foreach (var value in settings.Headers.Values)
        {
            if (!string.IsNullOrEmpty(value))
            {
                secrets.Add(value);
            }
        }

        return secrets;
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

    private static async ValueTask<bool> PromptForApprovalAsync(
        ApprovalRequest request,
        IReadOnlyList<string> secrets,
        IConsoleEnvironment console,
        CancellationToken cancellationToken)
    {
        var call = ConsoleText.ForConsole(
            ToolCallSummary.Describe(request.ToolName, request.ArgumentsJson, ApprovalSummaryLength, secrets),
            _unicodeConsole);
        if (console.IsInputRedirected)
        {
            Console.Error.WriteLine($"Denied {call}: approval is required but input is redirected.");
            return false;
        }

        Console.Error.WriteLine($"Approve {call}");
        Console.Error.Write($"  [{request.Level}] y/N? ");
        try
        {
            var answer = await ReadConsoleLineAsync(cancellationToken).ConfigureAwait(false);
            return answer is not null && answer.Equals("y", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine();
            throw;
        }
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

    private static int PrintConfig(WfxSettings settings, WorkspaceInfo workspace, string? userProfile)
    {
        PrintModels(settings, workspace);
        Console.WriteLine($"Approval: {WfxConfiguration.FormatApprovalMode(settings.Approval)}");
        Console.WriteLine($"Timeout: {settings.Timeout.TotalSeconds:F0}s");
        Console.WriteLine($"Maximum iterations: {settings.MaxIterations}");
        Console.WriteLine($"Project config: {Path.Combine(workspace.Root, ".wfx", "config.json")}");
        Console.WriteLine(
            $"User config: {Path.Combine(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wfx", "config.json")}");
        return 0;
    }

    private static int PrintSessions(ISessionStore sessionStore)
    {
        var listing = sessionStore.List();
        var sessions = listing.Sessions;

        if (sessions.Count == 0)
        {
            Console.WriteLine("No sessions.");
            Console.WriteLine($"Total on disk: {FormatBytes(listing.TotalSizeBytes)}");
            return 0;
        }

        Console.WriteLine($"{"SESSION",-22} {"WORKSPACE",-40} {"CREATED (UTC)",-21} {"UPDATED (UTC)",-21} {"SIZE"}");
        foreach (var session in sessions)
        {
            Console.WriteLine(
                $"{session.SessionId,-22} {Truncate(session.Workspace ?? "(unknown)", 40),-40} " +
                $"{FormatTimestamp(session.CreatedAt),-21} {FormatTimestamp(session.UpdatedAt),-21} {FormatBytes(session.SizeBytes)}");
        }

        Console.WriteLine();
        Console.WriteLine($"{sessions.Count} session(s), {FormatBytes(listing.TotalSizeBytes)} total on disk");
        return 0;
    }

    private static int PrintSessionsJson(ISessionStore sessionStore) =>
        PrintJsonResult(writer => JsonResultWriters.WriteSessionsResult(writer, sessionStore.List()));

    private static int PrintConfigJson(WfxSettings settings) =>
        PrintJsonResult(writer => JsonResultWriters.WriteConfigResult(writer, settings));

    private static int PrintModelsJson(WfxSettings settings)
    {
        // Unresolvable profiles still appear in the result object with null endpoint fields;
        // the reason is out-of-band on stderr so the stdout contract stays the spec shape.
        foreach (var profile in settings.ModelListing)
        {
            if (profile.Error is not null)
            {
                Console.Error.WriteLine(
                    $"wfx: warning: profile '{profile.Name}' could not be resolved: {profile.Error}");
            }
        }

        return PrintJsonResult(writer => JsonResultWriters.WriteModelsResult(writer, settings));
    }

    /// <summary>
    /// Writes one JSON result object to stdout through <see cref="Console.Out"/> so captured
    /// consoles see it, terminated by a newline for shell-friendly single-object output.
    /// </summary>
    private static int PrintJsonResult(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        Console.Out.Write(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        Console.Out.WriteLine();
        Console.Out.Flush();
        return 0;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var value = bytes / 1024.0;
        if (value < 1024)
        {
            return $"{value:F1} KB";
        }

        value /= 1024;
        if (value < 1024)
        {
            return $"{value:F1} MB";
        }

        value /= 1024;
        return $"{value:F1} GB";
    }

    private static string FormatTimestamp(DateTime? timestamp) =>
        timestamp?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown";

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "...";

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
              wfx sessions [options]        List sessions with workspace, timestamps, sizes, and total
              wfx resume [options]          Resume the latest session for this workspace

            Options:
              --model <model>               Model ID; openrouter/<id> selects OpenRouter
              --profile <name>              Named profile from user/project configuration
              --protocol <name>             chat_completions, responses, or anthropic_messages (reserved)
              --provider <name>             openai, openrouter, anthropic, local, or a custom name
              --base-url <url>              OpenAI-compatible API base URL
              --approval <mode>             always, workspace, never, or yolo
              --yolo                        Bypass tool approval prompts (same as --approval yolo)
              --timeout <seconds>           Provider timeout (1-3600)
              --max-iterations <count>      Agent loop limit (1-100)
              --verbose                     Show timing and progress details
              --debug                       Show tool result diagnostics
              --no-session                  Do not persist a session log for this invocation
              --json                        Machine-readable JSON for sessions, config, and models
              --id <session-id>             Resume a specific session (only with wfx resume)
              --force                       Rebind the session selected with --id
              --help                        Show help
              --version                     Show version

            Interactive commands:
              /model                        List configured models and choose one
              /model <id>                   Use a model ID on the current connection
              /help                         Show interactive commands
              /exit, /quit                  End the session

            Resume a session in a new process with wfx resume, or wfx resume --id <session-id>.

            Machine-readable output: wfx sessions --json, wfx config --json, and wfx models --json
            write one JSON result object to stdout, not an event stream. Shapes carry schema_version
            1 and are published under docs/schemas/ with every field marked public or internal.

            Configuration precedence: CLI > environment > project > user > defaults.
            Prefer WFX_API_KEY for credentials. WFX never prints API keys.
            Interactive mode and wfx run persist a JSONL session under %USERPROFILE%\.wfx\sessions\
            unless --no-session is passed. Session files remain sensitive despite secret redaction.

            wfx run and wfx resume refuse to start when stdin is not a terminal and approval is
            always or workspace: a tool prompt would block with nobody there to answer it.
            """);
        // Wrap the shared remediation wording to the help layout; the stderr refusal keeps the
        // same string as one unbroken sentence.
        foreach (var line in HelpText.Wrap(StartupApprovalGate.Remediation, RemediationWrapWidth))
        {
            Console.WriteLine(line);
        }
        Console.WriteLine("""

            Exit codes:
              0    success
              1    error
              2    config error, or run stopped at the iteration limit (--max-iterations)
              3    wfx run or wfx resume refused to start: approval needs a terminal
              130  cancelled
            """);
    }
}
