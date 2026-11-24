# ADR 0020: Dates and times

## Status

Accepted

## Context

Jobs run once or on a weekly timetable over a whole period. Conflicts, the job clock and "time to start" must be correct across daylight-saving changes and must give identical results on the server and in tests.

## Decision

- Instants are stored in UTC (`DateTime` with `Kind.Utc`, or `DateTimeOffset` where an offset matters).
- Calendar facts without a time of day, such as period start and end and absence days, are `DateOnly`. Times of day, such as timetable slots and work-pattern segments, are `TimeOnly`.
- Each tenant has an IANA time zone, and every conversion between a local time and an instant goes through it (`TenantClock`).
- The current time comes only from an injected `TimeProvider`.
- The UI uses one fixed culture for formatting.
- A job happens once (a day, a start and an end, ending on the next day when the end is at or before the start) or every week over a period ([ADR 0027](0027-generic-resource-staffing.md)). Its occurrences come from one rule (`JobRules.Occurrences`), each start and end turned into an instant through the tenant's zone. An occurrence that the spring change leaves without length does not occur, and one past midnight is judged whole and cut at local midnight only for the readers that draw days. The job clock reads a job's window as instants: a recurring job's period's days, a single job's times.

## Rationale

UTC instants with explicit date-only and time-only types, one conversion path through the tenant's zone and an injectable clock make time-dependent rules correct across daylight-saving changes and reproducible in tests.

## Consequences

- `DateTime.Now` and `DateTime.UtcNow` are banned outside an allow-list (source scan test), and date-only members are never `DateTime` (scan test).
- Tests use a fake clock and include a slot conflict across a daylight-saving change, a single job across midnight and a recurring period across both daylight-saving changes (`JobOccurrenceRulesTests`).
- The tenant's time zone is a tenant setting, defaulting to the platform's time zone at onboarding.

## Related Decisions

- [ADR 0027](0027-generic-resource-staffing.md): single and recurring jobs.
- [ADR 0018](0018-test-strategy.md): fake clocks in tests.
- [ADR 0043](0043-a-closed-day-is-a-row-without-a-lifecycle.md) and [ADR 0045](0045-public-holidays-are-computed-from-a-tenant-setting.md): whole days blocked from midnight to midnight in the tenant's zone.
