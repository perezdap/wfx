using System.Diagnostics;
using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class WorkspacePathPolicyTests
{
    [Fact]
    public void Resolve_AllowsPathsInsideWorkspace()
    {
        using var temporary = new TemporaryDirectory();
        var policy = new WorkspacePathPolicy(temporary.Path);

        var result = policy.Resolve(Path.Combine("src", "file.cs"));

        Assert.Equal(Path.Combine(temporary.Path, "src", "file.cs"), result);
    }

    [Fact]
    public void Resolve_RejectsParentTraversal()
    {
        using var temporary = new TemporaryDirectory();
        var policy = new WorkspacePathPolicy(temporary.Path);

        Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve(Path.Combine("..", "outside.txt")));
    }

    [Fact]
    public void Resolve_RejectsSiblingWithSharedPrefix()
    {
        using var temporary = new TemporaryDirectory();
        var sibling = temporary.Path + "-other";
        Directory.CreateDirectory(sibling);
        try
        {
            var policy = new WorkspacePathPolicy(temporary.Path);
            Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve(sibling));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void Resolve_RejectsDirectoryLinkOutsideWorkspace()
    {
        using var workspace = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var link = Path.Combine(workspace.Path, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside.Path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"Unable to create a directory symbolic link: {exception.Message}");
        }

        var policy = new WorkspacePathPolicy(workspace.Path);
        Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve(Path.Combine(link, "file.txt")));
    }

    [Fact]
    public void Resolve_MustExist_ThrowsForJunctionWithMissingTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("NTFS junctions are Windows-only.");
        }

        using var workspace = new TemporaryDirectory();
        var link = Path.Combine(workspace.Path, "jlink");
        var missingTarget = Path.Combine(workspace.Path, "missing-target");
        if (!TryCreateJunction(link, missingTarget))
        {
            Assert.Skip("Unable to create an NTFS junction.");
        }

        var policy = new WorkspacePathPolicy(workspace.Path);

        // Directory.Exists(link) is true for the junction reparse point even though its
        // target is missing, so the existence check must run against the resolved target.
        Assert.Throws<FileNotFoundException>(() => policy.Resolve("jlink", mustExist: true));
    }

    [Fact]
    public void Resolve_MustExist_SucceedsThroughJunctionToExistingFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("NTFS junctions are Windows-only.");
        }

        using var workspace = new TemporaryDirectory();
        var realDirectory = Path.Combine(workspace.Path, "real");
        Directory.CreateDirectory(realDirectory);
        File.WriteAllText(Path.Combine(realDirectory, "file.txt"), "ok");
        var link = Path.Combine(workspace.Path, "jlink");
        if (!TryCreateJunction(link, realDirectory))
        {
            Assert.Skip("Unable to create an NTFS junction.");
        }

        var policy = new WorkspacePathPolicy(workspace.Path);

        var result = policy.Resolve(Path.Combine("jlink", "file.txt"), mustExist: true);

        Assert.Equal(Path.Combine(workspace.Path, "jlink", "file.txt"), result);
    }

    [Fact]
    public void Resolve_MustExist_ThrowsForDirectorySymlinkWithMissingTarget()
    {
        using var workspace = new TemporaryDirectory();
        var link = Path.Combine(workspace.Path, "broken-link");
        var missingTarget = Path.Combine(workspace.Path, "missing-target");
        try
        {
            // A directory symlink (like a junction) is a reparse point that carries the
            // directory attribute, so Directory.Exists(link) is true even though its target
            // is missing. A dangling *file* symlink registers as non-existent and never
            // reaches ResolveLinkTarget, so it would not exercise the resolved-path check.
            Directory.CreateSymbolicLink(link, missingTarget);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"Unable to create a directory symbolic link: {exception.Message}");
        }

        var policy = new WorkspacePathPolicy(workspace.Path);

        Assert.Throws<FileNotFoundException>(() => policy.Resolve("broken-link", mustExist: true));
    }

    [Fact]
    public void Resolve_MustExist_SucceedsThroughDirectorySymlinkToExistingFile()
    {
        using var workspace = new TemporaryDirectory();
        var realDirectory = Path.Combine(workspace.Path, "real");
        Directory.CreateDirectory(realDirectory);
        File.WriteAllText(Path.Combine(realDirectory, "file.txt"), "ok");
        var link = Path.Combine(workspace.Path, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, realDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"Unable to create a directory symbolic link: {exception.Message}");
        }

        var policy = new WorkspacePathPolicy(workspace.Path);

        var result = policy.Resolve(Path.Combine("linked", "file.txt"), mustExist: true);

        Assert.Equal(Path.Combine(workspace.Path, "linked", "file.txt"), result);
    }

    [Fact]
    public void Resolve_RejectsWindowsDeviceAndDriveRelativePaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows device and drive-relative paths.");
        }

        using var temporary = new TemporaryDirectory();
        var policy = new WorkspacePathPolicy(temporary.Path);

        Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve(@"\\?\C:\Windows\win.ini"));
        Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve(@"\\.\C:\Windows\win.ini"));
        Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve(@"C:file.txt"));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
}
