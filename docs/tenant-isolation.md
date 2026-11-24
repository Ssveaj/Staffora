# Tenant isolation

A tenant never sees another tenant's data and never learns that another tenant exists ([the tenant promise](../PRODUCT.md#the-tenant-promise)). This document is the authoritative description of how the data context enforces that promise ([ADR 0013](adr/0013-tenant-isolation-in-the-data-context.md)). Other documents link here instead of restating the rules.

The rules are enforced in `Staffora.Data/ApplicationDbContext.TenantIsolation.cs`, `Staffora.Data/ApplicationDbContext.TenantReferences.cs` and `Staffora.Data/BulkUpdateGuard.cs`. Recipes and the review checklist for code that touches tenant data are in the `tenant-isolation` skill (`.claude/skills/tenant-isolation/SKILL.md`). Code that deviates from these rules is listed in [known-issues.md](known-issues.md).

## Layers

Isolation is enforced in several places, so a mistake in one does not leak data:

1. **The session.** `UserSessionService` resolves the tenant, the user and any impersonation from the auth cookie once per circuit into `IRuntimeExecutionContext` ([ADR 0014](adr/0014-session-on-the-auth-cookie-audited-impersonation.md)).
2. **The read filter.** Every `ITenantEntity` has a global query filter on `TenantId` that fails closed.
3. **The write checks.** `SaveChanges` checks every tenant row before it is written; `BulkUpdateGuard` covers `ExecuteUpdate` and `ExecuteDelete`.
4. **Leases, not flags.** Leaving the tenant takes an explicit, disposable, logged lease with a reason.
5. **Source scans.** `DataContextScanTests` restricts the dangerous calls to allow-listed files.
6. **Tests against SQL Server.** The integration suite proves the rules on a real database.

## The runtime tenant

The runtime tenant is the tenant a context reads and writes (`ApplicationDbContext.CurrentTenantId`):

- While impersonating, it is the impersonated user's tenant; otherwise it is the signed-in user's own tenant.
- The runtime keeps the two apart (`TenantId`, `BaseTenantId`, `ImpersonatedTenantId`).
- Impersonating a user without a tenant leaves no tenant; it never falls back to the base tenant.

## Reads: the query filter

Every `ITenantEntity` carries a query filter that passes a row only when:

- the context is in system scope, or
- the row's `TenantId` equals the runtime tenant.

With no runtime tenant, or no runtime at all, the filter returns nothing. A foreign id reads like a missing one. `TenantFilterCoverageTests` checks that every entity with a `TenantId` has the filter.

