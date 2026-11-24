# ADR 0046: The database reads committed rows by row versioning (READ_COMMITTED_SNAPSHOT)

## Status

Accepted

## Context

Every tenant shares one database and one schema ([ADR 0013](0013-tenant-isolation-in-the-data-context.md)). Within a tenant, steps that judge a rule over several rows are serialized by the tenant's row and its application locks, taken in one order ([Application locks](../database.md#application-locks)). Those locks are per tenant, so they never order the work of two different tenants.

Two unrelated tenants nevertheless deadlocked when they worked at the same time: two tenants creating organization units, and two portals sending orders. SQL Server's deadlock graphs showed the cause. It was not the order of the application locks but locking reads that reached beyond the tenant:

- The tenant query filter (`@IsInSystemScope = 1 OR (@hasTenant = 1 AND TenantId = @tenant)`) cannot be answered by an index seek, so a tenant's read scans the clustered index. Keys are `Guid`s, so that index mixes the rows of every tenant.
- Under locking READ COMMITTED, a scan inside a write transaction waits for the shared lock on another tenant's freshly inserted, uncommitted row, while that tenant's transaction scans into ours.
- The cycles seen were unit create against unit create (the workflow's reload of the new unit with its parent, and the structure check reading `OrganizationUnits WITH (READCOMMITTEDLOCK) WHERE TenantId = @tenant`) and order against order (`OrderLines`).

Under load, two tenants completed between 2 and 6 unit creates and orders in 8 to 10 seconds, with 2 to 6 deadlocks.

## Decision

- **The database runs with `READ_COMMITTED_SNAPSHOT` on.** A plain read at read committed sees the last committed version of each row and never waits on another transaction's uncommitted rows. `ApplicationDatabase.MigrateAsync` (`Staffora.Data/ApplicationDatabase.cs`) turns it on once, right after the migrations, and only when it is off. Every path that migrates goes through it: the application at startup (`Program.cs`), the end-to-end preparation (`Staffora.E2ESetup`, `FoundationPrep`) and the integration test pool (`DatabasePool`). Azure SQL Database has it on by default.
- **Reads that judge a rule stay locking, by explicit hints.** The organization structure check reads `WITH (READCOMMITTEDLOCK, FORCESEEK)` (`ApplicationDbContext.OrganizationStructure.cs`), and the tenant's row is held shared with `HOLDLOCK` before any application lock is taken (`ApplicationDbContext.HeldTenant.cs`).
- **A locking read seeks its own tenant's range.** `FORCESEEK` makes the structure check seek the tenant's own index range, so it never locks another tenant's rows.
- **Snapshot isolation stays off.** `ALLOW_SNAPSHOT_ISOLATION` is not enabled. Structure writes open their transaction at read committed by name (`ApplicationDbContext.OrganizationStructureIsolationLevel`), because a pooled connection keeps the isolation level its previous user set.

## Rationale

Row versioning removes the wait that formed the cross-tenant cycle without changing any query, filter or index. The locks that guard a tenant's rules are unchanged: those reads keep their locking semantics through their hints and keep to the tenant's own rows. With row versioning and the seeking structure check, the same two tenants completed 100 to 150 unit creates and orders each in the same time, with no deadlock.

## Consequences

- A plain read inside a transaction may see a row as it was committed before a concurrent write. Any read that must see the latest state to judge a rule takes its lock explicitly with a hint, and seeks a tenant-scoped index with `FORCESEEK` so it does not lock another tenant's rows. A new locking read without `FORCESEEK` on a tenant-scoped index reintroduces the deadlock.
- The database keeps row versions in `tempdb` while transactions that need them are open.
- Changing the option needs exclusive access to the database, so the statement uses `WITH ROLLBACK IMMEDIATE`. It runs only while the option is off, which is once per database, during startup or test setup.
- `DeadlockRetry` still reads a dialog's load once more when it is chosen as a deadlock victim; its common cause across tenants is gone.
- Verified by `TenantLockOrderTests.Two_unrelated_tenants_creating_units_and_sending_orders_at_once_never_deadlock`: two staffing companies, each with a portal, create units and save and send orders in parallel for eight seconds, with no deadlock and no refusal.

## Related Decisions

- [ADR 0007](0007-cross-tenant-ordering-customer-portal.md): the order send and its lock order across two tenants.
- [ADR 0013](0013-tenant-isolation-in-the-data-context.md): the tenant query filter and the write checks in the data context.
- [ADR 0021](0021-one-organization-tree-per-tenant.md): the organization tree and its structure check.
- [ADR 0022](0022-data-model-conventions.md): `Guid` keys and the `TenantId` index on every tenant row.
