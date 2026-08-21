using System.Text;

namespace Wfx.Core;

public sealed class AgentInstructionsContextProvider : IContextProvider
{
    private const int MaxInstructionBytes = 256 * 1024;
    private readonly string _workspaceRoot;
    private readonly string _workingDirectory;

    public AgentInstructionsContextProvider(string workspaceRoot, string workingDirectory)
    {
        var policy = new WorkspacePathPolicy(workspaceRoot);
        _workspaceRoot = policy.Root;
        _workingDirectory = policy.Resolve(workingDirectory, mustExist: true);
    }

    public async ValueTask<string?> GetContextAsync(CancellationToken cancellationToken = default)
    {
        var paths = DiscoverPaths();
        if (paths.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        var totalBytes = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length > MaxInstructionBytes || totalBytes + info.Length > MaxInstructionBytes)
            {
                throw new InvalidOperationException("AGENTS.md instructions exceed the 256 KiB safety limit.");
            }

            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            totalBytes += Encoding.UTF8.GetByteCount(content);
            var relative = Path.GetRelativePath(_workspaceRoot, path);
            builder.AppendLine($"--- {relative} ---");
            builder.AppendLine(content.Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public IReadOnlyList<string> DiscoverPaths()
    {
        var result = new List<string>();
        var current = new DirectoryInfo(_workspaceRoot);
        var relative = Path.GetRelativePath(_workspaceRoot, _workingDirectory);

        AddIfPresent(current.FullName, result);
        if (relative == ".")
        {
            return result;
        }

        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = new DirectoryInfo(Path.Combine(current.FullName, segment));
            AddIfPresent(current.FullName, result);
        }

        return result;
    }

    private static void AddIfPresent(string directory, ICollection<string> result)
    {
        var candidate = Path.Combine(directory, "AGENTS.md");
        if (File.Exists(candidate))
        {
            result.Add(candidate);
        }
    }
}
