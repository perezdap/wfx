using System.Runtime.CompilerServices;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class AgentLoopTests
{
    [Fact]
    public async Task RunsToolAndFeedsStructuredResultBackToModel()
    {
        using var workspace = new TemporaryDirectory();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("call-1", "echo", "{\"value\":\"hello\"}")]),
            new ModelMessage(ModelRole.Assistant, "finished")
        ]);
        var tool = new EchoTool();
        var agent = new Agent(
            model,
            new ToolRegistry([tool]),
            new PolicyApprovalService(ApprovalMode.Workspace, static (_, _) => ValueTask.FromResult(false)),
            new StaticContextProvider("test context"),
            new SilentObserver(),
            new AgentOptions("fake-model"),
            workspace.Path);

        var result = await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Equal("finished", result.FinalResponse);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(1, tool.ExecutionCount);
        var secondRequest = model.Requests[1];
        var toolMessage = Assert.Single(secondRequest.Messages, static message => message.Role == ModelRole.Tool);
        Assert.Contains("\"success\":true", toolMessage.Content!);
        Assert.Contains("echo:hello", toolMessage.Content!);
    }

    [Fact]
    public async Task DeniedToolReturnsStructuredFailureAndDoesNotExecute()
    {
        using var workspace = new TemporaryDirectory();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("call-1", "mutate", "{\"value\":\"hello\"}")]),
            new ModelMessage(ModelRole.Assistant, "stopped")
        ]);
        var tool = new MutatingTool();
        var agent = new Agent(
            model,
            new ToolRegistry([tool]),
            new PolicyApprovalService(ApprovalMode.Never, static (_, _) => ValueTask.FromResult(true)),
            new StaticContextProvider("test context"),
            new SilentObserver(),
            new AgentOptions("fake-model"),
            workspace.Path);

        var result = await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Equal("stopped", result.FinalResponse);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(0, tool.ExecutionCount);
        var toolMessage = Assert.Single(model.Requests[1].Messages, static message => message.Role == ModelRole.Tool);
        Assert.Contains("\"success\":false", toolMessage.Content!);
        Assert.Contains("denied", toolMessage.Content!, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SequenceModelProvider(IReadOnlyList<ModelMessage> responses) : IModelProvider
    {
        private int _index;

        public List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelCompleted(responses[_index++]);
        }
    }

    private sealed class EchoTool : ITool
    {
        public ToolDefinition Definition { get; } = new(
            "echo",
            "Echo a value.",
            new System.Text.Json.Nodes.JsonObject { ["type"] = "object" });

        public int ExecutionCount { get; private set; }

        public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.ReadOnly;

        public ValueTask<ToolResult> ExecuteAsync(
            JsonElement arguments,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return ValueTask.FromResult(ToolResult.Ok("echo:" + arguments.GetProperty("value").GetString()));
        }
    }

    private sealed class MutatingTool : ITool
    {
        public ToolDefinition Definition { get; } = new(
            "mutate",
            "Mutate a value.",
            new System.Text.Json.Nodes.JsonObject { ["type"] = "object" });

        public int ExecutionCount { get; private set; }

        public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.WorkspaceWrite;

        public ValueTask<ToolResult> ExecuteAsync(
            JsonElement arguments,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return ValueTask.FromResult(ToolResult.Ok("mutated"));
        }
    }

    private sealed class SilentObserver : IAgentObserver;
}
