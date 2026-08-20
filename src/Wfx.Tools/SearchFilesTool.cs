using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Tools;

public sealed class SearchFilesTool : WorkspaceTool, ITool
{
    public SearchFilesTool(WorkspacePathPolicy paths) : base(paths)
    {
        Definition = new ToolDefinition(
            "search_files",
            "Find workspace files by a simple wildcard pattern such as *.cs or *Tests*.",
            ToolJson.ObjectSchema([
                ("pattern", ToolJson.StringSchema("Simple wildcard pattern."), true),
                ("path", ToolJson.StringSchema("Directory to search."), true),
                ("max_results", ToolJson.IntegerSchema("Maximum matching paths.", 1, 5_000), false)
            ]));
    }

    public ToolDefinition Definition { get; }

    public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.ReadOnly;

    public ValueTask<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var pattern = ToolJson.RequiredString(arguments, "pattern");
        var directory = Paths.Resolve(ToolJson.RequiredString(arguments, "path"), mustExist: true);
        var maxResults = ToolJson.Integer(arguments, "max_results", 500, 1, 5_000);
        var matches = new StringBuilder();
        var count = 0;
        var ignoreCase = OperatingSystem.IsWindows();

        foreach (var file in EnumerateFilesSafely(directory, recursive: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Relative(Paths.Root, file);
            if (!FileSystemName.MatchesSimpleExpression(pattern, relative, ignoreCase) &&
                !FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(file), ignoreCase))
            {
                continue;
            }

            matches.AppendLine(relative);
            if (++count >= maxResults)
            {
                break;
            }
        }

        return ValueTask.FromResult(ToolResult.Ok(matches.ToString().TrimEnd(), new Dictionary<string, string>
        {
            ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["truncated"] = (count >= maxResults).ToString()
        }));
    }
}
