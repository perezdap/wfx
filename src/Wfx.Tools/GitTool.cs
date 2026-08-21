using System.Text.Json;
using System.Text.Json.Nodes;
using Wfx.Core;
using Wfx.PowerShell;

namespace Wfx.Tools;

public sealed class GitTool : WorkspaceTool, ITool
{
    private static readonly HashSet<string> AllowedOperations = new(StringComparer.Ordinal)
    {
        "status", "diff", "diff_staged", "log"
    };

    private readonly IProcessExecutor _processExecutor;

    public GitTool(WorkspacePathPolicy paths, IProcessExecutor processExecutor) : base(paths)
    {
        _processExecutor = processExecutor;
        Definition = new ToolDefinition(
            "git",
            "Run a bounded read-only Git operation: status, diff, diff_staged, or log.",
            ToolJson.ObjectSchema([
                ("operation", new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("status", "diff", "diff_staged", "log")
                }, true),
                ("max_count", ToolJson.IntegerSchema("Maximum log entries for log.", 1, 100), false)
            ]));
    }

    public ToolDefinition Definition { get; }

    public ApprovalLevel Classify(JsonElement arguments)
    {
        ValidateOperation(arguments);
        return ApprovalLevel.ReadOnly;
    }

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var operation = ValidateOperation(arguments);
        IReadOnlyList<string> operationArguments = operation switch
        {
            "status" => ["status", "--short", "--branch"],
            "diff" => ["diff", "--no-ext-diff"],
            "diff_staged" => ["diff", "--staged", "--no-ext-diff"],
            "log" => ["log", $"--max-count={ToolJson.Integer(arguments, "max_count", 10, 1, 100)}", "--oneline", "--decorate"],
            _ => throw new InvalidOperationException("Unsupported Git operation.")
        };

        var executable = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var result = await _processExecutor.ExecuteAsync(
            new ProcessCommand(executable, BoundGitArguments(operationArguments), Paths.Root, Timeout: TimeSpan.FromSeconds(30)),
            cancellationToken).ConfigureAwait(false);

        var output = result.StandardOutput;
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            output += $"\n[stderr]\n{result.StandardError}";
        }

        return result.ExitCode == 0
            ? ToolResult.Ok(output.TrimEnd())
            : ToolResult.Fail($"git {operation} failed with exit code {result.ExitCode}.", output.TrimEnd());
    }

    internal static IReadOnlyList<string> BoundGitArguments(IReadOnlyList<string> operationArguments)
    {
        var hooksPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var arguments = new List<string>
        {
            "--no-pager",
            "-c",
            $"core.hooksPath={hooksPath}",
            "-c",
            "core.fsmonitor="
        };
        arguments.AddRange(operationArguments);
        return arguments;
    }

    private static string ValidateOperation(JsonElement arguments)
    {
        var operation = ToolJson.RequiredString(arguments, "operation");
        if (!AllowedOperations.Contains(operation))
        {
            throw new JsonException($"Unsupported Git operation '{operation}'.");
        }

        return operation;
    }
}
