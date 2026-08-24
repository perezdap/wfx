using Wfx.Core;

namespace Wfx.Cli;

/// <summary>
/// The pre-turn check that refuses to start a turn nobody can approve: stdin is not a terminal
/// and the active approval mode can prompt. Per-tool denials inside a turn stay on the observer
/// path as structured rejections; this gate only decides whether a turn may begin at all.
/// </summary>
internal static class StartupApprovalGate
{
    public const string Remediation =
        "Pass --approval never or --yolo to run unattended; never refuses tool calls that need " +
        "approval, while yolo bypasses the prompts.";

    public static StartupApprovalRefusal? Evaluate(
        CliCommand command,
        ApprovalMode approval,
        IConsoleEnvironment console)
    {
        if (!IsTurnCommand(command) ||
            !ApprovalPolicy.CanPrompt(approval) ||
            !console.IsInputRedirected)
        {
            return null;
        }

        return new StartupApprovalRefusal(
            ExitCode: 3,
            Message:
                $"wfx: approval is {WfxConfiguration.FormatApprovalMode(approval)} and stdin is not a terminal, " +
                $"so no one can answer a tool approval prompt. {Remediation}");
    }

    private static bool IsTurnCommand(CliCommand command) =>
        command is CliCommand.Run or CliCommand.Resume;
}

internal sealed record StartupApprovalRefusal(int ExitCode, string Message);
