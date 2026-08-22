using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Providers;

/// <summary>
/// Speaks the OpenAI Responses protocol: the agent's provider-agnostic
/// conversation state is mapped to Responses input items, and the typed
/// server-sent events are mapped back to <see cref="ModelStreamEvent"/>.
/// </summary>
public sealed class OpenAiResponsesProvider : IModelProvider
{
    private readonly ProviderSseTransport _transport;

    public OpenAiResponsesProvider(HttpClient httpClient, OpenAiProviderOptions options)
    {
        _transport = new ProviderSseTransport(httpClient, options);
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var httpRequest = _transport.CreateRequest("/responses", BuildBody(request));
        var accumulator = new ResponseAccumulator(_transport.Redact);
        await foreach (var data in _transport.ReadDataEventsAsync(httpRequest, cancellationToken).ConfigureAwait(false))
        {
            string? delta;
            try
            {
                delta = accumulator.ApplyEvent(data);
            }
            catch (JsonException exception)
            {
                throw new ProviderProtocolException("The provider returned malformed streaming JSON.", exception);
            }

            if (!string.IsNullOrEmpty(delta))
            {
                yield return new ModelTextDelta(delta);
            }
        }

        if (!accumulator.Completed)
        {
            throw new ProviderProtocolException("The provider stream ended without a completion event.");
        }

        yield return new ModelCompleted(accumulator.BuildMessage(), accumulator.Usage);
    }

    private static byte[] BuildBody(ModelRequest request)
    {
        var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.Model);
            writer.WriteBoolean("stream", true);
            // wfx replays the whole conversation on every turn, so server-side
            // response state would only duplicate it.
            writer.WriteBoolean("store", false);
            writer.WriteStartArray("input");
            foreach (var message in request.Messages)
            {
                WriteInputItems(writer, message);
            }

