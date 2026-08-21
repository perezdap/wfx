using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Tools;

public sealed class SearchTextTool : WorkspaceTool, ITool
{
    public SearchTextTool(WorkspacePathPolicy paths) : base(paths)
    {
        Definition = new ToolDefinition(
            "search_text",
            "Search UTF-8 workspace files for literal text and return path, line number, and matching line.",
            ToolJson.ObjectSchema([
                ("query", ToolJson.StringSchema("Literal text to find."), true),
                ("path", ToolJson.StringSchema("Directory or file to search."), true),
                ("glob", ToolJson.StringSchema("Optional simple file wildcard; defaults to *."), false),
                ("case_sensitive", ToolJson.BooleanSchema("Use case-sensitive matching."), false),
                ("max_results", ToolJson.IntegerSchema("Maximum matching lines.", 1, 5_000), false)
            ]));
    }

    public ToolDefinition Definition { get; }

    public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.ReadOnly;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var query = ToolJson.RequiredString(arguments, "query");
        var target = Paths.Resolve(ToolJson.RequiredString(arguments, "path"), mustExist: true);
        var glob = ToolJson.String(arguments, "glob", "*");
        var comparison = ToolJson.Boolean(arguments, "case_sensitive", false)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var maxResults = ToolJson.Integer(arguments, "max_results", 200, 1, 5_000);
        IEnumerable<string> files = File.Exists(target)
            ? new[] { target }
            : EnumerateFilesSafely(target, recursive: true);
        var builder = new StringBuilder();
        var count = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileSystemName.MatchesSimpleExpression(glob, Path.GetFileName(file), OperatingSystem.IsWindows()))
            {
                continue;
            }

            if (new FileInfo(file).Length > 5 * 1024 * 1024 || await LooksBinaryAsync(file, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            using var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
            var lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                if (!line.Contains(query, comparison))
                {
                    continue;
                }

                builder.Append(Relative(Paths.Root, file));
                builder.Append(':');
                builder.Append(lineNumber);
                builder.Append(':');
                builder.AppendLine(line.Length > 500 ? line[..500] + "…" : line);
                if (++count >= maxResults)
                {
                    break;
                }
            }

            if (count >= maxResults)
            {
                break;
            }
        }

        return ToolResult.Ok(builder.ToString().TrimEnd(), new Dictionary<string, string>
        {
            ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["truncated"] = (count >= maxResults).ToString()
        });
    }

    private static async Task<bool> LooksBinaryAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return buffer.AsSpan(0, read).Contains((byte)0);
    }
}
