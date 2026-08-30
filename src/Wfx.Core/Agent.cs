using System.Text.Json;

namespace Wfx.Core;

/// <summary>
/// The (profile, provider, protocol, model) tuple a turn runs under, recorded per
/// turn because <c>/model</c> can change it mid-session.
/// </summary>
public sealed record EndpointIdentity(string? Profile, string Provider, string Protocol, string Model);

public sealed record AgentOptions
{
    public AgentOptions(string model, int? maxIterations = 24)
        : this(new EndpointIdentity(null, "openai", "chat_completions", model), maxIterations)
    {
    }

    public AgentOptions(EndpointIdentity endpoint, int? maxIterations = 24)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Endpoint = endpoint;
        MaxIterations = maxIterations is null ? null : Math.Clamp(maxIterations.Value, 1, 100);
    }

    public string Model => Endpoint.Model;

    public int? MaxIterations { get; }

    public EndpointIdentity Endpoint { get; }
}

public enum AgentRunStatus
{
    Completed,
    IterationLimitReached
}

public sealed record AgentRunResult(
    string FinalResponse,
    int Iterations,
    IReadOnlyList<ModelMessage> Messages,
    AgentRunStatus Status,
    string? Note = null,
    string? AccumulatedText = null);

public interface IAgentObserver
{
    ValueTask OnEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken) => agentEvent switch
    {
        TurnStartedEvent started => OnTurnStartedAsync(started.Endpoint, cancellationToken),
        MessageEvent message => OnMessageAsync(message.Message, cancellationToken),
        ToolStartedEvent started => OnToolStartedAsync(
            started.Name,
            started.ArgumentsJson,
            started.ApprovalLevel,
            cancellationToken),
        ToolCompletedEvent completed => OnToolCompletedAsync(
            completed.Name,
            completed.Result,
            completed.Duration,
            cancellationToken),
        ToolRejectedEvent rejected => OnToolRejectedAsync(
            rejected.Name,
            rejected.ArgumentsJson,
            rejected.Reason,
            cancellationToken),
        UsageEvent usage => OnUsageAsync(usage.Usage, cancellationToken),
        TurnCompletedEvent => ValueTask.CompletedTask,
        TurnInterruptedEvent => OnTurnInterruptedAsync(cancellationToken),
        TurnErrorEvent error => OnTurnErrorAsync(
            error.Exception ?? new InvalidOperationException(error.Error.Message),
            cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(agentEvent), agentEvent, "Unknown agent event.")
    };

    ValueTask OnModelTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask OnToolStartedAsync(
        string name,
        string argumentsJson,
        ApprovalLevel level,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask OnToolCompletedAsync(string name, ToolResult result, TimeSpan duration, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>
    /// Raised when a tool call never runs: the tool is unknown, its arguments are unusable, or approval was refused.
    /// </summary>
    ValueTask OnToolRejectedAsync(
        string name,
        string argumentsJson,
        string reason,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Raised once at the start of every turn with the endpoint identity the turn runs under.
    /// </summary>
    ValueTask OnTurnStartedAsync(EndpointIdentity endpoint, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>
    /// Raised for every message the turn appends to the conversation: the system prompt on a
    /// new conversation, the user prompt, each completed assistant message (content, tool calls,
    /// provider items), and each tool-result message (tool-call ID and name).
    /// </summary>
    ValueTask OnMessageAsync(ModelMessage message, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Raised per model call, not per turn, whenever the provider reports token usage.
    /// </summary>
    ValueTask OnUsageAsync(ModelUsage usage, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Raised when the turn is cancelled before completing. The turn's cancellation has already
    /// fired, so the observer receives an uncancelled token and can still record the outcome.
    /// </summary>
    ValueTask OnTurnInterruptedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Raised when the turn fails with an error other than cancellation.
    /// </summary>
    ValueTask OnTurnErrorAsync(Exception exception, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public interface IAgent
{
    Task<AgentRunResult> RunAsync(string prompt, CancellationToken cancellationToken = default);
}

public sealed class Agent : IAgent
{
    private const string BaseSystemPrompt = """
        You are WFX, a Windows-first AI coding agent operating inside a defined workspace.
        Use PowerShell and native Windows tooling rather than Bash conventions. Inspect before editing.
        Keep every file operation inside the workspace. Use tools for repository facts; do not invent them.
        Make the smallest coherent change, run relevant tests, and report evidence accurately.
        Never claim a command or test succeeded unless its tool result says it did.
        """;

    private readonly IModelProvider _modelProvider;
    private readonly IToolRegistry _tools;
    private readonly IApprovalService _approval;
    private readonly IContextProvider _context;
    private readonly IAgentObserver _observer;
    private readonly AgentOptions _options;
    private readonly string _workspaceRoot;
    private readonly IReadOnlyList<ModelMessage> _conversation;
    private readonly AgentTurnMetadata _turnMetadata;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<string>? _secrets;

    /// <param name="secrets">
    /// Explicit secret values (provider credentials, MCP header values, stored OAuth tokens)
    /// redacted from observed events and tool results at ingestion, in addition to the
    /// shape-based pass. The list may be live: it is enumerated at each redaction, so a
    /// secret added mid-turn is covered from then on.
    /// </param>
    public Agent(
        IModelProvider modelProvider,
        IToolRegistry tools,
        IApprovalService approval,
        IContextProvider context,
        IAgentObserver observer,
        AgentOptions options,
        string workspaceRoot,
        IReadOnlyList<ModelMessage>? conversation = null,
        AgentTurnMetadata? turnMetadata = null,
        TimeProvider? timeProvider = null,
        IReadOnlyList<string>? secrets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        _modelProvider = modelProvider;
        _tools = tools;
        _approval = approval;
        _context = context;
        _observer = observer;
        _options = options;
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _conversation = conversation?.ToArray() ?? [];
        _turnMetadata = turnMetadata ?? new AgentTurnMetadata(string.Empty, ApprovalMode.Always);
        _time = timeProvider ?? TimeProvider.System;
        _secrets = secrets;
    }

    public async Task<AgentRunResult> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        await _observer.OnEventAsync(
            new TurnStartedEvent(
                _turnMetadata.SessionId,
                _workspaceRoot,
                _options.Endpoint,
                _turnMetadata.ApprovalMode,
                _time.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
        var execution = new AgentExecutionState();
        try
        {
            return await RunTurnAsync(prompt, execution, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _observer.OnEventAsync(
                new TurnInterruptedEvent(
                    _turnMetadata.SessionId,
                    AgentInterruptionReason.Timeout,
                    _time.GetUtcNow()),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _observer.OnEventAsync(
                new TurnInterruptedEvent(
                    _turnMetadata.SessionId,
                    AgentInterruptionReason.Cancelled,
                    _time.GetUtcNow()),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await _observer.OnEventAsync(
                new TurnErrorEvent(
                    _turnMetadata.SessionId,
                    new AgentError(execution.ErrorKind, exception.Message),
                    _time.GetUtcNow())
                {
                    Exception = exception
                },
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<AgentRunResult> RunTurnAsync(
        string prompt,
        AgentExecutionState execution,
        CancellationToken cancellationToken)
    {
        var messages = new List<ModelMessage>(_conversation);
        var totalUsage = new UsageAccumulator();
        if (messages.Count == 0)
        {
            var supplementalContext = await _context.GetContextAsync(cancellationToken).ConfigureAwait(false);
            var systemPrompt = string.IsNullOrWhiteSpace(supplementalContext)
                ? BaseSystemPrompt
                : $"{BaseSystemPrompt}\n\nWorkspace context and project instructions:\n{supplementalContext}";
            var system = new ModelMessage(ModelRole.System, systemPrompt);
            messages.Add(system);
            await _observer.OnEventAsync(
                new MessageEvent(system, _time.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }

        var user = new ModelMessage(ModelRole.User, prompt);
        messages.Add(user);
        await _observer.OnEventAsync(
            new MessageEvent(user, _time.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        var assistantTexts = new List<string>();
        var iteration = 0;
        while (!_options.MaxIterations.HasValue || iteration < _options.MaxIterations.Value)
        {
            iteration++;
            execution.ErrorKind = AgentErrorKind.ProviderError;
            ModelCompleted? completed = null;
            await foreach (var modelEvent in _modelProvider.StreamAsync(
                new ModelRequest(_options.Model, messages, _tools.Definitions),
                cancellationToken).ConfigureAwait(false))
            {
                switch (modelEvent)
                {
                    case ModelTextDelta delta:
                        await _observer.OnModelTextAsync(delta.Text, cancellationToken).ConfigureAwait(false);
                        break;
                    case ModelCompleted response:
                        completed = response;
                        break;
                }
            }

            execution.ErrorKind = AgentErrorKind.ProtocolError;
            if (completed is null)
            {
                throw new InvalidOperationException("The model stream ended without a completed response.");
            }

            var assistant = completed.Message;
            if (assistant.Role != ModelRole.Assistant)
            {
                throw new InvalidOperationException("The model provider returned a non-assistant completion.");
            }

            messages.Add(assistant);
            // The observed event carries redacted tool-call arguments; the in-memory message
            // stays verbatim so execution and the replayed model view are unaffected.
            await _observer.OnEventAsync(
                new MessageEvent(RedactToolCallArguments(assistant), _time.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            if (completed.Usage is not null)
            {
                totalUsage.Add(completed.Usage);
                await _observer.OnEventAsync(
                    new UsageEvent(completed.Usage, _time.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(assistant.Content))
            {
                assistantTexts.Add(assistant.Content);
            }

            if (assistant.ToolCalls is not { Count: > 0 })
            {
                var result = new AgentRunResult(
                    assistant.Content ?? string.Empty,
                    iteration,
                    messages,
                    AgentRunStatus.Completed);
                await _observer.OnEventAsync(
                    new TurnCompletedEvent(
                        _turnMetadata.SessionId,
                        iteration,
                        result.FinalResponse,
                        totalUsage.ToModelUsage(),
                        _time.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
                return result;
            }

            execution.ErrorKind = AgentErrorKind.ToolError;
            foreach (var call in assistant.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteToolAsync(call, cancellationToken).ConfigureAwait(false);
                // Redact secrets exactly once at ingestion so observers, the model's view,
                // in-memory state, and any persisted transcript hold identical text.
                var toolMessage = new ModelMessage(
                    ModelRole.Tool,
                    result.ToProtocolJson(),
                    ToolCallId: call.Id,
                    Name: call.Name);
                messages.Add(toolMessage);
                await _observer.OnEventAsync(
                    new MessageEvent(toolMessage, _time.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var lastAssistant = messages.Last(static message => message.Role == ModelRole.Assistant);
        var interruptedResult = new AgentRunResult(
            lastAssistant.Content ?? string.Empty,
            iteration,
            messages,
            AgentRunStatus.IterationLimitReached,
            Note: $"Iteration limit of {iteration} model iteration(s) reached",
            AccumulatedText: string.Join("\n", assistantTexts));
        await _observer.OnEventAsync(
            new TurnInterruptedEvent(
                _turnMetadata.SessionId,
                AgentInterruptionReason.MaxIterations,
                _time.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
        return interruptedResult;
    }

    private async ValueTask<ToolResult> RejectAsync(
        ModelToolCall call,
        string reason,
        CancellationToken cancellationToken)
    {
        await _observer.OnEventAsync(
            new ToolRejectedEvent(
                call.Id,
                call.Name,
                SecretRedactor.Redact(call.ArgumentsJson, _secrets),
                reason,
                _time.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
        return ToolResult.Fail(reason);
    }

    private async ValueTask<ToolResult> ExecuteToolAsync(ModelToolCall call, CancellationToken cancellationToken)
    {
        if (!_tools.TryGet(call.Name, out var tool) || tool is null)
        {
            return await RejectAsync(call, $"Unknown tool '{call.Name}'.", cancellationToken).ConfigureAwait(false);
        }

        JsonDocument argumentsDocument;
        try
        {
            argumentsDocument = JsonDocument.Parse(call.ArgumentsJson);
        }
        catch (JsonException exception)
        {
            return await RejectAsync(call, $"Invalid JSON arguments: {exception.Message}", cancellationToken)
                .ConfigureAwait(false);
        }

        using (argumentsDocument)
        {
            ApprovalLevel level;
            try
            {
                level = tool.Classify(argumentsDocument.RootElement);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
            {
                return await RejectAsync(call, $"Invalid tool arguments: {exception.Message}", cancellationToken)
                    .ConfigureAwait(false);
            }

            var request = new ApprovalRequest(call.Name, call.ArgumentsJson, level, $"Run {call.Name}");
            if (!await _approval.ApproveAsync(request, cancellationToken).ConfigureAwait(false))
            {
                return await RejectAsync(
                    call,
                    $"Execution denied by approval policy ({level}).",
                    cancellationToken).ConfigureAwait(false);
            }

            await _observer.OnEventAsync(
                new ToolStartedEvent(
                    call.Id,
                    call.Name,
                    SecretRedactor.Redact(call.ArgumentsJson, _secrets),
                    level,
                    _time.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            ToolResult result;
            try
            {
                result = await tool.ExecuteAsync(
                    argumentsDocument.RootElement,
                    new ToolContext(_workspaceRoot),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                result = ToolResult.Fail($"{exception.GetType().Name}: {exception.Message}");
            }

            var duration = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            result = Redact(result);
            await _observer.OnEventAsync(
                new ToolCompletedEvent(call.Id, call.Name, result, duration, _time.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    private ModelMessage RedactToolCallArguments(ModelMessage message)
    {
        if (message.ToolCalls is not { Count: > 0 } calls)
        {
            return message;
        }

        return message with
        {
            ToolCalls = calls
                .Select(call => call with { ArgumentsJson = SecretRedactor.Redact(call.ArgumentsJson, _secrets) })
                .ToArray()
        };
    }

    private ToolResult Redact(ToolResult result)
    {
        IReadOnlyDictionary<string, string>? metadata = null;
        if (result.Metadata is not null)
        {
            metadata = result.Metadata.ToDictionary(
                static pair => pair.Key,
                pair => SecretRedactor.Redact(pair.Value, _secrets),
                StringComparer.Ordinal);
        }

        return new ToolResult(
            result.Success,
            SecretRedactor.Redact(result.Output, _secrets),
            result.Error is null ? null : SecretRedactor.Redact(result.Error, _secrets),
            metadata);
    }

    private sealed class AgentExecutionState
    {
        public AgentErrorKind ErrorKind { get; set; } = AgentErrorKind.ConfigError;
    }

    private sealed class UsageAccumulator
    {
        private long _inputTokens;
        private long _outputTokens;
        private bool _hasInputTokens;
        private bool _hasOutputTokens;

        public void Add(ModelUsage usage)
        {
            if (usage.InputTokens is { } inputTokens)
            {
                _inputTokens += inputTokens;
                _hasInputTokens = true;
            }

            if (usage.OutputTokens is { } outputTokens)
            {
                _outputTokens += outputTokens;
                _hasOutputTokens = true;
            }
        }

        public ModelUsage ToModelUsage() => new(
            _hasInputTokens ? _inputTokens : null,
            _hasOutputTokens ? _outputTokens : null);
    }
}
