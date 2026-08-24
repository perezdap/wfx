using System.Net;
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
    private readonly SseHttpChannel _channel;

    public OpenAiResponsesProvider(HttpClient httpClient, OpenAiProviderOptions options)
        : this(httpClient, options, null)
    {
    }

    internal OpenAiResponsesProvider(HttpClient httpClient, OpenAiProviderOptions options, Func<TimeSpan, CancellationToken, Task>? delayAsync)
    {
        _channel = new SseHttpChannel(httpClient, options, delayAsync);
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var accumulator = new ResponseAccumulator();
        var body = BuildBody(request);
        var canDowngrade = request.Messages.Any(static message => message.ProviderItemsJson is not null);
        await foreach (var data in _channel.ReadDataEventsAsync(
                           () => _channel.CreateRequest("/responses", body),
                           cancellationToken,
                           (statusCode, error) =>
                           {
                               if (!canDowngrade || !IsInvalidEncryptedContent(statusCode, error))
                               {
                                   return false;
                               }

                               body = BuildBody(request with { Messages = ProviderItemDowngrade.Strip(request.Messages) });
                               return true;
                           }).ConfigureAwait(false))
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

            if (accumulator.Failure is { } failure)
            {
                throw new ProviderProtocolException(_channel.Redact(failure));
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

    private static bool IsInvalidEncryptedContent(HttpStatusCode statusCode, string responseBody)
    {
        if (statusCode is not HttpStatusCode.BadRequest)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind is JsonValueKind.Object &&
                   error.TryGetProperty("code", out var code) &&
                   code.ValueKind is JsonValueKind.String &&
                   code.GetString() == "invalid_encrypted_content";
        }
        catch (JsonException)
        {
            return false;
        }
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
            // response state would only duplicate it. Reasoning content is
            // returned inline instead, which a stateless client must replay.
            writer.WriteBoolean("store", false);
            writer.WriteStartArray("include");
            writer.WriteStringValue("reasoning.encrypted_content");
            writer.WriteEndArray();
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
                // A turn the endpoint described itself is replayed as it came,
                // so reasoning items survive and nothing is written twice.
                if (message.ProviderItemsJson is { } items && TryWriteVerbatim(writer, items))
                {
                    return;
                }

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

    /// <summary>
    /// Writes provider-native items back verbatim. Returns false when the stored
    /// value is not a usable item array, so the caller rebuilds the turn instead.
    /// </summary>
    private static bool TryWriteVerbatim(Utf8JsonWriter writer, string itemsJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(itemsJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return false;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                item.WriteTo(writer);
            }
        }

        return true;
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

    private sealed class ResponseAccumulator
    {
        private readonly StringBuilder _content = new();
        private readonly ToolCallAccumulator _toolCalls = new();
        private readonly SortedDictionary<int, string> _rawItems = new();

        public bool Completed { get; private set; }

        public ModelUsage? Usage { get; private set; }

        /// <summary>
        /// The provider-reported reason this turn cannot be delivered, if any.
        /// The caller redacts it and throws, keeping secret handling in one place.
        /// </summary>
        public string? Failure { get; private set; }

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
                    ApplyOutputItem(root, recordVerbatim: false);
                    return null;

                case "response.output_item.done":
                    ApplyOutputItem(root, recordVerbatim: true);
                    return null;

                case "response.function_call_arguments.delta":
                    _toolCalls.GetOrAdd(RequireOutputIndex(root)).AppendArguments(RequireString(root, "delta"));
                    return null;

                case "response.function_call_arguments.done":
                    _toolCalls.GetOrAdd(RequireOutputIndex(root)).SetArguments(OptionalString(root, "arguments"));
                    return null;

                case "response.completed":
                    ReadUsage(root);
                    Completed = true;
                    return null;

                case "response.incomplete":
                    ReadUsage(root);
                    Failure = $"The provider returned an incomplete response: {IncompleteReason(root)}";
                    return null;

                case "response.failed":
                    Failure = $"The provider reported a failed response: {FailureMessage(root)}";
                    return null;

                case "error":
                    Failure = $"The provider reported a stream error: {FailureMessage(root)}";
                    return null;

                default:
                    return null;
            }
        }

        public ModelMessage BuildMessage() => new(
            ModelRole.Assistant,
            _content.Length == 0 ? null : _content.ToString(),
            _toolCalls.Build(),
            ProviderItemsJson: _rawItems.Count == 0 ? null : $"[{string.Join(',', _rawItems.Values)}]");

        private void ApplyOutputItem(JsonElement root, bool recordVerbatim)
        {
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Output-item event is missing an item object.");
            }

            if (recordVerbatim)
            {
                // Reasoning items carry state this transport cannot rebuild from
                // text and tool calls, so the finished turn is kept as sent.
                _rawItems[RequireOutputIndex(root)] = item.GetRawText();
            }

            if (OptionalString(item, "type") != "function_call")
            {
                return;
            }

            var builder = _toolCalls.GetOrAdd(RequireOutputIndex(root));
            builder.Identify(OptionalString(item, "call_id"), OptionalString(item, "name"));
            builder.SetArguments(OptionalString(item, "arguments"));
        }

        private static int RequireOutputIndex(JsonElement root)
        {
            if (!root.TryGetProperty("output_index", out var outputIndex) || !outputIndex.TryGetInt32(out var index))
            {
                throw new JsonException("Function-call event is missing an integer output index.");
            }

            return index;
        }

        private static string IncompleteReason(JsonElement root)
        {
            if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object &&
                response.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object &&
                OptionalString(details, "reason") is { } reason)
            {
                return reason;
            }

            return "no reason was provided.";
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
    }
}
