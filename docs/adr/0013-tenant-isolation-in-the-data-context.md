# ADR 0013: Tenant isolation is enforced in the data context

## Status

Accepted

## Context

The tenant promise in [PRODUCT.md](../../PRODUCT.md) states that a tenant never sees another tenant's data. Isolation that depends on every service remembering to filter by tenant fails as soon as one service forgets.

## Decision

- Every tenant-owned row is filtered on read and checked on write inside `ApplicationDbContext`, never by convention in services.
- The query filter shows only the runtime tenant's rows and returns nothing when there is no tenant.
- `SaveChanges` refuses a write without a tenant, an insert for another tenant, any change of `TenantId`, and an update or delete of a row that the database holds for another tenant.
- Leaving the boundary requires an explicit, logged, disposable system-scope lease on the context (`BeginSystemScope(reason)`, or the platform lease `BeginPlatformScope(runtime, reason)` in services), never a flag on the shared runtime.
- Rows written into another tenant belong to that tenant and are stamped with its system account.

## Rationale

Enforcing isolation in one place that fails closed means the tenant promise holds even when a service forgets it.

## Consequences

- Contexts come only from the factory (`CreateDbContext(runtime)` or `CreateSystemDbContext()`). A directly injected context is a system context for ASP.NET Identity only, and components never touch a context.
- `IgnoreQueryFilters`, `CreateSystemDbContext`, `BeginSystemScope` and raw SQL appear only in allow-listed files, enforced by a source scan test (`DataContextScanTests`).
- Every exception to the filter (platform tables, the `Tenant` table, custom field definitions, custom field values reached through filtered owners) is listed in [tenant-isolation.md](../tenant-isolation.md#exceptions-to-the-filter), and the context gives each its own filter and write check: a tenant reads only its own `Tenant` row and writes none, reads global and its own custom field definitions (which only the platform writes, [ADR 0032](0032-platform-owner-makes-custom-fields-for-a-tenant.md)), and reads and writes only the custom field values of its own rows.
- A row never refers to a row of another tenant, in system scope either: `SaveChanges` refuses a foreign key to a row that the database does not store for the row's tenant, and refuses a missing row the same way. No include or cascading delete crosses the boundary, and a refusal never reveals that an id exists elsewhere.
- `ExecuteUpdate` and `ExecuteDelete` bypass `SaveChanges`. The context refuses, in every scope, an `ExecuteUpdate` that sets `TenantId` or an insert-only audit column. Whether a bulk statement names `TenantId` in system scope is a review rule, since a check at query compilation cannot depend on the scope.
- Isolation is verified against a real database: read and write refusals, a read sweep across two seeded tenants, and a stranger sweep that fails on any row created by a user from another tenant.

## Related Decisions

- [ADR 0007](0007-cross-tenant-ordering-customer-portal.md): the only sanctioned cross-tenant writes.
- [ADR 0012](0012-platform-owner-and-tenant-onboarding.md): system accounts and platform reach.
- [ADR 0022](0022-data-model-conventions.md): audit stamping in `SaveChanges`.
- [ADR 0042](0042-retention-and-the-tenant-purge.md): the tenant purge, the only tagged bulk delete the guard admits.
