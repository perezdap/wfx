using Wfx.Tools;

namespace Wfx.Tools.Tests;

public sealed class BuiltInToolsTests
{
    [Fact]
    public void RegistersExpectedMilestoneOneTools()
    {
        using var workspace = new TemporaryDirectory();
        var registry = BuiltInTools.Create(workspace.Path);

        Assert.Equal(
            ["apply_patch", "git", "list_directory", "powershell", "read_file", "search_files", "search_text", "write_file"],
            registry.Definitions.Select(static definition => definition.Name).Order().ToArray());
        Assert.All(registry.Definitions, static definition =>
            Assert.Equal("object", definition.Parameters["type"]!.GetValue<string>()));
    }
}
