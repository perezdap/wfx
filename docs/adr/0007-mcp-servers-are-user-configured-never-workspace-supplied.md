# MCP servers are user-configured, never workspace-supplied

WFX acts as an MCP host, and a configured MCP server is an arbitrary executable launched on the user's machine with the user's authority. We decided that `mcp_servers` is read from the **user settings layer only** (`%USERPROFILE%\.wfx\config.json`); a `mcp_servers` key in project config (`<workspace>\.wfx\config.json`) is a configuration error, not a warning. This matches fx's rule that repository-local MCP files are never loaded or executed, and goes deliberately stricter than Claude Code, which loads project servers after a one-time approval: WFX already treats workspace config as a trust boundary for credentials (`base_url` suppression), and extending that boundary to executable launches would let any cloned repository run code on the next `wfx run`. Every MCP tool call is also classified `SystemChange` unconditionally and approved per call through the ordinary approval service — there is no per-server trust grant that persists across calls.

## Considered Options

- **Project config with per-server user approval** (Claude Code's model): rejected — an approval prompt is a trust decision made once and then forgotten; a repository can rotate the command under an approved name, and the noninteractive contract would need a story for first-run servers it cannot prompt about.
- **Project config with warnings**: rejected — a warning on stderr is invisible in scripted and embedded use, where WFX runs unattended by design.
- **User config only, project config errors** (chosen): the user who can edit `%USERPROFILE%\.wfx\config.json` already controls credential config and child-process execution; adding MCP there grants no new authority. The refusal makes the boundary legible to the model, the host, and the user alike.

## Consequences

- Team-shared MCP setups cannot be committed to a repository; sharing a server configuration means sharing a config snippet for the user file, documented as such.
- All MCP tools are namespaced (`mcp_<server>_<tool>`) and never auto-approved as `ReadOnly`, in any approval mode, including `yolo`'s path checks — `yolo` skips prompts but not workspace policy, and MCP tools are `SystemChange`, so they still require the mode that permits `SystemChange`.
- A server that fails to start or crashes degrades its own tools to structured failures; it never aborts the CLI, matching the optional-server semantics other harnesses settled on.
