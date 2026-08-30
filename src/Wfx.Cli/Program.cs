using System.Net;
using System.Text;
using System.Text.Json;
using Wfx.Core;
using Wfx.Mcp;
using Wfx.Providers;
using Wfx.Tools;

namespace Wfx.Cli;

internal static class Program
{
    private const string Version = "0.1.0";

    private const int ApprovalSummaryLength = 400;

    private const int RemediationWrapWidth = 80;

    private static bool _unicodeConsole;

    private static AnsiPalette _palette;

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
        IConsoleEnvironment? consoleEnvironment = null,
        Func<WfxSettings, HttpClient, IModelProvider>? modelProviderFactory = null,
        TimeProvider? timeProvider = null)
    {
        var console = consoleEnvironment ?? SystemConsoleEnvironment.Instance;
        try
        {
            var arguments = CliArguments.Parse(args);

            // Decoration lives on stderr (ADR 0008), so it is gated on stderr, not stdout.
            _palette = new AnsiPalette(!arguments.Quiet && !console.IsErrorRedirected && !NoColorRequested());
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

            var reportWarnings = !arguments.Json || !arguments.Quiet;

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
                if (resolution.OverridingProfile is not null && reportWarnings)
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
                    userProfile,
                    reportWarnings);
            }
            catch (InvalidOperationException exception)
                when (arguments.Command is CliCommand.Models or CliCommand.Config)
            {
                // Non-turn commands follow the outer exit-code table only: a configuration
                // error before the result object can be built is exit 2, not a usage error.
                Console.Error.WriteLine(_palette.Red($"wfx: {exception.Message}"));
                return 2;
            }

            if (reportWarnings)
            {
                foreach (var warning in settings.Warnings)
                {
                    Console.Error.WriteLine(_palette.Yellow($"wfx: warning: {warning}"));
                }
            }

            var refusal = StartupApprovalGate.Evaluate(arguments.Command, settings.Approval, console);
            if (refusal is not null)
            {
                Console.Error.WriteLine(_palette.Red(refusal.Message));
                return refusal.ExitCode;
            }

            using var resumedSession = resumeTranscript is not null
                ? SessionResume.Open(sessionStore, workspace, resumeTranscript.SessionId, arguments.Force)
                : null;

            return arguments.Command switch
            {
                CliCommand.Models => arguments.Json ? PrintModelsJson(settings, reportWarnings) : PrintModels(settings, workspace),
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
                    modelProviderFactory,
                    cancellationToken,
                    timeProvider,
                    userProfile).ConfigureAwait(false),
                CliCommand.Resume when arguments.Json => await RunJsonResumeAsync(
                    settings,
                    workspace,
                    arguments,
                    httpClient,
                    resumedSession!,
                    console,
                    modelProviderFactory,
                    cancellationToken,
                    timeProvider,
                    userProfile).ConfigureAwait(false),
                CliCommand.Resume => await RunInteractiveAsync(
                    settings,
                    workspace,
                    arguments,
                    httpClient,
                    sessionStore,
                    resumedSession,
                    console,
                    modelProviderFactory,
                    cancellationToken,
                    timeProvider,
                    userProfile).ConfigureAwait(false),
                _ => await RunInteractiveAsync(
                    settings,
                    workspace,
                    arguments,
                    httpClient,
                    sessionStore,
                    null,
                    console,
                    modelProviderFactory,
                    cancellationToken,
                    timeProvider,
                    userProfile).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(_palette.Red($"wfx: {exception.Message}"));
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
        Func<WfxSettings, HttpClient, IModelProvider>? modelProviderFactory,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider,
        string? userProfile = null)
    {
        EnsureRunnable(settings);
        if (!arguments.Json)
        {
            if (!arguments.Quiet)
            {
                Console.Error.WriteLine(_palette.Dim(settings.Profile is null
                    ? $"wfx: {settings.Provider}/{settings.Model}"
                    : $"wfx: profile '{settings.Profile}' ({settings.Provider}/{settings.Model})"));
            }

            WarnIfYolo(settings);
        }

        using var session = OpenSession(
            arguments,
            workspace,
            arguments.Json || arguments.Quiet ? TextWriter.Null : Console.Error,
            "wfx: session ",
            sessionStore);
        var provider = CreateModelProvider(settings, httpClient, modelProviderFactory);
        await using var mcp = await ConnectMcpAsync(settings, workspace, arguments, cancellationToken).ConfigureAwait(false);
        var skills = DiscoverSkills(userProfile, workspace, cancellationToken);
        var agent = CreateAgent(settings, workspace, arguments, provider, settings.MaxIterations, [], session, console, timeProvider, mcp.Tools, skills);
        return await RunTurnCommandAsync(agent, prompt, arguments, console, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunJsonResumeAsync(
        WfxSettings settings,
        WorkspaceInfo workspace,
        CliArguments arguments,
        HttpClient httpClient,
        SessionResume resumedSession,
        IConsoleEnvironment console,
        Func<WfxSettings, HttpClient, IModelProvider>? modelProviderFactory,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider,
        string? userProfile = null)
    {
        EnsureRunnable(settings);
        var prompt = await ReadConsoleLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("wfx resume --json requires one prompt on stdin.");
        }

        var provider = CreateModelProvider(settings, httpClient, modelProviderFactory);
        await using var mcp = await ConnectMcpAsync(settings, workspace, arguments, cancellationToken).ConfigureAwait(false);
        var skills = DiscoverSkills(userProfile, workspace, cancellationToken);
        var agent = CreateAgent(
            settings,
            workspace,
            arguments,
            provider,
            settings.MaxIterations,
            resumedSession.Transcript.Messages,
            resumedSession.Log,
            console,
            timeProvider,
            mcp.Tools,
            skills);
        return await RunTurnCommandAsync(agent, prompt, arguments, console, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunTurnCommandAsync(
        Agent agent,
        string prompt,
        CliArguments arguments,
        IConsoleEnvironment console,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await agent.RunAsync(prompt, cancellationToken).ConfigureAwait(false);
            if (!arguments.Json)
            {
                WriteFinalResponseToStdout(result, console);
            }

            if (result.Status is AgentRunStatus.IterationLimitReached)
            {
                if (!arguments.Json)
                {
                    WriteIterationLimitReached(result, "raise --max-iterations to let the run continue");
                }

                return 4;
            }

            if (!arguments.Json && !arguments.Quiet && arguments.Verbose)
            {
                Console.Error.WriteLine($"[wfx] completed in {result.Iterations} model iteration(s)");
            }

            return 0;
        }
        catch (Exception exception) when (arguments.Json)
        {
            Console.Error.WriteLine(_palette.Red($"wfx: {exception.Message}"));
            return exception is OperationCanceledException or TimeoutException ? 4 : 5;
        }
    }

    private static async Task<int> RunInteractiveAsync(
        WfxSettings settings,
        WorkspaceInfo workspace,
        CliArguments arguments,
        HttpClient httpClient,
        ISessionStore sessionStore,
        SessionResume? resumedSession,
        IConsoleEnvironment console,
        Func<WfxSettings, HttpClient, IModelProvider>? modelProviderFactory,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider,
        string? userProfile = null)
    {
        if (string.IsNullOrWhiteSpace(settings.Model) && settings.ConfiguredModels.Count == 0)
        {
            EnsureRunnable(settings);
        }

        Console.Error.WriteLine(_palette.Dim("WFX"));
        Console.Error.WriteLine();
        PrintActiveModel(settings);
        Console.Error.WriteLine(_palette.Dim($"Workspace: {workspace.Root}"));
        WarnIfYolo(settings);
        using var createdSession = resumedSession is null
            ? OpenSession(arguments, workspace, Console.Error, "Session: ", sessionStore)
            : null;
        var session = resumedSession?.Log ?? createdSession;
        var transcript = resumedSession?.Transcript;
        if (resumedSession is not null)
        {
            Console.Error.WriteLine(_palette.Dim($"Resumed session: {resumedSession.Transcript.SessionId}"));
        }

        Console.Error.WriteLine();

        var provider = CreateModelProvider(settings, httpClient, modelProviderFactory);
        await using var mcp = await ConnectMcpAsync(settings, workspace, arguments, cancellationToken).ConfigureAwait(false);
        var skills = DiscoverSkills(userProfile, workspace, cancellationToken);
        IReadOnlyList<ModelMessage> conversation = transcript?.Messages ?? [];
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.Write(_palette.Bold("> "));
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
                Console.Error.WriteLine();
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
                            provider = CreateModelProvider(settings, httpClient, modelProviderFactory);
                        }

                        foreach (var warning in settings.Warnings)
                        {
                            Console.Error.WriteLine(_palette.Yellow($"wfx: warning: {warning}"));
                        }

                        PrintActiveModel(settings);
                    }
                }

                Console.Error.WriteLine();
                continue;
            }

            try
            {
                EnsureRunnable(settings);
                var agent = CreateAgent(settings, workspace, arguments, provider, null, conversation, session, console, timeProvider, mcp.Tools, skills);
                var result = await agent.RunAsync(prompt, cancellationToken).ConfigureAwait(false);
                conversation = result.Messages;
                WriteFinalResponseToStdout(result, console);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.Error.WriteLine(_palette.Red($"wfx: {exception.Message}"));
            }

            Console.Error.WriteLine();
        }

        return 0;
    }

    private static WfxSettings LoadSettings(
        string workspaceRoot,
        WfxSettingsLayer layer,
        WfxSettingsLayer cliOnly,
        EndpointIdentity? recordedEndpoint,
        string? userProfile,
        bool reportWarnings)
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
            if (reportWarnings)
            {
                Console.Error.WriteLine(
                    $"wfx: recorded profile '{recordedEndpoint.Profile}' is no longer configured; using current settings instead.");
            }

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

        Console.Error.WriteLine("Configured models:");
        for (var index = 0; index < settings.ConfiguredModels.Count; index++)
        {
            var model = settings.ConfiguredModels[index];
            Console.Error.WriteLine($"  {index + 1}. {model.Profile}/{model.Provider}: {model.Model}");
        }

        Console.Error.Write("Select model: ");
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
            Console.Error.WriteLine(_palette.Yellow(
                "wfx: warning: approval is yolo; tool prompts are bypassed. Workspace path checks still apply."));
        }
    }

    private static void PrintActiveModel(WfxSettings settings)
    {
        if (settings.Profile is not null)
        {
            Console.Error.WriteLine(_palette.Dim($"Profile: {settings.Profile}"));
        }

        var model = string.IsNullOrWhiteSpace(settings.Model) ? "(not configured)" : settings.Model;
        Console.Error.WriteLine(_palette.Dim($"Model: {settings.Provider}/{model}"));
    }

    private static void PrintInteractiveHelp()
    {
        Console.Error.WriteLine("Commands:");
        Console.Error.WriteLine("  /model             List configured models and choose one");
        Console.Error.WriteLine("  /model <id>        Use a model ID on the current connection");
        Console.Error.WriteLine("  /help              Show interactive commands");
        Console.Error.WriteLine("  /exit, /quit       End the session");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Resume this session later with 'wfx resume' (or 'wfx resume --id <session-id>').");
    }

    /// <summary>
    /// The NO_COLOR convention: colour is disabled when the variable is present and non-empty.
    /// </summary>
    private static bool NoColorRequested() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    /// <summary>
    /// Writes the final response to stdout, and only when stdout is redirected (ADR 0008). A turn
    /// that did not complete has no final response, so a redirected stdout stays empty.
    /// </summary>
    private static void WriteFinalResponseToStdout(AgentRunResult result, IConsoleEnvironment console)
    {
        if (!console.IsOutputRedirected || result.Status is not AgentRunStatus.Completed)
        {
            return;
        }

        var text = result.FinalResponse;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Console.Out.Write(text);
        if (!text.EndsWith('\n'))
        {
            Console.Out.WriteLine();
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
            if (arguments.Json)
            {
                throw new IOException($"Could not create the session required by --json: {exception.Message}", exception);
            }

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

    private static ISkillLocator DiscoverSkills(
        string? userProfile,
        WorkspaceInfo workspace,
        CancellationToken cancellationToken = default)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var skills = SkillLocator.Discover(userProfile, workspace.Root, cancellationToken);
        foreach (var warning in skills.Warnings)
        {
            Console.Error.WriteLine($"wfx: warning: {warning}");
        }

        return skills;
    }

    private static Agent CreateAgent(
        WfxSettings settings,
        WorkspaceInfo workspace,
        CliArguments arguments,
        IModelProvider provider,
        int? maxIterations,
        IReadOnlyList<ModelMessage> conversation,
        SessionLog? session,
        IConsoleEnvironment console,
        TimeProvider? timeProvider,
        IReadOnlyList<ITool> mcpTools,
        ISkillLocator skills)
    {
        var tools = mcpTools.Count == 0
            ? BuiltInTools.Create(workspace.Root, skills)
            : new ToolRegistry([.. BuiltInTools.CreateTools(workspace.Root, skills), .. mcpTools]);
        var contextProviders = new List<IContextProvider>
        {
            new StaticContextProvider($"Workspace root: {workspace.Root}\nWorking directory: {workspace.WorkingDirectory}\nGit repository: {workspace.IsGitRepository}"),
            new AgentInstructionsContextProvider(workspace.Root, workspace.WorkingDirectory)
        };
        if (skills.Skills.Count > 0)
        {
            contextProviders.Add(new SkillContextProvider(skills));
        }

        var context = new CompositeContextProvider(contextProviders);
        var secrets = ProviderSecrets(settings);
        var approval = new PolicyApprovalService(
            settings.Approval,
            (request, token) => PromptForApprovalAsync(request, secrets, console, token));
        var observers = new List<IAgentObserver>();
        if (session is not null)
        {
            observers.Add(new SessionRecorder(session));
        }

        if (arguments.Json)
        {
            // stdout is the NDJSON event stream, so the console observer's human rendering —
            // which is all on stderr (ADR 0008) — would duplicate the stream's content as
            // decoration. Approval prompts are separate and still reach stderr.
            observers.Add(new NdjsonAgentObserver(Console.Out));
        }
        else
        {
            observers.Add(new ConsoleAgentObserver(
                arguments.Verbose,
                arguments.Debug,
                arguments.Quiet,
                _unicodeConsole && !arguments.Quiet && !console.IsErrorRedirected,
                secrets,
                _palette));
        }

        return new Agent(
            provider,
            tools,
            approval,
            context,
            new CompositeAgentObserver([.. observers]),
            new AgentOptions(
                new EndpointIdentity(settings.Profile, settings.Provider, settings.Protocol, settings.Model),
                maxIterations),
            workspace.Root,
            conversation,
            new AgentTurnMetadata(session?.Id ?? string.Empty, settings.Approval),
            timeProvider);
    }

    /// <summary>
    /// Connects every user-configured MCP stdio server eagerly. Unavailable servers warn
    /// and contribute no tools; a failed server never aborts the invocation.
    /// </summary>
    private static async ValueTask<McpHost> ConnectMcpAsync(
        WfxSettings settings,
        WorkspaceInfo workspace,
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        var reportWarnings = !arguments.Json || !arguments.Quiet;
        return await McpHost.ConnectAsync(
            settings.McpServers,
            workspace.Root,
            message =>
            {
                if (reportWarnings)
                {
                    Console.Error.WriteLine(_palette.Yellow($"wfx: warning: {message}"));
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static IModelProvider CreateModelProvider(
        WfxSettings settings,
        HttpClient httpClient,
        Func<WfxSettings, HttpClient, IModelProvider>? modelProviderFactory) =>
        modelProviderFactory?.Invoke(settings, httpClient) ?? ModelTransports.Create(
            settings.Protocol,
            httpClient,
            new OpenAiProviderOptions
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

        Console.Error.WriteLine(_palette.Yellow($"Approve {call}"));
        Console.Error.Write(_palette.Yellow($"  [{request.Level}] y/N? "));
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

    private static int PrintModelsJson(WfxSettings settings, bool reportWarnings)
    {
        // Unresolvable profiles still appear in the result object with null endpoint fields;
        // the reason is out-of-band on stderr so the stdout contract stays the spec shape.
        if (reportWarnings)
        {
            foreach (var profile in settings.ModelListing)
            {
                if (profile.Error is not null)
                {
                    Console.Error.WriteLine(
                        $"wfx: warning: profile '{profile.Name}' could not be resolved: {profile.Error}");
                }
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
              --max-iterations <count>      Noninteractive loop limit (1-100; default 24)
                                            Interactive mode is unlimited
              --verbose                     Show timing and progress details
              --debug                       Show tool result diagnostics
              --json                        Machine-readable output: NDJSON events for run/resume,
                                            one result object for sessions/config/models
              --quiet                       Presentation flag; suppress human decoration on stderr
                                            in interactive mode and the commands listed below
              --no-session                  Do not persist a session log for this invocation
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
            wfx run --json streams one event per line. wfx resume --id <session-id> --json reads one
            prompt from stdin and streams the resumed turn. The stream is credential-adjacent; do not
            send it to shared logs without reviewing its contents.

            Machine-readable output: wfx sessions --json, wfx config --json, and wfx models --json
            write one JSON result object to stdout, not an event stream. Shapes carry schema_version
            1 and are published under docs/schemas/ with every field marked public or internal.

            --quiet is available on run, resume, sessions, config, and models.
            It is also available in interactive mode and does not change stdout.
            In human mode, errors and warnings still use stderr.
            --json --quiet preserves the JSON output and limits stderr to terminal failures.

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
              2    config error
              3    wfx run or wfx resume refused to start: approval needs a terminal
              4    run stopped at maximum iterations, or JSON turn interrupted
              5    JSON turn error: provider, tool, protocol, or configuration
              130  human-mode turn cancelled
            """);
    }
}
