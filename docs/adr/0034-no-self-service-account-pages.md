# ADR 0034: No self-service account pages: nobody changes their own account

## Status

Accepted

## Context

ASP.NET Identity's scaffolded account pages (profile, change and set password, two-factor sign-in with authenticator and recovery codes, external logins, personal data, passkeys), their endpoints and passkey sign-in form a second account model beside the user workflow, with their own pages, policy, cookie re-issue paths and tests. They were not designed for the product. The product requires that ordinary users change nothing about their own account (email address, first and last name, user name, phone or password); only a tenant's system owners and administrators, and the platform owner, change those.

## Decision

- **One way to change an account.** A user's name, email address, user name and password change only through the user workflow: inside the tenant by a holder of `UsersEdit` in the user dialog (Save, New password), within the grant ceiling, and from the platform console by the platform owner ([ADR 0030](0030-platform-owner-corrects-a-users-sign-in-details.md)).
  - Only an active system owner takes those steps on their own row (the workflow's `NotTheirOwnUnlessOwner` condition on Save and New password). For anyone else, another administrator of the tenant, a system owner or the platform owner does it.
  - The platform owner changes every tenant user from the console, whatever role they hold.
  - Nobody deactivates their own account.
  - A platform owner's own password is reset only by the operations command `reset-platform-owner`.
- **A password is the only way in.** There is no passkey, no two-factor step and no external login. The Identity schema is version 2, without the passkeys table. The sign-in manager answers every user as having no second factor (`ApplicationSignInManager.IsTwoFactorEnabledAsync`), so a row that still has two-factor sign-in enabled signs in by its password. A New password step clears the user's Identity tokens and switches two-factor sign-in off, so an account keeps nothing beside the new password.
- **No `/Account/Manage`.** There are no scaffolded account pages, no two-factor sign-in pages, no invalid-user page, no passkey component or script, and no passkey, external-login or personal-data endpoints, and no own-account policy or passkey rate limit. What signing in needs remains: the sign-in page, sign-out, the lockout and access-denied pages, the session gate, the impersonation endpoints and the operations commands.
- **One's own profile is read-only.** `/me/profile` ("My profile", in the account menu, not in the navigation) shows the user's name, email address and user name and states that the organization's administrators change them; it offers no edit. `UserService` has no method that changes the caller's own row.

## Rationale

A single account model, the user workflow, means every credential change is authorized by the same rules, recorded in the same history and limited by the same grant ceiling. Removing the scaffolding removes paths that re-issue the cookie or change a credential outside those rules.

## Consequences

- The only paths that re-issue the cookie or change a credential are the user workflow's steps and the operations commands, each recorded in the user's history.
- A system owner gives themselves a new password in the user dialog; a user who is not an owner asks an owner or the platform owner.
- An account page of the product's own design, should one be wanted, is built on the user workflow rather than on Identity's scaffolding.

## Related Decisions

- [ADR 0012](0012-platform-owner-and-tenant-onboarding.md): system owners and the grant ceiling.
- [ADR 0014](0014-session-on-the-auth-cookie-audited-impersonation.md): session state and cookie re-issue.
- [ADR 0030](0030-platform-owner-corrects-a-users-sign-in-details.md): the platform owner's corrections from the console.
