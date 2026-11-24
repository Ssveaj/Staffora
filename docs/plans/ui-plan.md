# UI plan

The target look and feel of Staffora.Blazor, and the steps that take the current UI there. It serves [PRODUCT.md](../../PRODUCT.md) and runs beside the [build plan](build-plan.md); the Definition of done in [.github/copilot-instructions.md](../../.github/copilot-instructions.md) applies to every step unchanged.

**Status: complete.** U.0 to U.17 are done; U.18 (dark mode) is not built (P8). The deviations still open are collected in [Open deviations](#open-deviations) at the end.

## Why

The UI grew step by step inside feature steps and was never compared with a target design. The result works but reads as a generic admin template: an indigo header, a 300 px sidebar, every page a stack of rounded cards, uppercase filled buttons, labels above native selects.
The coordination workspace shows the cost most: a scrolling page with a sidebar, week cards and a Monday-to-Friday grid, where a coordinator needs one full-screen planning surface with an hour timeline. This plan fixes one design direction, gives it measured tokens, and makes every step prove itself against the target design side by side.

## Design principles

1. **Dense and calm.** Small type for data (0.8125 rem in tables and toolbars), generous white, one accent. Colour carries meaning (state, status, schedule layer), never decoration.
2. **White floor, thin lines.** The page is white; structure comes from 1 px borders and a muted band (`--color-surface-muted`), not from shadows or stacked cards.
3. **State is colour.** A state entity's dialog is framed in its state's colour (header, side column, footer tint); a transition button carries its target state's colour as an edge. State colours come from `[StateStyling]` and are never themed.
4. **The workspace is a tool, not a page.** Coordination fills the viewport under the top bar; panels scroll on their own, the page never does.
5. **Navigation on top, work below.** A 60 px top bar with section menus leaves the full width to the work. (The navigation was later changed to a left sidebar that folds to a 64 px rail, beside a 56 px top row with the breadcrumb; [ADR 0029](../adr/0029-left-sidebar-shell.md).)
6. **Same rules as today.** Lists find, rows open dialogs and carry no actions; state entities live only in StateEntityDialogs ([ADR 0009](../adr/0009-state-entity-dialog-only-and-workflow-governed-attributes.md)); comments stay one thread per audience ([ADR 0010](../adr/0010-comment-threads-per-audience.md)). The look changes, the behaviour contracts do not.
7. **Accessible by construction.** Every text/background pair reaches 4.5:1; where the target design would not, the UI deviates and says so.

## Design tokens

Token names stay those of `Staffora.Blazor/wwwroot/app.tokens.css` (components keep reading the same properties); values change, and the names marked *new* are added. Values are those of the target design.

### Colour

| Token | Light value | Role |
|---|---|---|
| `--color-accent` | `#0f766e` | Links, primary buttons, active nav, focus, selected (5.5:1 on white) |
| `--color-accent-strong` | `color-mix(in srgb, #0f766e 85%, black)` | Hover and pressed accent |
| `--color-accent-soft` | `#e8f5f2` | Selected row, active nav item, avatar, hover fill |
| `--color-accent-tint` *new* | `color-mix(in srgb, #0f766e 8%, white)` | Week badge, chip on, subtle selection |
| `--focus-ring` | `0 0 0 3px color-mix(in srgb, #0f766e 35%, transparent)` | Focus |
| `--color-on-accent` | `#ffffff` | Text on accent |
| `--color-text` | `#0f172a` | Body text, titles |
| `--color-text-muted` | `#475569` | Nav labels, secondary text (7.6:1) |
| `--color-text-subtle` | `#64748b` | Meta, captions, resting field labels, table heads (4.8:1) |
| `--color-text-faint` *new* | `#94a3b8` | Decorative icons, disabled; never text that informs (2.6:1) |
| `--color-text-placeholder` *new* | `#b1bac6` | Placeholders only |
| `--color-canvas` | `#ffffff` | Page floor (no grey canvas) |
| `--color-surface` | `#ffffff` | Cards, dialogs, panels |
| `--color-surface-muted` *new* | `#f8fafb` | Muted bands, table heads, toolbars, list footers |
| `--color-surface-sunken` | `#f1f5f9` | Workspace gutters, weekend cells, off-hours track |
| `--color-surface-hover` | `#f8fafc` | Row and menu hover |
| `--color-border` | `#e2e8f0` | All 1 px lines |
| `--color-border-strong` | `#cbd5e1` | Inputs at rest, work-pattern edges |
| `--color-divider-faint` *new* | `#eef2f6` | Lines inside panels |
| `--color-success` / `-soft` / `-border` / `-dot` | `#15803d` / `#f0fdf4` / `#bbf7d0` / `#16a34a` | Status family |
| `--color-warning` / `-soft` / `-border` / `-dot` | `#b45309` / `#fffbeb` / `#fde68a` / `#f59e0b` | Status family; impersonation |
| `--color-danger` / `-soft` / `-border` / `-dot` | `#b91c1c` / `#fef2f2` / `#fecaca` / `#dc2626` | Status family; badge count |
| `--color-info` / `-soft` / `-border` / `-dot` | `#1d4ed8` / `#eff6ff` / `#bfdbfe` / `#2563eb` | Status family |
| `--color-neutral` / `-soft` / `-dot` *new* | `#6b7280` / `#f8fafc` / `#d1d5db` | Status family |
| `--color-chrome` *new* | `#ffffff` | Top bar |
| `--color-chrome-platform` | `#1f2a44` | Platform console bar (open point P2) |

Filled status badges and buttons take the `fg` shade (`--color-success` etc.), never the `-dot` shade, which carries white text below 4.5:1. `-dot` is for dots, bars and lines only.

**Schedule layers** (*new*, not themed): `--sch-track` `#f1f5f9` with a 45° hatch of `rgba(148,163,184,.13)` (5 px gap, 1 px line) for off-hours; `--sch-work` `#ffffff` with `--sch-work-edge` `#cbd5e1` for the work-pattern window; `--sch-absence` `#f59e0b` / fg `#92400e` / bg `#fffbeb`; `--sch-leave` `#a1887f` for absence blocks; `--sch-now` `#e91e63` (now line, today label); `--sch-unassigned` `#2f3743` (To staff label column) and `#37414f` (an uncovered time bar);
`--sch-band-staff` = accent, `--sch-band-muted` `#a7b1bd`. Time bars take the assignment state's colour from `[StateStyling]`; their text is `#ffffff` or `--color-text`, whichever reaches 4.5:1 (see Accessibility).

### Type

Family: `system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif` (unchanged); Radzen controls read it through the bridge.

| Token | Value | Use |
|---|---|---|
| `--font-size-2xs` *new* | 0.6875 rem | Timeline, mini calendar, toolbar tools, badges |
| `--font-size-xs` | 0.75 rem | Meta lines, chips, table heads |
| `--font-size-dense` *new* | 0.8125 rem | Table cells, toolbar title, list tools |
| `--font-size-sm` | 0.875 rem | Secondary body, comments |
| `--font-size-md` | 1 rem | Body, field values |
| `--font-size-lg` | 1.125 rem | Dialog title |
| `--font-size-xl` | 1.25 rem | Panel titles |
| `--font-size-2xl` | 1.5 rem | Section titles, date block day |
| `--font-size-3xl` | 2 rem (was 1.875) | Console title |
| `--font-size-4xl` | 2.5 rem (was 2.25) | System page title |
| `--line-height-tight` / `snug` / `normal` / `relaxed` | 1.2 / 1.3 / 1.5 / 1.6 | |
| Weights | 400 / 500 / 600 / 700 | |

Composite styles: page title 1.05 rem/600; eyebrow (breadcrumb) 0.68 rem uppercase, letter-spacing 0.06 em; band title 0.72 rem/700 uppercase, 0.09 em; home title `clamp(1.35rem, 1.1rem + 0.8vw, 1.75rem)`/700, letter-spacing −0.015 em; figure value 1.75 rem/650 (hero 3 rem/700). Buttons are never uppercase (`text-transform: none; letter-spacing: normal` on `.rz-button`).

### Spacing, radii, elevation, density

- **Spacing:** keep the 4 px scale (`--space-1` 0.25 rem … `--space-12` 3 rem); it already holds every measured step (0.25, 0.5, 0.75, 1, 1.5, 2, 3 rem).
- **Radii:** `--radius-sm` 4 px (badges, chips, bars), `--radius-md` 6 px (inputs, buttons), `--radius-lg` 8 px (was 10; list containers, dense panels), `--radius-xl` 12 px (was 14; content cards, menus), `--radius-full` 999 px (pills, account trigger, comment composer).
- **Elevation:** `--shadow-1` `0 1px 2px rgba(15,23,42,.06)`; `--shadow-2` `0 1px 3px rgba(0,0,0,.1), 0 1px 2px rgba(0,0,0,.06)`; `--shadow-3` `0 12px 32px rgba(15,23,42,.14)` (panels); *new* `--shadow-pop` `0 12px 32px rgba(15,23,42,.16)` (menus, hover cards); *new* `--shadow-drawer` `-4px 0 24px rgba(0,0,0,.18)`. Cards and lists carry no shadow; only floating things do.
- **Density** (*new*): `--control-height-sm` 30 px, `--control-height-md` 34 px, `--control-height-lg` 40 px; `--app-header-height` 60 px; list toolbar search 32 px, filter chip 28 px, list tool 30 px, table head 34 px, cell padding 7 px × 10 px; workspace tool 22 px, panel head 26 px, timeline head 30 px, group head 30 px, row ≥ 26 px, day strip 18 px, name column 132 px (steps of 28, minimum 84).

### Dark mode

Not part of the target design; Staffora's own.
Dark tokens are declared in U.1 under `@media (prefers-color-scheme: dark)` but stay switched off until U.18 (open point P8): canvas and surface `#0f172a`, surface-muted `#131c2f`, sunken `#1e293b`, hover `#1a2438`, border `#334155`, border-strong `#475569`, text `#e2e8f0`, muted `#cbd5e1`, subtle `#94a3b8`, accent text `#5eead4` (accent fills stay `#0f766e` with white text), accent-soft `#134e4a`, status foregrounds `#4ade80` / `#fbbf24` / `#f87171` / `#60a5fa` with soft fills at 18 % over the surface.
State colours do not change.

## The shell

> Superseded by [ADR 0029](../adr/0029-left-sidebar-shell.md): a full-height left sidebar (the brand with "Collapse menu", the navigation's sections as labelled groups, folding to a 64 px icon rail remembered per browser, a drawer below 900 px) beside a 56 px top row with the breadcrumb and the account trigger; the page header band of U.3 is gone too, the top row carrying the page's title (its `h1`), help and actions. The text below records the top bar U.2 built.

Replaces the sidebar shell of [ADR 0023](../adr/0023-one-app-shell-two-menus.md) when the shell follows (open point P1); the session still chooses the menu.

- **Top bar:** 60 px, white, 1 px bottom border, `--shadow-1`, sticky, padding 0 1.25 rem, gap 1.5 rem. Skip link first, as today.
- **Left:** brand (icon + "Staffora"), then one dropdown per navigation section of `AppNavigation` (tenant: My work, Staffing, Administration; portal: its sections as plain text links; console: Platform, Platform health). Each is icon + label + chevron; the section holding the current page is accent text with a 2.5 px accent underline. The panel: white, radius 12, `--shadow-pop`, items icon + label at 2.5 rem, hover accent-soft.
- **Collapse:** when the labels do not fit, they hide from the rightmost section first and leave the icon (with its accessible name); at 768 px and below one "Menu" button (hamburger + chevron) opens every section stacked.
- **Right:** the bell (36 × 36, radius 9; badge 16 px, `--color-danger-dot`, white 10.5 px/800, 2 px white ring), then the account trigger: a 36 px pill with border, a 28 px initials avatar (accent on accent-soft), the user's name (0.84 rem/700) over the tenant's name (0.68 rem, subtle), a chevron. Its panel (19 rem, radius 12): "Signed in as" eyebrow, name, e-mail, tenant; Profile settings; Sign out.
- **Impersonation:** the account trigger turns warning (border `--color-warning-dot`, fill `--color-warning-soft`, avatar on `#fef3c7`), and its panel opens with a "Running as …" box and an "End impersonation" button. The always-visible warning trigger replaces today's header pill and End button.
- **Search:** not built (open point P4); nothing stands in its place.
- **Page frame:** no gutter and no card around the page; every page except the workspace opens with the page header band (Patterns).

## Patterns

### Page header

A white band under the top bar, padding 0.55 rem 1.25 rem 0.6 rem, 1 px bottom border. Line one: the breadcrumb as an eyebrow (links accent, separator "/" at half opacity). Line two: the title (1.05 rem/600), the list's count (0.8125 rem, subtle, tabular) and a help button (`?`, 18 px, aria-label "About this page") whose popover holds today's description sentence;
actions on the right (the primary one a filled accent button with an icon, 34 px). The page body sits on the white floor with 1.5 rem side padding (1 rem at 390 px). `PageHeader` keeps its parameters; `PageChromeScanTests` keeps holding every page to it.

### List pages

One bordered container (radius 8, no shadow) on the white floor.
- **Toolbar row** (padding 0.5 rem 10 px): quick-filter segmented pills with counts ("All 7 | Active 7"); a search box (32 px, search icon, placeholder); filter chips (28 px pills with a dashed border, "Status: All ▾"; on when set: accent text on accent-tint, solid border, an × to clear); spacer;
  "Group: None ▾" and "Sort: Name ▾" tools (30 px, 0.75 rem, icon 18 px). Each chip opens a popover listbox (radius 8, `--shadow-pop`). The URL state of [the list view](../features/lists-and-dialogs.md#list-view) is unchanged.
- **Table:** head 34 px on surface-muted, 0.75 rem/600 subtle; cells 7 px × 10 px, 0.8125 rem, 1 px bottom lines; the lead cell (36 px) holds an 8 px state strip at the row's left edge and the expand chevron; the name cell is two lines (0.875 rem name, 0.75 rem subtle second line, optional 32 px avatar or type icon); status as a dot (8 px, `-dot` shade) and a label, not a pill; a row that waits for the reader takes a 6 % warning tint; hover surface-hover.
- **Footer:** "1–7 of 7" left; "Rows per page [25]" and the pager right, 0.75 rem.
- **Phone:** rows become cards, filters fold behind "Filters (n)" (as today).
- **Card presentation** (*new*, for a person's own work): the same `ListView` renders rows as cards: a date block (weekday 0.6875 rem uppercase, day 1.5 rem/700, month), title 1 rem/600 with a deadline badge, an icon line of time and place, a subtle hint line, a chevron; a 3 px left edge in the state's colour.

### StateEntityDialog

- **Frame:** Radzen dialog, radius 4; width from content up to 1180 px; at 390 px full screen.
- **Header:** filled with the current state's colour; entity icon (1.7 rem) and title (1.125 rem/600) in white or `--color-text`, whichever reaches 4.5:1 on that colour; on the right a history button and the close button (36 × 36), both icon-only with `aria-label`.
- **State pill:** hangs on the header's bottom edge at the right: 1.4 rem high, fill `color-mix(state 80%, white)`, 0.75 rem uppercase state name, a divider, a clock and the last step's time ("today 13:48"). Its hover card (23 rem, radius 12, `--shadow-pop`): a 3.25 rem state badge, title and description, Changed/Created facts with who, the last steps, "Open full history". This is today's pill and hover card (plan step 4.2) restyled.
- **Tabs:** under the header, text only, the selected one underlined and labelled in the state's colour.
- **Side column:** right of the body, 22 rem, on `color-mix(state 18%, white)`; its side tabs are folder tabs (3 rem × 2 rem, icon only with `aria-label`, radius 8 on top; behind: state at 14 %; open: the column's own tint, opening through a baseline of state at 38 %), with count badges. Below a dialog width of 56 rem the column moves under the body at 32 rem high.
- **Footer:** on `color-mix(state 7%, white)`, no top border, padding 10 px 1.5 rem. Leading group (steps that leave the flow) left, Trailing group right, ending with the step forward. A transition button: white, 1 px border, a 3 px left edge and the icon in the target state's colour, radius 6, 600 weight, one line, `--shadow-1`; hover fills the target colour at 8 %. A blocked step stays greyed with its reasons on hover (as today).
- **Form dialogs** (confirm, prompt, error, message, forms): an accent-filled header with a white title and close; body padding 1.25 rem; footer right with a neutral Cancel and the primary action.

### Forms and fields

- Radzen outlined form fields: 1 px `--color-border-strong`, radius 4, floating label on the border; the label at rest in `--color-text-subtle` (deviation: the target design rests it at placeholder grey, below 4.5:1), floated 0.75 rem; focus 1 px accent plus a 1 px inner ring; inside a state dialog the focus and the selected tab follow the state colour.
- Two fields per row from 640 px of body width, one below; helper text under the field in 0.8125 rem subtle with an info icon; a section title as a 0.75 rem uppercase eyebrow.
- Drop-downs everywhere are Radzen drop-downs (no native `select` in filters or forms); disabled values in `#748194`, darker than the resting label.
- Required: an asterisk in the label; a refused save shows the field message under the field in danger.

### Comment block

In the side column ([ADR 0010](../adr/0010-comment-threads-per-audience.md)): a heading line with the audience's icon and name and the count; day dividers (0.75 rem, centred, hairline at 14 %; the "New" divider in warning); bubbles up to 85 % (520 px) wide, 0.875 rem, 0.75 rem meta line (name, time). Others' bubbles left, radius 3 14 14 14, on `--color-surface-sunken`; one's own right, radius 14 3 14 14, on `color-mix(accent 22%, white)`;
a step's comment carries a chip with the step's icon and a 3 px left edge in its colour. Composer at the bottom: a white pill (radius 1.4 rem) with a round send button, "Enter sends · Shift+Enter starts a new line" under it.

### Empty, loading, toasts

- **Empty state:** centred; a 64 px tile (radius 12) on accent-soft with a 24 px accent icon, a title (1.25 rem/500), one sentence (0.875 rem subtle), at most one primary button. Inside a panel: a dashed 1 px box with one muted line.
- **Loading:** a dialog loads into a centred spinner and one muted line (min height 16 rem); a page shows the boot spinner before the circuit; lists keep their rows and dim to 60 % while a new page loads; no skeleton screens.
- **Toasts:** removed. There are no toasts: a step's outcome is said in its dialog, and an error opens the error dialog (`ShowErrorAsync`).
- **Reconnect:** a small banner in the bottom corner (spinner, one line, Reload), never a full-screen overlay.
- **Notification panel:** a drawer from the right (430 px, `--shadow-drawer`), title "Notifications" with "All read" under it, Unread / All, rows as today.

## Per-view targets

### Coordination workspace

Per D1, `/coordination` becomes a full-viewport tool under the top bar: `height: calc(100vh - var(--app-header-height))`, no page scroll, no page header band, `--color-surface-sunken` between panels.

- **Grid:** left panel 232 px | splitter 5 px | centre | splitter 5 px | right panel 268 px; at 1400 px and below 200 px and 224 px. Each side panel has a 26 px head (0.6875 rem/700 uppercase title, chevron) and collapses to a 26 px rail with its title written vertically.
- **Left: calendar rail.** A month picker ("2026 – November") and a Today button; mini months stacked down the rail (as many as fit, the rest scroll): caption "NOV 2026", weekday heads (0.625 rem) and a week-number column (a click selects the week), day cells ≥ 1.35 rem with 0.6875 rem numbers, weekends on sunken, today filled accent with white bold, the selected day or week banded in accent-soft.
  Under each day number, small bars mark that day's slots: uncovered (danger dot), at risk (warning dot), waiting for a pick (`#8e24aa`), covered (success dot);
  a legend sits at the rail's foot. This replaces the period-week strip.
- **Centre: toolbar** (34 px, surface-muted): the shown day ("Tuesday 3 November 2026", 0.8125 rem/600) and a week badge ("w45", 0.6875 rem/700, accent on accent-tint); "+ New job" (small primary, with `JobsCreate`); spacer; a Schedule | List segmented control; ‹ ›; Day | Week segmented; Auto (fit), −, +, Now; Plan (amber: warning text on warning-soft). Tools are 22 px, 0.6875 rem/600, radius 4; "on" is filled accent.
- **Centre: hour timeline.** A 30 px time head: a corner over the name column ("8 staff members", name-column width tools) and hour ticks with labels (0.6875 rem); hours outside the tenant's working day shaded. Day mode shows one day's hours (default 07–18, Auto fits the day's times);
  Week mode shows Monday to Sunday on the same axis with day labels at each day's start and weekends shaded. Rows are grouped (open point P6): the **To staff** group first (label cells `#2f3743` with white text, the uncovered times as `#37414f` bars), then one group per organization unit with staff members. A group head is 30 px with a caret, the title (0.95 rem/700), a count chip and a hint ("2 without times hidden"), and a 3 px left band.
  A staff row is ≥ 26 px: name (0.8125 rem/600) and a second line (work hours or a qualification, 0.6875 rem subtle) in the 132 px name column with a 3 px band;
  the track is hatched off-hours, the work-pattern window white with edged sides, absence a `--sch-leave` block with its label, times radius-4 bars with sticky captions (0.6875 rem/500). A 2 px `--sch-now` line crosses head and rows. A time's hover card (radius 12, `--shadow-pop`): job, customer, unit, need, when, staffing ("1 of 1: Kim Berg"), the availability's warning in danger.
- **Centre: day strip.** 18 px at the foot: « previous week | Monday … Sunday | next week »; weekends on surface-muted, past days muted, the shown day as an accent-soft pill, today's label in `--sch-now`, a thumb for the visible range that drags; a click shows that day.
- **Centre: List mode.** The Schedule | List switch shows the slots list (today's centre `ListView`) for the shown day or week.
- **Right: needs attention.** Bands ("Urgent 4" in danger, 0.6875 rem uppercase on surface-muted) and rows: a state icon and a relative time ("now", "22h") left, the customer and job (0.75 rem/600) over the slot (0.75 rem subtle, one line), the date and time right; hover surface-muted; empty as a dashed box.
- **Planning mode:** removed. The workspace has no planning mode: every staffing step goes through the slot's and the job's Staffing view.
- **Kept:** one read and a fixed number of queries, one availability rule and its parity test, live refresh from the change channel, URL state for the shown day, mode, zoom and panels.

### Staff pages and My work

- **My assignments:** a left section nav ("To answer: Requests (1)", "My assignments: Upcoming, Answer sent, Earlier", each with an icon and count) and the card presentation of the list; a card opens the SlotAssignment dialog, whose footer holds Accept and Decline (open point P5). Phone: the section nav becomes two columns above the cards.
- **My schedule:** a month of day rows on one hour axis (replacing the workspace timeline in person mode): working time, assignments and absence in three layers, each day's notes, the month moved by ‹ Today › ([Staff pages](../features/coordination-and-homes.md#staff-pages)).
- **My work (home):** see Homes.
- **Request dialog:** the StateEntityDialog with the assignment's facts in sections (When, Where, Who, About the job) with icons, and the staff thread in the side column ("Questions to the coordinator").

### Homes

Bands instead of cards: a white head band (eyebrow with the tenant's name or the area, the home title, on the right the date, "Updated hh:mm" and a refresh button), a white "To do" band (figures: value 1.75 rem/650 over a 0.8 rem label, the leading one 3 rem/700 with an accent underline;
a check list on the right, each line a check-circle icon, a bold name and a subtle "nothing to handle"), and a muted "Situation" band running to the window's foot (columns of figures and short lists). Each band's content is at most 82.5 rem wide and centred.

### The portal

The top bar with text-only section links and no section icons; content in a centred column of 54 rem. "My orders" is a list page; order details: a back link, the title (1.5 rem/700) with a filled state badge, a meta line (0.8125 rem subtle), the Cancel step as an outlined danger button, times as expandable cards (radius 8: date bold and time subtle, title and place, state badge, chevron;
open: an info notice, then field cards with icon, 0.75 rem label and 1 rem value), and the customer thread in the order dialog's side column.

### The platform console

The same shell and patterns (bar colour per P2). Overview: an eyebrow "Platform", the title (2 rem/700) and a welcome line; an info card (icon tile on accent, who is signed in and what the console is for); two columns of cards: Impersonate a user (primary button, recent sessions), Errors in the log (two figures and the newest rows), Tenants by state (figures linking to the filtered list). The tenant list is a standard list with the tenant tree indented and a type icon per row;
the tenant console keeps its tabs as underline tabs with icons.

### Settings

Three columns: a section nav (240 px; uppercase group headings 0.72 rem/700; items with icons; the open one accent-soft with accent text, radius 8), the panel (bordered card, radius 12, padding 1.5 rem: title 1.25 rem/700, one subtle sentence, fields up to 32 rem wide, Save as a primary button), and no quick-help column (open point P7). At 768 px and below the nav becomes a select-like list above the panel.

## Responsive rules

| Width | Rules |
|---|---|
| 1500 px | All section labels in the top bar. Workspace panels 232 and 268 px, about 14 hours visible in Day mode. Dialogs up to 1180 px with the side column beside the body. Lists show every column. |
| 1280 px | Top bar labels collapse from the right as needed (icons keep their names). Workspace panels 200 and 224 px (the 1400 px rule). Dialogs keep the side column while their width is ≥ 56 rem. Lists drop columns marked optional. |
| 390 px | Top bar: brand, "Menu", bell, avatar only. Page header: breadcrumb and title, actions wrap under. Lists become cards; filters fold. Dialogs full screen, side column under the body, footer sticky and wrapping. Workspace below 1000 px: the timeline first (≥ 60 vh), then the panels (≤ 46 vh each), rails horizontal (30 px); the toolbar wraps and the day strip stays. No horizontal page scroll anywhere. |

Breakpoints: 768 px (phone shell, cards), 1000 px (workspace stacks), 1400 px (narrow workspace panels), and a 56 rem container query in dialogs.

## Accessibility

The existing rules stay and gate every step: axe on every `gotoReady` page and the accessibility sweep fail on serious or critical violations; `IconButtonScanTests` requires an `aria-label` on every icon-only button (folder side tabs, timeline tools, rails, close and history buttons included); every form field names its input. On top:
- **Contrast:** every token pair used for text reaches 4.5:1 (large text 3:1); `--color-text-faint` and `--color-text-placeholder` never carry information. A state-coloured surface with text (dialog header, time bar, filled badge) picks white or `--color-text` by WCAG contrast from the hex in `[StateStyling]`; where neither reaches 4.5:1 the fill is mixed with black until white does. A unit test runs that choice over every registered state colour.
- **Workspace:** the timeline is a labelled region with a grid role (rows are staff members, cells are times as buttons with full accessible names: job, need, time, state); the calendar rail is a grid of buttons; the panels' collapse buttons say what they collapse and whether expanded; drag keeps its click-and-keyboard equivalent ([ADR 0019](../adr/0019-coordination-workspace.md)); reduced motion turns off panel and zoom animation.
- **Top bar:** each section is a menu button (`aria-haspopup`, `aria-expanded`), arrow keys move inside the panel, Escape closes and returns focus.

## Design choices

- **D1 Coordination workspace rebuild.** Full viewport, three columns, a left calendar rail, an hour timeline with Day and Week modes, a toolbar, resource groups, a bottom day strip, and the colours above. ADR 0019 changes with step U.4: its rules (one availability rule, staged plans applied as workflow transitions, live channel, fixed queries, keyboard equivalent) stay; "a staff member × week board" and "a period-week navigator" become the hour timeline and the calendar rail.
- **D2 Tokens before views.** Token values change in place under today's names, so every component moves at once and later steps restyle structure only.
- **D3 Behaviour contracts unchanged.** No step changes a service, a workflow, the URL state or a permission check; a step that needs one stops and asks.
- **D4 Comparison is the gate.** Every step ends with side-by-side screenshots and a written deviation list (the comparison rule below).

## Out of scope

Global search, the guide and quick-help panels, tenant-chosen colours, avatars other than initials, time reports, economy and every other area outside [PRODUCT.md](../../PRODUCT.md). Text: the UI stays English ([ADR 0005](../adr/0005-code-style-and-english-ui.md)), so text differences are never deviations to fix.

## Phases

**Comparison rule.** A step's **Done when** names its compare set. For each listed view: one side-by-side PNG (ours left, the target design right, the same width and state where the target design has it, otherwise the nearest width named in the file name) under `output/playwright/<area>/compare/`, made with the U.0 harness from our journey screenshot and the target screenshot named for that step.
Every difference still visible is either fixed or written, with its reason, into the step's **Deviations** line when it is ticked (reasons: English text, product scope, accessibility, a design choice or open point of this plan). A difference with neither fails the step. Each step also re-reviews and commits the showcase baselines (`tests/e2e/baselines/`) it changes, and runs the Definition of done.

### Phase U-A: Foundation

- [x] **U.0 Comparison harness.** A committed script (`scripts/compare-screens.mjs`, Playwright's Chromium, no new dependency) that takes two PNGs and writes one side-by-side PNG with a caption bar per side ("Staffora.Blazor" / "Target design") and the width of each; both paths come from the command line. The E2E README documents it.
  - **Done when** the script turns our `lists/01-users-searched-detail-open-1280.png` and the users list target screenshot into `output/playwright/lists/compare/users-1280.png`, a test in `Staffora.Test/E2E/` runs it on two fixture images and checks the output's size, and the deviation list for that pair is written as a sample in the step's tick note.
  - **Built:** `scripts/compare-screens.mjs`; `CompareScreensTests` draws two PNGs, checks the output's size and the refusal; documented in `Staffora.Test/E2E/README.md`.
  - **Deviations:** None for the harness.
  - **Verified by:** `CompareScreensTests`.
- [x] **U.1 Tokens and theme.** The values of Design tokens in `app.tokens.css` and `app.typography.css`; the new tokens; the Radzen bridge extended (`--rz-border-color`, `--rz-input-placeholder-color`, `--rz-input-disabled-color`, header tokens, `.rz-button` without uppercase, the resting floating label in subtle); the dark block declared but inactive; `--color-canvas` white.
  A scan (`TokenUsageScanTests`) refuses hex colour literals in component stylesheets outside an allow-list; a unit test (`TokenContrastTests`) checks every text pair of the token table and the contrast choice for every `[StateStyling]` colour.
  - **Done when** both tests pass, the showcase baselines are re-reviewed and committed, the accessibility sweep is green, and compare set `showcase`: users list, settings General, a state-entity dialog, at 1280 px, with deviations listed.
  - **Built:** Token values and the new tokens in `app.tokens.css`, the Radzen bridge, `app.css` template colours on tokens; read-only fields' labels muted; warning text on tinted fills a deeper shade of the warning foreground, which reaches only 4.49:1 on the accent tint; `ColorContrast` with `TokenContrastTests` and `TokenUsageScanTests`; [ADR 0024](../adr/0024-visual-language-tokens-and-state-colour.md).
  - **Deviations:** The floated label rests at the subtle text colour and `--rz-input-placeholder-color` is the subtle token, not the placeholder grey; a read-only field's label is muted, not subtle; structure differences in the compare set belong to later steps; text differences (P9).
  - **Verified by:** `TokenUsageScanTests`, `TokenContrastTests`.

### Phase U-B: Shell and page frame

- [x] **U.2 Top-bar shell.** Per The shell, if P1 is answered "the shell follows"; otherwise this step only restyles today's sidebar and header with the tokens. `AppShell` draws the top bar, `NavMenu` renders the sections as menus, `AccountMenu` becomes the trigger and panel with the impersonation state, the drawer script becomes the menu script (open, arrow keys, Escape, collapse measuring). ADR 0023 is superseded by a new ADR that keeps "the session chooses the menu".
  - **Done when** `navigation.spec.ts` covers opening a section by keyboard, the collapse at 1280 px and the phone menu at 390 px, the impersonation journey finds the warning trigger and ends impersonation from its panel, `AppNavigationTests` still pass unchanged, axe is green, and compare set `navigation`: tenant home, account menu open, impersonating, platform console, phone menu open, at 1500, 1280 and 390 px.
  - **Built:** `AppShell`, `NavMenu`, `AccountMenu`, the bell restyled, `AppShell.razor.js` as the menu script; [ADR 0025](../adr/0025-top-bar-shell-section-menus.md) supersedes 0023.
  - **Deviations:** The console bar is dark with a "Platform" tag (P2); no search box (P4); no bell on the console; the trigger and panel show no tenant name and no environment line; a section button is a disclosure over links, not `aria-haspopup` with a `role=menu`, so the links stay links; the collapse is proven at the first width below 1280 px where a label gives way, since every tenant label fits at 1280 and 1500 px.
  - **Verified by:** `tests/e2e/navigation.spec.ts`, `AppNavigationTests`.
- [x] **U.3 Page header and page frame.** `PageHeader` as the white header band (eyebrow breadcrumb, title, count, help popover holding the description, actions); the main area without gutter cards; `PageSection` and `OverviewCard` restyled as bordered blocks (radius 12, no shadow); page tabs as underline tabs with icons; `SystemPage` as a two-panel card (tinted panel with a large icon, text with an eyebrow pill, a 2.5 rem title, one button).
  - **Done when** `PageChromeScanTests` passes unchanged, a bUnit test shows the description inside the help popover with the button's accessible name, and compare set `chrome`: users page, settings, access denied, not found, at 1500 and 390 px.
  - **Built:** `PageHeader` as the band, `PageScaffold`, `.app-main` without gutter, `PageSection` and `OverviewCard` bordered without shadow, the tenant console's underline tabs, `SystemPage` as a two-panel card with an `Eyebrow` pill on every system page; `PageHeaderTests`, `page-chrome.spec.ts`.
  - **Deviations:** No page passes `Count` yet, so no count shows; the band shows no icon; system pages carry no header band; the system page's panel is the tone's tint with an icon, not an illustration, and the card has no shadow; the way-on is a neutral button as in the target design; the users list, its primary button and the settings columns keep today's look; text differences (P9).
  - **Verified by:** `PageChromeScanTests`, `PageHeaderTests`, `tests/e2e/page-chrome.spec.ts`.

### Phase U-C: Coordination workspace (D1)

- [x] **U.4 Workspace frame and calendar rail.** `/coordination` as the full-viewport grid with the two side panels, collapse rails and splitters, no page scroll; the left calendar rail with day marks and legend (from the coordination snapshot, no new query); the right panel holding today's priority list restyled; the centre a placeholder for U.5 holding today's board. The page is allow-listed in `PageChromeScanTests` as the one full-bleed page, with its toolbar as header.
  ADR 0019 is superseded by a new ADR per D1; [docs/features.md](../features.md) updated.
  - **Done when** a journey asserts no document scroll at 1280 and 1500 px, collapsing a panel keeps the URL state after a reload, a bUnit test shows a week-number click selecting that week and a day mark for an uncovered slot, axe is green, and compare set `coordination`: workspace at 1500 and 1280 px, left panel collapsed, at 390 px.
  - **Built:** `/coordination` as a full-viewport grid under the top bar, each side panel with a 26 px head whose title folds it to a 26 px rail, the folded state in the address; below 1000 px the columns stack, the timeline first.
  - **Deviations:** The splitters do not drag and keep no widths; the month stack scrolls natively, without the target design's vertical month rail and thumb; the month picker is a native select; the marks are this domain's four staffing states, not the target design's states; the jobs behind sit under the calendar.
  - **Verified by:** `PageChromeScanTests`, `tests/e2e/coordination.spec.ts`, `CalendarRailTests`, `CoordinationPageTests`.
- [x] **U.5 Hour timeline, toolbar and day strip.** The timeline component (time head, groups, rows, layers, bars, now line, hover card) in Day and Week modes, zoom (Auto, −, +, Now), the toolbar and the bottom day strip, fed by the snapshot `StaffBoardService` already reads (its query count unchanged); the old week board removed.
  - **Done when** the parity test and the board query-count test pass unchanged, bUnit tests place a 10:00–11:30 occurrence at the right left and width for a given hour width and hide staff members without times behind the group hint, a journey moves Day → Week → next week by the day strip and keyboard, the time bar's text passes the contrast test, axe is green, and compare set `coordination`: Day mode with times, Week mode, a time hover card, To staff group, at 1500, 1280 and 390 px.
  - **Built:** `CoordinationTimeline`: the 34 px toolbar; the hour timeline as a grid; and the 18 px day strip.
  - **Deviations:** A Day axis and a Week axis instead of the target design's one continuous canvas that scrolls through midnight; the day strip's range thumb does not drag (a click on a day does it; P11); the name column's width moves by its two buttons, not a drag grip; no utilisation meter, lane count chip, location icons, on-call or debt layers; the hover card is this product's own.
  - **Note:** the Week mode (Day | Week) and the filter button were later removed; the axis is the whole day, 00:00 to 24:00, wider than its area and scrolling sideways, opening on now (today) or the first booking or 07:00; the now line is red with its time at its top and moves once a minute; a week number on the rail is a label ([ADR 0026](../adr/0026-coordination-workspace-hour-timeline.md)).
  - **Verified by:** `StaffBoardTests`, `StaffBoardPageTests`, `TimelineRulesTests`, `TimelineContrastTests`, `tests/e2e/coordination.spec.ts`, `tests/e2e/coordination-timeline.spec.ts`, `tests/e2e/teacher-board.spec.ts`.
- [x] **U.6 Planning mode and List mode on the timeline.** Planning mode (amber toolbar, staged bars, verdicts, ghost, Apply/Undo/Discard) and the Schedule | List switch on the new timeline; the planning journeys of plan step 5.16 move to the new surface.
  - **Done when** `plan-a-week.spec.ts`, `teacher-board.spec.ts` (renamed for the timeline) and `coordination.spec.ts` pass on the new surface, the keyboard path stages and applies a move, and compare set `plan-a-week`: planning on, a staged move, a blocked verdict, List mode, at 1500 px.
  - **Built:** Planning mode on the timeline: Plan turns the toolbar warning-soft with the "Planning" badge, the count of staged moves, Undo, Discard and Apply; a strip under it says what is being placed and lists the staged moves, each with its Undo, and the live region; every staff member of the page is shown while planning.
  - **Deviations:** The target design has no capture of planning mode in its current look, so planning is compared with its workspace and toolbar; the staged moves are listed in a strip under the toolbar, each with its Undo (plan step 5.16's behaviour, D3); a verdict and "Place here" are chips in words at the track's start, not colour alone; List mode is today's list surface; English text (P9).
  - **Note:** planning mode was later removed (its toolbar, strip, verdicts, ghost, staged bars, batch dialog and service), and so were List mode and the Schedule | List switch (`CoordinationSlotList`, `CoordinationSnapshot.Slots`, the `view` key on the workspace, their styles and tests, and the showcase's List mode screen);
    the workspace always shows the hour timeline, and the Priorities dock has no focus card ([ADR 0026](../adr/0026-coordination-workspace-hour-timeline.md)). The centre toolbar's Schedule | List control above, and U.5's toolbar, describe the surface as it was built.
  - **Verified by:** `tests/e2e/plan-a-week.spec.ts`, `tests/e2e/teacher-board.spec.ts`, `tests/e2e/coordination.spec.ts`, `tests/e2e/coordination-timeline.spec.ts`, `PlanningPageTests`.

### Phase U-D: Shared patterns

- [x] **U.7 List pattern.** `ListView` per List pages: segmented quick filters, search box, filter chips with popover listboxes, Group and Sort tools, the table, the footer and pager; the card presentation. No list definition or service changes.
  - **Done when** `lists.spec.ts` passes with the chips (keyboard: open, choose, clear), a bUnit test shows a chip's label and clear button for a set filter, the URL state tests pass unchanged, axe is green, and compare set `lists`: users, units grouped, a filter chip open, phone cards, at 1500 and 390 px.
  - **Built:** `ListView` as one bordered frame: the toolbar row with the quick filter, the search box, a dashed chip per filter, a chip per toggle, and the Group and Sort tools; each chip and tool a button over a popover listbox whose keys live in `scripts/list-view.js`.
  - **Deviations:** No rows-per-page choice; the pager shows only when there is more than one page; the quick filter counts its chosen segment only, and only the users list names one so far; a grouped list keeps one table with heading rows, not one framed table per group, so columns line up across groups and the table keeps its roles; the Sort tool offers each sort ascending and descending in one listbox.
  - **Verified by:** `tests/e2e/lists.spec.ts`, `ListViewTests`.
- [x] **U.8 StateEntityDialog look.** Header in the state colour with contrast-chosen text, the hanging state pill and its hover card, text tabs, the side column on the state tint with folder side tabs, the tinted footer and edge-coloured transition buttons, form dialogs with the accent header; the skill's `DIALOGS.md` updated.
  - **Done when** the state-entity journeys pass, a bUnit test renders the header text colour for a light and a dark state colour, `IconButtonScanTests` covers the side tabs and header buttons, axe and the sweep are green, and compare set `dialogs`: user dialog, assignment dialog with the side column, a blocked step's hover card, a prompt dialog, at 1500, 1280 and 390 px.
  - **Built:** The header filled with the state's colour, its icon, title, unsaved note and buttons in white or the text colour by WCAG contrast, a history button beside the close button, both 36 px.
  - **Deviations:** The header's text is the text colour on light state colours, not white (P10, the axe gate); the pill shows no time of the last step and no unsaved chip: the time would mean reading the history as the dialog loads, which stays lazy, and the unsaved note stays a quiet outlined note beside the title; the pill's card lists the last steps with when and who in place of the target design's Changed and Created facts.
  - **Verified by:** `IconButtonScanTests`, `tests/e2e/state-entity-dialog.spec.ts`, `tests/e2e/assignments.spec.ts`, `tests/e2e/jobs.spec.ts`, `tests/e2e/comments.spec.ts`, `StateEntityDialogTests`, `TokenContrastTests`.
- [x] **U.9 Forms and fields.** Outlined fields, resting and floated labels, helper text, two-column field rows, section eyebrows, Radzen drop-downs in place of native selects in forms and filters.
  - **Done when** `state-entity-fields.spec.ts` passes, a scan refuses a native `select` in components outside an allow-list, every field still names its input (the existing scan), and compare set `dialogs`: job dialog, customer dialog, absence dialog, at 1500 and 390 px.
  - **Built:** The shared forms stylesheet `wwwroot/app.forms.css`: Radzen's outlined field as the bridge sets it, its focus line and inner ring re-declared on `.state-entity-dialog` so the focus, the focused label and an open drop-down follow the state's colour as text.
  - **Deviations:** The floated and resting label stay the subtle colour and a read-only field keeps its sunken fill and muted label; a refused save still answers in the footer with the service's sentence, not as a message under the field: the service names no field and mapping one is a service change (D3); an empty required field shows its mark and greys the step with its reason, as before.
  - **Verified by:** `tests/e2e/state-entity-fields.spec.ts`, `NativeSelectScanTests`, `AbsenceDialogsTests`, `StaffBoardPageTests`, `tests/e2e/coordination-timeline.spec.ts`.
- [x] **U.10 Comment block.** `CommentBubbleList` per Comment block, in every side column.
  - **Done when** `comments.spec.ts` and `conversations.spec.ts` pass, and compare set `comments`: a long thread, own and other bubbles, the composer, at 1500 and 390 px.
  - **Built:** `CommentBubbleList` per Comment block: everybody else's bubbles on the sunken grey, the reader's own on the tone at 22 %, up to 85 % or 520 px, padding 0.45 × 0.75 rem; day dividers with a hairline at 14 %; a step's note with a chip and a 3 px edge in its target state's colour, read from the thread's workflow; the hint "Enter posts · Shift+Enter starts a new line".
  - **Deviations:** Own bubbles on a side column take the state colour at 30 %, not 36 %: the muted time on 34 % already fell below 4.5:1 on the job's purple; the send button stays the accent at every state colour, white on teal at 4.5:1, rather than the state's colour; the meta line's author and time keep the text and muted colours rather than the target design's translucent text.
  - **Note:** the step's chip on a comment was later removed: a comment written with a step reads as any other, its author, its time and its text, so `CommentEntry.StepName` and its lookup are removed.
  - **Verified by:** `tests/e2e/comments.spec.ts`, `tests/e2e/conversations.spec.ts`, `CommentBubbleListTests`, `StateEntityDialogTests`.
- [x] **U.11 Empty, loading, toasts, panels.** `EmptyState`, the loaders, a toast helper in `DialogServiceExtensions`' family (status, polite region), the reconnect banner and the notification drawer.
  - **Done when** a bUnit test shows the toast's live region and status edge, `notifications.spec.ts` passes on the drawer, the reconnect banner never covers the page (a journey drops the circuit), and compare set `feedback`: an empty list, a loading dialog, the notification drawer, at 1500 and 390 px.
  - **Built:** `EmptyState` per the pattern and `Compact`; `LoadingState` on the app's own spinner, which the boot loader, the session gate, the home loaders and the reconnect banner share, in place of the pages', settings' and form dialogs' ad-hoc Radzen spinners; `ToastService` with `ToastHost` in both layouts, used by the custom field editor as it saves, creates or deletes and closes.
  - **Deviations:** The state-entity dialog's loader and a list's first load were left to the merge with U.7 and U.8, since their markup is `StateEntityDialogLayout`'s and `ListView`'s; done in the merge's review: the dialog's loader is `LoadingState` and a list's first load `LoadingState Inline`, its rows dimming to 60 % on a later load as before.
  - **Note:** the toasts were later removed: `ToastService` and `ToastHost` and their test went, a step's outcome is said in its dialog and an error opens `ShowErrorAsync`.
  - **Verified by:** `tests/e2e/notifications.spec.ts`, `DialogUsageScanTests`, `TokenUsageScanTests`, `ToastHostTests`, `tests/e2e/feedback.spec.ts`.

### Phase U-E: Views

- [x] **U.12 Homes.** The band layout for every home of plan step 5.17 (coordinator, staff member's My work, administrator, platform owner, portal).
  - **Done when** `home.spec.ts` passes for every persona and compare set `home`: coordinator, staff member, administrator, at 1500 and 390 px.
  - **Built:** The home parts in `Components/Common/Ui/`.
  - **Deviations:** The coordinator's home stays the coordination workspace (plan step 5.17's routing, D3), so it is compared with the target design's start page as it is; no job list with reasons, "Feedback to handle" or "Wishes to follow up" blocks, no charts of the next 14 days or of jobs per step and no "Absent today" figures.
  - **Note:** `HomeBlock`, `HomeTool` and `HomePanel` had no page left that used them once the homes took their later looks, and were removed with their styles and test.
  - **Verified by:** `tests/e2e/home.spec.ts`, `PageChromeScanTests`, `HomePagesTests`, `HomePartsTests`, `PortalPagesTests`.
- [x] **U.13 Staff pages.** My assignments with the section nav and cards, My schedule on the person timeline, the order dialog's sections.
  - **Done when** `staff-pages.spec.ts` and `assignments.spec.ts` pass, the staff member never sees another staff member's row (the existing test), and compare set `staff`: requests, upcoming, request dialog, my schedule, at 1500 and 390 px.
  - **Built:** "My assignments" with a section navigation beside a panel: "To answer" and "My assignments", each a link to the list with its own state filter in the address, an icon and its count, the section the address names marked as the current page, its title and one sentence over the list's cards; on a phone the two groups stand side by side above the panel.
  - **Deviations:** No Accept or Decline on a card (P5); no answer deadline badge and no place line on a card; "Upcoming" is "Assigned" and "Earlier" is the list's Show closed under "All open": a section by the period's end or of closed rows only is a list definition and service change (D3); the bare address still shows every open row (the list's default, D3), so the page lands on All open, not on Requests.
  - **Note:** "My schedule" no longer runs on `PersonTimeline` and its week tools: a month of day rows (`PersonSchedule`, `Components/Common/Schedule/`) replaced them, and `PersonTimeline` and its tests are gone.
  - **Verified by:** `tests/e2e/staff-pages.spec.ts`, `tests/e2e/assignments.spec.ts`, `tests/e2e/conversations.spec.ts`, `StaffPagesTests`, `StaffConversationDialogsTests`, `PersonTimelineTests`, `OwnWorkTests`, `StateFieldScanTests`.
- [x] **U.14 Administration and staffing pages, settings.** Every list page of the tenant on U.3 and U.7 (users, units, roles, staff members, qualifications, work patterns, absence, customers, jobs); the settings shell in three columns.
  - **Done when** the area journeys pass, `settings.spec.ts` covers the section nav by keyboard, and compare set `settings`: General, Custom fields, units, staff members, customers with a dialog open, at 1500 and 390 px.
  - **Built:** The settings shell per Settings without the quick-help column: the section navigation with uppercase group headings, items with their icons, the open one accent on accent-soft, radius 8, semibold; the arrow keys, Home and End move between its links, wrapping round, Enter follows one and the focus stays on it.
  - **Deviations:** No quick-help column (P7) and no guide tab or search box (P4, out of scope); the target design's further settings panels are out of product scope, and roles keep their own page under Administration (its place in the navigation, D3); the General panel's Save stays disabled until the zone changes (plan step 3.10's behaviour, D3), where the target design shows it enabled.
  - **Verified by:** `tests/e2e/settings.spec.ts`, `tests/e2e/lists.spec.ts`, `tests/e2e/users.spec.ts`, `tests/e2e/custom-fields.spec.ts`, `tests/e2e/staff.spec.ts`, `tests/e2e/qualifications.spec.ts`, `tests/e2e/work-patterns.spec.ts`, `tests/e2e/absence.spec.ts`, `tests/e2e/customers.spec.ts`, `tests/e2e/jobs.spec.ts`, `tests/e2e/lifecycles.spec.ts`, `tests/e2e/accessibility.spec.ts`, `ListPagesTests`, `SettingsPageTests`, `CustomFieldsPanelTests`.
- [x] **U.15 Portal.** Per The portal. Run after plan step 6.11.
  - **Done when** `portal.spec.ts` passes, the portal pages scan still allows only portal pages, and compare set `portal`: home, my requests, request details with a time open, new request, at 1500 and 390 px.
  - **Built:** The portal's centred column of 54 rem: `PageScaffold` with Class `page-scaffold--column` on My orders, New order, the order's page and Users; the portal's home bands at 54 rem.
  - **Deviations:** No times on an order, so there is nothing to expand: the order's facts take the open time's field-card look and its notice; the Cancel step stays in the order dialog's footer, not an outlined button on the page, which "Open order" opens; the title and badge stay in the page header band rather than a second title in the column, so the breadcrumb is the way back.
  - **Verified by:** `tests/e2e/portal.spec.ts`, `tests/e2e/portal-round-trip.spec.ts`, `PortalPagesTests`, `OrderTests`, `PortalUserTests`, `CustomerPortalPageScanTests`, `NavigationGateTests`, `LifecycleCallScanTests`.
- [x] **U.16 Platform console.** Per The platform console. Run after plan step 6.12.
  - **Done when** `platform-console.spec.ts` passes and compare set `platform-console`: overview, tenant list, a tenant's console, the error log, at 1500 and 390 px.
  - **Built:** The overview as a page of panels on the muted floor: the "Platform" eyebrow, the title and a welcome line; a card with an accent icon tile saying who is signed in and what the console is for, with a tip under a dashed line; "Impersonate a user", "Errors in the log" and "Tenants by state".
  - **Note:** the overview was later redesigned ("Brand hero"): an ink hero band with the greeting, four figures and the health ring, the "Tenants" and "Act as a user" cards lifted over its edge, then "Onboard a company" and "Today" ([Homes](../features/coordination-and-homes.md#homes), the platform console's overview). The panels described in Built and the overview's deviations below are history; the tenant list, a tenant's console and the error log are as built here.
  - **Deviations:** The console bar stays dark with the "Platform" tag (P2); no search box, bell or guide tab; "Tenants by state" in place of the target design's most active organizations with activity bars and no platform administrators panel; the error figures count among the newest five the overview reads.
  - **Verified by:** `tests/e2e/platform-console.spec.ts`, `SuperAdminOverviewPageTests`, `TenantConsolePageTests`, `TenantDialogTests`.

### Phase U-F: Close

- [x] **U.17 Responsive and accessibility sweep.** Every view at 1500, 1280 and 390 px; fixes for overflow, wrapping and focus order found there.
  - **Done when** a journey asserts no horizontal scroll on every routable page at the three widths, the accessibility sweep is green with an empty allow-list, every compare set of U.1–U.16 is regenerated, and the open deviations are collected in one table at the end of this plan.
  - **Built:** The accessibility sweep at the three widths of Responsive rules: every page it visits at 1280 px is checked again at 1500 and 390 px, the first dialog of each component a test of the sweep opens at 1280 px likewise with the dialog open, and the tenant console's tabs at all three.
  - **Deviations:** Focus order is checked as a dialog keeping Tab inside it and a page's tab stops all being in reach, not as a comparison of the tab order with the visual order; a dialog family's other widths are checked by resizing the window with the dialog open rather than opening it again at each width.
  - **Verified by:** `tests/e2e/sweep-tenant.spec.ts`, `tests/e2e/accessibility.spec.ts`, `AccessibilitySweepCoverageTests`, `tests/e2e/settings.spec.ts`, `DialogArchitectureScanTests`.
- [ ] **U.18 Dark mode (only if P8 is answered yes).** The dark block switched on by `prefers-color-scheme` with a choice in profile settings.
  - **Done when** the contrast test passes for the dark pairs, the sweep is green in a dark-scheme browser context, and compare set `dark`: home, list, dialog, workspace, at 1500 px (ours only; the target design has none).
  - **Not built** (P8): no dark mode until it is asked for; the dark tokens stay declared and inactive (U.1).

## Interleaving with the build plan

The build plan's remaining steps are 6.7–6.13 and 7.1–7.3. U-steps touch shared chrome, so they run one at a time between build steps, never beside one.

The foundation and the coordination workspace ran first, the rest after the build plan.

| Slot | U-steps | Reason |
|---|---|---|
| After 6.6 | U.0, U.1 | Tokens first, so every later step's screenshots already carry the new values. |
| Then | U.2, U.3 | The shell and header change every screenshot; settle them before more pages arrive. |
| Then | U.4, U.5, U.6 | The coordination workspace (D1). |
| Then | 6.7 to 6.13, 7.1 | The rest of the portal is built on the new tokens, shell and header. |
| After 7.1 | U.7 to U.17 | Shared patterns and views, then the responsive and accessibility sweep, with demo data for full compare screenshots. |
| Last | 7.2, then 7.3 | The style sweep, and the README's screenshots from the finished UI. |

## Open points

Outcome: P1 yes, the whole shell follows; P8 no dark mode for now (U.18 is not built until it is asked for); P10 the contrast-chosen text colour, keeping the axe gate. P2 to P7, P9 and P11 follow the recommendation given with each. The points are kept below for their reasons.

- **P1 Does the whole shell follow?** Recommend yes: a top bar with section menus, white chrome and the teal accent everywhere. The workspace needs the full width a 300 px sidebar takes, and a coordinator moving between the workspace and the lists should not change visual worlds. Changes [ADR 0023](../adr/0023-one-app-shell-two-menus.md) (a new ADR supersedes it, keeping "the session chooses the menu"). If no: U.2 restyles the sidebar only, and the workspace hides the sidebar on its own page.
- **P2 Platform console bar.** Recommend keeping a dark console bar (`--color-chrome-platform`) in the new top-bar layout: it is the platform owner's cue that they are outside every tenant ([ADR 0023](../adr/0023-one-app-shell-two-menus.md)); the target design uses the same white bar for both. A deliberate deviation.
- **P3 Which ADRs change.** Recommend: a new ADR supersedes 0019 (U.4, D1); a new ADR supersedes 0023 (U.2, if P1 is yes); a new ADR records the visual language (tokens, state-coloured dialogs, contrast-chosen text) with U.1. [ADR 0009](../adr/0009-state-entity-dialog-only-and-workflow-governed-attributes.md) and [ADR 0010](../adr/0010-comment-threads-per-audience.md) do not change (their rules hold;
  only the look moves). [ADR 0003](../adr/0003-separate-superadmin-layout.md) is already superseded and stays as it is.
- **P4 Search in the top bar.** Recommend leaving it out until global search (build plan, Later) is scheduled; an inert search box would mislead.
- **P5 Accept and decline on a staff member's card.** The target design answers a request with buttons on the card. Recommend no: rows carry no actions and steps run only in the StateEntityDialog ([ADR 0009](../adr/0009-state-entity-dialog-only-and-workflow-governed-attributes.md), `LifecycleCallScanTests`); the card says "Your answer is needed" and opens the dialog.
- **P6 Resource groups in the timeline.** Recommend To staff first, then one group per organization unit that holds staff members (a staff member in several units listed under the first, by tree order), staff members without times in range hidden behind the group's hint. The alternative, groups by role (staff member, coordinator who works), has no clear meaning in this domain.
- **P7 Quick-help column in settings and the page help.** Recommend no quick-help column; the page description moves into the header's help popover (U.3).
- **P8 Dark mode.** Recommend declaring the dark tokens in U.1 and shipping it only after U.17, as U.18, when it is asked for; the target design has no dark mode, so it doubles the review work without a comparison.
- **P9 UI language.** Recommend English, per [ADR 0005](../adr/0005-code-style-and-english-ui.md). The target screenshots are in another language; text differences are never deviations.
- **P10 State-colour contrast.** The target design puts white text on bright state colours (2.2:1 on orange, 2.8:1 on green). Recommend the contrast-chosen text colour described under Accessibility; a deliberate deviation that keeps the axe gate.
- **P11 Splitter dragging and panel widths.** Recommend collapse buttons in U.4 and draggable splitters with remembered widths in the same step only if it fits within the step; otherwise a later step.

## Open deviations

The differences from the target design still visible once the UI plan was done, collected from the Deviations lines of U.0 to U.17. Each is deliberate;
its reason is one of the comparison rule's: English text, product scope, accessibility, a design choice (D1–D4) or an open point (P1–P11). A difference that is only the target design having no capture of a state (a folded panel, an open phone menu, a dialog at 390 px) is not listed: those pairs are compared with the target design's nearest view, as each step's line says.

| Area | Deviation | Reason | Steps |
|---|---|---|---|
| Everywhere | The UI is in English; the target screenshots are not | P9, ADR 0005 | all |
| Everywhere | No search box in the top bar, no guide tab or quick-help panel | P4, P7, out of scope | U.2, U.12, U.14, U.16 |
| Everywhere | Text on a state colour (dialog header, pill, time bar, filled badge) is white or the text colour by WCAG contrast, not always white | P10, accessibility | U.5, U.8, U.13, U.15 |
| Everywhere | Muted and subtle text where the target design uses faint grey (resting and floated labels, a read-only field's label, zero figures, a drawer's times, a bubble's meta line) | Accessibility (faint grey is 2.6:1, the placeholder grey 1.9:1) | U.1, U.9, U.10, U.11, U.12 |
| Everywhere | Avatars are initials only; no type icons in name cells | Out of scope | U.7, U.14 |
| Shell | A left sidebar (folding to an icon rail, a drawer on a phone) and a top row with the breadcrumb replace the top bar with section menus; the platform console's chrome is white like a tenant's, told apart by its breadcrumb ("Platform / ..."), its navigation and the trigger's "Platform owner" line, not a dark bar with a "Platform" tag | [ADR 0029](../adr/0029-left-sidebar-shell.md) | U.2, U.16 |
| Shell | A section is a group of plain links named by its title, not a `menu` role; the sidebar's fold is kept in a cookie of its own the server reads, not in browser storage | Accessibility (WAI-ARIA navigation landmark), ADR 0014 | U.2 |
| Shell | The account trigger and panel show no tenant name or environment line; no bell on the console | D3 (the session holds no tenant name), no notice reaches a platform owner | U.2, U.12, U.16 |
| Page frame | No header band: the shell's top row carries the page's title, help and actions (ADR 0029); the title shows no icon; system pages have no band and a tinted panel with an icon instead of an illustration | Principle 2, `PageChromeScanTests` | U.3 |
| Workspace | Splitters do not drag and keep no widths; the day strip's thumb does not drag; the name column's width moves by buttons | P11 (the app keeps no state in browser storage) | U.4, U.5 |
| Workspace | Day and Week axes instead of one canvas scrolling through midnight; no utilisation meter, lane chip, location icons, on-call or debt layers | Product scope | U.5 |
| Workspace | The calendar's marks are the four staffing states; the jobs behind sit under the calendar; Prioritized keeps the list surface's search, filters and bands | Product scope, D3 | U.4 |
| Workspace | Asked and said-yes bars have a dashed inner edge, not stripes (the verdict chips, "Place here" and the staged moves went with planning mode, since removed) | Accessibility (colour never the only sign) | U.5, U.6 |
| Lists | No rows-per-page choice; the quick filter counts only its chosen segment; the pager shows only with more than one page | D3, accessibility (no tab stop that does nothing) | U.7, U.14 |
| Lists | A grouped list is one table with heading rows; on a phone rows are labelled cards and chips fold behind "Filters (n)" | Accessibility (columns line up, table roles kept), this plan's List pages rule | U.7, U.14 |
| Lists | The roles page has no list toolbar, pager, grouping or holder count; the units page has no counts per unit, expand-all or drag to move | D3 (a service read each), ADR 0009 | U.14 |
| Lists | Jobs keep their state as a chip, not a quick filter | Eight states would crowd a quick filter | U.14 |
| Dialogs | The pill shows no time of the last step and no unsaved chip; its card lists the last steps instead of Changed and Created | Plan step 4.2 (history stays lazy) | U.8 |
| Dialogs | View tabs keep sentence case; on a phone a dialog spans the width and grows with its content | This product's own | U.8 |
| Dialogs | A refused save answers in the footer, not under a field; dialog help stays paragraphs under the fields | D3 (the service names no field) | U.9 |
| Dialogs | The bodies show no map, address, arrival information, booking number, organization number, contracts or response-deadline cards | Product scope | U.8, U.9, U.13, U.14 |
| Comments | Own bubbles on a side column at 30 %, the send button in the accent; internal notes stay flat cards; no suggestion buttons or date chips | Accessibility, ADR 0010, product scope | U.10 |
| Feedback | The notification drawer leaves the page undimmed and does not close on a click beside it; the reconnect banner offers Retry and Resume, not Reload | Accessibility (a disclosure, not a modal), D3 | U.11 |
| Feedback | Removed: there are no toasts; an error opens the error dialog | The toasts' removal | U.11 |
| Homes | The coordinator's home is the workspace; no job lists, charts, feedback or "Absent today" blocks | D3 (plan step 5.17's routing and reads), product scope | U.12 |
| Homes | The setup checklist says Done or To do in words beside its marks | Accessibility | U.12 |
| Staff | No Accept or Decline on a card; no answer deadline or place line; "Upcoming" is "Assigned" and "Earlier" is Show closed | P5, product scope, D3 | U.13 |
| Staff | My schedule shows one week, no hover card, no booked-hours count, no "Change my availability" | Product scope, D3 | U.13 |
| Settings | The General panel's Save stays disabled until the zone changes; Custom fields keeps "New field" beside its lead and its entity tabs as the "Applies to" chip | D3 (plan steps 3.8 and 3.10) | U.14 |
| Portal | No times on an order (it asks for a job over a period); Cancel stays in the order dialog's footer; New order is a dialog, not a wizard page; no count tiles or order cards | Product scope, ADR 0009, D3 | U.15 |
| Platform console | No platform administrators panel; the overview counts active users, not sign-ins (no sign-in time is stored); its design is the later "Brand hero" overview | D3 | U.16 |
| Platform console | The tenant list keeps the list surface without subscription, structure or contact columns; a tenant's console keeps its header band and its own tabs; the error log keeps its grid with figures as type | Product scope, D3, principle 2 | U.16 |
