using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wfx.Core;

public enum ApprovalLevel
{
    ReadOnly = 0,
    WorkspaceWrite = 1,
    SystemChange = 2,
    Dangerous = 3
}

public sealed record ToolContext(string WorkspaceRoot);

public sealed record ToolResult(
    bool Success,
    string Output,
    string? Error = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public static ToolResult Ok(string output, IReadOnlyDictionary<string, string>? metadata = null) =>
        new(true, output, null, metadata);

    public static ToolResult Fail(string error, string output = "") =>
        new(false, output, error);

    public string ToProtocolJson()
    {
        var root = new JsonObject
        {
            ["success"] = Success,
            ["output"] = Output
        };

        if (Error is not null)
        {
            root["error"] = Error;
        }

        if (Metadata is not null)
        {
            var metadata = new JsonObject();
            foreach (var pair in Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }

            root["metadata"] = metadata;
        }

        return root.ToJsonString();
    }
}

public interface ITool
{
    ToolDefinition Definition { get; }

    ApprovalLevel Classify(JsonElement arguments);

    ValueTask<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default);
}

public interface IToolRegistry
{
    IReadOnlyList<ToolDefinition> Definitions { get; }

    bool TryGet(string name, out ITool? tool);
}

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (!_tools.TryAdd(tool.Definition.Name, tool))
            {
                throw new ArgumentException($"A tool named '{tool.Definition.Name}' is already registered.", nameof(tools));
            }
        }

        Definitions = _tools.Values.Select(static tool => tool.Definition).ToArray();
    }

    public IReadOnlyList<ToolDefinition> Definitions { get; }

    public bool TryGet(string name, out ITool? tool) => _tools.TryGetValue(name, out tool);
}