            writer.WriteEndArray();
            if (request.Tools.Count > 0)
            {
                writer.WriteStartArray("tools");
                foreach (var tool in request.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    tool.Parameters.WriteTo(writer);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteInputItems(Utf8JsonWriter writer, ModelMessage message)
    {
        switch (message.Role)
        {
            case ModelRole.Tool:
                if (message.ToolCallId is null)
                {
                    throw new ArgumentException("A tool result requires the identifier of the call it answers.", nameof(message));
                }

                writer.WriteStartObject();
                writer.WriteString("type", "function_call_output");
                writer.WriteString("call_id", message.ToolCallId);
                writer.WriteString("output", message.Content ?? string.Empty);
                writer.WriteEndObject();
                return;
            case ModelRole.Assistant:
                if (!string.IsNullOrEmpty(message.Content))
                {
                    WriteMessageItem(writer, "assistant", "output_text", message.Content);
                }

                if (message.ToolCalls is { Count: > 0 })
                {
                    foreach (var call in message.ToolCalls)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "function_call");
                        writer.WriteString("call_id", call.Id);
                        writer.WriteString("name", call.Name);
                        writer.WriteString("arguments", call.ArgumentsJson);
                        writer.WriteEndObject();
                    }
                }

                return;
            case ModelRole.System:
                WriteMessageItem(writer, "system", "input_text", message.Content ?? string.Empty);
                return;
            case ModelRole.User:
                WriteMessageItem(writer, "user", "input_text", message.Content ?? string.Empty);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(message));
        }
    }

    private static void WriteMessageItem(Utf8JsonWriter writer, string role, string contentType, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "message");
        writer.WriteString("role", role);
        writer.WriteStartArray("content");
        writer.WriteStartObject();
        writer.WriteString("type", contentType);
        writer.WriteString("text", text);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private sealed class ResponseAccumulator(Func<string, string> redact)
    {
        private readonly StringBuilder _content = new();
        private readonly SortedDictionary<int, ToolCallBuilder> _toolCalls = new();
        private readonly Dictionary<string, int> _itemIndexes = new(StringComparer.Ordinal);

        public bool Completed { get; private set; }

        public ModelUsage? Usage { get; private set; }

        public string? ApplyEvent(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("Streaming event is missing a type.");
            }

            switch (typeElement.GetString())
            {
                case "response.output_text.delta":
                {
                    var text = RequireString(root, "delta");
                    _content.Append(text);
                    return text;
                }

                case "response.output_item.added":
                    ApplyOutputItem(root, authoritativeArguments: false);
                    return null;

                case "response.output_item.done":
                    ApplyOutputItem(root, authoritativeArguments: true);
                    return null;

                case "response.function_call_arguments.delta":
                    ResolveBuilder(root).Arguments.Append(RequireString(root, "delta"));
                    return null;

                case "response.function_call_arguments.done":
                    SetArguments(ResolveBuilder(root), OptionalString(root, "arguments"));
                    return null;

                case "response.completed":
                case "response.incomplete":
                    ReadUsage(root);
                    Completed = true;
                    return null;

                case "response.failed":
                    throw new ProviderProtocolException(
                        $"The provider reported a failed response: {redact(FailureMessage(root))}");

                case "error":
                    throw new ProviderProtocolException(
                        $"The provider reported a stream error: {redact(FailureMessage(root))}");

                default:
                    return null;
            }
        }

        public ModelMessage BuildMessage()
        {
            var calls = new List<ModelToolCall>();
            foreach (var pair in _toolCalls)
            {
                var call = pair.Value;
                if (string.IsNullOrWhiteSpace(call.CallId) || string.IsNullOrWhiteSpace(call.Name))
                {
                    throw new ProviderProtocolException("The provider returned an incomplete tool call.");
                }

                var arguments = call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString();
                try
                {
                    using var _ = JsonDocument.Parse(arguments);
                }
                catch (JsonException exception)
                {
                    throw new ProviderProtocolException("The provider returned malformed tool-call arguments.", exception);
                }

                calls.Add(new ModelToolCall(call.CallId, call.Name, arguments));
            }

            return new ModelMessage(
                ModelRole.Assistant,
                _content.Length == 0 ? null : _content.ToString(),
                calls.Count == 0 ? null : calls);
        }

        private void ApplyOutputItem(JsonElement root, bool authoritativeArguments)
        {
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Output-item event is missing an item object.");
            }

            if (OptionalString(item, "type") != "function_call")
            {
                return;
            }

            var index = RequireOutputIndex(root);
            var builder = Builder(index);
            builder.CallId ??= OptionalString(item, "call_id");
            builder.Name ??= OptionalString(item, "name");
            if (OptionalString(item, "id") is { } itemId)
            {
                _itemIndexes[itemId] = index;
            }

            if (authoritativeArguments)
            {
                SetArguments(builder, OptionalString(item, "arguments"));
            }
        }

        private ToolCallBuilder ResolveBuilder(JsonElement root)
        {
            if (root.TryGetProperty("output_index", out var outputIndex) && outputIndex.TryGetInt32(out var index))
            {
                return Builder(index);
            }

            if (OptionalString(root, "item_id") is { } itemId && _itemIndexes.TryGetValue(itemId, out var mapped))
            {
                return Builder(mapped);
            }

            throw new JsonException("Function-call event is missing a resolvable output index.");
        }

        private ToolCallBuilder Builder(int index)
        {
            if (!_toolCalls.TryGetValue(index, out var builder))
            {
                builder = new ToolCallBuilder();
                _toolCalls.Add(index, builder);
            }

            return builder;
        }

        private static void SetArguments(ToolCallBuilder builder, string? arguments)
        {
            if (string.IsNullOrEmpty(arguments))
            {
                return;
            }

            builder.Arguments.Clear();
            builder.Arguments.Append(arguments);
        }

        private static int RequireOutputIndex(JsonElement root)
        {
            if (!root.TryGetProperty("output_index", out var outputIndex) || !outputIndex.TryGetInt32(out var index))
            {
                throw new JsonException("Output-item event is missing an integer output index.");
            }

            return index;
        }

        private static string FailureMessage(JsonElement root)
        {
            if (OptionalString(root, "message") is { } message)
            {
                return message;
            }

            if (root.TryGetProperty("error", out var directError) && directError.ValueKind == JsonValueKind.Object &&
                OptionalString(directError, "message") is { } directMessage)
            {
                return directMessage;
            }

            if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object &&
                response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                OptionalString(error, "message") is { } nested)
            {
                return nested;
            }

            return "no message was provided.";
        }

        private void ReadUsage(JsonElement root)
        {
            if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            long? input = usage.TryGetProperty("input_tokens", out var inputTokens) && inputTokens.TryGetInt64(out var inputValue)
                ? inputValue
                : null;
            long? output = usage.TryGetProperty("output_tokens", out var outputTokens) && outputTokens.TryGetInt64(out var outputValue)
                ? outputValue
                : null;
            Usage = new ModelUsage(input, output);
        }

        private static string RequireString(JsonElement root, string name) =>
            OptionalString(root, name) ?? throw new JsonException($"Streaming event is missing the '{name}' string.");

        private static string? OptionalString(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private sealed class ToolCallBuilder
        {
            public string? CallId { get; set; }

            public string? Name { get; set; }

            public StringBuilder Arguments { get; } = new();
        }
    }
}
