# ADR 0024: One visual language: measured tokens, a warm floor, state as colour, text chosen for contrast

## Status

Accepted

## Context

Per-component colour, radius and type values drift apart as pages are added, and contrast checked by eye fails the accessibility gate. The UI needs one design direction whose values are shared and whose contrast is guaranteed by construction.

## Decision

- The UI follows one design direction, written down in the [UI plan](../plans/ui-plan.md): dense and calm, a floor structured by 1 px lines and a muted band instead of shadows and stacked cards, one teal accent, and colour that carries meaning (state, status, schedule layer) and nothing else.
- Its values are design tokens in `Staffora.Blazor/wwwroot/app.tokens.css`, under the names components already read, so a token change moves every component at once.
- The floor (`--color-canvas`) is a warm off-white, `#FAF9F6`; cards, dialogs, the shell's top row and sidebar stay white on it. Every text colour reaches 4.5:1 on the floor.
- Radzen is pointed at the tokens through the bridge at the end of the tokens file (primary, text, lines, input placeholder and disabled colours, header), and buttons read as written, never in uppercase.
- A state's colour comes from `[StateStyling]` and is never themed.
- Where a surface is filled with a state's colour, its text is white or the text colour, whichever reaches 4.5:1 on it; where neither does, the fill is darkened until white does (`ColorContrast`), even where the target design puts white on a bright colour.
- A state-entity dialog's header text is always white, its fill is the state's colour darkened as far as that needs (`ColorContrast.ForWhite`), and its left edge takes the header's fill.

## Rationale

Shared values checked by tests keep every new page in the same visual language and keep the accessibility (axe) gate green, which per-component values cannot guarantee. A warm floor keeps a page from being all white while white surfaces stand out on it.

## Consequences

- A component stylesheet and the shared stylesheets contain no hex colour; they read tokens. `TokenUsageScanTests` enforces this, with an allow-list that states its reasons.
- Every text pair of the token table reaches 4.5:1, and every `[StateStyling]` colour gets a fill and a text colour that reach it together (`TokenContrastTests`). The faint and placeholder greys never carry information.
- Status families have a foreground for text and filled badges, and a dot shade for dots and bars only; white on a dot shade falls short of the contrast requirement.
- Dark mode is not offered. Dark token values are declared under `data-color-scheme="auto"`, which nothing sets.
- The visual baselines change with any token change; they are regenerated, reviewed and committed with the change.

## Related Decisions

- [ADR 0029](0029-left-sidebar-shell.md): the shell's sizes as tokens.
- [ADR 0036](0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md): state chips in the history use the same colours.
