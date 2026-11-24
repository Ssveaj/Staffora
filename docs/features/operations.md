# Operations

The work described here runs without a Blazor circuit: hosted services and logging pipelines that act for a tenant through its system account ([tenant-isolation.md](../tenant-isolation.md)) or on the platform's own rows. The platform console shows their state to the platform owner:

- a tenant's console lists its integration dead letters ([Integration outbox](#integration-outbox));
- **Platform health** (`/superadmin/errorlog`) lists the error log and the last retention pass ([Error log and log scopes](#error-log-and-log-scopes), [Retention](#retention)).

Traces, metrics and logs leave the app over OpenTelemetry when a collector is configured ([Telemetry](#telemetry)).

## Integration outbox

How a tenant sends a linked tenant something after a portal request was sent ([ADR 0007](../adr/0007-cross-tenant-ordering-customer-portal.md)):

- the supplier's job state with the staff members' names (`job-state`);
- the customer's cancellation (`order-cancel`);
- the customer thread's comments;
- the service catalogue's offers ([below](#the-service-catalogues-messages)).

Code: `Staffora.Services/Integration/`, [IntegrationOutbox.cs](../../Staffora.Data/IntegrationOutbox.cs). Tables: [IntegrationMessage](../database.md#integrationmessage), [IntegrationReceipt](../database.md#integrationreceipt).

### Queuing

- `IntegrationOutbox.Enqueue(context, kind, targetTenantId, correlationId, payload)` adds a pending message of the context's runtime tenant, due at once, to the caller's context. The caller saves it with the write it belongs to, so a rolled-back step sends nothing.
- It is refused under a lease, where the runtime's tenant need not be the sender, and it is the only constructor of a message (a source scan).
- The data context refuses a message inserted as anything but new and due, and a change of its delivery outside the dispatcher's leases.
- A step that queues holds its record's lock (a job's messages under its job lock), so a correlation's messages are numbered in the order they commit.
- Payloads carry only what the sender chooses to expose, at most 4000 characters.

### Handlers

- `services.AddIntegrationHandler<THandler>(kind)` registers one scoped `IIntegrationHandler` per kind. As the app starts, `IntegrationHandlerRegistry` refuses a kind registered twice, an empty or overlong kind, and a type that handles nothing.
- A handler gets the message as a snapshot (`IntegrationDelivery`) and a context of the target tenant to write on. It never commits and never opens a lease.
- A handler need not guard against a second delivery: the registry runs it at most once per message and target tenant, adding the message's receipt in the same transaction and running nothing when the receipt is there already.
- **Moving records.** A handler moves a state entity only through a system-driven step on the context it is handed (`WorkflowService.RunSystemStepInTransactionAsync` with `IntegrationDelivery.AfterCommit`). The step (cascades included), its history and its notices as outbox rows commit with the receipt and the mark, and the dispatcher hands the notices on only after the delivery's commit. A delivery rolled back after the step leaves the record unmoved and tells nobody; its retry moves it once.
- `IntegrationHandlerScanTests` holds every handler to its delivery: no public workflow step, no context or transaction of its own, no lease.

### The dispatcher

`IntegrationOutboxDispatcher`, one per process. The hosted `IntegrationOutboxService` runs its passes every `IntegrationOutbox:Interval` (15 seconds by default) while `IntegrationOutbox:Enabled` is set: on in `appsettings.json`, off in the test hosts.

A pass lists what is due on a system context under one lease (ids only), then takes each message in turn:

1. **Claim**, in a transaction of its own. Both tenants' rows are held first; their states, the link between them, the correlation's order and the attempts left are judged again. Then one more attempt is counted, the `ClaimToken` renewed and a two-minute lease taken.
2. **Delivery**, in a service scope acting as the target tenant's system account (`SystemAccountSession`), in one transaction begun on a context of that session so the target tenant's row is its first lock:
   - the sender's row and the message are read under the delivery's lease;
   - the handler runs through the registry outside the lease, and its writes and the receipt are saved as the target tenant (filtered, held, stamped with its system account);
   - the message is marked delivered under the lease, stamped with the sender's system account (`ActorIn`), and the whole is committed.

A message that becomes due within the pass (the next of its correlation) is taken in the same pass. At most `IntegrationOutbox:BatchSize` (100) messages are taken per pass.

### The service catalogue's messages

`service-offered`, `service-changed` and `service-withdrawn` go from a staffing company to each of its portals, handled by `OfferedServiceCopy`.

- Each is a full snapshot. A withdrawal names the service alone. Letters of every language cross unescaped so the longest texts fit; only a description of characters JSON always escapes is shortened until the snapshot fits.
- Their correlation is one per service and portal (`ServiceOfferSync.CorrelationFor`: a name-based id over the service's cross-tenant id and the portal), so one service's messages to one portal are delivered in order and a held portal stalls no other.

### Order

A message waits while an earlier one of its sender's correlation is pending, retrying included, so a correlation's messages are handled in the order written. A dead letter does not hold the next one up.

### Retry and dead letter

- A delivery that throws is rolled back whole and recorded. The next attempt comes after 30 seconds, doubling, at most an hour (`IntegrationRetryRules.RetryAfter`), logged as a warning.
- After the eighth attempt the message is a dead letter, logged as an error, so the platform's error log holds why.
- The message keeps only the failure's kind (`LastFailure`), never its details: it is the sender's row, and the failure arose in the target tenant.

**On the platform console.** A tenant's console has a **Dead letters** tab (`/superadmin/tenants/{id}/dead-letters`, `TenantConsoleDeadLetters`) on the one list surface (`PlatformDeadLetterList`):

- searched by kind, filtered by direction, the newest given up first;
- answered by `PlatformConsoleService.QueryDeadLettersAsync` under the platform lease alone, platform reach checked first; the messages are their senders' tenant rows, so every query names the tenant;
- lists the messages the tenant sent and those meant for it, each with its kind, the other tenant, its attempts, when it was given up and its last failure's kind;
- a row opens `DeadLetterDialog`, read-only, which adds its correlation, its place in its sender's order and when it was written, never its payload, which holds what the tenants' people wrote;
- the tenant's Overview counts them (`TenantConsoleSummary.DeadLetters`).

Nothing is retried from the console.

### Held

Messages flow only between two Active tenants (`TenantStates.ExchangesMessages`). A message whose sender or target is in any other state is not taken and spends no attempt, however long it waits, and goes out at the first pass once both are Active again (an archived tenant's never). A tenant that stops being Active between the listing and the claim, or between the claim and the delivery, is seen under its row's lock, and the claim is given back with its attempt.

### Linked tenants only

A message between two tenants of which neither manages the other (`Tenant.ParentTenantId`, `TenantStates.AreLinked`) is a dead letter at once ("The two tenants are not linked."), and nothing is written. So is one to or from the platform tenant, which is never linked, whatever a row written past the data context's rules says.

### Two dispatchers

- A message is claimed before it is delivered, and the claim is checked on every later write of the row, so two dispatchers never both deliver it.
- A dispatcher whose claim lapsed (it stalled past the lease) and was taken over writes nothing, and the receipt's unique key refuses its handler's writes should it get that far.
- Only a conflict on the message's own mark is read as a claim taken over. A concurrency conflict in the handler's writes is its failed attempt, recorded and retried after its back-off.
- A failure outside a delivery (a claim lost to a deadlock) is logged and the pass goes on.

### Notices

The same passes deliver again the in-app notices the messaging pipeline could not ([Notifications](notifications-and-comments.md#notifications), "The pipeline"): a failed one after its back-off, a pending one once it is a minute old, only while the tenant's data is not held. Each is claimed through its attempt count, delivered as the tenant's system account, and given up after eight attempts.

Verified by: `IntegrationOutboxTests`, `IntegrationOutboxRulesTests`, `IntegrationHandlerScanTests`, `OrderSyncTests`, `NoticeRetryTests`, `ActorInTests`, `ActingInAnotherTenantTests`, `PlatformConsoleTests`, `TenantConsolePageTests`.

## Error log and log scopes

Every entry of level Error or Critical the application logs is stored in the platform's error log ([ErrorLog](../database.md#errorlog)). The platform owner reads it on `/superadmin/errorlog`.

### Log scopes

Each request logs inside a scope (`LogScope`, `Staffora.Services/Diagnostics/`) naming its correlation id, its path (never the query string) and the tenant, effective user and impersonator it acts as.

- **Correlation id.** The caller's `X-Correlation-ID` header when it is well formed (letters, digits, `-`, `_`, `.`, `:`, at most 64 characters), otherwise the request's trace id or a new one. The response returns it in the same header.
- **Where the scope opens.** In the outermost middleware ([RequestLogScopes.cs](../../Staffora.Blazor/Helper/RequestLogScopes.cs)), so the exception handler logs an unhandled exception inside it.
- **Identity.** Once authentication has read the cookie, `UseRequestLogIdentity` names who the request acts as: from the runtime's snapshot when a session is resolved, otherwise from the ids the cookie names (`UserSessionService.DescribeRequestAsync`). That method reads the effective user's tenant once per process, remembers a user that does not exist for a minute (so a deleted user's cookie costs no query per request) and decides nothing.
- **The error page** (`Pages/Error.razor`) shows the correlation id as the request id, so a user's report leads to the request's rows.
- **Circuits.** A circuit runs inside its connection's request. Each inbound activity of the circuit (an event, a JavaScript call) adds a scope with a correlation id (the current trace id, or a new one), the current page and the identity of the circuit's resolved session, read once from the runtime's snapshot. The scope follows the trace, so an error inside a traced workflow step carries that step's trace id ([Telemetry](#telemetry)).

### The pipeline

- `ErrorLogLoggerProvider` turns each Error or Critical entry into an `ErrorLog` row with its scope's values and queues it on `ErrorLogQueue`, a bounded queue that never makes the logging caller wait. When it is full, the entry is dropped and counted.
- `ErrorLogWriter`, a background service, stores what gathered in batches on a system context under a lease of its own. The first batch stored after entries were dropped carries one more row, a Warning saying how many were dropped since the last such row. The page shows the instance's live count above the list.
- The writer's whole flow is suppressed, so nothing a failing write logs comes back as a row. The error log's own classes and EF Core's categories are left out: their errors repeat a failed command or report an outcome a service turns into a failed `Result`.
- `ErrorLogQueue.FlushAsync` completes once everything queued before it is handled, which tests wait on instead of the clock.

### No secrets

Only the formatted message and the exception's text are kept, never the entry's state or a scope's other values. A template value whose name marks a secret (`{NewPassword}`, `{AccessToken}`) is cut out of the message and out of the exception's text. Both texts then pass `SecretScrubber`, which replaces:

- the value of `name=value` or `name: value` (quotes and spaces allowed, as in JSON or a connection string) when the name, in any case and without `_`, `-` and `.`:
  - contains password, passwd, pwd, secret, token, credential, connectionstring, signature, securitystamp or a known kind of key (apikey, accesskey, accountkey, privatekey, signingkey, encryptionkey, masterkey, subscriptionkey, storagekey), or
  - is key, sig, code, hash, otp, authcode, authorizationcode, verificationcode or resetcode on its own.

  So `newPassword=`, `db_pwd=`, `AccountKey=` and a query string's `code=` are caught, while `StatusCode=`, `CacheKey=` and `Timestamp=` are not. An exception type's name and a stack trace's `File.cs:line 12` are never pairs.
- the credentials of an `Authorization` header (any scheme; the scheme is kept), and a `Cookie` or `Set-Cookie` header to the end of its line;
- a bearer token, and anything shaped like a JSON web token (three base64url segments, the first starting `eyJ`), named or not.

The scrubber is defence in depth, not a guarantee: a secret in any other shape, such as prose ("the password is …"), is kept. Each text is cut to its column plus 2,048 characters before it is scanned, so a huge message is never scanned in full on the logging caller's thread, and to its column after.

### Database commands

`DatabaseCommandLog` (`Staffora.Data/`), on every context beside the bulk-update guard, logs a failed command as an error with its SQL text, its parameters' names (never their values) and its duration, and a command slower than one second as a warning. A command that ends after its caller's token was canceled is reported by EF Core as canceled, not failed, so it logs nothing.

### A reader who left is no failure

A page left while it reads (a full page load elsewhere ends the circuit and disposes the page, which cancels its token) may see SQL Server end the command with a `SqlException` ("A severe error occurred on the current command", "Operation cancelled by user") rather than a cancellation.

`ReadCancellation` (`Staffora.Services/Diagnostics/`) handles this. These reads call `ReadCancellation.ThrowIfReaderLeft` first in their catch-all:

- the coordination workspace (`CoordinationService.ReadAsync`, called with the page's own token);
- the staff timeline (`CoordinationService.ReadStaffMembersAsync` and `ReadBookingsAsync`);
- the availability reads they ask (`StaffAvailabilityService`);
- every read through `DeadlockRetry`.

Once the caller's token was canceled, whatever ended the read goes on as a cancellation, logged as information when it was not one already. Only a failure while the reader still waits is logged as an error and answered.

### The page: Platform health

`Pages/SuperAdmin/ErrorLog.razor` at `/superadmin/errorlog`, "Platform health" in the console's Platform group. A plain page on the one list surface, with no banner and no refresh.

- A `ListView` over `PlatformConsoleService.QueryErrorLogAsync` (`ErrorLogList`): searched by message, source, tenant, request path, exception type and correlation id; filtered by level and by period (Last day, Last 30 days, Last 90 days; none is the last seven days, and the address keeps `?days=30` and `?level=critical`); grouped by UTC day ("Today · Sunday 22 November"); newest first; fifty rows a page. Filtered, sorted, grouped and cut in SQL without the exception text.
- Each row has its level in its state strip (Warning info, Error warning, Critical danger), its time, its message with the request path (or the logger's own name) under it, its tenant, the user it acted for and who impersonated them, and its correlation id.
- A row unfolds nothing beneath it. A click opens a read-only dialog (`ErrorEventDialog`, through `OpenFormDialogAsync`) with the entry's facts and its exception, which the page reads as the row opens (`PlatformConsoleService.GetExceptionDetailAsync`, platform reach only).
- Errors this instance dropped because the queue was full are counted above the list.
- A Retention section above the list shows the retention pass's last run and what each rule removed ([Retention](#retention)).

### Registration

`services.AddErrorLog()` (`ErrorLogRegistration`) and `services.AddRequestLogScopes()` in `Program.cs`, with `app.UseRequestLogIdentity()` early in the pipeline.

Verified by: `ErrorLogTests`, `ErrorLogTextTests`, `ErrorLogPipelineTests`, `ErrorLogPageTests`, `ReaderLeftTests`, `RetentionSummaryTests`, and the platform console's and smoke journeys.

## Retention

[ADR 0042](../adr/0042-retention-and-the-tenant-purge.md). One hosted pass removes what is past its age and purges the tenants whose deletion is due ([Retention.cs](../../Staffora.Services/Platform/Retention.cs), [Retention.TenantPurge.cs](../../Staffora.Services/Platform/Retention.TenantPurge.cs), [TenantPurge.cs](../../Staffora.Data/TenantPurge.cs)).

### When

`RetentionService` runs `Retention.RunOnceAsync` a minute after the app starts (`Retention:FirstPassDelay`) and then every `Retention:Interval` (an hour), on the injected `TimeProvider`, while `Retention:Enabled` is set: on in `appsettings.json`, off in the test hosts (`AppHost`). Registration: `services.AddRetention(configuration)`.

### The periods

Platform configuration, the `Retention` section of `appsettings.json` (`RetentionOptions`, validated as the app starts):

| Setting | Default | Limit |
|---|---|---|
| `ErrorLogDays` | 90 | at least 1 |
| `ReadNoticeDays` | 180 | at least 1 |
| `ProcessedOutboxDays` | 30 | at least 1 |
| `PendingDeletionGraceDays` | 30 | at least 1 |
| `BatchSize` (rows one statement or one save removes) | 1000 | 1 to 4000 |

### The rules

Each rule runs in batches, every query naming its tenant. One tenant's or rule's failure is logged and the pass goes on.

- **Error log.** Rows whose `Occurred` is older than the period: a bulk delete in system scope, ordered by key.
- **Read notices.** Per tenant: a person's copy read longer ago than the period; then a delivered notice older than the period with no copy left; then a queued notice delivered, older than the period, with no delivered notice left. Unread copies and their notices stay. Tracked deletes in system scope, the one path the data context leaves for a notice's rows.
- **Processed outbox.** Per sending tenant: an integration message Delivered whose `Finished` is older than the period; then a receipt older than the period, unless a message of its id is still pending or a dead letter. Pending messages and dead letters stay.
- **Tenant purge.** A tenant whose latest entry into Pending deletion (its state history) is older than the grace period. In one transaction, as the platform tenant's system account:
  1. `TenantPurge.BeginAsync` holds the tenant's row and refuses any tenant but one in Pending deletion that is not the platform tenant;
  2. the grace is read again;
  3. the tenant moves to Archived through its system step `tenant-purged`;
  4. every row of the tenant goes, in the order the model gives (`TenantPurge.PlanFor`), and so do the integration messages other tenants sent it.

  Only the `Tenants` row stays, Archived, together with the users an impersonation audit names, which the append-only audit keeps. A tenant moved to Pending deletion without a history entry is never purged. A cancellation of the deletion waits for a running purge and then finds the tenant Archived.

### Counted

- Each rule logs what it removed (per tenant, and per table for a purge), and the pass logs one summary.
- The telemetry counter `staffora.retention.rows_removed` takes the rows per rule ([Telemetry](#telemetry)).
- The last pass since the app started (`RetentionPass`: when, the rows each rule removed, the tenants purged and their rows, the failures) is kept in memory and shown on **Platform health** (`RetentionSummary`, read through `PlatformConsoleService.LastRetentionPass`, platform reach only).

Verified by: `RetentionTests`, `TenantPurgeGuardTests`, `TenantPurgePlanTests`, `TenantWorkflowTests`, `RetentionSummaryTests`.

## Telemetry

[ADR 0039](../adr/0039-opentelemetry-is-the-telemetry-standard.md). The app speaks OpenTelemetry: traces, metrics and logs, exported over OTLP to whatever collector `OTEL_EXPORTER_OTLP_ENDPOINT` names, and nothing at all when it names none.

### Off unless configured

`services.AddTelemetry(configuration)` ([Telemetry.cs](../../Staffora.Blazor/Helper/Telemetry.cs)) always registers the app's own instruments (`StafforaTelemetry`). Only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (an environment variable or the same key in `appsettings`) does it register the OpenTelemetry SDK:

- the resource's service name `Staffora`;
- **traces** from ASP.NET Core, Blazor's circuits and events (`Microsoft.AspNetCore.Components`, `Microsoft.AspNetCore.Components.Server.Circuits`), `HttpClient`, SQL Server (the client under EF Core) and the `Staffora` source;
- **metrics** from ASP.NET Core, Blazor (its components, their lifecycle and its circuits), `HttpClient`, SQL Server, EF Core's own meter (`Microsoft.EntityFrameworkCore`) and the `Staffora` meter;
- the app's **logs**;
- all three sent by `UseOtlpExporter`, which reads the usual `OTEL_EXPORTER_OTLP_*` settings (gRPC by default).

Unset, no provider, exporter or hosted service is registered and nothing listens to the `Staffora` source, so a step starts no activity and a gauge whose value takes a query is not read.

### No personal data

- The app's tags are keys and ids only (an entity's type and id, a step's key, an outcome, a message kind and id, a retention rule), never a name or an address.
- A database span carries the command text EF Core sent, whose values are parameters, never the parameters themselves: `OTEL_DOTNET_EXPERIMENTAL_SQLCLIENT_ENABLE_TRACE_DB_QUERY_PARAMETERS` stays unset.
- Logs go out as the app writes them, and no log message names a person ([No secrets](#no-secrets)).

### The app's instruments

`StafforaTelemetry` (`Staffora.Services/Diagnostics/`) is a singleton registered with the workflow services, the job clock and the outbox, its meter made by the host's `IMeterFactory`. It owns one `ActivitySource` and one `Meter`, both named `Staffora`.

| Instrument | Kind | Unit | Tags | Written by |
|---|---|---|---|---|
| `staffora.workflow.steps` | counter | `{step}` | `staffora.entity.type`, `staffora.step`, `staffora.outcome` (`done`, `refused`, `failed`) | every step `WorkflowService` takes, a user's or the system's inside another write |
| `staffora.workflow.step.duration` | histogram | `s` | the same | the same |
| `staffora.outbox.pending` | gauge | `{message}` | none | the dispatcher, at the end of each pass, while someone listens |
| `staffora.outbox.dead_letters` | gauge | `{message}` | none | the same |
| `staffora.outbox.dispatch.duration` | histogram | `s` | `staffora.outbox.kind`, `staffora.outcome` (`delivered`, `failed`, `dead_lettered`, `held`, `skipped`) | the dispatcher, per message |
| `staffora.job_clock.passes` | counter | `{pass}` | none | the job clock, per pass |
| `staffora.job_clock.steps` | counter | `{step}` | `staffora.step` (`job-period-starts`, `job-period-ends`) | the job clock, per pass, for the steps it took |
| `staffora.retention.rows_removed` | counter | `{row}` | `staffora.retention.rule` | the retention pass, per rule that removed rows (`RecordRetentionRemoved`) |

Durations are read from the injected `TimeProvider`'s timestamps.

The activities:

- `staffora.workflow.step` around each step, named by its entity type and step key, tagged with the entity's type and id, the step, whether the system took it and the outcome. A step that throws is marked an error.
- `staffora.outbox.pass` and `staffora.job_clock.pass` around each pass of the hosted services.
- `staffora.outbox.dispatch` per message (its id, kind and outcome).
- `staffora.retention.pass` for the retention pass.

### A logged error leads to its trace

The error log's correlation id is the trace id wherever one runs:

- a request's scope takes the trace id when the caller sent no `X-Correlation-ID`;
- a circuit event's scope follows the trace (`LogScope.FollowsTrace`), so an error inside a workflow step carries that step's trace id;
- the hosted passes run inside their activity, so an error there carries the pass's.

The error log's search finds the id, and its dialog shows it. With no collector, a circuit event and a hosted pass have no trace, and their errors keep an id of their own.

### Looking at it locally

Any OTLP receiver shows the traces, metrics and structured logs, for example the .NET Aspire dashboard. Start the app with the endpoint set (PowerShell: `$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"; dotnet run --project Staffora.Blazor`).

- **Traces** shows a step as `staffora.workflow.step` under the request or circuit event that took it, with its SQL commands beneath.
- **Metrics** lists the `Staffora` meter.
- **Structured logs** holds the logs. An error's correlation id from the platform console's error log is the trace id to search for.

Verified by: `TelemetryRegistrationTests`, `StafforaTelemetryTests`, `WorkflowServiceTests`, `ErrorLogPipelineTests`.
