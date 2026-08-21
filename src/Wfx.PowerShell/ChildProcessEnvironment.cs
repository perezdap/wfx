namespace Wfx.PowerShell;

public static class ChildProcessEnvironment
{
    public static bool IsSecretVariableName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return name.Equals("WFX_API_KEY", StringComparison.OrdinalIgnoreCase)
            || name.Equals("OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase)
            || name.Equals("OPENROUTER_API_KEY", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase);
    }

    public static void Apply(
        IDictionary<string, string?> environment,
        IReadOnlyDictionary<string, string?>? overlay = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach (var key in environment.Keys.ToArray())
        {
            if (IsSecretVariableName(key))
            {
                environment.Remove(key);
            }
        }

        environment["GIT_PAGER"] = "cat";
        environment["PAGER"] = "cat";

        if (overlay is null)
        {
            return;
        }

        foreach (var pair in overlay)
        {
            environment[pair.Key] = pair.Value;
        }
    }
}
