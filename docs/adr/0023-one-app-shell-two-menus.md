# ADR 0023: One app shell; the session chooses the menu

## Status

Superseded by [ADR 0025](0025-top-bar-shell-section-menus.md)

## Context

With chrome of its own in each layout ([ADR 0003](0003-separate-superadmin-layout.md)), the pages that every session can reach (not found, access denied, the status and error pages) render inside `MainLayout` for a platform owner as well, and would show that platform owner a tenant's chrome and menu. Those pages would have to know who is looking, or show the wrong area.

## Decision

- Every signed-in page sits in one shell, `AppShell` (`Layout/`): a banner with the navigation toggle, the area's name and the account menu, a sidebar with `NavMenu`, and the page in a `main` landmark.
- `NavMenu` holds two menus and the session chooses between them (`AppNavigation`). A platform owner who is not impersonating gets the platform console's sections; everyone else, an impersonating platform owner included, gets the tenant's sections that the effective user's permissions open.
- The shell draws the console's header bar dark, so the platform owner always sees which side they are on.
- `MainLayout` and `SuperAdminLayout` are thin wrappers that host the session gate and the shell. `MainLayout` draws the tenant's chrome, or the console's for a platform owner who is not impersonating; `SuperAdminLayout` always draws the console's. A visitor who is not signed in gets the plain frame in either.

## Rationale

One shell keeps the header, drawer, skip link and account menu behaving the same everywhere, and keeps the choice of menu in one place that tests can read. The shared pages show the platform owner the console's chrome and menu without knowing who is looking.

## Consequences

- `/superadmin/*` pages still declare `@layout SuperAdminLayout` and open by the `PlatformConsole` policy, which the folder's `_Imports.razor` applies ([ADR 0014](0014-session-on-the-auth-cookie-audited-impersonation.md)). Neither the layout nor the menu protects a page by itself.
- Both layouts host the same session gate, so the session is resolved once per circuit and the menu and the impersonation indicator read the resolved session in either layout.
- A platform owner who impersonates sees the tenant's chrome and the impersonated user's menu, exactly as that user does, and no console link.
- A tenant link names the permissions its page gates on, so the menu never offers a page that would refuse. The console's links need no permission, since platform reach opens all of them.
- The drawer, the skip link and the account menu behave in the browser (`AppShell.razor.js`), so they also work on pages rendered on the server.

## Related Decisions

- Supersedes [ADR 0003](0003-separate-superadmin-layout.md).
- [ADR 0025](0025-top-bar-shell-section-menus.md) supersedes this decision with a top bar; the current shell is described in [ADR 0029](0029-left-sidebar-shell.md). The principle of one shell whose menu the session chooses carries over.
