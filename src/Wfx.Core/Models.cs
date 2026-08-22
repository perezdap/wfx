using System.Text.Json.Nodes;

namespace Wfx.Core;

public enum ModelRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed record ModelToolCall(string Id, string Name, string ArgumentsJson);

/// <param name="ProviderItemsJson">
/// The turn as the endpoint itself expressed it: a JSON array of provider-native
/// items, opaque to the agent loop, which stores and replays it unchanged. A
/// transport sets this when the wire format carries state it cannot reconstruct
/// from <see cref="Content"/> and <see cref="ToolCalls"/> — OpenAI Responses
/// reasoning items, for instance. Transports that need nothing extra leave it null.
/// </param>
public sealed record ModelMessage(
    ModelRole Role,
    string? Content,
    IReadOnlyList<ModelToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null,
    string? ProviderItemsJson = null);

public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters);

public sealed record ModelRequest(
    string Model,
    IReadOnlyList<ModelMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools);

public abstract record ModelStreamEvent;

public sealed record ModelTextDelta(string Text) : ModelStreamEvent;

public sealed record ModelCompleted(ModelMessage Message, ModelUsage? Usage = null) : ModelStreamEvent;

public sealed record ModelUsage(long? InputTokens, long? OutputTokens);

public interface IModelProvider
{
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}
