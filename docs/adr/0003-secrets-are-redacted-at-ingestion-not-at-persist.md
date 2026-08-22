# Secrets are redacted at tool-result ingestion, not on the persistence path

Persisting transcripts moves tool output — file contents, PowerShell output — from process memory into
a plaintext file under `%USERPROFILE%` that outlives the run, so sooner or later a session file will
contain whatever key-bearing output a tool read. We decided to mask known secret shapes **once, at
tool-result ingestion**, so the same text is what the model sees, what the agent holds in memory, and
what is written to disk; and to additionally create `%USERPROFILE%\.wfx\sessions\` with an ACL granting
only the current user. Matching is **prefix-anchored only** — environment-style assignments
(`API_KEY=`, `PASSWORD=`, `DATABASE_URL=`, …), inline token shapes (`sk-`, `github_pat_`, `ghp_`,
`AKIA…`, `Bearer `), and basic-auth URLs — never entropy or length heuristics.

## Considered Options

- **Store verbatim, rely on directory ACL alone**: rejected. It was the initial proposal, and a survey
  of the field argued against it: of five harnesses examined (fx, OpenAI Codex, Aider, OpenHands,
  Claude Code) only fx both redacts and restricts permissions, and it is also the closest comparable
  design. Codex and Aider persist tool output unfiltered; Claude Code's docs state that OS file
  permissions are the only protection.
- **Redact at write time, on the persistence path**: rejected, and this is the substantive decision. It
  makes the transcript disagree with what the model actually received, so the authoritative record of a
  session becomes a record of something that never happened. Redacting at ingestion means there is only
  ever one version of the text, and the divergence cannot occur.
- **Entropy-based or length-based secret detection**: rejected — unreliable in both directions. It
  misses real secrets and corrupts legitimate content; a filename like `ask-turn-default-auto.txt`
  must not be mangled by a rule looking for `sk-`.
- **Substituting only known configured secret values** (the OpenHands approach): rejected as the sole
  mechanism — it catches only secrets WFX was told about, and the common case is a tool reading a
  `.env` file WFX has never seen. Worth combining with prefix matching later.

## Consequences

- The model no longer sees secret values that appear in tool output. This is a behavior change to the
  agent loop, not only to storage, and it is the point of the decision.
- Redaction is lossy and irreversible: a tool result whose secret was masked cannot be recovered from
  the transcript. Accepted deliberately — the alternative is a durable plaintext credential store.
- WFX's existing child-process environment scrubbing stays. It prevents the leak rather than papering
  over it, and is the stronger of the two mechanisms.
- The existing diagnostics redaction is for *display* and is unrelated to this path; the two must not be
  conflated or merged.
- False negatives are expected. Session files remain sensitive by nature, must be documented as such,
  and the ACL — not the matcher — is what makes them acceptable to write.
