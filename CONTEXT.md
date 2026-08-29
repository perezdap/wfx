# WFX

WFX is an embeddable, Windows-first coding-agent runtime. This glossary defines the language used for its configuration, model-endpoint, and session concepts.

## Language

### Configuration

**Settings layer**:
One level of the configuration stack, merged lowest to highest precedence: built-in defaults, user config (`%USERPROFILE%\.wfx\config.json`), project config (`<workspace>\.wfx\config.json`), environment variables, CLI arguments.
_Avoid_: config level, config scope

**Profile**:
A named settings layer stored under `profiles` in a config file and selected per invocation. A profile carries the same keys as top-level config; selecting one expands it into its file's layer.
_Avoid_: endpoint, connection, preset, environment

### Model endpoints

**Provider**:
A preset name (`openai`, `openrouter`, `anthropic`, `local`, or a custom name) that supplies a default base URL and a credential environment-variable convention. Orthogonal to protocol.
_Avoid_: backend, endpoint, service

**Protocol**: 
The wire format spoken to a model endpoint. Settable in every settings layer as `protocol`, `WFX_PROTOCOL`, or `--protocol`. Values: `chat_completions` (default), `responses`, and the reserved `anthropic_messages` (errors with "not implemented yet"). Protocol-specific defaults supply base URL and credential environment-variable conventions; provider remains orthogonal preset sugar.
_Avoid_: endpoint type, API style, transport

**Model**:
The model identifier sent to an endpoint. A configured model is always a (profile, model) pair — the same model name may be offered by several profiles, so model identity includes its endpoint.
_Avoid_: model name (for the pair)

### Sessions

**Session**:
One conversation with the agent, identified by a session ID, bound to the workspace it was started in, and durable across process exits. A session is its transcript plus the endpoint identity each turn ran under — not a running process, and not a window.
_Avoid_: conversation (for the durable record), history, thread, chat

**Transcript**:
The ordered record of what happened in a session: the messages exchanged, the tool calls and their results, token usage, interruptions, and errors. The transcript is the authoritative account of a session; anything the agent replays to an endpoint is derived from it.
_Avoid_: log, history, message list

**Turn**:
One user prompt and everything the agent did in response, up to the point it stopped and returned control — including every model call and tool call in between. A turn is the unit that carries an endpoint identity and the unit an interrupt cancels.
_Avoid_: iteration (that is one model call inside a turn), exchange, round

**Endpoint identity**:
The (profile, provider, protocol, model) tuple a turn ran under, recorded per turn because `/model` can change it mid-session. Approval mode is deliberately not part of it: approval is a posture of the current invocation, not a property of history.
_Avoid_: session config, settings (for the recorded tuple)

**Workspace binding**:
The workspace root a session's transcript describes and against which its future turns may run. Resume refuses a different current workspace by default. A forced rebind, selected explicitly by session ID, appends a `workspace_rebound` event and makes the current workspace the new binding without rewriting history.
_Avoid_: session path, working directory

**Session lease**:
Exclusive ownership of session appends for one process lifetime. Resume fails fast with "session in use" when another owner holds the lease; read-only session inspection does not acquire it.
_Avoid_: session lock (for the domain concept), file lock

**Resume**:
Continuing an existing session: its transcript is loaded, the last turn's endpoint identity is restored, a session lease is held, and new turns append to the same session. Resuming refuses by default when the workspace binding is not the current workspace; `--id <session-id> --force` deliberately rebinds it.
_Avoid_: restore, reopen, replay (replay is re-sending a transcript to an endpoint, not resuming a session)

**Provider items**:
A turn as the endpoint itself expressed it — opaque provider-native JSON that WFX stores and replays unchanged because it carries state that cannot be reconstructed from message content and tool calls (OpenAI Responses reasoning items, for instance). Provider items are valid only for the endpoint identity that issued them.
_Avoid_: reasoning, raw response, native message

