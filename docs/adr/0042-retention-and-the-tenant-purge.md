# ADR 0042: Retention removes rows by age, and a tenant past its grace period is purged and archived

## Status

Accepted

## Context

The error log, the notices and the integration outbox grow without limit unless old rows are removed. A tenant scheduled for deletion would otherwise stay in Pending deletion with all its data until a platform owner archived it, and archiving alone keeps the data too.

## Decision

### One hosted pass

`RetentionService` runs `Retention.RunOnceAsync` a minute after start and then every `Retention:Interval` (one hour by default), using the injected `TimeProvider`. The periods are platform configuration in the `Retention` section of `appsettings.json` (`RetentionOptions`), validated at start:

| Option | Default |
|---|---|
| `ErrorLogDays` | 90 |
| `ReadNoticeDays` | 180 |
| `ProcessedOutboxDays` | 30 |
| `PendingDeletionGraceDays` | 30 |
| `BatchSize` | 1000 (at most 4000, so one statement stays below SQL Server's per-statement lock escalation threshold) |

`Retention:Enabled` turns the pass on; it is on in `appsettings.json` and off in the test hosts.

### Four rules, each in batches

- **Error log.** Rows whose `Occurred` is older than the period are removed by a bulk delete in system scope, ordered by key, one batch per statement. The error log is a platform table; its `TenantId` only labels a row.
- **Read notices.** Per tenant: a person's copy (`MessageRecipient`) read longer ago than the period; then a delivered notice (`StoredMessage`) older than the period with no copy left; then a queued notice (`OutgoingMessage`) that was delivered, is older than the period and has no stored notice left. An unread copy is never removed, nor the notice it belongs to.
- **Processed integration messages.** Per sending tenant: a message Delivered whose `Finished` is older than the period; then a receipt older than the period unless a message with its id is still pending or a dead letter (the receipt is what keeps a retried delivery from running twice). Pending messages and dead letters are never removed.
- **The tenant purge**, below.

Notices and messages are removed by tracked deletes in system scope, the one path the data context leaves for them (bulk statements on their rows remain refused), with every query naming its tenant. A failure for one tenant or rule is logged and the pass continues; the next pass tries again.

### The purge

- A tenant whose latest entry into Pending deletion in its state history is older than the grace period is purged in one transaction of its own, as the platform tenant's system account (`SystemAccountSession`).
- `TenantPurge.BeginAsync` reads the tenant's row under the shared lock that every tenant write takes (`HoldTenantAsync`) and refuses anything but a tenant in Pending deletion that is not the platform tenant. The grace period is checked again inside the transaction.
- The tenant moves to Archived through its workflow's system step (`tenant-purged`, PendingDeletion to Archived, system-driven, never a button). Then every row of the tenant is removed, together with the integration messages other tenants sent it, and the transaction commits.
- Because the transaction holds the tenant's row from its first statement, a "Cancel deletion" waits for the purge and then finds the tenant Archived, and a failure anywhere rolls back the purge and the step together.

### The order comes from the model

`TenantPurge.PlanFor(model)` classifies every entity type as one of:

- a tenant row (it has `TenantId`; custom field definitions included);
- a row that a tenant row owns through a required key (custom field values, Identity's user joins);
- one of the types listed as outside the tenant, each with its reason (`Tenant`, `ErrorLog`, `ImpersonationAudit`, Identity's application roles and their claims).

A type that is none of these stops the plan, so a new table cannot be left out. The plan orders the types so that every row is removed before the rows it refers to (the keys are `NoAction`), follows a table's references to itself leaves first (units under their parents, users created by other users), and fails on a cycle. Users go last, since every row names its creator.

### The guard admits only the purge

The purge's bulk deletes carry a tag (`TenantPurge.Tag`). `BulkUpdateGuard` lets a tagged delete past its workflow and isolation rules (state entities, history, comment threads, notices, outbox rows, custom fields, units) only while a purge is open on the context, and judges every tagged statement as it executes: it runs only in the purge's transaction and only naming the purged tenant. A tagged delete outside the purge, or one compiled inside and run outside, is refused. Bulk statements on the impersonation audit remain refused in every case.

### The impersonation audit outlives the purge

The audit is append-only and its rows refer to the users they name, so a user named by an audit row is kept, together with the users it refers to, and the purge reports them as kept. Every other row of the tenant is removed; a purged tenant that was never impersonated keeps only its Archived row.

### Reporting

Every rule logs what it removed per tenant, and the pass logs one summary line. The pass records its counts per rule on the telemetry counter of [ADR 0039](0039-opentelemetry-is-the-telemetry-standard.md). The last pass since the application started is kept in memory (`Retention.LastPass`) and shown on the platform console's health page (`/superadmin/errorlog`), read through `PlatformConsoleService.LastRetentionPass`, which checks platform reach. No table records passes.

## Rationale

Age-based rules in small batches keep the tables bounded while keeping each statement below SQL Server's per-statement lock escalation threshold, which lowers the chance that one tenant's cleanup blocks another tenant's writes. Running the purge in one transaction that holds the tenant's row makes it all-or-nothing and serialises it with "Cancel deletion". Deriving the deletion order from the model, and refusing a type it cannot classify, means a new table cannot be forgotten. Tagging the purge's statements lets the guard admit exactly those statements and no others.

## Consequences

- A purge is one transaction: a large tenant writes a large transaction log, and the tenant's row is held for its duration. Each statement remains a batch, which keeps it below the per-statement escalation threshold, but the transaction holds its locks until commit: SQL Server can still escalate under lock-memory pressure, so a large purge may briefly block other tenants' writes to the same tables.
- A user named by an impersonation audit row stays, with its name and address, until the audit's key to the user is changed, which requires a schema change.
- A staffing company's customer that linked a purged portal keeps the link to the Archived tenant's row: it is the staffing company's record, and a purge removes nothing of another tenant except the messages sent to the purged one.
- A tenant put in Pending deletion without a history entry (a direct write) is never purged, since its grace period cannot be established.
- The last pass is each application instance's own and is lost on restart, like the job clock's in-memory state (KI-69).

## Related Decisions

- [ADR 0007](0007-cross-tenant-ordering-customer-portal.md): the integration outbox.
- [ADR 0012](0012-platform-owner-and-tenant-onboarding.md): the tenant lifecycle.
- [ADR 0013](0013-tenant-isolation-in-the-data-context.md): system scope and bulk statement guards.
- [ADR 0039](0039-opentelemetry-is-the-telemetry-standard.md): telemetry counters.
