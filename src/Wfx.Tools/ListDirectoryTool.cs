using System.Text;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Tools;

public sealed class ListDirectoryTool : WorkspaceTool, ITool
{
    public ListDirectoryTool(WorkspacePathPolicy paths) : base(paths)
    {
        Definition = new ToolDefinition(
            "list_directory",
            "List files and directories in the workspace. Recursive traversal skips .git, bin, obj, and links.",
            ToolJson.ObjectSchema([
                ("path", ToolJson.StringSchema("Directory path; defaults conceptually to the workspace root."), true),
                ("recursive", ToolJson.BooleanSchema("Recursively list child directories."), false),
                ("max_entries", ToolJson.IntegerSchema("Maximum entries to return.", 1, 10_000), false)
            ]));
    }

    public ToolDefinition Definition { get; }

    public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.ReadOnly;

    public ValueTask<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var directory = Paths.Resolve(ToolJson.RequiredString(arguments, "path"), mustExist: true);
        if (!Directory.Exists(directory))
        {
            return ValueTask.FromResult(ToolResult.Fail("The requested path is not a directory."));
        }

        var recursive = ToolJson.Boolean(arguments, "recursive", false);
        var maxEntries = ToolJson.Integer(arguments, "max_entries", 500, 1, 10_000);
        var builder = new StringBuilder();
        var count = 0;
        var pending = new Queue<string>();
        pending.Enqueue(directory);

        while (pending.Count > 0 && count < maxEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.OrdinalIgnoreCase))
            {
                var isDirectory = Directory.Exists(entry);
                builder.Append(Relative(Paths.Root, entry));
                if (isDirectory)
                {
                    builder.Append('/');
                }

                builder.AppendLine();
                count++;
                if (count >= maxEntries)
                {
                    break;
                }

                if (recursive && isDirectory)
                {
                    var info = new DirectoryInfo(entry);
                    if ((info.Attributes & FileAttributes.ReparsePoint) == 0 && !IsIgnoredDirectory(info.Name))
                    {
                        pending.Enqueue(Paths.Resolve(entry, mustExist: true));
                    }
                }
            }
        }

        return ValueTask.FromResult(ToolResult.Ok(builder.ToString().TrimEnd(), new Dictionary<string, string>
        {
            ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["truncated"] = (count >= maxEntries).ToString()
        }));
    }
}
