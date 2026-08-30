# 0008 — Human output is a stderr stream; stdout carries only a redirected final response

## Context

Without `--json`, WFX wrote every model text delta to stdout (`ConsoleAgentObserver.OnModelTextAsync`) and every tool line to stderr, with nothing coordinating the cursor between them. Model text is never newline-terminated until the end of a turn, so on a multi-iteration turn a tool line landed mid-sentence: `I'll check the config first.● read_file(...)`. Separately, `wfx run "..." > notes.md` captured the model's narration run together with its final response, while `turn_completed.final_response` in the event stream carried only the latter — the same concept with two different contents on two surfaces.

## Decision

All human-facing output — narration, final response, tool lines, prompts, decoration — is written to stderr as one ordered stream. Stdout receives the turn's final response exactly once, at turn end, and only when stdout is redirected. On a terminal, stdout carries nothing.

## Considered options

Streaming every delta to stdout as today was rejected because it makes a redirected stdout a mixture of narration and answer. Buffering each iteration to emit only the final response on stdout was rejected because an iteration is not known to be the last until it ends without tool calls, which would delay the streaming answer.

## Consequences

- Human-mode stdout now agrees with `turn_completed.final_response` (ADR 0006): one concept, two encodings.
- The cross-stream cursor collision becomes structurally impossible rather than something delimiters have to paper over.
- What appears on stdout is conditional on `Console.IsOutputRedirected` — a deliberate wart, in the tradition of tools that columnise only when interactive.
- `wfx run "..." 2>/dev/null` at a terminal shows nothing.
- Streaming is preserved: the final response still streams live, to stderr.
