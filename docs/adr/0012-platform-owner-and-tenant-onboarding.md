# ADR 0012: Platform owner, tenant onboarding and system owners

## Status

Accepted. Refined by [ADR 0014](0014-session-on-the-auth-cookie-audited-impersonation.md); amended by [ADR 0021](0021-one-organization-tree-per-tenant.md), [ADR 0030](0030-platform-owner-corrects-a-users-sign-in-details.md) and [ADR 0032](0032-platform-owner-makes-custom-fields-for-a-tenant.md).

## Context

Companies buy the platform independently of each other, and a platform operator must be able to create and support them. The operator must not become a backdoor into tenant data, each tenant must always be able to administer itself, and the tenant promise in [PRODUCT.md](../../PRODUCT.md) must hold by construction.

## Decision

### Platform owners

- The platform is operated by **platform owners**: users who hold the `Admin` application role **and** belong to the dedicated platform tenant (marked `IsRootTenant`). Either condition alone grants nothing.
- Platform owners manage tenants from the platform console: create, activate, suspend, reactivate, schedule deletion, cancel deletion, archive and inspect. Only platform owners change a tenant's state.
- Inside a tenant a platform owner acts only by impersonating one of its users. The server checks the target (active tenant, no platform users, no system accounts) and audits the impersonation at start and end. The exceptions are the sign-in corrections of [ADR 0030](0030-platform-owner-corrects-a-users-sign-in-details.md) and the custom fields of [ADR 0032](0032-platform-owner-makes-custom-fields-for-a-tenant.md).

### Onboarding

- Creating a tenant onboards it in one step, from an onboarding form of its own on the platform console: the tenant in Setup, its `Company` root unit ([ADR 0021](0021-one-organization-tree-per-tenant.md)), its system account, the role presets (Administrator, Coordinator, Staff), a locked **System owner** role and the first system owner are written together. The tenant's dialog opens afterwards.
- The transition from Setup to Active requires that system owner. A tenant in Setup admits its active system owners at sign-in and as impersonation targets.
- Role presets are applied to every new tenant, not only to the demo database, and existing tenants are brought in line.

### System owners

- Each tenant always keeps at least one active system owner.
- The System owner role is held only on the `Company` root, which is never out of use. It counts as a grant of its own beside its permissions: only an active system owner of the tenant gives or removes it, or changes a system owner's credentials and access, since an administrator may hold every permission an owner holds.

### Grant ceiling

Nobody can grant a permission they do not hold. The ceiling covers every permission except the `Personal` area (own work: answering requests, own schedule, own absence), so a coordinator can make someone a staff member without holding a staff member's own-work permissions.

## Rationale

Companies that buy the platform are independent from the first day; the platform can support them without becoming a backdoor, because every action inside a tenant is either an audited impersonation or one of a small set of named, recorded exceptions; and the tenant promise holds by construction.

## Consequences

- A platform owner who opens a tenant page without impersonating is refused.
- Suspended tenants refuse sign-in and impersonation and hold their integration outbox messages.
- Outside Development, the platform tenant and its first owner come from a one-time bootstrap command ([ADR 0022](0022-data-model-conventions.md)).
- Tenant-editable role templates managed by the platform are not implemented.

## Related Decisions

- [ADR 0013](0013-tenant-isolation-in-the-data-context.md): tenant isolation in the data context.
- [ADR 0014](0014-session-on-the-auth-cookie-audited-impersonation.md): session state and audited impersonation.
- [ADR 0021](0021-one-organization-tree-per-tenant.md): the organization tree created at onboarding.
- [ADR 0030](0030-platform-owner-corrects-a-users-sign-in-details.md), [ADR 0032](0032-platform-owner-makes-custom-fields-for-a-tenant.md): the platform owner's actions in a tenant outside impersonation.
- [ADR 0007](0007-cross-tenant-ordering-customer-portal.md): managed customer tenants, onboarded by a staffing company's step.
