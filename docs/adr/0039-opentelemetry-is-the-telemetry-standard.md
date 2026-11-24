# ADR 0039: OpenTelemetry is the telemetry standard

## Status

Accepted

## Context

The application and its own background work (workflow steps, the integration outbox, the job clock, the retention pass) need traces, metrics and logs, and an error in the platform's error log must lead to its trace. The error log records what went wrong, but not how long operations take, how much waits in the outbox, or which commands a slow step sent.

## Decision

- **OpenTelemetry, exported over OTLP.** The application uses the OpenTelemetry SDK for .NET with the ASP.NET Core, `HttpClient` and SQL Server instrumentation, EF Core's own meter, and the application's own source and meter, all named `Staffora`, and sends traces, metrics and logs with the OTLP exporter. No vendor agent or SDK is used: any OTLP collector or backend accepts the data, and the Aspire dashboard shows it on a developer's machine ([Operations: Telemetry](../features/operations.md#telemetry)).
- **Off unless an endpoint is set.** The SDK is registered only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (`AddTelemetry`). When it is unset, the application registers its instruments and nothing else: no provider, exporter or hosted service, no listener on the application's source (so no step starts an activity) and no gauge read that costs a query. A collector is opt-in.
- **One place for the application's instruments.** `StafforaTelemetry` (`Staffora.Services/Diagnostics/`) holds the one `ActivitySource`, the one `Meter` (created by the host's `IMeterFactory`) and every instrument's name and tags. Services call it and never create instruments of their own. Its durations come from the injected `TimeProvider` ([ADR 0020](0020-dates-and-times.md)).
- **SQL Server instrumentation rather than EF Core's.** EF Core's tracing package is still a beta, while the SQL Server client's instrumentation is stable and sees every command EF Core sends. EF Core's metrics come from its own meter.
- **Ids, never people.** Tags are keys and ids only. Database spans carry the command text with its parameters' names, never their values (the SQL Server instrumentation's parameter recording stays off).
- **The error log's correlation id is the trace id.** A request's scope takes the trace id when the caller sent none; a circuit event's scope follows the trace; and the hosted passes run inside their own activity, so an error logged inside a workflow step or a pass carries that trace's id.

## Rationale

OTLP is vendor-neutral, so any collector or backend can receive the data. Registering nothing without an endpoint keeps the cost of telemetry at zero where it is not used. One class for every instrument keeps names and tags consistent and keeps personal data out of them.

## Consequences

- A traced application starts an activity per workflow step, per outbox message and per hosted pass (every 15 seconds for the outbox), and reads the outbox's backlog with one grouped count per pass while a meter listens.
- Logs exported over OTLP carry what the application logs, as the console does. The error log's scrubbing of secrets ([Operations: Error log and log scopes](../features/operations.md#error-log-and-log-scopes)) applies to its own rows, not to the export, so no log message may hold a secret or a person's name.
- Package versions are pinned in `Staffora.Blazor.csproj` (the SDK and exporter 1.20.1, the instrumentation 1.20.2) and are upgraded together.

## Related Decisions

- [ADR 0020](0020-dates-and-times.md): time from the injected `TimeProvider`.
- [ADR 0042](0042-retention-and-the-tenant-purge.md): the retention pass records its counts on the telemetry counter.
