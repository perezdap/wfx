# 0010 — Markdown rendering is a hold-back scanner with a closed feature list

## Context

Decoration includes styling the model's markdown, but a construct cannot be styled until it is seen whole, and WFX renders a stream of provider deltas. A line buffer — accumulate, flush on `\n` — is the simple approach, but models emit unwrapped paragraphs as a single long line, so prose would appear a paragraph at a time. That trades away live token streaming, the most visible responsiveness signal a coding agent has.

## Decision

A hold-back scanner in `Wfx.Cli` (`MarkdownStreamWriter`, taking a `TextWriter`): bytes stream through continuously, withheld only from the first unresolved inline marker until it closes or the line ends. Line-level constructs are detected at the line start, so they cost nothing.

The feature list is closed at **bold, inline code, ATX headings, bullet markers, and fence
lines**.

A fenced block is the one construct the scanner must track rather than merely style: inside a
fence every character is code, so `**p` and a stray backtick have to survive byte for byte. The
fence line itself is dimmed and keeps its language label; the body is never scanned.

When decoration is suppressed the writer is a pass-through. Markers are the model's text, not
WFX's, so with nothing to style there is nothing to consume either — `## Title` stays `## Title`
rather than becoming an unstyled `Title`.

## Considered options

A line buffer was rejected for the streaming cost above. Rendering nothing was viable and remains the fallback if the scanner proves fragile.

## Consequences

- Token-by-token streaming survives; a stall is possible only inside an actual `**…**` or `` `…` ``.
- Bold and dim share the SGR 22 closing sequence, so a span closing inside a heading reopens the heading's weight afterwards. Inline markers inside a bold span are emitted literally as part of that span rather than scanned recursively; nesting any further weight would need a depth counter, which the closed feature list avoids.
- Styling never touches stdout (ADR 0011), so a redirected `> notes.md` receives the model's raw markdown source — the right content for a `.md` file.
- The exclusions are deliberate, not gaps:
  - **Italics**: `*foo*` collides with literal asterisks, `_foo_` with `snake_case` identifiers a coding agent emits constantly. Poor precision for low value.
  - **Tables**: require every row before the first can be drawn, plus display-width measurement for CJK and emoji.
  - **Fenced blocks as boxes**: the fence is dimmed and its language labelled, and the body is passed through untouched; borders need terminal width and resize handling.
  - **Links, footnotes, blockquotes, task lists, setext headings**: not worth their parser.
- Adding any excluded item reopens this ADR. The comparison point is fx, whose equivalent package is ~5,000 lines of Zig plus a 30 KB display-width module.
