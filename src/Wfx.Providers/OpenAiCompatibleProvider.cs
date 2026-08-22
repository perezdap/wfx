using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Providers;

public sealed class OpenAiCompatibleProvider : IModelProvider
{
    private readonly ProviderSseTransport _transport;
    private readonly OpenAiProviderOptions _options;

    public OpenAiCompatibleProvider(HttpClient httpClient, OpenAiProviderOptions options)
    {
        _options = options;
        _transport = new ProviderSseTransport(httpClient, options);
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var httpRequest = _transport.CreateRequest("/chat/completions", BuildBody(request));
        var accumulator = new ResponseAccumulator();
        await foreach (var data in _transport.ReadDataEventsAsync(httpRequest, cancellationToken).ConfigureAwait(false))
        {
            string? delta;
            try
            {
                delta = accumulator.ApplyChunk(data);
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

        yield return new ModelCompleted(accumulator.BuildMessage(), accumulator.Usage);
    }

    private byte[] BuildBody(ModelRequest request)
    {
        var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.Model);
            writer.WriteBoolean("stream", true);
            if (_options.IncludeStreamOptions)
            {
                writer.WriteStartObject("stream_options");
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }

            writer.WriteStartArray("messages");
            foreach (var message in request.Messages)
            {
                WriteMessage(writer, message);
            }

            writer.WriteEndArray();
            if (request.Tools.Count > 0)
            {
                writer.WriteStartArray("tools");
                foreach (var tool in request.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    tool.Parameters.WriteTo(writer);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteMessage(Utf8JsonWriter writer, ModelMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", message.Role switch
        {
            ModelRole.System => "system",
            ModelRole.User => "user",
            ModelRole.Assistant => "assistant",
            ModelRole.Tool => "tool",
            _ => throw new ArgumentOutOfRangeException(nameof(message))
        });

        if (message.Content is null)
        {
            writer.WriteNull("content");
        }
        else
        {
            writer.WriteString("content", message.Content);
        }

        if (message.ToolCallId is not null)
        {
            writer.WriteString("tool_call_id", message.ToolCallId);
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            writer.WriteStartArray("tool_calls");
            foreach (var call in message.ToolCalls)
            {
                writer.WriteStartObject();
                writer.WriteString("id", call.Id);
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", call.Name);
                writer.WriteString("arguments", call.ArgumentsJson);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private sealed class ResponseAccumulator
    {
        private readonly StringBuilder _content = new();
        private readonly SortedDictionary<int, ToolCallBuilder> _toolCalls = new();

        public ModelUsage? Usage { get; private set; }

        public string? ApplyChunk(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            ReadUsage(root);
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Streaming response is missing choices.");
            }

            if (choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? text = null;
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString();
                _content.Append(text);
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    if (!toolCall.TryGetProperty("index", out var indexElement) || !indexElement.TryGetInt32(out var index))
                    {
                        throw new JsonException("Tool-call delta is missing an integer index.");
                    }

                    if (!_toolCalls.TryGetValue(index, out var builder))
                    {
                        builder = new ToolCallBuilder();
                        _toolCalls.Add(index, builder);
                    }

                    if (toolCall.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        builder.Id ??= id.GetString();
                    }

                    if (toolCall.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
                    {
                        if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        {
                            builder.Name ??= name.GetString();
                        }

                        if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String)
                        {
                            builder.Arguments.Append(arguments.GetString());
                        }
                    }
                }
            }

            return text;
        }

        public ModelMessage BuildMessage()
        {
            var calls = new List<ModelToolCall>();
            foreach (var pair in _toolCalls)
            {
                var call = pair.Value;
                if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name))
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

                calls.Add(new ModelToolCall(call.Id, call.Name, arguments));
            }

            return new ModelMessage(
                ModelRole.Assistant,
                _content.Length == 0 ? null : _content.ToString(),
                calls.Count == 0 ? null : calls);
        }

        private void ReadUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            long? input = usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt64(out var promptValue)
                ? promptValue
                : null;
            long? output = usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt64(out var completionValue)
                ? completionValue
                : null;
            Usage = new ModelUsage(input, output);
        }

        private sealed class ToolCallBuilder
        {
            public string? Id { get; set; }

            public string? Name { get; set; }

            public StringBuilder Arguments { get; } = new();
        }
    }
}
