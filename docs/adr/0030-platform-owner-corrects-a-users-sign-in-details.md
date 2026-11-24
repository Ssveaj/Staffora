# ADR 0030: The platform owner corrects a tenant user's name, address and password from the console

## Status

Accepted. Amends [ADR 0012](0012-platform-owner-and-tenant-onboarding.md).

## Context

Under [ADR 0012](0012-platform-owner-and-tenant-onboarding.md) the platform console is read-only inside a tenant: the platform owner acts there only by impersonating one of its users, the one exception being the Unlock step, since anyone who knows an address can keep a lockout in place. A tenant's owners who cannot help a person sign in (a misspelt address, a changed name, a password forgotten while no administrator is available) would have to wait for the platform owner to impersonate one of them.

## Decision

- The platform owner corrects a tenant user's sign-in details from the user's dialog on the console's Users tab:
  - their first and last name, their email address and their user name (the user workflow's Save step);
  - a new password, shown once (the staffing company's New password step).
- Nothing else. The phone number and the custom fields stay read-only on the console, and the user service refuses a console save that changes them (`UserService.ConsoleEditableColumns`). The lifecycle (Deactivate, Reactivate), the role holdings and a customer portal's own steps stay inside the tenant.
- Inside a tenant nothing changes: only a holder of `UsersEdit` (the Administrator preset and the system owners; coordinators and staff hold no user permission) changes another person's details or gives them a new password.

## Rationale

Correcting sign-in details is support, not work inside the tenant. Letting the console take these two steps avoids an impersonation for a support task, and the console records who took each step.

## Consequences

- The user workflow's Save step and the staffing company's New password step carry `InOwnTenantOrOnTheConsole()` instead of `InOwnTenant()`, the condition Unlock already had. The grant ceiling, the rule that only a system owner changes a system owner's sign-in name or password (`SignInNameWithinCeiling`, `OwnerByOwner`) and New password's refusal of a platform owner's account (`NotAPlatformOwner`) still hold. Deactivate, Reactivate and the portal's steps keep `InOwnTenant()`.
- The user dialog on the console offers Unlock, Save and New password (its context's `TransitionFilter`), with the name, address and user name editable and every other field read-only (`IsLockedMember`).
- Each step is written on the tenant's behalf and recorded in the user's history, as the console's Unlock is.
- [PRODUCT.md](../../PRODUCT.md) lists these corrections among the platform owner's capabilities; everything else inside a tenant still goes through an audited impersonation.

## Related Decisions

- Amends [ADR 0012](0012-platform-owner-and-tenant-onboarding.md).
- [ADR 0034](0034-no-self-service-account-pages.md): the user workflow is the one way to change an account.