**Interrupted turn**:
A turn cancelled before it completed. It is recorded as such, and any trailing model request it left unanswered — tool calls with no results — is not replayed on resume.
_Avoid_: aborted, cancelled turn (in the record), partial turn

### Extensions

**MCP server**:
A user-configured external tool process WFX launches and speaks the Model Context Protocol to, contributing its tools to the registry. Configured only in the user settings layer; a workspace may not supply one. See [ADR 0007](docs/adr/0007-mcp-servers-are-user-configured-never-workspace-supplied.md).
_Avoid_: plugin, integration, MCP connection, external server

**MCP tool**:
A tool backed by an MCP server, named `mcp_<server>_<tool>` and classified `SystemChange` unconditionally. Approved per call through the ordinary approval service like any other tool; there is no per-server trust grant.
_Avoid_: remote tool, server tool, dynamic tool

**Skill**:
A self-contained instruction package — a `SKILL.md` with `name` and `description` frontmatter — discovered from user or workspace skill directories. Its description is always available to the model; its full body loads on demand through the `skill` tool.
_Avoid_: plugin, slash command, prompt template

### Noninteractive contract

**Noninteractive contract**:
The scriptable surface `wfx` exposes when driven by another program rather than a human at a terminal: machine output on stdout, a documented exit-code table, and a startup approval gate that refuses ambiguous configurations. Covers `run`, `resume`, `sessions`, `config`, and `models`.
_Avoid_: machine mode, batch mode, headless (headless refers to the embeddable core, not this contract), scripting mode

**Event stream**:
The NDJSON output written to stdout by `--json` on turn commands (`run`, `resume`): one JSON object per line, one per agent event, drawn from the same event vocabulary as the session transcript (`turn_started`, `message`, `tool_started`, `tool_completed`, `tool_rejected`, `usage`, `turn_completed`, `turn_interrupted`, `turn_error`). The first line of any turn is always `turn_started` and carries the session ID.
_Avoid_: JSON output, log, trace, NDJSON log

**Result object**:
The single JSON object written to stdout by `--json` on non-turn commands (`sessions`, `config`, `models`). A result object is not an event and does not carry a per-event `schema_version`; its shape is defined per command in the JSON Schema, with `schema_version` at the top level.
_Avoid_: response, output object, one-shot event

**Turn command**:
A `wfx` subcommand that runs the agent loop — currently `run` and `resume`. Turn commands emit an event stream under `--json`. Non-turn commands (`sessions`, `config`, `models`) emit a result object.
_Avoid_: agent command, loop command

**Public field**:
A field in `--json` output documented in the JSON Schema as part of the contract. Public fields are append-only within a schema version; removing or renaming one bumps the version. Fields not marked public are internal and may change without a bump. Applies to both event streams and result objects.
_Avoid_: stable field, contract field

**Internal field**:
A field emitted on stdout but not part of the contract — provider items, raw endpoint payloads, and other consumer-hostile shapes. Present because the event stream is a projection of the transcript, but callers must not depend on them.
_Avoid_: private field, unstable field

**Schema version**:
The `schema_version` value on the `turn_started` event, naming the contract that governs the rest of that turn's stream. Bumped when a public field is renamed or removed; additive changes do not bump. Result objects carry their own per-command `schema_version` at the top level.
_Avoid_: protocol version, stream version, event version

**Startup approval gate**:
The check `wfx` performs before starting a turn when stdin is not a TTY: if the active approval mode can prompt (`always` or `workspace`), the process exits with a usage error naming the two accepted fixes (`--approval never` or `--yolo`). Distinct from per-tool approval decisions, which continue to flow through `OnToolRejectedAsync` as structured rejections the model can see.
_Avoid_: approval check, TTY gate, noninteractive check

**Presentation flag**:
A flag that affects only human-facing decoration on stderr — spinners, ANSI, progress dots, tool-call summary lines. `--quiet` is the presentation flag; under `--json` it silences stderr chatter, under human output it silences spinners and ANSI. Presentation flags never change what appears on stdout.
_Avoid_: output flag, verbosity flag (verbosity is separate)
