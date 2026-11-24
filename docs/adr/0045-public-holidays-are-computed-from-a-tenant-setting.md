# ADR 0045: Public holidays are computed from a tenant setting, never stored

## Status

Accepted

## Context

A tenant that does not work on public holidays would otherwise have to enter each one as a closed day ([ADR 0043](0043-a-closed-day-is-a-row-without-a-lifecycle.md)), year after year, including the movable ones (Easter, Ascension Day, Midsummer) on new dates every year. A tenant's public holidays must be computed, never entered by hand.

## Decision

- **One pure rule answers the days.** `PublicHolidays` (`Staffora.Core/Scheduling/`) answers the public holidays and eves of any year or date range for a calendar (`HolidayCalendar`), each a `PublicHoliday` with its date, its English name and its kind (`PublicHolidayKind.Holiday` or `Eve`). It is deterministic and reads nothing: no table, no clock, no network. Easter Sunday comes from the anonymous Gregorian computus, and the movable days follow it (Good Friday -2, Easter Monday +1, Ascension Day +39, Whit Sunday +49).
- **Sweden is the one calendar.** The Swedish calendar names New Year's Day, Epiphany, Good Friday, Easter Sunday, Easter Monday, May Day, Ascension Day, Whit Sunday, National Day, Midsummer Day (the Saturday from 20 to 26 June), All Saints' Day (the Saturday from 31 October to 6 November), Christmas Day and Boxing Day, and the eves most people have off: Midsummer Eve (the Friday before Midsummer Day), Christmas Eve and New Year's Eve. Whit Sunday is a Swedish public holiday and always a Sunday, so it is listed for the calendar's sake and blocks nothing a weekday job would meet. Another country is a new `HolidayCalendar` value and a new case in `PublicHolidays.OfYear`, nothing else.
- **Two tenant settings choose it.** `General.PublicHolidays` (`TenantSettingDefinitions.PublicHolidays`, the calendar's code: `SE` by default, empty for none) chooses the calendar, and `General.ClosedOnPublicHolidays` (`TenantSettingDefinitions.ClosedOnPublicHolidays`, `true` or `false`, off by default) makes its days block staffing. Both are ordinary rows of `TenantSettings`, written only by `TenantSettingsService` and read through the lookup cache, so no schema change is needed. `TenantClock.GetHolidaysAsync` and `GetHolidaysOfAsync(tenantId)` read both as one `TenantHolidays`.
- **A blocking holiday is a closed day of the whole organization.** When the tenant is closed on its holidays, `StaffAvailabilityService.ComputeAsync` adds each holiday and eve in the range as a `ScheduledClosedDay` without a unit, its id derived from its date and name, and `AvailabilityRules.Compute` blocks it from midnight to midnight in the tenant's zone ("Closed for Christmas Day") by the closed-day path of [ADR 0043](0043-a-closed-day-is-a-row-without-a-lifecycle.md). Every staffing surface therefore agrees ([ADR 0026](0026-coordination-workspace-hour-timeline.md)): the candidate list, the assignment and request steps, the job guard, the timeline's pairs and the priorities. The holidays are computed and the settings come from the cache, so the read takes no extra queries.
- **Marked whether it blocks or not.** The calendar rail, the timeline's day title and the staff member's own schedule (`PersonSchedule`) mark every holiday of the chosen calendar in the holiday colour (`--color-holiday` on `--color-holiday-soft`, both with a dark-scheme value) with its name in words, "Closed for …" while the tenant is closed on it. A tenant's own closed days still apply beside them, and a day that is both shows both names.

## Rationale

Computing the holidays from a pure rule removes the yearly manual entry of every holiday, including the movable ones, and needs no table. Feeding blocking holidays into the existing closed-day path reuses the one availability rule, so no staffing surface needs changes of its own.

## Consequences

- No holiday row exists to edit or delete. A tenant that works on one holiday but not another turns the setting off and keeps the days it closes as closed days.
- The names are English, as all UI text is. A tenant that wants other names keeps its own closed days instead.
- A change of the settings reaches an open coordination workspace when it next reads, as a change of closed days does.

## Related Decisions

- [ADR 0043](0043-a-closed-day-is-a-row-without-a-lifecycle.md): the closed-day path.
- [ADR 0026](0026-coordination-workspace-hour-timeline.md): the one availability rule.
- [ADR 0020](0020-dates-and-times.md): the tenant's time zone.
