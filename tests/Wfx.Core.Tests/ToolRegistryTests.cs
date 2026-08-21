using System.Text.Json;
using System.Text.Json.Nodes;
using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class ToolRegistryTests
{
    [Fact]
    public void RejectsDuplicateNames()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new StubTool(), new StubTool()]));
    }

    [Fact]
    public void ExposesToolSchema()
    {
        var registry = new ToolRegistry([new StubTool()]);
        var definition = Assert.Single(registry.Definitions);

        Assert.Equal("stub", definition.Name);
        Assert.Equal("object", definition.Parameters["type"]!.GetValue<string>());
    }

    private sealed class StubTool : ITool
    {
        public ToolDefinition Definition { get; } = new("stub", "Stub.", new JsonObject { ["type"] = "object" });

        public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.ReadOnly;

        public ValueTask<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ToolResult.Ok("ok"));
    }
}
