# Sessions are append-only event logs, per-user and workspace-bound

WFX needs conversations that survive process exit so a user can resume work and so an interrupted turn
leaves an account of what it did to the working tree. We decided a session is a versioned,
**append-only JSONL event log** stored **per user** at
`%USERPROFILE%\.wfx\sessions\<session-id>.jsonl`, with the workspace recorded as a field rather than
encoded in the path. Line 1 is a `header` event (`schema_version`, session id, created-at, workspace
root); every later line is one event. A session ID is `yyyyMMddTHHmmssZ-<6 chars>`, which sorts
chronologically as a filename, stays unique under concurrent starts, and is short enough to type.

## Considered Options

- **A single JSON document rewritten per turn**: rejected — every turn rewrites the whole file, and a
  crash mid-write can lose the entire session. Append-only loses at most the last line.
- **Sessions inside the workspace** (`<workspace>\.wfx\sessions\`): rejected on security grounds. That
  directory is inside the tree `write_file` and `apply_patch` are allowed to modify, so the agent could
  rewrite its own history. It also lands in the user's `git status`.
- **Per-user, partitioned by workspace hash** (`sessions\<workspace-hash>\<id>.jsonl`): rejected — a
  hash directory is unreadable to a human, and answering "which workspace was this?" requires opening
  files anyway. A flat store with `workspace` as a queryable field gives the same filtering for free.
- **Per-user, flat, workspace as a field** (chosen).

## Consequences

- The on-disk format ships with this feature and is costly to reverse once users have sessions, so
  `schema_version` is present from the first commit. Readers **must** skip unknown event types within a
  known schema version, and **must** refuse a newer schema version with a clear message rather than
  parsing it optimistically.
- Event types at v1: `header`, `turn_started` (carries the endpoint identity), `message`, `usage`,
  `interrupted`, `error`. `usage` is recorded per model call even though nothing consumes it yet —
  it is nearly free now and is exactly the data the later context-budget and usage-reporting work
  needs, so omitting it would force a migration.
- Durability is **incremental**: events are appended as the turn progresses, not buffered until it
  completes. A turn that ran `apply_patch` and then died is the case a user most needs recorded. The
  cost is that a transcript can end mid-turn, which the reader must repair (see ADR-0004).
- `wfx run` persists by default (`--no-session` opts out). Unbounded growth is accepted for this
  milestone: `wfx sessions` reports total on-disk size and pruning is an explicit opt-in command.
  WFX deleting a user's history unasked is a worse failure than a large directory.
- `resume last` means the most recently updated session **whose workspace is the current workspace**,
  so resuming in the wrong repository is impossible without passing `--id`. Resuming a session whose
  recorded workspace does not match refuses by default and prints the recorded path; `--force` rebinds.
  Transcripts are full of absolute paths and tool results describing one specific tree, and replaying
  them against a different tree makes the model confidently wrong about the user's files.
- Resume takes an advisory exclusive lock and fails fast with "session in use" rather than blocking.
  Append-only makes concurrent writes survive, but two interactive processes appending to one session
  interleave turns into nonsense. Reads (`wfx sessions`) stay lock-free.
