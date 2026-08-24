# 0006 — Event stream and session transcript share one vocabulary

The `--json` event stream emits the same event names and payload shape as the session JSONL transcript. Fields are marked `public` or `internal` in the JSON Schema; only public fields are part of the contract.

## Context

Emitting different event shapes on stdout vs. on disk would fork the vocabulary and force every future event to be defined twice. Emitting the transcript verbatim would leak consumer-hostile shapes — opaque provider items (ADR 0004), raw endpoint payloads — into the public contract.

## Decision

One vocabulary, two audiences. The stream is a live view of the transcript. The JSON Schema at `docs/schemas/wfx-events.v1.json` marks each field `public` or `internal`. Public fields are the contract; internal fields are present because the stream is a projection of the transcript and may change without a schema-version bump.

Redaction is inherited from the transcript unchanged (ADR 0003); the stream applies no additional pass. If stricter redaction is warranted, ADR 0003 is the place to change it, and both stream and transcript move together.

## Consequences

- `SessionRecorder` and the new `NdjsonAgentObserver` both serialize from the same event types, so drift is a compile-time concern, not a coordination concern.
- Callers wanting a curated shape filter the stream themselves; WFX does not ship a second serialization path.
- Adding a new provider-native field is not a breaking change as long as it is marked internal.
- Stream output is credential-adjacent for the same reason session files are, and the flag docs warn against piping it to shared log services.
