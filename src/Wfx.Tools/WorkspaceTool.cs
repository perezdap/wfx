using Wfx.Core;

namespace Wfx.Tools;

public abstract class WorkspaceTool
{
    protected WorkspaceTool(WorkspacePathPolicy paths) => Paths = paths;

    protected WorkspacePathPolicy Paths { get; }

    protected static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    protected IEnumerable<string> EnumerateFilesSafely(string startDirectory, bool recursive)
    {
        var pending = new Queue<string>();
        pending.Enqueue(startDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                yield return Paths.Resolve(file, mustExist: true);
            }

            if (!recursive)
            {
                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var info = new DirectoryInfo(child);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || IsIgnoredDirectory(info.Name))
                {
                    continue;
                }

                pending.Enqueue(Paths.Resolve(child, mustExist: true));
            }
        }
    }

    protected static bool IsIgnoredDirectory(string name) =>
        name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("obj", StringComparison.OrdinalIgnoreCase);
}
