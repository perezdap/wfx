using Wfx.Core;

namespace Wfx.Cli;

/// <summary>
/// The pre-turn check that refuses to start a turn nobody can approve: stdin is not a terminal
/// and the active approval mode can prompt. Per-tool denials inside a turn stay on the observer
/// path as structured rejections; this gate only decides whether a turn may begin at all.
/// </summary>
internal static class StartupApprovalGate
{
    /// <summary>Exit code for a turn the gate refused to start.</summary>
    public const int RefusedExitCode = 3;

    public static bool Refuses(CliCommand command, ApprovalMode approval, IConsoleEnvironment console) =>
        IsTurnCommand(command) &&
        CanPrompt(approval) &&
        console.IsInputRedirected;

    public static string RefusalMessage(ApprovalMode approval) =>
        $"wfx: approval is {WfxConfiguration.FormatApprovalMode(approval)} and stdin is not a terminal, " +
        "so no one can answer a tool approval prompt. Pass --approval never to refuse tool calls that " +
        "need approval, or --yolo to bypass the prompts.";

    private static bool IsTurnCommand(CliCommand command) =>
        command is CliCommand.Run or CliCommand.Resume;

    private static bool CanPrompt(ApprovalMode approval) =>
        approval is ApprovalMode.Always or ApprovalMode.Workspace;
}
