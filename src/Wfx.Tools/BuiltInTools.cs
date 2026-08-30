using Wfx.Core;
using Wfx.PowerShell;

namespace Wfx.Tools;

public static class BuiltInTools
{
    public static IReadOnlyList<ITool> CreateTools(string workspaceRoot, ISkillLocator? skills = null)
    {
        var paths = new WorkspacePathPolicy(workspaceRoot);
        var processExecutor = new ProcessExecutor();
        var powerShellRunner = new PowerShellRunner(processExecutor);
        var tools = new List<ITool>
        {
            new ReadFileTool(paths),
            new WriteFileTool(paths),
            new ApplyPatchTool(paths),
            new ListDirectoryTool(paths),
            new SearchFilesTool(paths),
            new SearchTextTool(paths),
            new PowerShellTool(paths, powerShellRunner),
            new GitTool(paths, processExecutor)
        };

        var skillLocator = skills ?? SkillLocator.Empty;
        if (skillLocator.Skills.Count > 0)
        {
            tools.Add(new SkillTool(skillLocator));
        }

        return tools;
    }

    public static ToolRegistry Create(string workspaceRoot, ISkillLocator? skills = null) =>
        new(CreateTools(workspaceRoot, skills));
}
