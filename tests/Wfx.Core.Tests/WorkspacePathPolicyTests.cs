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
    public void Resolve_RejectsDirectoryLinkOutsideWorkspace_WhenLinksAreAvailable()
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
            return;
        }

        var policy = new WorkspacePathPolicy(workspace.Path);
        Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve(Path.Combine(link, "file.txt")));
    }
}
