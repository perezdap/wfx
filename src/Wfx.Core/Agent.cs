using System.Text.Json;

namespace Wfx.Core;

public sealed record AgentOptions
{
    public AgentOptions(string model, int maxIterations = 24)
    {
        Model = model;
        MaxIterations = Math.Clamp(maxIterations, 1, 100);
    }

    public string Model { get; }

    public int MaxIterations { get; }
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

    public Agent(
        IModelProvider modelProvider,
        IToolRegistry tools,
        IApprovalService approval,
        IContextProvider context,
        IAgentObserver observer,
        AgentOptions options,
        string workspaceRoot,
        IReadOnlyList<ModelMessage>? conversation = null)
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
    }

    public async Task<AgentRunResult> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var messages = new List<ModelMessage>(_conversation);
        if (messages.Count == 0)
        {
            var supplementalContext = await _context.GetContextAsync(cancellationToken).ConfigureAwait(false);
            var systemPrompt = string.IsNullOrWhiteSpace(supplementalContext)
                ? BaseSystemPrompt
                : $"{BaseSystemPrompt}\n\nWorkspace context and project instructions:\n{supplementalContext}";
            messages.Add(new ModelMessage(ModelRole.System, systemPrompt));
        }

        messages.Add(new ModelMessage(ModelRole.User, prompt));

        var assistantTexts = new List<string>();
        for (var iteration = 1; iteration <= _options.MaxIterations; iteration++)
        {
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
            if (!string.IsNullOrEmpty(assistant.Content))
            {
                assistantTexts.Add(assistant.Content);
            }

            if (assistant.ToolCalls is not { Count: > 0 })
            {
                return new AgentRunResult(assistant.Content ?? string.Empty, iteration, messages, AgentRunStatus.Completed);
            }

            foreach (var call in assistant.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteToolAsync(call, cancellationToken).ConfigureAwait(false);
                messages.Add(new ModelMessage(
                    ModelRole.Tool,
                    result.ToProtocolJson(),
                    ToolCallId: call.Id,
                    Name: call.Name));
            }
        }

        var lastAssistant = messages.Last(static message => message.Role == ModelRole.Assistant);
        return new AgentRunResult(
            lastAssistant.Content ?? string.Empty,
            _options.MaxIterations,
            messages,
            AgentRunStatus.IterationLimitReached,
            Note: $"Iteration limit of {_options.MaxIterations} model iteration(s) reached.",
            AccumulatedText: string.Join("\n", assistantTexts));
    }

    private async ValueTask<ToolResult> RejectAsync(
        ModelToolCall call,
        string reason,
        CancellationToken cancellationToken)
    {
        await _observer.OnToolRejectedAsync(call.Name, call.ArgumentsJson, reason, cancellationToken)
            .ConfigureAwait(false);
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

            await _observer.OnToolStartedAsync(call.Name, call.ArgumentsJson, level, cancellationToken)
                .ConfigureAwait(false);
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
            await _observer.OnToolCompletedAsync(call.Name, result, duration, cancellationToken).ConfigureAwait(false);
            return result;
        }
    }
}
