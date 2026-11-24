# ADR 0003: Separate SuperAdmin layout, one router

## Status

Superseded by [ADR 0023](0023-one-app-shell-two-menus.md)

## Context

The platform console and the tenant UI serve different audiences. System administration needed to stay apart from the organization-centric tenant UI without the tenant navigation having to know about the platform console.

## Decision

The application has one `Router` with two layouts. `MainLayout` (header, `NavMenu` sidebar, tenant navigation) is the default through `AuthorizeRouteView DefaultLayout` in `Components/Routes.razor`. `SuperAdminLayout` (header with `AccountMenu` and a sidebar with the platform console's own navigation) is opted into per page with `@layout SuperAdminLayout` on every `/superadmin/*` page.

## Rationale

Keeping the platform console in its own layout separates system administration visually and structurally from the tenant UI, and the tenant navigation never has to know that the platform console exists.

## Consequences

- Every `/superadmin/*` page declares `@layout SuperAdminLayout` and opens by the `PlatformConsole` policy, which the folder's `_Imports.razor` applies ([ADR 0014](0014-session-on-the-auth-cookie-audited-impersonation.md)). The layout provides no protection by itself.
- Both layouts host the same session gate component and `AccountMenu`, so the session is resolved once per circuit and the impersonation indicator works in either layout.
- A platform owner who impersonates moves into `MainLayout` pages and sees the tenant UI exactly as the target user does.

## Related Decisions

- [ADR 0023](0023-one-app-shell-two-menus.md) supersedes this decision with one shell whose menu the session chooses. The current shell is described in [ADR 0029](0029-left-sidebar-shell.md).
- [ADR 0014](0014-session-on-the-auth-cookie-audited-impersonation.md): session policies and impersonation.