Some tables have filters of their own, defined in the same place (see [Exceptions to the filter](#exceptions-to-the-filter)). In system scope every row passes.

Load tenant rows with a filtered query, not `FindAsync`, which can return a tracked row without applying the filter.

## Writes: checks in SaveChanges

`SaveChanges` checks every `ITenantEntity` before saving. A refusal throws a `TenantIsolationException` whose message names no other tenant, and is logged as a warning naming the entity, its key and the tenants involved.

| Entry state | Rule |
|---|---|
| Any | Refused when there is no runtime tenant. |
| Added | An empty `TenantId` is filled from the runtime tenant; a different `TenantId` is refused. |
| Modified | A row the database stores for another tenant is refused, whatever `TenantId` the caller sent. The check reads the stored row, not the tracked original, and the message is the same as for a missing row. On the runtime tenant's own row, a `TenantId` that differs from the tracked original or from the stored row is refused, so `Update()` on a detached row cannot move it either. |
| Deleted | A row the database stores for another tenant is refused. |
| Added and Deleted | A row deleted and inserted again with the same key in one save is refused, for every entity type except owned types (none exist), in every scope. EF would otherwise send the pair as one unchecked `UPDATE` with a new creation stamp. |
| Added, Modified | Every foreign key (added, or changed on an update) to a row with a tenant must name a row the database stores for the row's own tenant: another tenant row, a user (`ApplicationUser.TenantId`) or a custom field definition, where a global one fits every tenant. A missing row is refused the same way, so a refusal never reveals that an id exists elsewhere. Audit columns are left to stamping. Join rows (role permissions, role holdings, unit relations) are checked like any other row. |

The shape of an organization tree is checked in the same `SaveChanges`, after the tenant checks and inside the tenant's structure lock ([OrganizationUnit](database.md#organizationunit)).

### In system scope

In system scope the runtime-tenant checks are skipped, but:

- every update still reads the stored row, and a changed `TenantId` is still refused;
- an insert must name its tenant when the runtime has none;
- a row never refers to a row of another tenant;
- every creator or editor stamped must be a user of the row's own tenant (see [Stamping](#stamping)).

### Structural guarantees

- `ConfigureTenantEntity()` makes `TenantId` read-only after save (`PropertySaveBehavior.Throw`), so EF never writes the column on an update.
- No foreign key crosses the tenant boundary, so no cascading delete in one tenant can remove a row of another.
- The reference checks read the stored tenants of referenced rows in one query per table, without the filter, and only compare them (`ApplicationDbContext.TenantReferences.cs`).

## Stamping

Audit columns are stamped from one snapshot of the runtime per save (`IRuntimeExecutionContext.Current`, the same snapshot the save's tenant checks use, so a row's creator and impersonator always belong to one session) and from the context's `Clock`, the injected `TimeProvider` ([ADR 0020](adr/0020-dates-and-times.md)).

- **On insert:** `Created` comes from the clock (in system scope only when the caller left it unset, so a seeded or imported row may keep its own), and `Modified`/`ModifiedById` are cleared.
- **On update:** `Modified` comes from the clock every time; `Created`, `CreatedById` and `ImpersonatedById` keep what the insert recorded.

### Outside system scope

The runtime decides who wrote the row and the clock decides when:

- `CreatedById` on insert and `ModifiedById` on update are the effective user.
- `ImpersonatedById` is the real platform owner while impersonating, otherwise null.
- A creation time, creator, editor or impersonator set by the caller is overwritten.

### In system scope

- A creation time set by the caller is kept.
- A creator set by the caller is kept: a seeded tenant's own user, or the system account of the tenant a row is written on behalf of. Such a creator records no impersonator.
- Where the caller set no creator, the runtime's effective user is stamped as outside system scope.
- An editor is kept only when the caller named it for this save: through `context.NameEditor(row, editorId)`, or by giving `ModifiedById` a value other than the one the row was loaded or attached with. An editor left as the row held it is the last writer, not this one, whoever it names (`Update()` on a detached row marks every column modified as well). It is replaced with the runtime's effective user; with none, the save is refused.
- A name holds for one save, refused or not. A writer on a tenant's behalf names its system account on every write, including the second.
- A user added in the same save counts as a user of its tenant, so a system account is written as its own creator.
- Every creator or editor stamped in system scope, whether set or stamped, must be a user of the row's own tenant: one of its people, or its system account ([ApplicationUser](database.md#applicationuser)), which passes only as the account derived for that tenant. Another tenant's system account is refused like any other user of another tenant. A violation throws a `TenantIsolationException`.

### Refusals and tenants

- A write with no user to stamp and no stamp set by the caller is refused.
- A `Tenant`, inserted only in system scope, gets `Created` from the clock and its creator from the runtime on insert unless the caller set them.
- Seeding stamps each tenant row with a user of its own tenant, never an empty id.

## System scope and leases

Leaving the tenant boundary takes a lease on the context:

```csharp
using (context.BeginSystemScope("reason"))
```

- Leases nest, are logged with their reason when opened and closed, and end on dispose, also when an exception is thrown.
- There is no system-scope flag on the runtime or the factory: a context is in system scope only while a lease is open on it.
- In services, the lease is the platform lease (see [Platform scope](#platform-scope)); `BeginSystemScope` is used only in allow-listed code.

## Choosing a context

Contexts come from the singleton `IApplicationDbContextFactory<ApplicationDbContext>` (`Staffora.Data/ApplicationDbContextFactory.cs`):

| Method | Scope | Use |
|---|---|---|
| `CreateDbContext(runtime)` / `CreateDbContextAsync(runtime)` | The runtime tenant, filtered | Every service |
| `CreateSystemDbContext()` | No runtime: no tenant and no user, so no tenant row is read or written until a lease is opened on it | Startup (migrations, seeding, the platform bootstrap), Identity's stores, session resolution |

- The caller disposes every context it creates, with `using` or `await using`.
- Only the factory sets a context's `RuntimeContext`; its setter is internal to `Staffora.Data` and visible to the tests.
- Production code never builds a `RuntimeExecutionContext` itself: the circuit's runtime comes from dependency injection.
- A directly injected `ApplicationDbContext` is a system context for ASP.NET Identity's stores only (`ApplicationDbContextRegistration.AddApplicationDbContexts`, used by the app and the tests).
- Components never touch a context, and no constructor in `Staffora.Services` or `Staffora.Blazor` takes one; they call services or the factory.

## Platform scope

A platform owner's context is filtered to its runtime tenant like any other: the platform tenant, or the impersonated user's tenant while impersonating.

Reads across tenants from the platform console open the platform lease on the service's own context:

```csharp
using var lease = context.BeginPlatformScope(runtime, reason);
```

`BeginPlatformScopeFor(runtime, tenantId, reason)` opens none for the runtime's own tenant (`Staffora.Services/Authorization/PlatformScope.cs`).

- **Platform reach.** The lease opens a system-scope lease named `Platform console: <reason>` only when the runtime has platform reach (`PlatformScope.HasPlatformReach`): the `Admin` role and membership of the platform tenant, both read from the database when the session resolves, while not impersonating. Without reach it returns no lease and the query stays in the runtime tenant, so an impersonating platform owner sees exactly the impersonated tenant.
- **What the console reads.** The overview's counts, errors and impersonations; the tenant list; a tenant's console (its people, units, roles, holdings, settings and custom fields). The custom fields a dialog of another tenant renders come from the lookup cache, which the owning service lets the console read for any tenant.
- **Workflow steps.** `WorkflowService` takes the platform lease too, so a platform owner moves a tenant, or an entity of any tenant, from the console. What the step writes into another tenant is stamped with that tenant's system account. The tenant dialog loads a tenant under the lease.
- **Onboarding.** Tenant onboarding creates a tenant through `WorkflowService.CreateAndTransitionAsync`. It inserts the tenant row in system scope, stamped with the platform owner (a tenant has no tenant of its own). Before the create step is stored, it inserts the tenant's system account (its own creator) and its Company root (stamped with that account). The step's history belongs to the new tenant, stamped with its account. The roles and the first system owner a new tenant starts with are written on the tenant's behalf inside its create, stamped with its system account (`TenantOnboardingWriter`).
- **What the console writes.** The console writes into no tenant's people, units, roles or settings: its tenant pages are read-only ([ADR 0012](adr/0012-platform-owner-and-tenant-onboarding.md)). The steps it takes on a tenant's users (Unlock, New password, and Save of a user's name, email address and user name, [ADR 0030](adr/0030-platform-owner-corrects-a-users-sign-in-details.md)) run through `WorkflowService`, stamped with the tenant's system account. Custom field definitions, global or a tenant's, are written under the platform lease ([ADR 0032](adr/0032-platform-owner-makes-custom-fields-for-a-tenant.md)).
- **Roles and holdings.** A tenant's roles and role holdings are written without a lease, so the platform owner changes them only by impersonating one of that tenant's users.

## Users

Users are tenant rows like any other: a platform owner belongs to the platform tenant, a system account to the tenant it acts for, and the filter keeps every other tenant's users out of a query.

- A user is written with a creator of its own tenant: the person creating them in their own tenant, otherwise the tenant's system account (the seeder, the bootstrap, the platform console, Identity's own writes).
- Identity's role, claim, login and token tables belong to no tenant. Identity's user store reads and saves users under leases of its own ([ApplicationUser](database.md#applicationuser)).
- Only three places find a user before any tenant applies, each under a lease with its reason: Identity's user store (`ApplicationUserStore`), the session's first read of the signed-in user, and the impersonation authorizer (`ImpersonationAuthorizer`). No other such lookup is allowed.
- Because the store saves under a lease, it refuses to create, update or delete a user of a tenant other than the circuit's effective one while the circuit has a user and no platform reach.

## Acting for a tenant with no circuit

Hosted services have no circuit: the job clock (`JobClock`), the integration outbox's dispatcher (`IntegrationOutboxDispatcher`) and the retention pass (`Retention`).

- A hosted service lists what it needs across tenants on a system context under a lease with its reason, reading ids only (the job clock lists the Active tenants and leaves the platform tenant out).
- It does each tenant's work in a service scope of its own, whose fresh runtime takes that tenant's system account's session in that tenant (`SystemAccountSession.Apply`, refused on a runtime that already holds a session). This is the one `ApplySession` call outside `UserSessionService`, allow-listed in `DataContextScanTests`. The retention pass archives a purged tenant as the platform tenant's system account.
- Every context it opens there belongs to that runtime: filtered to the tenant, held when the tenant is held (`TenantHoldInterceptor`), stamped with the tenant's own account, with the tenant named in its queries as well. It never uses a system context for that work.
- A setting it needs (the time zone) is read for the named tenant through the owning service (`TenantSettingsService.GetOfTenantAsync`), so the lookup cache's rule holds.
- Its writes take the same locks as a user's step, in the same lock order ([Application locks](database.md#application-locks)).
- One tenant's failure is logged and the next tenant goes on.

## Held tenants

A tenant that is Suspended, Pending deletion or Archived holds its data: no workflow step changes its records, whoever asks. Contexts of a runtime in such a tenant are held by `TenantHoldInterceptor`. The states and their access are described in [authorization.md](authorization.md#tenant-states-and-access).

## Writing into another tenant

The only link between tenants is between a staffing company and the customer tenants it manages for its portal ([ADR 0007](adr/0007-cross-tenant-ordering-customer-portal.md)). There is no parent-to-child read path: a staffing company never reads its managed customer tenants' rows; `Tenant.ParentTenantId` links them for the integration outbox alone.

One rule says who acts in a tenant:

- `context.ActorIn(tenantId)` is the runtime's effective user in its own tenant, and that tenant's system account in any other tenant (and on a context without a runtime).
- `context.StampOnOtherTenantsBehalf()`, called inside the lease right before the save, names that actor as the creator of every new row (unless a creator is already named) and the editor of every changed row of a tenant other than the runtime's. `ImpersonatedById` stays null on those rows.
- The runtime's own rows in the same save are left to the save, which records the impersonator beside the effective user.
- The platform console's steps, the delivery of their notices and the integration outbox all stamp through it.

Sending an order writes the job straight into the staffing company's tenant in system scope, stamped with that tenant's system account. A customer's "Enable portal" step creates the managed tenant and its rows inside the staffing company's step transaction, under its customer lock, in system scope under leases with their reasons. Every row is created by the new tenant's own system account, and every history entry has that account as its actor (`StateHistoryWriter.AddOnBehalfOf`). Everything after that crosses as outbox messages.

### The integration outbox

The integration outbox is not an exception to the filter ([Integration outbox](features/operations.md#integration-outbox)):

- Its messages ([IntegrationMessage](database.md#integrationmessage)) are ordinary tenant rows of the sending tenant, written in the sender's step under its filter, hold and stamping.
- Its receipts ([IntegrationReceipt](database.md#integrationreceipt)) are tenant rows of the receiving tenant. Neither tenant reads the other's.
- Only the hosted dispatcher crosses. It lists what is due across tenants (ids only) and claims and marks one message of its sender at a time on a system context, each under a lease named for it (`IntegrationOutboxDispatcher.ListReason`, `ClaimReason`, `RecordReason`), with the sender's row stamped with the sender's system account.
- It delivers in a service scope acting as the target tenant's system account. The handler's writes and the receipt are saved on a context of that session (filtered to the target, held, stamped with its account), and the sender's message is read and marked under the delivery's own lease (`DeliverReason`), all in one transaction.

## Exceptions to the filter

Every table outside the plain `TenantId` filter is listed here; adding one needs an entry and a reason.

| Table | Why | Access |
|---|---|---|
| `Tenants` | It defines the boundary itself; a tenant never learns that another exists. | Its own filter on `Id`: outside system scope a context reads only its runtime tenant's row (none without a tenant). Written only in system scope, the tenant's own row included. The platform owner reads and changes tenants from the platform console. |
| `ErrorLogs`, `ImpersonationAudits` | Platform-owned, not tenant data. | Each has a filter of its own that passes nothing outside system scope, so neither a tenant nor the platform owner's own context reads them without a lease. The error log's `TenantId` is a label of the row, not its owner, so a tenant does not read even the errors that name it. The platform console reads the error log under the platform lease (`PlatformConsoleService.QueryErrorLogAsync`). |
| Custom field definitions | Nullable `TenantId` (null means global). | Their own filter shows the global definitions and the runtime tenant's. A `TenantId` foreign key to `Tenants` (`NoAction`) and a unique index on `(TenantId, Name)` lead with the tenant for the filter and make a name unique per scope. A definition is a state entity ([ADR 0033](adr/0033-custom-fields-have-a-lifecycle.md)): written through its workflow, never deleted, and only an active one reaches a tenant. |
| Custom field values | No `TenantId` of their own. | Reached only through their owner: their filter reads the owner's `TenantId` through the `Entity` navigation. |
| Identity tables (`AspNetRoles`, claims, logins, tokens) | Owned by ASP.NET Identity. | Through Identity managers on the Identity-only context. |

The write rules for these tables are checked in the same `SaveChanges` pass:

| Table | Write rule |
|---|---|
| `Tenants` | Written only in system scope; outside it every insert, update and delete is refused, the tenant's own row included. A platform owner changes a tenant through the tenant workflow, which `WorkflowService` runs under the platform lease. |
| Custom field definitions | `TenantId` never changes. Every definition, global or a tenant's, is written only in system scope (the platform console's lease), through its workflow; its create step inserts it, stamped with its tenant's system account for a tenant's field. Outside system scope every write is refused with one message. Audit columns are stamped as on a tenant row; in system scope the creator or editor must be a user of its tenant, or of the platform tenant for a global definition. The field rules (`AttributeDefinitionRules`) are judged against the stored row ([Entity attributes](database.md#entity-attributes)). |
| Custom field values | Outside system scope the owner must be stored for the runtime tenant (insert, update and delete). On insert and update, in system scope too, the definition must be global or the owner's tenant's, and the value must pass its field's rules (`AttributeValueRules`, a retired option judged against the stored row) and hold something; a row that holds nothing is refused. The field's foreign key is `NoAction`, so no cascade reaches another tenant's values through a global field. |
| `ErrorLogs` | Outside system scope every write is refused. In system scope an insert (the error-log writer's) and a delete (the retention pass's, of rows past the error log's period, [Retention](features/operations.md#retention)) are allowed, and every update is refused: a recorded error is never rewritten. |
| `ImpersonationAudits` | Append-only. Outside system scope every write is refused. In system scope an insert is allowed, and an update only when it gives the stored row's empty `Ended` a value and leaves every other column as stored (judged against the stored row); every other update is refused, a closed row's included. A delete is refused in every scope, and `BulkUpdateGuard` refuses every `ExecuteUpdate` and `ExecuteDelete` of the table in every scope. The trail is never rewritten or erased. |

## Bypass rules

### Query filters

`IgnoreQueryFilters` is allowed only inside a system-scope lease, and only in `Staffora.Data` or a named platform service.

### Bulk statements

`ExecuteUpdate` and `ExecuteDelete` bypass the `SaveChanges` checks and stamping. In tenant scope the filter confines them to the runtime tenant's rows. In system scope they must name `TenantId` in their `Where`. This is a review rule, not enforced: EF caches a compiled query for every context that runs the same shape, so a check at compilation cannot depend on the scope, and a lease is already an explicit, logged decision to leave the boundary.

`BulkUpdateGuard` (an `IQueryExpressionInterceptor` registered in `ApplicationDbContextOptions`) refuses the following in tenant and in system scope, before anything reaches the database:

- **`TenantIsolationException`:**
  - an `ExecuteUpdate` that sets `TenantId`, `Created`, `CreatedById` or `ImpersonatedById`, or a tenant's `ParentTenantId`;
  - an `ExecuteUpdate` or `ExecuteDelete` of a custom field definition or value (a tenant's filter shows the global fields, and only `SaveChanges` judges the field and value rules), except a step's transition lock of definitions, an `ExecuteUpdate` that sets `IsInTransition` alone ([ADR 0033](adr/0033-custom-fields-have-a-lifecycle.md));
  - an `ExecuteUpdate` or `ExecuteDelete` of `ImpersonationAudit`.
- **`WorkflowFieldEditException`:**
  - an `ExecuteUpdate` or `ExecuteDelete` of `StateHistoryEntry` (history is never rewritten or removed by a statement no check sees);
  - an `ExecuteUpdate` or `ExecuteDelete` of `Comment` or `CommentContainer` (comments are never edited or deleted, [ADR 0010](adr/0010-comment-threads-per-audience.md));
  - an `ExecuteDelete` of a notice's rows, an `ExecuteUpdate` of a `StoredMessage`, and an `ExecuteUpdate` of an `OutgoingMessage` or a `MessageRecipient` that sets anything but its bookkeeping (`ApplicationDbContext.NoticeBookkeepingColumns`) and the editor stamp ([StoredMessage](database.md#storedmessage));
  - an `ExecuteUpdate` or `ExecuteDelete` of the integration outbox's rows ([IntegrationMessage](database.md#integrationmessage), [IntegrationReceipt](database.md#integrationreceipt));
  - an `ExecuteDelete` of a state entity, which is deleted only through its workflow ([ADR 0016](adr/0016-deletion-cascades-through-child-workflows.md)).
- **`OrganizationStructureException`:** an `ExecuteUpdate` that sets a unit's `ParentId`, `UnitType` or `State`, or a role holding's `OrganizationUnitId`, which only a structure-checked save changes ([OrganizationUnit](database.md#organizationunit)).

An `ExecuteUpdate` of a state entity stays allowed: the workflow's transition lock sets `IsInTransition` with one.

Because both statements also skip field enforcement and the parent-state check on child rows, the source scan admits their names (a call, a method group or a `nameof`, outside comments and literals) only in these allow-listed files, each with a reason:

- the transition lock step of `WorkflowService`;
- the user store's count of a failed sign-in (`ApplicationUserStore.CountFailedAccessAsync`): one `UPDATE` of the count, the lockout at the threshold and the editor stamp, so concurrent wrong passwords are each counted ([Sign-in hardening](authentication.md#sign-in-hardening-and-security-headers));
- the notification bell's mark-read (`NoticeService`): one `UPDATE` of the reader's own unread copies in the tenant, setting the read flag, the read time and the editor stamp;
- a coordinator's step that handles a staff member's question or message, or a reply in that conversation (`HandledNotices`, `StaffConversationService`): one `UPDATE` of the unread copies of the notices it raised about that record in its tenant, inside the step's or the reply's own transaction, setting only the read flag, the read time and the editor stamp;
- the tenant sign-out of Suspend and Schedule deletion (`TenantSignOut`): one `UPDATE` that rotates the security and concurrency stamps of every user of the tenant inside the step's transaction, naming the tenant and stamped by its system account ([Tenant states and access](authorization.md#tenant-states-and-access));
- the retention pass's removal of error-log rows past their period (`Retention`);
- the tenant purge (`TenantPurge`, below);
- `BulkUpdateGuard`, which names them to judge them.

The authoritative allow-list, with the reason for each entry, is in `DataContextScanTests`.

### The tenant purge

The tenant purge is the one bulk delete past the rules above ([ADR 0042](adr/0042-retention-and-the-tenant-purge.md)).

- `TenantPurge.BeginAsync` opens it on a context in system scope, inside a transaction begun on the context, for one tenant that is in Pending deletion and is not the platform tenant. The tenant row is read under the shared lock (`HoldTenantAsync`). Anything else is refused with a `TenantIsolationException`.
- While the purge is open, `BulkUpdateGuard` lets an `ExecuteDelete` tagged `TenantPurge.Tag` past the workflow, custom field and structure rules (never the impersonation audit's).
- As each tagged statement executes, the guard refuses it unless the purge is open, the statement runs in the purge's transaction and a parameter names the purged tenant. A tagged statement compiled inside a purge and cached is therefore refused outside it.
- The order of the tables comes from the model (`TenantPurge.PlanFor`): each table's rows go before the rows they refer to, a table's references to itself are followed leaves first, and a row an outside table refers to (a user the impersonation audit names) stays.
- Every entity type is either purged with its tenant (through its `TenantId`, or a required key to a tenant row: custom field values, Identity's user joins) or listed outside the tenant with its reason (`TenantPurge.OutsideTheTenant`: `Tenants`, `ErrorLogs`, `ImpersonationAudits`, Identity's application roles and their claims). `TenantPurgePlanTests` holds this for every new table.

### The lookup cache

The lookup cache (`Staffora.Services/Caching/TenantLookupCache.cs`) reads each tenant's catalogue on a system context under a lease of its own (`Lookup cache: read the <catalogue> of one tenant`), with a query that names that tenant. An entry therefore holds what its key names, whichever circuit filled it, and the cache refuses to hold a row of any other tenant. The owning service decides, on every call and before it asks the cache, whether the caller may read that tenant at all ([Lookup cache](features/custom-fields-and-settings.md#lookup-cache)).

### Raw SQL

Raw SQL is allowed only in tests and in the allow-listed files:

- every application lock (`sp_getapplock`), taken in one place (`Staffora.Data/AppLock.cs`): a tenant's structure, schedule, customer and job locks, a record's comment thread lock, the tenant-create lock of the platform console and the platform bootstrap's session lock ([Application locks](database.md#application-locks));
- the migration step that turns on `READ_COMMITTED_SNAPSHOT` once (`Staffora.Data/ApplicationDatabase.cs`, [Isolation level](database.md#isolation-level));
- the structure check's queries, which read with `READCOMMITTEDLOCK, FORCESEEK` (`ApplicationDbContext.OrganizationStructure.cs`), so they see committed rows and seek the tenant's own index range without locking another tenant's rows.

The workflow transition lock is an `ExecuteUpdate`, not raw SQL.

## Source scans

`DataContextScanTests` (`Staffora.Test/Unit/Architecture/`) allows `IgnoreQueryFilters`, `CreateSystemDbContext`, `BeginSystemScope`, `BeginPlatformScope` and raw SQL (`ExecuteSql*`, `FromSql*`, `SqlQuery*`) only in listed files, each with a reason. It also fails on:

- a context created outside a `using`;
- a lease (`BeginSystemScope`, `BeginPlatformScope`, `BeginPlatformScopeFor`) taken outside a `using`;
- a transition save scope (`BeginTransitionSaveScope`) named outside its allow-list (production and test files) or, in production, not the resource of a `using`;
- a name of `ExecuteUpdate` or `ExecuteDelete` outside its allow-list;
- `RuntimeContext =` outside the factory;
- `new RuntimeExecutionContext(` outside `UserSessionService`, which builds the runtime of a session it resolved only to prime its permissions;
- a call of the runtime's `ApplySession`, `SetUser`, `SetBaseTenant`, `StartImpersonation` or `EndImpersonation` outside `UserSessionService` (and the allow-listed `SystemAccountSession`), so nothing else makes a circuit act as another user or tenant;
- a component injecting a context or a context factory;
- a constructor in `Staffora.Services` or `Staffora.Blazor` taking an `ApplicationDbContext`;
- a `new StateHistoryEntry` outside `StateHistoryWriter`, so no other code records history with an actor of its choosing;
- a `new IntegrationMessage` outside `IntegrationOutbox`, or a `new IntegrationReceipt` outside `IntegrationHandlerRegistry`, so no message is queued past its rules and no receipt is written ahead of its message;
- an allow-list entry that no longer matches.

The scans read names only. Reaching `BeginTransitionSaveScope`, the context's set of save-scoped entities, or `ExecuteUpdate`/`ExecuteDelete` through reflection or through a new `ApplicationDbContext` partial class is forbidden, and a reviewer refuses it, since no scan sees it.

## Testing isolation

Isolation is tested against a real SQL Server database (`Staffora.Test/Integration/Data/` and `Staffora.Test/Integration/Infrastructure/TenantSweeps.cs`):

- read and write refusals, including cross-tenant references, the `Tenants` table, custom fields and bulk statements;
- a read sweep across two seeded tenants, repeated after a full portal round trip;
- a stranger sweep over that round trip that fails on any row created or last edited by a user from another tenant.

The recipes are in the `tenant-isolation` skill.

Verified by: `TenantIsolationTests`, `TenantReferenceTests`, `TenantTableTests`, `AttributeIsolationTests`, `AuditStampingTests`, `SystemAccountTests`, `ActingInAnotherTenantTests`, `BulkUpdateGuardTests`, `TenantPurgeGuardTests`, `TenantPurgePlanTests`, `ReadSweepTests`, `TenantFilterCoverageTests`, `DataContextScanTests`.
