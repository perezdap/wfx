using Wfx.Core;
using Wfx.PowerShell;

namespace Wfx.Tools;

public static class BuiltInTools
{
    public static IReadOnlyList<ITool> CreateTools(string workspaceRoot)
    {
        var paths = new WorkspacePathPolicy(workspaceRoot);
        var processExecutor = new ProcessExecutor();
        var powerShellRunner = new PowerShellRunner(processExecutor);
        ITool[] tools =
        [
            new ReadFileTool(paths),
            new WriteFileTool(paths),
            new ApplyPatchTool(paths),
            new ListDirectoryTool(paths),
            new SearchFilesTool(paths),
            new SearchTextTool(paths),
            new PowerShellTool(paths, powerShellRunner),
            new GitTool(paths, processExecutor)
        ];
        return tools;
    }

    public static ToolRegistry Create(string workspaceRoot) => new(CreateTools(workspaceRoot));
}
