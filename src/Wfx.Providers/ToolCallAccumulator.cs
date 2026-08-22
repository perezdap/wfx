using System.Text;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Providers;

/// <summary>
/// Collects tool calls that arrive in fragments, keyed by the position the
/// endpoint gives them, and validates them once the stream ends. Shared by the
/// Chat Completions and Responses transports, which differ only in how they
/// name the fragments.
/// </summary>
internal sealed class ToolCallAccumulator
{
    private readonly SortedDictionary<int, ToolCallBuilder> _builders = new();

    public bool IsEmpty => _builders.Count == 0;

    public ToolCallBuilder At(int index)
    {
        if (!_builders.TryGetValue(index, out var builder))
        {
            builder = new ToolCallBuilder();
            _builders.Add(index, builder);
        }

        return builder;
    }

    public IReadOnlyList<ModelToolCall>? Build()
    {
        if (_builders.Count == 0)
        {
            return null;
        }

        var calls = new List<ModelToolCall>(_builders.Count);
        foreach (var builder in _builders.Values)
        {
            if (string.IsNullOrWhiteSpace(builder.Id) || string.IsNullOrWhiteSpace(builder.Name))
            {
                throw new ProviderProtocolException("The provider returned an incomplete tool call.");
            }

            var arguments = builder.Arguments.Length == 0 ? "{}" : builder.Arguments.ToString();
            try
            {
                using var _ = JsonDocument.Parse(arguments);
            }
            catch (JsonException exception)
            {
                throw new ProviderProtocolException("The provider returned malformed tool-call arguments.", exception);
            }

            calls.Add(new ModelToolCall(builder.Id, builder.Name, arguments));
        }

        return calls;
    }

    internal sealed class ToolCallBuilder
    {
        private bool _argumentsAreWhole;

        public string? Id { get; private set; }

        public string? Name { get; private set; }

        internal StringBuilder Arguments { get; } = new();

        /// <summary>Records identity from the first fragment that carries it.</summary>
        public void Identify(string? id, string? name)
        {
            Id ??= id;
            Name ??= name;
        }

        /// <summary>Appends one argument fragment, replacing any whole value seen so far.</summary>
        public void AppendArguments(string? fragment)
        {
            if (_argumentsAreWhole)
            {
                Arguments.Clear();
                _argumentsAreWhole = false;
            }

            Arguments.Append(fragment);
        }

        /// <summary>Replaces the arguments with a whole value the endpoint reported.</summary>
        public void SetArguments(string? arguments)
        {
            if (string.IsNullOrEmpty(arguments))
            {
                return;
            }

            Arguments.Clear();
            Arguments.Append(arguments);
            _argumentsAreWhole = true;
        }
    }
}
