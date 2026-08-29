using System.Text.Json;
using Wfx.Core;

namespace Wfx.Tools;

public sealed class SkillTool : ITool
{
    private readonly ISkillLocator _skills;

    public SkillTool(ISkillLocator skills)
    {
        _skills = skills;
        Definition = new ToolDefinition(
            "skill",
            "Load the full SKILL.md instructions for a named skill.",
            ToolJson.ObjectSchema([
                ("name", ToolJson.StringSchema("Name of the skill to load."), true)
            ]));
    }

    public ToolDefinition Definition { get; }

    public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.ReadOnly;

    public ValueTask<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var name = ToolJson.RequiredString(arguments, "name");
        if (!_skills.Skills.TryGetValue(name, out var skill) || skill is null)
        {
            return ValueTask.FromResult(ToolResult.Fail($"No skill named '{name}' is available."));
        }

        return ValueTask.FromResult(ToolResult.Ok(skill.Body, new Dictionary<string, string>
        {
            ["name"] = skill.Name
        }));
    }
}
