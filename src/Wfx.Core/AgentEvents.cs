using System.Text.Json;

namespace Wfx.Core;

public abstract record AgentEvent;

/// <summary>
/// The canonical event names shared by the stream, the transcript writer, and the transcript
/// reader, so the vocabulary cannot drift between them.
/// </summary>
public static class AgentEventNames
{
    public const string TurnStarted = "turn_started";
    public const string Message = "message";
    public const string ToolStarted = "tool_started";
    public const string ToolCompleted = "tool_completed";
    public const string ToolRejected = "tool_rejected";
    public const string Usage = "usage";
    public const string TurnCompleted = "turn_completed";
    public const string TurnInterrupted = "turn_interrupted";
    public const string TurnError = "turn_error";
    public const string Interrupted = "interrupted";
    public const string Header = "header";
    public const string WorkspaceRebound = "workspace_rebound";
}

public sealed record TurnStartedEvent(
    string SessionId,
    string Workspace,
    EndpointIdentity Endpoint,
    ApprovalMode ApprovalMode,
    DateTimeOffset StartedAt) : AgentEvent;

public sealed record MessageEvent(ModelMessage Message, DateTimeOffset At) : AgentEvent;

public sealed record ToolStartedEvent(
    string CallId,
    string Name,
    string ArgumentsJson,
    ApprovalLevel ApprovalLevel,
    DateTimeOffset At) : AgentEvent;

public sealed record ToolCompletedEvent(
    string CallId,
    string Name,
    ToolResult Result,
    TimeSpan Duration,
    DateTimeOffset At) : AgentEvent;

public sealed record ToolRejectedEvent(
    string CallId,
    string Name,
    string ArgumentsJson,
    string Reason,
    DateTimeOffset At) : AgentEvent;

public sealed record UsageEvent(ModelUsage Usage, DateTimeOffset At) : AgentEvent;

public sealed record TurnCompletedEvent(
    string SessionId,
    int Iterations,
    string FinalMessage,
    ModelUsage TotalUsage,
    DateTimeOffset EndedAt) : AgentEvent;

public enum AgentInterruptionReason
{
    Cancelled,
    Timeout,
    MaxIterations
}

public sealed record TurnInterruptedEvent(
    string SessionId,
    AgentInterruptionReason Reason,
    DateTimeOffset At) : AgentEvent;

public enum AgentErrorKind
{
    ProviderError,
    ToolError,
    ProtocolError,
    ConfigError
}

public sealed record AgentError(AgentErrorKind Kind, string Message);

public sealed record TurnErrorEvent(
    string SessionId,
    AgentError Error,
    DateTimeOffset At) : AgentEvent
{
    internal Exception? Exception { get; init; }
}

public sealed record AgentTurnMetadata(string SessionId, ApprovalMode ApprovalMode);

public static class AgentEventJson
{
    public const int SchemaVersion = 1;

    public static void Write(Utf8JsonWriter writer, AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(agentEvent);

        writer.WriteStartObject();
        switch (agentEvent)
        {
            case TurnStartedEvent started:
                WriteTurnStarted(writer, started);
                break;
            case MessageEvent message:
                WriteMessage(writer, message);
                break;
            case ToolStartedEvent toolStarted:
                WriteToolStarted(writer, toolStarted);
                break;
            case ToolCompletedEvent toolCompleted:
                WriteToolCompleted(writer, toolCompleted);
                break;
            case ToolRejectedEvent toolRejected:
                WriteToolRejected(writer, toolRejected);
                break;
            case UsageEvent usage:
                WriteUsage(writer, usage);
                break;
            case TurnCompletedEvent completed:
                WriteTurnCompleted(writer, completed);
                break;
            case TurnInterruptedEvent interrupted:
                WriteTurnInterrupted(writer, interrupted);
                break;
            case TurnErrorEvent error:
                WriteTurnError(writer, error);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(agentEvent), agentEvent, "Unknown agent event.");
        }

