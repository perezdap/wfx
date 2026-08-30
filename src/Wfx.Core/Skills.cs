using System.Text;

namespace Wfx.Core;

public enum SkillSource
{
    User,
    Workspace
}

public sealed record SkillInfo(
    string Name,
    string Description,
    string Body,
    SkillSource Source,
    string Path);

public interface ISkillLocator
{
    IReadOnlyDictionary<string, SkillInfo> Skills { get; }

    IReadOnlyList<string> Warnings { get; }
}

public sealed class SkillLocator : ISkillLocator
{
    public IReadOnlyDictionary<string, SkillInfo> Skills { get; }

    public IReadOnlyList<string> Warnings { get; }

    private SkillLocator(IReadOnlyDictionary<string, SkillInfo> skills, IReadOnlyList<string> warnings)
    {
        Skills = skills;
        Warnings = warnings;
    }

    public static ISkillLocator Empty { get; } =
        new SkillLocator(new Dictionary<string, SkillInfo>(StringComparer.Ordinal), []);

    public static SkillLocator Discover(string? userProfile, string? workspaceRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var userSkills = DiscoverDirectory(userProfile, SkillSource.User, warnings, cancellationToken);
        var workspaceSkills = DiscoverDirectory(workspaceRoot, SkillSource.Workspace, warnings, cancellationToken);

        var merged = new Dictionary<string, SkillInfo>(StringComparer.Ordinal);

        foreach (var skill in userSkills)
        {
            if (!merged.TryAdd(skill.Name, skill))
            {
                warnings.Add($"User skill directory '{skill.Name}' has the same name as another user skill; skipping duplicate.");
            }
        }

        foreach (var skill in workspaceSkills)
        {
            // Workspace wins over user skills and over earlier workspace skills with the same name.
            merged[skill.Name] = skill;
        }

        return new SkillLocator(merged, warnings);
    }

    private static IReadOnlyList<SkillInfo> DiscoverDirectory(string? root, SkillSource source, List<string> warnings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return [];
        }

        var skillsDir = Path.Combine(root, ".wfx", "skills");
        if (!Directory.Exists(skillsDir))
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        var pathPolicy = source == SkillSource.Workspace ? new WorkspacePathPolicy(root) : null;
        var skills = new List<SkillInfo>();
        foreach (var skillDir in Directory.EnumerateDirectories(skillsDir).OrderBy(static d => d, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryName = Path.GetFileName(skillDir);
            var skillPath = pathPolicy is null
                ? Path.Combine(skillDir, "SKILL.md")
                : ResolveSkillPath(pathPolicy, directoryName, warnings);
            if (skillPath is null)
            {
                continue;
            }

            var skill = TryLoadSkill(skillPath, directoryName, source, warnings, cancellationToken);
            if (skill is not null)
            {
                skills.Add(skill);
            }
        }

        return skills;
    }

    private static string? ResolveSkillPath(WorkspacePathPolicy policy, string directoryName, List<string> warnings)
    {
        try
        {
            return policy.Resolve(Path.Combine(".wfx", "skills", directoryName, "SKILL.md"), mustExist: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not resolve workspace skill path '{directoryName}': {exception.Message}");
            return null;
        }
    }

    private static SkillInfo? TryLoadSkill(string path, string directoryName, SkillSource source, List<string> warnings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string body;
        try
        {
            body = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not read skill file '{path}': {exception.Message}");
            return null;
        }

        var (frontmatter, skillBody) = ExtractFrontmatter(body);
        if (frontmatter is null)
        {
            warnings.Add($"Skill '{directoryName}' at '{path}' is missing YAML frontmatter.");
            return null;
        }

        if (!TryGetSimpleValue(frontmatter, "name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            warnings.Add($"Skill '{directoryName}' at '{path}' is missing a non-empty 'name' in frontmatter.");
            return null;
        }

        if (!name.Equals(directoryName, StringComparison.Ordinal))
        {
            warnings.Add($"Skill '{directoryName}' at '{path}' has frontmatter name '{name}' that does not match the directory name.");
            return null;
        }

        if (!TryGetSimpleValue(frontmatter, "description", out var description) || string.IsNullOrWhiteSpace(description))
        {
            warnings.Add($"Skill '{directoryName}' at '{path}' is missing a non-empty 'description' in frontmatter.");
            return null;
        }

        return new SkillInfo(name, description, skillBody, source, path);
    }

    private static (string? Frontmatter, string Body) ExtractFrontmatter(string text)
    {
        // YAML frontmatter starts at the first byte with --- and ends with a matching --- on its own line.
        var content = text.TrimStart('\uFEFF');
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return (null, content);
        }

        var startIndex = 3;
        while (startIndex < content.Length && (content[startIndex] == '\r' || content[startIndex] == '\n'))
        {
            startIndex++;
        }

        var endIndex = content.IndexOf("\n---", startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            return (null, content);
        }

        var frontmatter = content[startIndex..endIndex].Trim();
        var bodyStart = endIndex + 4;
        while (bodyStart < content.Length && (content[bodyStart] == '\r' || content[bodyStart] == '\n'))
        {
            bodyStart++;
        }

        var body = content[bodyStart..];
        return (frontmatter, body);
    }

    private static bool TryGetSimpleValue(string frontmatter, string key, out string value)
    {
        value = string.Empty;
        foreach (var line in frontmatter.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith($"{key}:", StringComparison.Ordinal))
            {
                value = StripMatchingQuotes(trimmed[(key.Length + 1)..].Trim());
                return true;
            }
        }

        return false;
    }

    private static string StripMatchingQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }
}

public sealed class SkillContextProvider : IContextProvider
{
    private readonly ISkillLocator _skills;

    public SkillContextProvider(ISkillLocator skills) => _skills = skills;

    public ValueTask<string?> GetContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_skills.Skills.Count == 0)
        {
            return ValueTask.FromResult<string?>(null);
        }

        var builder = new StringBuilder();
        builder.AppendLine("Available skills:");
        foreach (var skill in _skills.Skills.Values.OrderBy(static s => s.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AppendLine($"- {skill.Name}: {skill.Description}");
        }

        return ValueTask.FromResult<string?>(builder.ToString().TrimEnd());
    }
}
