# ADR 0029: A left sidebar and a top row with the breadcrumb; the session still chooses the menu

## Status

Accepted. Supersedes [ADR 0025](0025-top-bar-shell-section-menus.md).

## Context

The top bar with section menus of [ADR 0025](0025-top-bar-shell-section-menus.md) hid each section's pages behind a menu and shortened labels when many sections competed for the bar, and the bar left no place to tell readers where they are. The coordination workspace still needs nearly the full width of the window.

## Decision

### The shell

Every signed-in page sits in one shell, `AppShell` (`Layout/`), in every layout: the platform console, a tenant's pages, a staff member's own pages, a customer portal and the system pages.

- **The sidebar.** A full-height sidebar runs down the window's left edge, white with a 1 px line at its right. At its top is the brand (the mark on a rounded accent square and "Staffora" in the deep accent, a link to the start page) with a "<<" button, "Collapse menu", at its right. Under it is the navigation (`NavMenu`): each section of `AppNavigation` is a group whose title stands in small muted capitals over its links. A link is a 40 px row of a line icon and its label, rounded 8 px; the current link sits on the soft accent with its text in the deep accent, semibold, and a small accent dot at its end.
- **The top row.** Beside the sidebar, a 56 px top row, white with a 1 px line under it and the page's one banner, holds the breadcrumb at its start (`AppBreadcrumb`: for example "Platform / Overview", the section muted and a link to its first page, the page semibold) and the bell and the account trigger at its end (`AccountMenu`; an impersonation shows on the trigger). The page follows in a `main` landmark on the warm floor, which scrolls on its own.
- **The rail.** The sidebar folds to a 64 px rail of icons: the labels and section titles give way (each link keeps its label as its accessible name and its tooltip), the brand shows only its mark, and the button becomes ">>", "Expand menu".
- **The fold is a browser preference, not session state.** The button writes it to a cookie of its own (`staffora.sidebar`, no identity, no session state). The server reads it for every page it serves and marks the document (`data-sidebar` on the root element, `SidebarPreference`), so the page renders folded or not from its first paint and the choice survives every navigation.
- **The drawer.** Below 900 px the sidebar is an off-canvas drawer instead. The top row's menu button ("Open menu") opens it over a scrim and moves the focus into it, with the page behind it inert. Its close button, Escape, a click on the scrim or a followed link closes it, and the focus returns to the menu button.

### Menus and the breadcrumb

- `NavMenu` renders one of two menus and the session chooses between them (`AppNavigation.ForSessionAsync`): a platform owner who is not impersonating gets the platform console's sections; everyone else, an impersonating platform owner included, gets the tenant's sections that the effective user's permissions open, and a customer portal's session gets its portal's, as labelled groups like the others.
- The breadcrumb and the sidebar's current link find the page's place in those same sections by one rule (`AppNavigation.Locate`): the link whose page it is, or the link with the longest path the page lies under. A tenant's console therefore reads "Platform / Tenants / Company A", its last step the title the page's header gives.

### The page header

There is no page header band. The breadcrumb row is the page's header: its last step is the page's title and the page's one `h1`, with the page's "?" help popover beside it. The page's actions, meta facts and filters stay inside the page, at its top, with the actions at the end of its first row, never in the shell's top row.

## Rationale

The sidebar shows every page of the user's sections at a glance without opening a menu, keeps the labels whole however many sections a session holds, and frees the top row for the breadcrumb, which tells readers where they are. The rail gives the coordination workspace all but 64 px of the window, and the workspace still fills the window under the top row without a page scroll. The reasons for one shell whose menu the session chooses still hold. A page's own steps, such as "New job", belong with the page's content rather than in the shell.

## Consequences

- The fold and the drawer behave in the browser (`AppShell.razor.js`, loaded on every page), on state the circuit never renders: the root's `data-sidebar`, the shell's `data-drawer-open`, the content's `inert` and the menu button's `aria-expanded`. Both the "<<" and ">>" buttons are rendered and the stylesheet shows the one that fits, so no attribute the circuit renders changes in the browser. They work the same on pages rendered on the server.
- The fold's cookie holds no session state, so the session stays on the auth cookie alone ([ADR 0014](0014-session-on-the-auth-cookie-audited-impersonation.md)); nothing is kept in browser storage (`SessionStateScanTests`).
- The platform console's chrome is not dark: the console is distinguished by its breadcrumb ("Platform / …"), its own navigation and the account trigger's "Platform owner" line. The shell's sizes are tokens (`--app-header-height` 56 px, `--app-sidebar-width` 248 px, `--app-sidebar-rail` 64 px), and the drawer's scrim is `--color-scrim`.
- Pages that fill the window (`calc(100dvh - var(--app-header-height))`: the coordination workspace, the homes' boards, the system pages) keep that rule; the sidebar takes width, not height.
- `/superadmin/*` pages still declare `@layout SuperAdminLayout` and open by the `PlatformConsole` policy; neither the layout nor the menu protects a page by itself.
- Every routable page declares the shared `PageHeader` (or gets it from its layout), enforced by `PageChromeScanTests`. `Title`, `Count` and `Description` go to the breadcrumb's last step (`AppBreadcrumb.PageSection`); `Actions`, `Meta` and `Filters` go to a row at the top of the page, the actions at its end; `RootText`, `RootPath` and `Icon` are drawn nowhere. A page without it (a home, the coordination workspace, a system page; allow-listed) has its own `h1`, and the breadcrumb shows the link's text as its last step instead.

## Related Decisions

- Supersedes [ADR 0025](0025-top-bar-shell-section-menus.md), which superseded [ADR 0023](0023-one-app-shell-two-menus.md) and [ADR 0003](0003-separate-superadmin-layout.md).
- [ADR 0014](0014-session-on-the-auth-cookie-audited-impersonation.md): session state on the auth cookie.
- [ADR 0024](0024-visual-language-tokens-and-state-colour.md): tokens and the warm floor.
- [ADR 0026](0026-coordination-workspace-hour-timeline.md): the coordination workspace inside the shell.
