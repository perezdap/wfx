using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wfx.Core;
using Wfx.PowerShell;

namespace Wfx.Tools;

public sealed class PowerShellTool : WorkspaceTool, ITool
{
    private static readonly Regex Dangerous = Pattern("""
        (
            (?:Remove-Item|rm|ri|del|erase|rd|rmdir)\b
                (?=[^\r\n]*(?:['"]?(?:[A-Za-z]:\\|\\\\)))
                (?=[^\r\n]*-Recurse)
            | Clear-Disk | Format-Volume | Remove-Partition | Stop-Computer | Restart-Computer | bcdedit | diskpart
        )
        """);
    private static readonly Regex SystemChange = Pattern(@"\b(
        winget\s+(install|uninstall|upgrade)
        | choco\s+(install|uninstall|upgrade)
        | Set-Service | New-Service | Remove-Service
        | Set-ExecutionPolicy
        | Enable-WindowsOptionalFeature | Disable-WindowsOptionalFeature
        | Register-ScheduledTask | Unregister-ScheduledTask
        | reg(?:\.exe)?\s+(add|delete)
        | msiexec(?:\.exe)?\s+/(i|x)
        | dism(?:\.exe)?
        | Invoke-Expression | iex
        | Invoke-Command | icm
        | Start-Process | saps
    )\b");
    private static readonly Regex WorkspaceWrite = Pattern(@"\b(
        Set-Content | sc | Add-Content | ac | Out-File
        | New-Item | ni
        | Remove-Item | rm | ri | del | erase | rd | rmdir
        | Move-Item | mi | move
        | Copy-Item | cpi | copy | cp
        | Rename-Item | rni | ren
        | Set-ItemProperty | New-ItemProperty | Remove-ItemProperty
    )\b");
    private static readonly Regex TestCommand = Pattern(@"\b(dotnet\s+(test|build|restore|format)|Invoke-Pester|msbuild(?:\.exe)?|vstest\.console(?:\.exe)?)\b");
    private static readonly Regex UntrustedPowerShell = Pattern("""
        [A-Za-z]:
        | \\\\
        | \.\.
        | \$
        | `
        | &
        | [(){}]
        """);

    // Env: provider reads leak process environment and must never auto-approve as ReadOnly.
    private static readonly Regex EnvironmentProvider = Pattern(@"\bEnv:");

    // Output redirection (>, >>, 2>) writes to the file system; PowerShell has no
    // '>' comparison operator, so any '>' is treated as a write.
    private static readonly Regex Redirection = Pattern(@">");

    // Matches a single read-only statement. Arguments may not contain a redirection
    // operator; statements are evaluated one at a time so a read-only prefix cannot
    // swallow a following command on another line or after a separator.
    private static readonly Regex ReadOnlyStatement = Pattern(@"^\s*(?:
        Get-[\w-]+ | gc | cat | type | gi | gci | ls | dir | gl | pwd
        | Test-[\w-]+ | Select-[\w-]+ | Where-Object | Sort-Object | Measure-Object
        | Format-[\w-]+ | Convert(?:To|From)-[\w-]+ | Resolve-Path
        | git\s+(?:status|diff|log) | dotnet\s+--info
    )(?:\s+[^>]*)?$");

    private static readonly char[] StatementSeparators = ['\n', '\r', ';', '|', '&'];

    private readonly IPowerShellRunner _runner;

    public PowerShellTool(WorkspacePathPolicy paths, IPowerShellRunner runner) : base(paths)
    {
        _runner = runner;
        Definition = new ToolDefinition(
            "powershell",
            "Execute a PowerShell script in the workspace using pwsh.exe with Windows PowerShell fallback.",
            ToolJson.ObjectSchema([
                ("script", ToolJson.StringSchema("PowerShell script to execute."), true),
                ("working_directory", ToolJson.StringSchema("Workspace directory in which to run."), false),
                ("timeout_seconds", ToolJson.IntegerSchema("Timeout in seconds.", 1, 1_800), false),
                ("inherit_environment", ToolJson.StringArraySchema("Parent environment variable names to restore in the child process. Secret-bearing variables (*_API_KEY, *_TOKEN, *_SECRET, plus WFX_API_KEY, OPENAI_API_KEY, and OPENROUTER_API_KEY) are omitted by default."), false)
            ]));
    }

    public ToolDefinition Definition { get; }

    public ApprovalLevel Classify(JsonElement arguments)
    {
        var scriptLevel = ClassifyScript(ToolJson.RequiredString(arguments, "script"));
        if (ToolJson.Strings(arguments, "inherit_environment").Count == 0)
        {
            return scriptLevel;
        }

        return scriptLevel >= ApprovalLevel.SystemChange ? scriptLevel : ApprovalLevel.SystemChange;
    }

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var script = ToolJson.RequiredString(arguments, "script");
        var workingDirectory = Paths.Resolve(
            ToolJson.String(arguments, "working_directory", Paths.Root),
            mustExist: true);
        if (!Directory.Exists(workingDirectory))
        {
            return ToolResult.Fail("PowerShell working_directory is not a directory.");
        }

        var timeout = TimeSpan.FromSeconds(ToolJson.Integer(arguments, "timeout_seconds", 120, 1, 1_800));
        var result = await _runner.ExecuteAsync(
            new PowerShellRequest(script, workingDirectory, ResolveInheritedEnvironment(arguments), timeout),
            cancellationToken).ConfigureAwait(false);
        var combined = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            combined.Append(result.StandardOutput.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            if (combined.Length > 0)
            {
                combined.AppendLine();
            }

            combined.AppendLine("[stderr]");
            combined.Append(result.StandardError.TrimEnd());
        }

        var metadata = new Dictionary<string, string>
        {
            ["exit_code"] = result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["timed_out"] = result.TimedOut.ToString(),
            ["truncated"] = result.Truncated.ToString(),
            ["duration_ms"] = result.Duration.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
        };
        return result.ExitCode == 0 && !result.TimedOut
            ? ToolResult.Ok(combined.ToString(), metadata)
            : new ToolResult(false, combined.ToString(), result.TimedOut ? "PowerShell command timed out." : "PowerShell command failed.", metadata);
    }

    public static ApprovalLevel ClassifyScript(string script)
    {
        if (Dangerous.IsMatch(script))
        {
            return ApprovalLevel.Dangerous;
        }

        if (SystemChange.IsMatch(script))
        {
            return ApprovalLevel.SystemChange;
        }

        if (UntrustedPowerShell.IsMatch(script) || EnvironmentProvider.IsMatch(script))
        {
            return ApprovalLevel.SystemChange;
        }

        if (Redirection.IsMatch(script) || WorkspaceWrite.IsMatch(script) || TestCommand.IsMatch(script))
        {
            return ApprovalLevel.WorkspaceWrite;
        }

        var statements = script.Split(StatementSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return statements.Length > 0 && statements.All(statement => ReadOnlyStatement.IsMatch(statement))
            ? ApprovalLevel.ReadOnly
            : ApprovalLevel.SystemChange;
    }

    private static IReadOnlyDictionary<string, string?>? ResolveInheritedEnvironment(JsonElement arguments)
    {
        var names = ToolJson.Strings(arguments, "inherit_environment");
        if (names.Count == 0)
        {
            return null;
        }

        Dictionary<string, string?>? environment = null;
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is null)
            {
                continue;
            }

            environment ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            environment[name] = value;
        }

        return environment;
    }

    private static Regex Pattern(string expression) => new(
        expression,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant);
}
