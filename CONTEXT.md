# WFX

WFX is an embeddable, Windows-first coding-agent runtime. This glossary defines the language used for its configuration and model-endpoint concepts.

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
