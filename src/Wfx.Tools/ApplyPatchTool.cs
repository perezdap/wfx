using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wfx.Core;

namespace Wfx.Tools;

public sealed class ApplyPatchTool : WorkspaceTool, ITool
{
    private static readonly Regex HunkHeader = new(
        "^@@ -(\\d+)(?:,\\d+)? \\+(\\d+)(?:,\\d+)? @@",
        RegexOptions.CultureInvariant);

    public ApplyPatchTool(WorkspacePathPolicy paths) : base(paths)
    {
        Definition = new ToolDefinition(
            "apply_patch",
            "Apply a unified diff to one UTF-8 workspace file. Each hunk must match the current file exactly.",
            ToolJson.ObjectSchema([
                ("path", ToolJson.StringSchema("File to patch."), true),
                ("patch", ToolJson.StringSchema("Unified diff containing @@ hunks."), true)
            ]));
    }

    public ToolDefinition Definition { get; }

    public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.WorkspaceWrite;

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

        var patch = ToolJson.RequiredString(arguments, "patch").Replace("\r\n", "\n", StringComparison.Ordinal);
        var originalText = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var newline = originalText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hadTrailingNewline = originalText.EndsWith('\n');
        var original = originalText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (hadTrailingNewline && original.Count > 0)
        {
            original.RemoveAt(original.Count - 1);
        }

        IReadOnlyList<string> updated;
        try
        {
            updated = Apply(original, patch);
        }
        catch (InvalidOperationException exception)
        {
            return ToolResult.Fail(exception.Message);
        }

        var output = string.Join(newline, updated) + (hadTrailingNewline ? newline : string.Empty);
        await File.WriteAllTextAsync(path, output, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok($"Applied patch to {Relative(Paths.Root, path)}.");
    }

    internal static IReadOnlyList<string> Apply(IReadOnlyList<string> source, string patch)
    {
        var patchLines = patch.Split('\n');
        var result = new List<string>();
        var sourceIndex = 0;
        var patchIndex = 0;
        var foundHunk = false;

        while (patchIndex < patchLines.Length)
        {
            var header = HunkHeader.Match(patchLines[patchIndex]);
            if (!header.Success)
            {
                patchIndex++;
                continue;
            }

            foundHunk = true;
            var oldStart = int.Parse(header.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var targetIndex = Math.Max(0, oldStart - 1);
            if (targetIndex < sourceIndex || targetIndex > source.Count)
            {
                throw new InvalidOperationException("Patch hunks overlap or reference a line outside the file.");
            }

            while (sourceIndex < targetIndex)
            {
                result.Add(source[sourceIndex++]);
            }

            patchIndex++;
            while (patchIndex < patchLines.Length && !HunkHeader.IsMatch(patchLines[patchIndex]))
            {
                var line = patchLines[patchIndex];
                if (line.StartsWith("\\ No newline", StringComparison.Ordinal))
                {
                    patchIndex++;
                    continue;
                }

                if (line.Length == 0 && patchIndex == patchLines.Length - 1)
                {
                    break;
                }

                if (line.Length == 0 || line[0] is not (' ' or '+' or '-'))
                {
                    break;
                }

                var content = line[1..];
                switch (line[0])
                {
                    case ' ':
                        RequireMatch(source, sourceIndex, content);
                        result.Add(content);
                        sourceIndex++;
                        break;
                    case '-':
                        RequireMatch(source, sourceIndex, content);
                        sourceIndex++;
                        break;
                    case '+':
                        result.Add(content);
                        break;
                }

                patchIndex++;
            }
        }

        if (!foundHunk)
        {
            throw new InvalidOperationException("Patch contains no valid unified-diff hunks.");
        }

        while (sourceIndex < source.Count)
        {
            result.Add(source[sourceIndex++]);
        }

        return result;
    }

    private static void RequireMatch(IReadOnlyList<string> source, int index, string expected)
    {
        if (index >= source.Count || !source[index].Equals(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Patch context did not match at source line {index + 1}.");
        }
    }
}