        writer.WriteEndObject();
    }

    private static void WriteTurnStarted(Utf8JsonWriter writer, TurnStartedEvent started)
    {
        writer.WriteString("event", AgentEventNames.TurnStarted);
        writer.WriteNumber("schema_version", SchemaVersion);
        writer.WriteString("session_id", started.SessionId);
        writer.WriteString("workspace", started.Workspace);
        writer.WriteStartObject("endpoint");
        WriteOptionalString(writer, "profile", started.Endpoint.Profile);
        writer.WriteString("provider", started.Endpoint.Provider);
        writer.WriteString("protocol", started.Endpoint.Protocol);
        writer.WriteString("model", started.Endpoint.Model);
        writer.WriteEndObject();
        writer.WriteString("approval_mode", WfxConfiguration.FormatApprovalMode(started.ApprovalMode));
        writer.WriteString("started_at", started.StartedAt.UtcDateTime);
    }

    private static void WriteMessage(Utf8JsonWriter writer, MessageEvent messageEvent)
    {
        var message = messageEvent.Message;
        writer.WriteString("event", AgentEventNames.Message);
        writer.WriteString("role", SessionMessageRoles.Name(message.Role));
        WriteOptionalString(writer, "content", message.Content);

        if (message.ToolCalls is { Count: > 0 })
        {
            writer.WriteStartArray("tool_calls");
            foreach (var call in message.ToolCalls)
            {
                writer.WriteStartObject();
                writer.WriteString("id", call.Id);
                writer.WriteString("name", call.Name);
                writer.WriteString("arguments", call.ArgumentsJson);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (message.ToolCallId is not null)
        {
            writer.WriteString("tool_call_id", message.ToolCallId);
        }

        if (message.Name is not null)
        {
            writer.WriteString("name", message.Name);
        }

        writer.WriteString("at", messageEvent.At.UtcDateTime);
        if (!string.IsNullOrEmpty(message.ProviderItemsJson))
        {
            writer.WritePropertyName("provider_items");
            writer.WriteRawValue(message.ProviderItemsJson);
        }
    }

    private static void WriteToolStarted(Utf8JsonWriter writer, ToolStartedEvent started)
    {
        writer.WriteString("event", AgentEventNames.ToolStarted);
        writer.WriteString("call_id", started.CallId);
        writer.WriteString("name", started.Name);
        writer.WriteString("arguments_json", started.ArgumentsJson);
        writer.WriteString("approval_level", ApprovalLevelName(started.ApprovalLevel));
        writer.WriteString("at", started.At.UtcDateTime);
    }

    private static void WriteToolCompleted(Utf8JsonWriter writer, ToolCompletedEvent completed)
    {
        writer.WriteString("event", AgentEventNames.ToolCompleted);
        writer.WriteString("call_id", completed.CallId);
        writer.WriteString("name", completed.Name);
        writer.WriteNumber("duration_ms", Math.Max(0, completed.Duration.TotalMilliseconds));
        writer.WriteString("outcome", completed.Result.Success ? "completed" : "failed");
        writer.WriteStartObject("result");
        writer.WriteString(
            "content",
            completed.Result.Success || string.IsNullOrEmpty(completed.Result.Error)
                ? completed.Result.Output
                : completed.Result.Error);
        writer.WriteBoolean("is_error", !completed.Result.Success);
        writer.WriteEndObject();
        writer.WriteString("at", completed.At.UtcDateTime);
    }

    private static void WriteToolRejected(Utf8JsonWriter writer, ToolRejectedEvent rejected)
    {
        writer.WriteString("event", AgentEventNames.ToolRejected);
        writer.WriteString("call_id", rejected.CallId);
        writer.WriteString("name", rejected.Name);
        writer.WriteString("arguments_json", rejected.ArgumentsJson);
        writer.WriteString("reason", rejected.Reason);
        writer.WriteString("at", rejected.At.UtcDateTime);
    }

    private static void WriteUsage(Utf8JsonWriter writer, UsageEvent usageEvent)
    {
        writer.WriteString("event", AgentEventNames.Usage);
        WriteUsageProperties(writer, usageEvent.Usage);
        writer.WriteString("at", usageEvent.At.UtcDateTime);
    }

    private static void WriteTurnCompleted(Utf8JsonWriter writer, TurnCompletedEvent completed)
    {
        writer.WriteString("event", AgentEventNames.TurnCompleted);
        writer.WriteString("session_id", completed.SessionId);
        writer.WriteNumber("iterations", completed.Iterations);
        writer.WriteString("final_message", completed.FinalMessage);
        writer.WriteStartObject("total_usage");
        WriteUsageProperties(writer, completed.TotalUsage);
        writer.WriteEndObject();
        writer.WriteString("ended_at", completed.EndedAt.UtcDateTime);
    }

    private static void WriteTurnInterrupted(Utf8JsonWriter writer, TurnInterruptedEvent interrupted)
    {
        writer.WriteString("event", AgentEventNames.TurnInterrupted);
        writer.WriteString("session_id", interrupted.SessionId);
        writer.WriteString("reason", InterruptionReasonName(interrupted.Reason));
        writer.WriteString("at", interrupted.At.UtcDateTime);
    }

    private static void WriteTurnError(Utf8JsonWriter writer, TurnErrorEvent error)
    {
        writer.WriteString("event", AgentEventNames.TurnError);
        writer.WriteString("session_id", error.SessionId);
        writer.WriteStartObject("error");
        writer.WriteString("kind", ErrorKindName(error.Error.Kind));
        writer.WriteString("message", error.Error.Message);
        writer.WriteEndObject();
        writer.WriteString("at", error.At.UtcDateTime);
    }

    private static void WriteUsageProperties(Utf8JsonWriter writer, ModelUsage usage)
    {
        WriteOptionalInt64(writer, "input_tokens", usage.InputTokens);
        WriteOptionalInt64(writer, "output_tokens", usage.OutputTokens);
        WriteOptionalInt64(writer, "total_tokens", TotalTokens(usage));
    }

    private static long? TotalTokens(ModelUsage usage) =>
        usage.InputTokens is null && usage.OutputTokens is null
            ? null
            : usage.InputTokens.GetValueOrDefault() + usage.OutputTokens.GetValueOrDefault();

    private static string ApprovalLevelName(ApprovalLevel level) => level switch
    {
        ApprovalLevel.ReadOnly => "read_only",
        ApprovalLevel.WorkspaceWrite => "workspace_write",
        ApprovalLevel.SystemChange => "system_change",
        ApprovalLevel.Dangerous => "dangerous",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown approval level.")
    };

    private static string InterruptionReasonName(AgentInterruptionReason reason) => reason switch
    {
        AgentInterruptionReason.Cancelled => "cancelled",
        AgentInterruptionReason.Timeout => "timeout",
        AgentInterruptionReason.MaxIterations => "max_iterations",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown interruption reason.")
    };

    private static string ErrorKindName(AgentErrorKind kind) => kind switch
    {
        AgentErrorKind.ProviderError => "provider_error",
        AgentErrorKind.ToolError => "tool_error",
        AgentErrorKind.ProtocolError => "protocol_error",
        AgentErrorKind.ConfigError => "config_error",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown agent error kind.")
    };

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteOptionalInt64(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, value.Value);
        }
    }
}
