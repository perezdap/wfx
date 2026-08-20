using System.Text.Json;
using Wfx.Core;

namespace Wfx.Tools;

public sealed class ReadFileTool : WorkspaceTool, ITool
{
    public ReadFileTool(WorkspacePathPolicy paths) : base(paths)
    {
        Definition = new ToolDefinition(
            "read_file",
            "Read a UTF-8 text file inside the workspace.",
            ToolJson.ObjectSchema([
                ("path", ToolJson.StringSchema("Workspace-relative or absolute file path."), true),
                ("max_chars", ToolJson.IntegerSchema("Maximum characters to return.", 1, 1_000_000), false)
            ]));
    }

    public ToolDefinition Definition { get; }

    public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.ReadOnly;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var path = Paths.Resolve(ToolJson.RequiredString(arguments, "path"), mustExist: true);
        if (!File.Exists(path))
        {
            return ToolResult.Fail("The requested path is not a file.");
        }

        var maxChars = ToolJson.Integer(arguments, "max_chars", 200_000, 1, 1_000_000);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxChars + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        var truncated = count > maxChars;
        var output = new string(buffer, 0, Math.Min(count, maxChars));
        return ToolResult.Ok(output, new Dictionary<string, string>
        {
            ["path"] = Relative(Paths.Root, path),
            ["truncated"] = truncated.ToString()
        });
    }
}
