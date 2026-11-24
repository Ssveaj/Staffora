# ADR 0014: Session state lives on the auth cookie; impersonation is authorized and audited

## Status

Accepted

## Context

A Blazor Server session needs some state before its first render, notably whether the user is impersonating someone. State kept in browser storage can outlive its authorization and leak between users of the same browser, and impersonation must leave an audit trail.

## Decision

- Everything a session needs before its first render (the impersonated user) is a claim on the auth cookie, set only by POST endpoints that re-issue the cookie, and never kept in browser storage.
- Claims carry identifiers, not authority. Every circuit re-checks impersonation against the database, resolves the session once, applies it atomically and renders nothing until it is ready.
- A platform owner may impersonate an eligible user in any tenant. While impersonating, the cookie carries the target's roles; permissions and tenant are the target's; the platform console is closed; rows record the target as creator and the platform owner as impersonator; and each impersonation's start and end are logged in an impersonation audit.
- Pages open by a session policy (`SessionPolicies`): the platform console only to a platform owner who is not impersonating, tenant pages to everyone else.

## Rationale

Re-authorizing on every circuit and keeping nothing in browser storage means impersonation cannot outlive its authorization and cannot leak between users of the same browser, and the audit leaves a trail of every impersonation.

## Consequences

- Eligible targets are active users of an active tenant (in Setup, only its system owners), never a platform user, a system account or oneself. Starting while already impersonating switches the target and closes the previous audit row.
- Nothing is kept in `ProtectedSessionStorage` or any other browser storage (`SessionStateScanTests`). The security-stamp refresh carries the session claims over. Revalidation runs every five minutes and signs out users who are no longer active; a revalidation pass that fails signs the circuit out too.
- The runtime holds the applied session as one immutable snapshot. A reader that needs more than one fact (platform reach, stamping, the permission key) reads that snapshot once, so it never combines two sessions.
- Signing out while impersonating closes that impersonation's own audit row before the cookie is cleared.
- Permissions are cached per tenant and effective user with a short time-to-live, and are invalidated when the current user's own roles change.
- `UserSessionService` is the only class that reads claims to populate the runtime, and `ImpersonationService` the only other, to re-issue the cookie behind the impersonation endpoints.

## Related Decisions

- [ADR 0012](0012-platform-owner-and-tenant-onboarding.md): this decision refines the platform owner's impersonation.
- [ADR 0029](0029-left-sidebar-shell.md): the shell shows an active impersonation on the account trigger.
- [ADR 0034](0034-no-self-service-account-pages.md): the remaining paths that re-issue the cookie or change a credential.
