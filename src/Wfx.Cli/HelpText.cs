using System.Text;
using Wfx.Core;

namespace Wfx.Cli;

/// <summary>
/// Presentation helpers for the console help text. Canonical wording stays in a single string
/// per topic; these helpers render it for the help layout without duplicating it.
/// </summary>
internal static class HelpText
{
    private const int RemediationWrapWidth = 80;

    public static void Print()
    {
        Console.WriteLine("""
            WFX — Windows-first embeddable AI coding agent

            Usage:
              wfx [options]                 Start interactive mode
              wfx run [options] <prompt>    Run one task
              wfx models [options]          Show provider/model configuration
              wfx config [options]          Inspect effective configuration
              wfx sessions [options]        List sessions with workspace, timestamps, sizes, and total
              wfx resume [options]          Resume the latest session for this workspace
              wfx mcp auth <server>         Sign in to a remote (HTTP) MCP server via OAuth
              wfx mcp auth --revoke <name>  Drop the stored credential for a server

            Options:
              --model <model>               Model ID; openrouter/<id> selects OpenRouter
              --profile <name>              Named profile from user/project configuration
              --protocol <name>             chat_completions, responses, or anthropic_messages (reserved)
              --provider <name>             openai, openrouter, anthropic, local, or a custom name
              --base-url <url>              OpenAI-compatible API base URL
              --approval <mode>             always, workspace, never, or yolo
              --yolo                        Bypass tool approval prompts (same as --approval yolo)
              --timeout <seconds>           Provider timeout (1-3600)
              --max-iterations <count>      Noninteractive loop limit (1-100; default 24)
                                            Interactive mode is unlimited
              --verbose                     Show timing and progress details
              --debug                       Show tool result diagnostics
              --json                        Machine-readable output: NDJSON events for run/resume,
                                            one result object for sessions/config/models
              --quiet                       Presentation flag; suppress human decoration on stderr
                                            in interactive mode and the commands listed below
              --no-session                  Do not persist a session log for this invocation
              --id <session-id>             Resume a specific session (only with wfx resume)
              --force                       Rebind the session selected with --id
              --help                        Show help
              --version                     Show version

            Interactive commands:
              /model                        List configured models and choose one
              /model <id>                   Use a model ID on the current connection
              /help                         Show interactive commands
              /exit, /quit                  End the session

            Resume a session in a new process with wfx resume, or wfx resume --id <session-id>.
            wfx run --json streams one event per line. wfx resume --id <session-id> --json reads one
            prompt from stdin and streams the resumed turn. The stream is credential-adjacent; do not
            send it to shared logs without reviewing its contents.

            Machine-readable output: wfx sessions --json, wfx config --json, and wfx models --json
            write one JSON result object to stdout, not an event stream. Shapes carry schema_version
            1 and are published under docs/schemas/ with every field marked public or internal.

            --quiet is available on run, resume, sessions, config, and models.
            It is also available in interactive mode and does not change stdout.
            In human mode, errors and warnings still use stderr.
            --json --quiet preserves the JSON output and limits stderr to terminal failures
            and MCP sign-in remediations, which are never suppressed.

            Configuration precedence: CLI > environment > project > user > defaults.
            Prefer WFX_API_KEY for credentials. WFX never prints API keys.
            Interactive mode and wfx run persist a JSONL session under %USERPROFILE%\.wfx\sessions\
            unless --no-session is passed. Session files remain sensitive despite secret redaction.

            wfx run and wfx resume refuse to start when stdin is not a terminal and approval is
            always or workspace: a tool prompt would block with nobody there to answer it.
            """);
        // Wrap the shared remediation wording to the help layout; the stderr refusal keeps the
        // same string as one unbroken sentence.
        foreach (var line in Wrap(StartupApprovalGate.Remediation, RemediationWrapWidth))
        {
            Console.WriteLine(line);
        }
        Console.WriteLine("""

            Exit codes:
              0    success
              1    error
              2    config error
              3    wfx run or wfx resume refused to start: approval needs a terminal
              4    run stopped at maximum iterations, or JSON turn interrupted
              5    JSON turn error: provider, tool, protocol, or configuration
              130  human-mode turn cancelled
            """);
    }

    /// <summary>
    /// Greedily wraps <paramref name="text"/> at word boundaries so no line is wider than
    /// <paramref name="maxWidth"/>. A single word longer than the width gets a line to itself.
    /// Empty or whitespace-only text yields one empty line.
    /// </summary>
    public static IReadOnlyList<string> Wrap(string text, int maxWidth)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in words)
        {
            if (current.Length == 0)
            {
                current.Append(word);
            }
            else if (current.Length + 1 + word.Length <= maxWidth)
            {
                current.Append(' ').Append(word);
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear().Append(word);
            }
        }

        lines.Add(current.ToString());
        return lines;
    }
}
