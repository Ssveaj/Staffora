# ADR 0025: A top bar with section menus; the session still chooses the menu

## Status

Superseded by [ADR 0029](0029-left-sidebar-shell.md)

## Context

The sidebar shell of [ADR 0023](0023-one-app-shell-two-menus.md) took 300 px of width that the work needs, above all the coordination workspace, and a coordinator moving between the workspace and the lists should not change visual worlds.

## Decision

- Every signed-in page sits in one shell, `AppShell` (`Layout/`): a 60 px top bar and, under it, the page in a `main` landmark across the full width.
- The bar holds the brand (a link to the start page), the navigation's sections as menus (`NavMenu`) and, at its end, the bell and the account trigger (`AccountMenu`). Each section is a button that opens its panel of links. When the labels do not fit, they give way from the rightmost section first and leave the icon; at 768 px and below one "Menu" button holds every section.
- `NavMenu` still holds two menus and the session chooses between them (`AppNavigation`): a platform owner who is not impersonating gets the platform console's sections; everyone else, an impersonating platform owner included, gets the tenant's sections that the effective user's permissions open, and a customer portal's session gets its portal's, as plain links.
- The console's bar is drawn dark, so the platform owner always sees that they are outside every tenant.
- The account trigger shows who the session acts as. While impersonating it is drawn in the warning tone on every page, and its panel opens with the impersonated user and the end button.
- `MainLayout` and `SuperAdminLayout` remain thin wrappers that host the session gate and the shell.

## Rationale

The top bar returns the sidebar's width to the work. The reasons for one shell whose menu the session chooses still hold: the pages every session can reach must show a platform owner the console's chrome and menu, and one shell keeps the bar, the menus, the skip link and the account panel behaving the same everywhere.

## Consequences

- The menus open and close in the browser (`AppShell.razor.js`), on attributes the circuit never renders, so they also work on pages rendered on the server. Each section is a disclosure button (`aria-expanded`, `aria-controls`) over a list of links; the links stay links. Down or Up opens a panel at its first or last link; the arrow keys, Home and End move inside it; Left and Right move between sections; Escape, a click outside or the focus leaving the section closes it.
- The section holding the current page is drawn as current, decided by the circuit from the address, so it holds on every page.
- The impersonation shows on the account trigger rather than as a separate badge and end button; ending it is a form post from the panel ([ADR 0014](0014-session-on-the-auth-cookie-audited-impersonation.md)).
- `/superadmin/*` pages still declare `@layout SuperAdminLayout` and open by the `PlatformConsole` policy; neither the layout nor the menu protects a page by itself.

## Related Decisions

- Supersedes [ADR 0023](0023-one-app-shell-two-menus.md).
- [ADR 0029](0029-left-sidebar-shell.md) supersedes this decision with a left sidebar and a breadcrumb row.
