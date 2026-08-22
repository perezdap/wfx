# Provider items are persisted verbatim and downgraded on targeted rejection

WFX runs the OpenAI Responses API statelessly (`"store": false`) and requests
`reasoning.encrypted_content`, so a turn's reasoning state exists only as an opaque blob WFX holds in
`ModelMessage.ProviderItemsJson`. Persisting sessions forces a choice: write those blobs to disk, or
drop them at write time. We decided to **persist them verbatim** and to treat endpoint rejection as a
recoverable path — on rejection, strip provider items using the existing
`ModelSwitchResult.MapConversation` downgrade and retry the request **once**.

The retry is deliberately narrow: only `HTTP 400` with `error.code == "invalid_encrypted_content"`.

## Considered Options

- **Drop provider items at write time**: rejected. Resume would always succeed, but every resumed
  session would silently lose reasoning fidelity — the same degradation a transport switch already
  causes, except unconditional and invisible.
- **Persist, and treat any error as a reason to strip and retry**: rejected. A broad catch would mask
  unrelated failures as reasoning problems and retry requests that cannot succeed.
- **Persist, with a narrow rejection-triggered downgrade** (chosen).

## Consequences

- A resumed session is not guaranteed to replay at full fidelity indefinitely. Documentation review
  found **no documented TTL** for encrypted reasoning content, but issue-tracker evidence (including an
  OpenAI staff comment that the encryption key derives from the signed-in account) indicates the blob is
  bound in practice to the issuing organization and to the same model/provider. The docs do not state
  this as a contract, so WFX must not depend on either replay succeeding or failing.
- Because validity is tied to the issuing endpoint and credential, the per-turn endpoint identity in the
  transcript is part of what makes replay valid. Resuming under a different profile, provider, model, or
  credential should expect the downgrade path rather than be surprised by it.
- `HTTP 404` with "Items are not persisted when store is set to false" is a **different** failure and
  must not be swallowed by this handler. WFX never sends bare item IDs, so that response indicates a bug
  and must surface as an error.
- The downgrade reuses machinery that already exists for mid-session transport switches, so this adds a
  trigger, not a mechanism.
- Interrupted turns interact with replay: on load, a trailing assistant message whose tool calls have no
  matching results is dropped and replaced with a synthetic `interrupted` marker, so the model is told
  the previous turn was cancelled instead of seeing a silent gap. Both Chat Completions and Responses
  reject unmatched tool calls, so this repair is required, not cosmetic. Synthesising fake `"cancelled"`
  tool results was rejected: it puts words in the tool's mouth and is indistinguishable, later, from a
  tool that really said that.
