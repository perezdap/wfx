using Wfx.Core;

namespace Wfx.Cli;

internal enum CliCommand
{
    Interactive,
    Run,
    Models,
    Config,
    Sessions
}

internal sealed record CliArguments(
    CliCommand Command,
    string? Prompt,
    WfxSettingsLayer Settings,
    bool Verbose,
    bool Debug,
    bool NoSession,
    bool ShowHelp,
    bool ShowVersion)
{
    public static CliArguments Parse(string[] args)
    {
        var command = CliCommand.Interactive;
        var promptParts = new List<string>();
        string? provider = null;
        string? protocol = null;
        string? baseUrl = null;
        string? model = null;
        string? profile = null;
        int? timeout = null;
        int? maxIterations = null;
        ApprovalMode? approval = null;
        var verbose = false;
        var debug = false;
        var noSession = false;
        var showHelp = false;
        var showVersion = false;
        var commandSelected = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--help" or "-h":
                    showHelp = true;
                    break;
                case "--version":
                    showVersion = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--debug":
                    debug = true;
                    verbose = true;
                    break;
                case "--no-session":
                    noSession = true;
                    break;
                case "--provider":
                    provider = RequireValue(args, ref index, argument);
                    break;
                case "--protocol":
                    protocol = RequireValue(args, ref index, argument);
                    break;
                case "--base-url":
                    baseUrl = RequireValue(args, ref index, argument);
                    break;
                case "--profile":
                    profile = RequireValue(args, ref index, argument);
                    break;
                case "--model":
                    var parsedModel = WfxConfiguration.ParseModelShorthand(RequireValue(args, ref index, argument));
                    model = parsedModel.Model;
                    provider = parsedModel.Provider ?? provider;
                    break;
                case "--timeout":
                    timeout = ParseInteger(RequireValue(args, ref index, argument), argument, 1, 3600);
                    break;
                case "--max-iterations":
                    maxIterations = ParseInteger(RequireValue(args, ref index, argument), argument, 1, 100);
                    break;
                case "--approval":
                    approval = RequireValue(args, ref index, argument).ToLowerInvariant() switch
                    {
                        "always" => ApprovalMode.Always,
                        "workspace" => ApprovalMode.Workspace,
                        "never" => ApprovalMode.Never,
                        _ => throw new ArgumentException("--approval must be always, workspace, or never.")
                    };
                    break;
                case "run" when !commandSelected:
                    command = CliCommand.Run;
                    commandSelected = true;
                    break;
                case "models" when !commandSelected:
                    command = CliCommand.Models;
                    commandSelected = true;
                    break;
                case "config" when !commandSelected:
                    command = CliCommand.Config;
                    commandSelected = true;
                    break;
                case "sessions" when !commandSelected:
                    command = CliCommand.Sessions;
                    commandSelected = true;
                    break;
                default:
                    if (argument.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option '{argument}'.");
                    }

                    if (command != CliCommand.Run)
                    {
                        throw new ArgumentException($"Unexpected argument '{argument}'. Use 'wfx run <prompt>'.");
                    }

                    promptParts.Add(argument);
                    break;
            }
        }

        var prompt = promptParts.Count == 0 ? null : string.Join(' ', promptParts);
        if (command == CliCommand.Run && string.IsNullOrWhiteSpace(prompt) && !showHelp && !showVersion)
        {
            throw new ArgumentException("The run command requires a prompt.");
        }

        return new CliArguments(
            command,
            prompt,
            new WfxSettingsLayer
            {
                Provider = provider,
                Protocol = protocol,
                BaseUrl = baseUrl,
                Model = model,
                Profile = profile,
                TimeoutSeconds = timeout,
                MaxIterations = maxIterations,
                Approval = approval
            },
            verbose,
            debug,
            noSession,
            showHelp,
            showVersion);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[index];
    }

    private static int ParseInteger(string value, string option, int minimum, int maximum) =>
        int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
            ? parsed
            : throw new ArgumentException($"{option} must be between {minimum} and {maximum}.");
}
