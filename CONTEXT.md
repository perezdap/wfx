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

**Resume**:
Continuing an existing session: its transcript is loaded, the last turn's endpoint identity is restored, and new turns append to the same session. Resuming refuses by default when the recorded workspace is not the current workspace.
_Avoid_: restore, reopen, replay (replay is re-sending a transcript to an endpoint, not resuming a session)

**Provider items**:
A turn as the endpoint itself expressed it — opaque provider-native JSON that WFX stores and replays unchanged because it carries state that cannot be reconstructed from message content and tool calls (OpenAI Responses reasoning items, for instance). Provider items are valid only for the endpoint identity that issued them.
_Avoid_: reasoning, raw response, native message

**Interrupted turn**:
A turn cancelled before it completed. It is recorded as such, and any trailing model request it left unanswered — tool calls with no results — is not replayed on resume.
_Avoid_: aborted, cancelled turn (in the record), partial turn
