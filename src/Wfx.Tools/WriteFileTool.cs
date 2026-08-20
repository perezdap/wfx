using System.Text;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Tools;

public sealed class WriteFileTool : WorkspaceTool, ITool
{
    public WriteFileTool(WorkspacePathPolicy paths) : base(paths)
    {
        Definition = new ToolDefinition(
            "write_file",
            "Create or replace a UTF-8 text file inside the workspace.",
            ToolJson.ObjectSchema([
                ("path", ToolJson.StringSchema("Workspace-relative or absolute file path."), true),
                ("content", ToolJson.StringSchema("Complete replacement file content."), true),
                ("create_directories", ToolJson.BooleanSchema("Create missing parent directories."), false)
            ]));
    }

    public ToolDefinition Definition { get; }

    public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.WorkspaceWrite;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var path = Paths.Resolve(ToolJson.RequiredString(arguments, "path"));
        var content = ToolJson.RequiredStringAllowEmpty(arguments, "content");
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            if (!ToolJson.Boolean(arguments, "create_directories", false))
            {
                return ToolResult.Fail("The parent directory does not exist.");
            }

            Directory.CreateDirectory(directory);
            Paths.Resolve(directory, mustExist: true);
        }

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
        return ToolResult.Ok($"Wrote {content.Length} characters to {Relative(Paths.Root, path)}.");
    }
}
