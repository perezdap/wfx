# 0009 — Decoration uses the basic eight ANSI colours

## Context

Decoration (ADR 0008) needs colour to separate machinery from prose. Two colour spaces were available: the basic ANSI set (`30`–`37`, `90`–`97`, plus `1m` bold and `2m` dim), whose hues are resolved by the user's terminal theme, or 256-colour (`38;5;NNN`), which renders identically everywhere.

## Decision

Basic eight only. WFX names a role — red, yellow, dim, bold — and the terminal decides the hue.

The palette is deliberately four values: prose is never coloured; tool lines, the banner, and verbose timings are dim; warnings and the approval prompt are yellow; errors, `failed:`, and `skipped:` are red; the interactive prompt is bold.

Colour is suppressed when a presentation flag is set, when stderr is redirected, or when
`NO_COLOR` is present and non-empty. No `--no-color` flag is added: `--quiet` already promises to
silence ANSI, and `NO_COLOR` is the conventional escape hatch.

"Prose is never coloured" governs hue, not weight. Markdown styling (ADR 0010) uses bold and dim on
the model's own text, which carries no hue and so does not compete with the yellow and red that mean
warning and error.

## Consequences

- WFX does not own a light/dark theme, and needs no terminal-background detection. Choosing 256-colour would require it: a shade legible on a dark background is not legible on a light one, so exact values must come in pairs and be switched at runtime by an OSC 11 query with a timeout and inconsistent terminal support.
- Colours are inherited from a palette the user has already accepted as readable in their terminal.
- Exact rendering varies between terminals. This is intended, not a defect to correct by moving to 256-colour.
