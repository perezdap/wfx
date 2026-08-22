namespace Wfx.Core;

public sealed record WorkspaceInfo(string Root, string WorkingDirectory, bool IsGitRepository)
{
    public static WorkspaceInfo Discover(string? startDirectory = null)
    {
        var workingDirectory = Path.GetFullPath(startDirectory ?? Environment.CurrentDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {workingDirectory}");
        }

        var current = new DirectoryInfo(workingDirectory);
        while (current is not null)
        {
            var gitPath = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return new WorkspaceInfo(current.FullName, workingDirectory, true);
            }

            current = current.Parent;
        }

        return new WorkspaceInfo(workingDirectory, workingDirectory, false);
    }
}

public sealed class WorkspacePathPolicy
{
    private readonly string _root;
    private readonly string _resolvedRoot;
    private readonly StringComparison _comparison;

    public WorkspacePathPolicy(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _root = TrimEndingSeparator(Path.GetFullPath(root));
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"Workspace root does not exist: {_root}");
        }

        _resolvedRoot = TrimEndingSeparator(ResolveLinks(_root));
    }

    public string Root => _root;

    public string Resolve(string path, bool mustExist = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Paths cannot contain null characters.", nameof(path));
        }

        if (OperatingSystem.IsWindows() &&
            (path.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
             path.StartsWith("\\\\.\\", StringComparison.Ordinal)))
        {
            throw new UnauthorizedAccessException("Windows device paths are not allowed.");
        }

        if (OperatingSystem.IsWindows() && Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
        {
            throw new UnauthorizedAccessException("Drive-relative paths are not allowed.");
        }

        var fullPath = Path.GetFullPath(path, _root);
        EnsureInside(fullPath, _root, "Path escapes the workspace root.");

        var resolvedPath = ResolveLinks(fullPath);
        EnsureInside(resolvedPath, _resolvedRoot, "Path resolves outside the workspace through a link or junction.");

        if (mustExist && !File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"Workspace path does not exist: {path}", resolvedPath);
        }

        return fullPath;
    }

    private void EnsureInside(string candidate, string root, string message)
    {
        var normalizedCandidate = TrimEndingSeparator(Path.GetFullPath(candidate));
        if (normalizedCandidate.Equals(root, _comparison))
        {
            return;
        }

        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(rootWithSeparator, _comparison))
        {
            throw new UnauthorizedAccessException(message);
        }
    }

    private static string ResolveLinks(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);
        var pathRoot = Path.GetPathRoot(normalized)
            ?? throw new ArgumentException("Path has no root.", nameof(fullPath));
        var current = pathRoot;
        var relative = normalized[pathRoot.Length..];

        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;

            if (info is null)
            {
                continue;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                current = target.FullName;
            }
        }

        return Path.GetFullPath(current);
    }

    private static string TrimEndingSeparator(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is not null && path.Equals(root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            return path;
        }

        return Path.TrimEndingDirectorySeparator(path);
    }
}
