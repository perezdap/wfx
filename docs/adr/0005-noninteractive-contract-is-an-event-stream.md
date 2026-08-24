# 0005 — The noninteractive contract is an NDJSON event stream

`wfx --json` on turn commands (`run`, `resume`) writes one JSON object per line, one per agent event, rather than a single object at turn completion. The event vocabulary is the same one the session transcript already uses through `IAgentObserver` and `SessionRecorder`.

## Context

A noninteractive `wfx run` needs machine-parseable output. Two shapes were available: a single terminal `{ session_id, final_message, usage, tool_calls }` object emitted at completion, or a stream of events emitted as the turn runs.

## Decision

Stream. `IAgentObserver` is already event-shaped, `SessionStore` already writes NDJSON to disk in the same vocabulary (ADR 0002), and long-running turns benefit from progress a caller can act on. A terminal-object shape is trivially derivable client-side by filtering for `event: "turn_completed"`; the reverse is not.

Non-turn commands (`sessions`, `config`, `models`) are one-shot data commands and emit a single result object rather than a stream. Forcing a stream shape on them is ceremony without benefit.

## Consequences

- The first line of a turn's stdout is always `turn_started` and carries the session ID, so a caller can recover it even if the turn crashes mid-stream.
- The stream is a live view of the transcript, not a curated CLI shape, so events carry both public and internal fields (see ADR 0006).
- A single-object mode may be added later behind a distinct flag without invalidating this contract.
