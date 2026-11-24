---
name: tenant-isolation
description: Tenant isolation in the data context (query filters, SaveChanges write checks, system-scope leases, system accounts). Use when adding an entity or table, writing a query or service method that reads or writes tenant data, crossing the tenant boundary, reaching for IgnoreQueryFilters, ExecuteUpdate/ExecuteDelete or raw SQL, or writing isolation tests.
---

# Tenant isolation

A tenant never sees another tenant's data ([PRODUCT.md](../../../PRODUCT.md), "The tenant promise"). `ApplicationDbContext` enforces this in one place and fails closed ([ADR 0013](../../../docs/adr/0013-tenant-isolation-in-the-data-context.md)); services and components rely on it instead of filtering by hand.

The authoritative rules (read filters, write checks, stamping, system scope, the exception table, the bypass rules, the source scans) are in [docs/tenant-isolation.md](../../../docs/tenant-isolation.md). Session and impersonation, which decide the runtime tenant, are in [ADR 0014](../../../docs/adr/0014-session-on-the-auth-cookie-audited-impersonation.md) and [docs/authentication.md](../../../docs/authentication.md#session). This skill holds the recipes, the lock order, the review checklist and the test recipes.

## 1. Classify the data

Decide before writing any code. Done when the entity is in exactly one row:

| Kind | Examples | Shape |
|---|---|---|
| **Tenant-owned** | jobs, resources, roles, users, comments, integration messages (the sender's) and their receipts (the receiver's) | Implements `ITenantEntity` (usually through `TenantEntityBase` or `TenantStateEntityBase<TState>`); filtered and write-checked automatically |
| **Platform-owned** | error log, impersonation audit | No `TenantId` of their own (the error log's `TenantId` only labels the row); a filter of their own passes nothing outside system scope; written only in system scope (the audit is never deleted, an error row never updated); read only by the platform owner in system scope |
| **Listed exception** | `Tenants`, attribute definitions and values, Identity tables | A row in the [exception table](../../../docs/tenant-isolation.md#exceptions-to-the-filter) with the reason and the only access path, and its own filter and write check in the context |

When in doubt, it is tenant-owned. A new exception requires an ADR and its row in `docs/tenant-isolation.md` in the same change.

## 2. Recipes

### New tenant-owned entity

Inherit the tenant base type; call `ConfigureTenantEntity()` in its configuration (it adds the `TenantId` index, the audit columns and their `NoAction` foreign keys, [ADR 0022](../../../docs/adr/0022-data-model-conventions.md)); give every relationship `OnDelete(DeleteBehavior.NoAction)` unless the child is an owned row without a lifecycle; add the `DbSet` and a migration. The filter and the write checks attach through `ITenantEntity`; no per-entity code. Done when the model sweep test (section 4) passes without touching its exemption list.

### Query or service method

- Create the context from the factory: `await using var context = dbFactory.CreateDbContext(runtime);`. Every context is disposed.
- Query with LINQ and let the filter scope it. Load single rows with a filtered `FirstOrDefaultAsync(e => e.Id == id)`, never `FindAsync`.
- A foreign id behaves like a missing one: return a failed `Result` with the same message, never a hint that the row exists elsewhere.
- A decision that needs two or more facts of the runtime (tenant, user, platform reach) reads `runtime.Current` once and takes them all from that snapshot (`SessionStateScanTests`; [docs/authentication.md](../../../docs/authentication.md#one-snapshot)).

### Leaving the boundary

Only with a concrete reason the caller can name:

```csharp
using (context.BeginSystemScope("Platform console: list all tenants"))
{
}
```

`IgnoreQueryFilters` and cross-tenant reads belong inside such a block only. The lease nests, logs its reason when opened and closed, and ends on dispose, also on exceptions. Never keep a lease open across awaits that call other services. A system context (`CreateSystemDbContext()`) has no runtime and no scope of its own: without a lease it reads and writes no tenant row.

In a service, the platform console's cross-tenant reads use the platform lease. It opens only with platform reach (`PlatformScope.HasPlatformReach`: the `Admin` role and membership of the platform tenant, both read from the database when the session resolves, and not impersonating); otherwise it leaves the context in the runtime tenant, so an impersonating platform owner sees the impersonated tenant only:

```csharp
await using var context = dbFactory.CreateDbContext(runtime);
using var lease = context.BeginPlatformScopeFor(runtime, tenantId, "read a tenant's roles");
```

### Caching a tenant catalogue

A catalogue read repeatedly (custom field definitions, tenant settings, the qualification catalogue) goes through `TenantLookupCache`, never a cache of a service's own ([docs/features/custom-fields-and-settings.md](../../../docs/features/custom-fields-and-settings.md#lookup-cache)).

- Declare a `LookupCatalogue<TRow>` as a static field of the owning service, with immutable `ILookupRow` rows and a load that names its tenant (`TenantId == tenantId`, or `null` for global rows). The cache reads on a system context under its own lease, where no filter applies, so an entry depends on its key alone; it refuses to cache a row of another tenant.
- Before asking the cache, the service checks the permission and the tenant the caller may read, from one snapshot (the runtime's own tenant, another only with platform reach), so no caller reads or fills another tenant's entry.
- After every save, once it has returned, the service calls `Invalidate(catalogue, tenantId)`, or `InvalidateEveryTenant(catalogue)` for a global row.
- Prove it with two service hosts sharing one cache (`ServiceHost.SharingLookupCacheOf`) and the command log.

### Acting for a tenant with no circuit

Hosted services (`JobClock`, `IntegrationOutboxDispatcher`, `Retention`) follow [docs/tenant-isolation.md](../../../docs/tenant-isolation.md#acting-for-a-tenant-with-no-circuit):

1. List what is needed across tenants on a system context under a lease with its reason, ids only.
2. Do each tenant's work in a service scope of its own whose fresh runtime takes that tenant's system-account session through `SystemAccountSession.Apply` (the one allow-listed `ApplySession` outside `UserSessionService`).
3. Open only contexts of that runtime for the work (filtered, held, stamped with the tenant's account), with the tenant named in the queries as well; never a system context.
4. Read a setting for the named tenant through its owning service (`TenantSettingsService.GetOfTenantAsync`).
5. Take the same step locks as a user's step, in the lock order, before reading the record (the job clock takes the job's registered `IStepLock`s).
6. Log one tenant's failure and continue with the next.

Prove it with two tenants: work done for one writes nothing in the other, and each tenant's rows are stamped by its own account only (`JobClockTests`).

### Users

Users are tenant rows ([docs/tenant-isolation.md](../../../docs/tenant-isolation.md#users)).

- A user is written with a creator of its own tenant: the person creating them in their own tenant, otherwise the tenant's system account.
- Never add a user lookup that runs before a tenant applies: only `ApplicationUserStore`, the session's first read of the signed-in user and `ImpersonationAuthorizer` do, each under a lease with its reason.
- A creator set by the caller in system scope records no impersonator. A user created while impersonating goes through the user workflow's create step on the runtime's context, which stamps the effective user and the platform owner like any other row.
- A query that lists, counts or picks users leaves the system accounts out with `ExceptSystemAccounts()`.

### Writing into another tenant

One rule says who acts in a tenant: `context.ActorIn(tenantId)`, the runtime's effective user in its own tenant and that tenant's system account in any other.

- **Stamping.** Inside a system-scope lease with its reason, set the row's `TenantId` to the target tenant and call `context.StampOnOtherTenantsBehalf()` right before the save. Every new row of a tenant other than the runtime's is created by that tenant's system account (unless a creator is already named), and every changed one is named as edited by it (`NameEditor`). `ImpersonatedById` stays null, also when an impersonating platform owner made the write; the runtime's own rows in the same save are stamped with its user and impersonator. The row then belongs to the target tenant as if written there (`ActingInAnotherTenantTests`).
- **Never stamp a user from the writing tenant.** In system scope `SaveChanges` keeps a creator or editor the caller set, stamps the acting user only where none is set, and refuses the save when either is not a user of the row's tenant. An editor is kept only when named for that save (`NameEditor`, or a `ModifiedById` changed from the loaded value); an editor left as the row held it is replaced with the runtime's user, so name the account on every write. Outside system scope the runtime's user and the clock are always stamped, overwriting caller-set values ([docs/tenant-isolation.md](../../../docs/tenant-isolation.md#stamping)).
- **The system account.** Its id is `SystemAccount.IdFor(tenantId)`, no lookup needed. `SystemAccountService` makes it exist for every tenant (the seeder, startup, `TwoTenantScenario` and tenant onboarding call it). Tenant onboarding writes the new tenant's role presets and first system owner this way (`TenantOnboardingWriter`). Another tenant's system account is refused like any foreign user.
- **History and workflow rows.** `StateHistoryWriter` stamps history this way when the actor acts from outside the moved tenant, and `WorkflowService` stamps every other row a platform owner's step adds or changes in another tenant the same way, its notices' outbox rows included. `InAppChannelSender` delivers such a notice under the platform lease and stamps its rows with that account.
- **References.** The row may refer only to rows of the target tenant: `SaveChanges` refuses a foreign key to another tenant's row (or a missing one) in every scope.
- **A managed tenant created from another tenant's step** (a customer's "Enable portal"). The step's follow-up creates the managed tenant and its rows inside the staffing company's step transaction, under its customer lock, in system scope under leases with their reasons. Every row is created by the new tenant's own system account, and every history entry has that account as its actor (`StateHistoryWriter.AddOnBehalfOf`): a tenant written into by another ordinary tenant never holds that tenant's user's id; only the platform console's steps keep the platform owner as actor (`TenantOnboardingService.OnboardManagedTenantAsync`, `TenantOnboardingWriter.WriteManagedAsync`). Lock order: the staffing company's row, its customer lock, then the new tenant's row and its structure lock. The new tenant takes the customer's name, or the name with a number appended when another tenant holds it, and the staffing company is not told which, so it never learns that another tenant's name exists; its Company root keeps the customer's own name.
- **A portal's order send** (the one direct write of one tenant into an existing other tenant). The order's Send step's follow-up (`OrderSendFollowUp`), inside the step's transaction and under one lease with its reason, judges and writes the staffing company's side on its behalf: the orderer's paired contact, the job in AwaitingCoordination with its `job-receive` history, and the coordinators' notice through `OutgoingMessageWriter.AddOnBehalfOf`. Each row is created by that tenant's system account, and each history entry has it as actor. Fixed lock order across the two tenants: the portal's tenant row and the request's row (the step's), then the staffing company's tenant row (`HoldTenantAsync`, judged with `TenantStates.ExchangesMessages` and the link with `TenantStates.AreLinked`), its structure lock, its customer lock and its job lock, then its rows. Nothing ever takes a portal's lock after a staffing company's. A refusal from the other side answers the portal with one sentence that names nothing of it (`OrderSendFollowUp.SupplierUnavailable`, through `StepFollowUpRefusedException`). The transaction is the compensation: a failure anywhere rolls both tenants back (`OrderSendTests`).
- **Editions.** A tenant's edition (`TenantEditions.Of`) is read from its own row and caps every permission set (`PermissionService`): a portal's people hold the portal's permissions only, whatever a role names.
- **The integration outbox.** A message is queued with `IntegrationOutbox.Enqueue` in the sender's own step, never under a lease (refused there: the runtime's tenant need not be the sender). Only the dispatcher writes its delivery, in system scope. A handler delivers as the target tenant itself (its system account's session, a context filtered to it, held and stamped), and crosses only to read and mark the sender's message under its leases ([docs/tenant-isolation.md](../../../docs/tenant-isolation.md#the-integration-outbox)). `new IntegrationMessage` and `new IntegrationReceipt` occur only in `IntegrationOutbox` and the handler registry (`DataContextScanTests`).
- **The tenant link.** `Tenant.ParentTenantId`, the one link between tenants, is held by the context in every scope: set only when the tenant is created, never on the platform tenant or a managed tenant, never changed afterwards, including by a step's own save or a bulk update.

### `ExecuteUpdate` / `ExecuteDelete`

Prefer tracked updates unless the set is large. Bulk statements bypass the `SaveChanges` checks, stamping, field enforcement and the parent-state check on child rows. What `BulkUpdateGuard` refuses in every scope is listed in [docs/tenant-isolation.md](../../../docs/tenant-isolation.md#bulk-statements).

- In tenant scope the filter confines them. In system scope always include `TenantId` in the `Where`: EF caches compiled queries across contexts, so the context cannot check this by scope (a review rule, not enforced).
- Never set `TenantId`, `Created`, `CreatedById`, `ImpersonatedById` or `ParentTenantId` with one, and never use one on state history, comments, notices, the integration outbox, custom fields, the impersonation audit or the organization tree's structure columns: the guard refuses them.
- `ExecuteDelete` of a state entity is always refused; delete through its workflow ([ADR 0016](../../../docs/adr/0016-deletion-cascades-through-child-workflows.md)).
- Outside system scope, the guard also refuses a statement while the runtime tenant is held (see "Held tenant").
- The names `ExecuteUpdate` and `ExecuteDelete` (a call, a method group or a `nameof`) are admitted only in files on the allow-list in `DataContextScanTests`, each with a reason. Add an entry only with a reason a reviewer accepts.

### Purging a tenant

The one bulk delete past the rules above, and only through `TenantPurge` ([ADR 0042](../../../docs/adr/0042-retention-and-the-tenant-purge.md); [docs/tenant-isolation.md](../../../docs/tenant-isolation.md#the-tenant-purge)): `TenantPurge.BeginAsync` on a context in system scope inside a transaction begun on the context, then `TenantPurge.DeleteBatchAsync` per step of `TenantPurge.PlanFor(context.Model)` and `DeleteMessagesToBatchAsync` until each removes nothing.

- A new table needs nothing: the plan classifies every entity type from the model and fails on one it cannot place, unless it is listed in `TenantPurge.OutsideTheTenant` with its reason (`TenantPurgePlanTests`).
- Never tag another statement with `TenantPurge.Tag`, and never open a purge outside the retention pass (`Retention.TenantPurge.cs`), which checks the grace period again inside the transaction.

### Raw SQL

Only tests use it, apart from the allow-listed files: every application lock (`sp_getapplock`) is taken in `Staffora.Data/AppLock.cs`, the structure check's queries read with `READCOMMITTEDLOCK, FORCESEEK` in `ApplicationDbContext.OrganizationStructure.cs`, and the migration's one-time `READ_COMMITTED_SNAPSHOT` switch is in `ApplicationDatabase.cs` (`DataContextScanTests`). Everything else, the workflow transition lock included, goes through LINQ.

### Held tenant

While a tenant is Suspended, Pending deletion or Archived (`TenantStates.HoldsData`; a tenant that is not found counts as held), nothing changes its data outside system scope, in one shared check that fails closed (`ApplicationDbContext.HeldTenant.cs`):

- A save that writes a tenant row, a custom field definition or a value reads the runtime tenant's state inside the transaction it writes in (the caller's, or the one EF opens for the save: `AutoTransactionBehavior.Always`) before any row is sent, and refuses with a `TenantHeldException` whose message is `TenantStates.HeldDataReason`. `BulkUpdateGuard` refuses a bulk statement the same way as it executes.
- The read takes a shared key lock on the tenant's row that the transaction keeps (`WITH (HOLDLOCK, ROWLOCK)`), once per transaction and tenant, so a step that moves the tenant waits for every write in flight, and a write after it waits and reads the new state.
- System scope is exempt (sign-in bookkeeping, the ensures, the dispatcher's marks, the platform lease). A workflow step judges its record's tenant itself, on the same locked read (`ApplicationDbContext.HoldTenantAsync`), so the console's steps are held too. A `Tenant` row belongs to no tenant, so the tenant's own lifecycle steps are never held.
- A new writer of tenant data needs nothing more than a save or a bulk statement on a context of the runtime. It catches `TenantHeldException` before any catch-all and answers with its message (as role holdings, settings, notices, comments, custom fields and every `DetachedEntityUpdater` save do). It never writes in system scope to get past a held tenant.

### Lock order

One order holds for every transaction that touches a tenant's records, in every scope:

1. **The tenant's row (shared).**
2. **Application locks**, in rank order: the structure lock, the schedule lock, the customer lock, the job lock, a record's comment thread lock (`AppLock.Structure`, `Schedules`, `Customers`, `Jobs`, `CommentThread` in `Staffora.Data/AppLock.cs`). A transaction takes the ones it needs in that order and never one ranked before a lock it already holds. `AppLock.TakeInOrderAsync` takes a set in rank order.
3. **Rows.**

Every application lock is an `AppLock`, taken only through it: it belongs to the caller's transaction, holds the tenant's row first (`HoldTenantBeforeLockAsync`), and throws `AppLockBusyException` with its `BusyReason` when it is not granted in time (the structure lock answers an `OrganizationStructureException` with the same reason). A new lock on a tenant's records is a new `AppLock` with its rank, never a raw `sp_getapplock` elsewhere.

Which locks each step type takes is registered once in `AddWorkflowServices` (`services.AddStepLock<TEntity>(AppLock...)`, a `TenantStepLock<TEntity>`):

| Step type | Locks |
|---|---|
| Unit, role, user, staff record | Structure |
| Work pattern, pattern assignment | Schedules |
| Customer, customer contact | Customers |
| Job | Structure, Customers, Jobs |
| Service of the catalogue | Customers, Jobs |
| Slot, assignment, request round, qualification, absence | Jobs |

A step's comment thread lock is taken after its step locks, as the step writes its comment.

- **Tenant row first.** Every transaction begun on a context outside system scope with a runtime tenant reads its tenant's state under the shared lock as it begins (`TenantHoldInterceptor`), and its saves reuse the read. In system scope (a console step under the platform lease, the Company root's ensure, a seed) no transaction starts with the row, so each `AppLock` holds the row itself before it is taken.
- **Why on the console too.** A shared request for the tenant's row queues behind a waiting exclusive one, so a console step that held the structure lock and only then asked for the row would wait for a suspension, which waits for a tenant session holding the row, which waits for the console's structure lock: a deadlock (`TenantLockOrderTests`).
- **Across tenants.** A portal's send holds the portal's tenant row and its request row first, then the staffing company's row and its locks (`AppLock.TakeInOrderAsync(context, supplierTenantId, [Structure, Customers, Jobs])` in `OrderSender`). Nothing ever takes a portal's lock after a staffing company's.
- **Moving a tenant.** A step moving a tenant takes no application lock before its first lock, the exclusive update of the tenant's row, then rotates its users' stamps and writes its history.
- **Creating a tenant.** A tenant's create takes the platform's tenant-create lock (`AppLock.TenantCreate`, through `TenantCreateStepLock`) as its first lock, so two onboardings run one after the other; nothing takes it after another lock.
- **Comment posts.** A post holds its record's row before the record's comment thread lock (`CommentThreadWriter.LockAsync`). A post with effects (an assignment's) takes the row exclusively with its whose-turn `UPDATE` as its transaction's first statement, takes no other application lock and, holding the row, reads no other row (what its notices name is read before its transaction), so it never waits on a step that holds the job lock and then the row.
- **Staff records** follow their user: `ResourceLinker` writes only inside a role holding's or a user, role or unit step's transaction, which already holds the tenant's row and the structure lock. The startup ensure of the System owner roles (`SystemOwnerPermissionsEnsure`) opens one transaction per tenant in system scope and takes the structure lock before it reads the role.
- Never read or write a tenant's rows in a long transaction of your own begun outside the context (a raw connection, a transaction begun past the context); begin it on the context.
- **Reads across tenants never block.** The database runs `READ_COMMITTED_SNAPSHOT`, turned on once after the migrations by `ApplicationDatabase.MigrateAsync`: a plain read never waits on another transaction's uncommitted rows. The tenant filter cannot be answered by an index seek, so a locking read would scan and lock other tenants' rows and deadlock across tenants. A read that must lock to judge a rule says so with hints and seeks its own tenant's range (the structure check's `WITH (READCOMMITTEDLOCK, FORCESEEK)`, the tenant row's `HOLDLOCK`); never add a locking read without `FORCESEEK` on a tenant-scoped index (`TenantLockOrderTests`, two unrelated tenants creating units and sending orders at once; [ADR 0046](../../../docs/adr/0046-the-database-reads-committed-rows-by-row-versioning.md)).

### Organization tree writes

A save that inserts or deletes a unit, changes a unit's `ParentId` or `UnitType`, or inserts or moves a role holding is a structure write ([docs/database.md](../../../docs/database.md#organizationunit), "One writer at a time per tree"). `SaveChanges` takes the tenant's structure lock in the save's transaction, refuses deleting the Company root, writes, and before commit refuses a unit cut off from the root (a cycle) or a holding on one, a holding on a grouping, and any unit but the root or any holding in the platform tenant, with an `OrganizationStructureException` carrying the reason.

A service that judges such a write opens its transaction at `ApplicationDbContext.OrganizationStructureIsolationLevel` (never without a level: a pooled connection keeps its previous user's), calls `context.LockOrganizationStructureAsync(tenantId)` before it reads the tree it judges, and returns the exception's message as its `Result`.

## 3. Review checklist

Done when every line holds for the change:

- Every new table is classified (section 1), and exceptions are listed in `docs/tenant-isolation.md`.
- Services create contexts from the factory with the runtime and dispose them (`using` / `await using`); no component injects or creates a context.
- A decision that needs two or more runtime facts reads `runtime.Current` once.
- `IgnoreQueryFilters`, `CreateSystemDbContext`, `BeginSystemScope`, `BeginPlatformScope`, `ExecuteSql*`, `FromSql*` and `SqlQuery*` appear only in files on the source-scan allow list, each with a reason; so do `ExecuteUpdate` and `ExecuteDelete`, on a list of their own.
- Every system-scope lease names its reason and is disposed by `using`.
- A cached catalogue goes through `TenantLookupCache` with a load that names its tenant; the service checks permission and tenant before every cache read; every save invalidates after it returns.
- Cross-tenant writes are stamped with the target tenant's system account through `ActorIn` (`StampOnOtherTenantsBehalf`), never by hand.
- Something a tenant sends another after a portal request goes through the integration outbox (`IntegrationOutbox.Enqueue` in the sender's own step, or in a handler's delivery when it answers a message it cannot act on, such as `IJobStateSync.QueueAnswerAsync` for a cancellation of a job already completed or canceled), never a write of the sender into the receiver.
- An integration handler writes on the context it is handed only, and moves a record only through a system-driven step on that context (`RunSystemStepInTransactionAsync` with the delivery's `AfterCommit`), never a public step, a context, a transaction or a lease of its own (`IntegrationHandlerScanTests`). It finds its record by the message's correlation (a cross-tenant id) and acts only when the record's own link names the sending tenant.
- What crosses is what the sender chose: a job's state and its staff members' names, never their ids, addresses or phone numbers. A customer-thread comment crosses as its cross-tenant id and words (and a portal writer's name and address, for contact pairing); the handler writes its copy through `CommentThreadWriter` as the receiving tenant's system account, idempotent on the source comment (`Comment.LinkedCrossTenantId`, unique per tenant), authored by a row of its own tenant (a contact, the supplier party), never a user of the other. Service offers cross with one correlation per service and portal (`ServiceOfferSync.CorrelationFor`); a portal's `OfferedService` names the service by its cross-tenant id and its supplier party, never the service's id.
- A query that lists, counts or picks users uses `ExceptSystemAccounts()`.
- No row refers to a row of another tenant; ids from the client are never trusted to belong to the runtime tenant (the context refuses, but the service answers as for a missing id).
- `Tenants` are written only in system scope. Attribute definitions, global or a tenant's, are written only in system scope (the platform console, under the platform lease, [ADR 0032](../../../docs/adr/0032-platform-owner-makes-custom-fields-for-a-tenant.md)), through their workflow ([ADR 0033](../../../docs/adr/0033-custom-fields-have-a-lifecycle.md): created by a first step, never deleted), stamped by a user of their tenant (its system account) or, for a global one, of the platform tenant. A tenant's field's history is that tenant's, a global field's the platform tenant's. No cascade runs from a definition to its values. Only an active field reaches a tenant.
- A service that writes a unit's place or type, or a role holding, begins its transaction at `OrganizationStructureIsolationLevel`, takes the structure lock before it reads the tree, and maps `OrganizationStructureException` to its `Result`.
- No query trusts a client id without the filter (no `FindAsync`, no `IgnoreQueryFilters` to "find" a row).
- Error messages for foreign ids read the same as for missing ids.
- A writer of tenant data catches `TenantHeldException` before any catch-all and answers with its message; a transaction it begins is begun on the context, so its tenant's row is its first lock.
- Anything told across circuits (the tenant change channel, the notice bus) carries ids only, is keyed by the tenant the write belongs to, is published only after its commit, and reaches a page only through a door that decides the tenant and what is heard from the session (`TenantChangeFeed`, `NoticeService.Subscribe`), never from the page. The page re-reads through its permission-checked services and disposes its subscription ([docs/authorization.md](../../../docs/authorization.md#the-tenant-change-channel)). Extra ids a publisher adds before its commit are read on its own context under its filter with the tenant named. Subscribers are told off the publisher's thread, without its execution context. A write outside `WorkflowService` that a page shows attaches a `TenantChangeCollector` to its context and publishes after its commit.
- A read in steps that hands one step's answer to the next carries the tenant it was read in, and the next step refuses an answer of any tenant other than the session's.
- No reflection or new `ApplicationDbContext` partial class reaches `BeginTransitionSaveScope`, the context's save-scoped entity set, or `ExecuteUpdate`/`ExecuteDelete`: the scans read names only and cannot see it.

## 4. Tests

**Unit (no database), in `Staffora.Test`:**

- **Model sweep** (`Unit/Data/TenantFilterCoverageTests`). Every entity type with a `TenantId` implements `ITenantEntity` and has a query filter, unless it is in the test's exemption list, which mirrors the exception table in `docs/tenant-isolation.md`.
- **Write checks decided before the database** (`Unit/Data/TenantWriteCheckTests`, `Unit/Data/ActorInTests`).
- **Source scan** (`Unit/Architecture/DataContextScanTests`): what it allows and fails on is listed in [docs/tenant-isolation.md](../../../docs/tenant-isolation.md#source-scans). Each allow-list fails on a stale entry, and each scan is proven on a sample. Extend an allow-list only with a reason a reviewer accepts.

**Real database** (`Staffora.Test/Integration/`, `[Trait("Category", "Integration")]`; a migrated template database restored per test class through the `RealDatabase` fixture, two tenants seeded by `TwoTenantScenario`, per [ADR 0018](../../../docs/adr/0018-test-strategy.md) and [Staffora.Test/Integration/README.md](../../../Staffora.Test/Integration/README.md)):

- Reads in tenant A's context return none of tenant B's rows.
- Refused, each test also asserting the stored rows are unchanged: an insert with B's `TenantId` from A; a `TenantId` change, tracked or through `Update()` on a detached row, also in system scope; an update or delete of an attached B row, with the same message as for a missing row even when the row claims A's `TenantId`; any write with no runtime tenant; in system scope, a creator or editor from another tenant; a foreign key to a row of B (per relationship, join rows and navigation fix-up included, also in system scope) or to a missing row, with the same message; an `ExecuteUpdate` setting `TenantId` or an insert-only audit column, a bulk statement on state history and an `ExecuteDelete` of a state entity, in both scopes; any write to `Tenants`, `ImpersonationAudits` or `ErrorLogs` outside system scope, an update of an error-log row in any scope, a delete of an audit row in any scope, an update of an audit row in system scope other than setting its empty `Ended`, and any bulk statement on the audit; a value of B's row, a value using B's definition, a global or B's definition written from A; a row deleted and inserted again with the same key in one save.
- A tenant reads only its own `Tenants` row, the global and its own attribute definitions, and the values of its own rows.
- Deleting in B never removes a row of A. A custom field is never deleted: its delete is refused in every scope (`Integration/Data/CustomFieldRuleTests`, `Integration/Data/AttributeIsolationTests`), and a field out of use is deactivated with its values kept (`Integration/Services/CustomFieldServiceTests`).
- A system-scope lease nests, ends on dispose, and ends when the block throws.
- A held tenant's data does not change: every writer outside a step, run from a live circuit of the suspended tenant, is refused with `TenantStates.HeldDataReason` and stores nothing; a step and a suspension racing never commit the step after the suspension; a tenant transaction holding a user row and a suspension do not deadlock (`Integration/Services/TenantLifecycleAccessTests`). The lock order holds in system scope too, and a timed mix of suspensions, owner steps, holdings, renames and console unlocks has no deadlock victim (`Integration/Services/TenantLockOrderTests`).
- In system scope a row stamped with its own tenant's system account is saved; one stamped with another tenant's account, or with a user flagged as a system account under any id but the derived one, is refused (`Integration/Data/SystemAccountTests`).
- A write into tenant B from a session of tenant A, also while a platform owner impersonates A's user, stores B's rows created and edited by B's system account with no impersonator, and is refused without `StampOnOtherTenantsBehalf` (`Integration/Data/ActingInAnotherTenantTests`). A portal's send writes the staffing company's rows on its behalf, sends nothing to a suspended, unlinked or out-of-use side, and leaves neither tenant half-written whichever save or the commit fails (`Integration/Services/OrderSendTests`). An integration message is delivered only between linked tenants, only while both are Active, as the target tenant, once (`Integration/Services/IntegrationOutboxTests`).
- A platform owner impersonating a user of A sees only A, through the services as well (`Integration/Services/PlatformReachTests`, using `ServiceHost`).
- A circuit applies the impersonation its cookie names only while the authorizer allows it and the cookie's audit row is open for exactly that owner and target (`Integration/Services/ImpersonationTests`); the cookie's roles and the pages it opens are proven over HTTP (`Integration/Web/ImpersonationEndpointTests`).
- A's context reads neither B's users nor the platform owner (`TenantIsolationTests`); the `Admin` role outside the platform tenant gives no platform reach (`Integration/Services/PlatformIdentityTests`); sign-in finds users of every tenant only under the user store's lease (`Integration/Services/IdentitySignInTests`).
- **Read sweep.** Seed every tenant table for A and B; A's context sees no row of B in any table.
- **Stranger sweep.** After a full portal round trip (send, confirm, staff names, thread, cancel), walk every `ITenantEntity` table with raw SQL and fail on any row whose `CreatedById` or `ModifiedById` is not a user of that row's tenant. Seed a deliberate stranger on each side first and assert the sweep finds it, so the detector itself is proven. Both sweeps walk the model (`Integration/Infrastructure/TenantSweeps`: `TenantsReadAsync`, `StrangersAsync`), so a new tenant table joins them without a change; the round trip is `Integration/Services/OrderSyncTests.Sweeps.cs`.

The real-database isolation tests live in `Integration/Data/` (`TenantIsolationTests`, `TenantReferenceTests`, `TenantTableTests`, `AttributeIsolationTests`, `BulkUpdateGuardTests`, `AuditStampingTests`, `SystemAccountTests`, `ReadSweepTests`). Extend those when a rule changes.
