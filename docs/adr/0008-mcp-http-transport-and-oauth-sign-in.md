# MCP HTTP transport and OAuth sign-in

WFX connects to remote MCP servers over the Streamable HTTP transport, and authorizes against OAuth-gated servers with an explicit `wfx mcp auth <server>` command (OAuth 2.1 authorization code + PKCE). This extends ADR 0007: HTTP servers follow the same trust boundary — user configuration only, unconditional `SystemChange` classification, per-call approval.

Decisions, answering the open questions in issue #73:

- **One server, one transport, discriminated by key presence.** An `mcp_servers` entry defines `command` (stdio) or `url` (HTTP); both or neither is a configuration error naming the keys and the rule. No `transport` field: the discriminating key is already unambiguous, and a second field could contradict it. `args`/`env` on an HTTP entry and `headers` on a stdio entry are likewise rejected rather than silently ignored.
- **Token store is a plain JSON file** at `%USERPROFILE%\.wfx\mcp-tokens.json`, written only by `wfx mcp auth` and read by the HTTP transport. OS credential-vault integration was considered and deferred: the file already lives under the same user-profile boundary as `config.json` credentials, and the store records the token endpoint and client id alongside the tokens so refresh needs no rediscovery. Tokens are never logged and never enter the event stream; the file is write-then-replace so a crash cannot truncate it.
- **Client identity is metadata-driven.** When the authorization server advertises a registration endpoint, wfx registers a public client per sign-in (RFC 7591); otherwise it falls back to the fixed public client id `wfx`. Shipping one pre-registered client id per hosted server was rejected: wfx cannot know the universe of servers, and per-server registration is what the MCP authorization spec mandates when available.
- **Sign-in is an explicit CLI command, never mid-turn.** `wfx mcp auth <server>` runs discovery (`/.well-known` resource and authorization-server metadata), loopback redirect, and PKCE exchange; `wfx mcp auth --revoke <server>` drops the stored credential. A noninteractive `run --json` that hits a 401 without a valid token fails that server's tools with a structured remediation naming the command — no browser is launched mid-turn. The transport refreshes expired tokens inline (non-interactive by design) and drops a rejected grant so the next failure re-remediates.

## Consequences

- The stdio and HTTP transports share `McpJsonRpcSession` framing and the `McpProtocolClient` handshake; only the byte-mover differs (`McpHttpTransport` POSTs each line and feeds JSON or SSE response payloads back as lines). To the model, a remote server is indistinguishable from a local one.
- The loopback redirect listener is a bare TCP socket, not `HttpListener`: http.sys URL ACL reservations are unavailable to unprivileged users.
- Tokens and MCP header values join the redaction set, so console output and prompts never echo them.
- Legacy standalone SSE (the pre-Streamable-HTTP revision) and dynamic client registration beyond the advertised endpoint remain out of scope.
