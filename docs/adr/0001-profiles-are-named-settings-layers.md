# Profiles are named settings layers, expanded in place

WFX needs several preconfigured model endpoints switchable per invocation and mid-session. We decided that a **profile** is a named settings layer stored under `"profiles"` in the existing config files and expanded in place inside its file's layer when selected (`--profile` > `WFX_PROFILE` > `"profile"` key; project overrides user). This reuses the existing layered merge machinery exactly — profiles carry the same keys as top-level config, and precedence, credential-suppression, and partial-override behavior all fall out unchanged.

## Considered Options

- **Separate profile files** (`%USERPROFILE%\.wfx\profiles\<name>.json`): rejected — adds file discovery and a second parsing path, and project-scoped profiles would need their own convention.
- **A registry object with its own merge rules** (profiles as first-class structs resolved after layer merging): rejected — duplicates the merge logic and creates two competing precedence stories.
- **Named layers expanded in place** (chosen): a selected profile is indistinguishable from top-level values in its file; no new merge semantics.

## Consequences

- The config schema shape ships with this feature and is costly to reverse once config files exist in the wild — future profile features must stay compatible with in-place expansion.
- Same-named profiles in user and project files merge key-by-key (project wins), which is the layer behavior, not a special case.
- A profile can never select another profile (no recursion), and env/CLI values still override the selected profile.
- The reserved `protocol: anthropic_messages` value errors with "not implemented yet"; native Anthropic Messages transport is deferred — Claude is served day one by the Anthropic OpenAI-compatibility shim via the new `anthropic` provider preset.
