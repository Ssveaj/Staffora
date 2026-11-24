# ADR 0043: A closed day is a row without a lifecycle that blocks staffing through the one availability rule

## Status

Accepted

## Context

A tenant needs to state that nobody works on a given day, such as a public holiday or a day one office is closed. Without that, every staffing surface treats such a day as an ordinary day and the coordinator has to remember it.

## Decision

- **A closed day is a tenant row without a lifecycle.** `ClosedDay` (`TenantEntityBase`) holds a name (at most 100 characters), a first and a last day (`DateOnly`; the last never before the first, `CK_ClosedDays_LastNotBeforeFirst`; at most 366 days) and an optional organization unit. It has no states: a closed day either exists or does not, nothing waits for a decision, and no other row follows it through steps.
- **It is edited in a form dialog.** `ClosedDayDialog`, opened through `DialogServiceExtensions.OpenFormDialogAsync`, edits it and deletes it after a confirmation, as [ADR 0009](0009-state-entity-dialog-only-and-workflow-governed-attributes.md) allows for an entity without a lifecycle. The dialog sends the service a draft of its four fields (`ClosedDayDraft`), never a whole entity ([ADR 0015](0015-dialogs-save-through-a-detached-entity-updater.md)). The last save wins, since the row has no state to guard and nothing else writes it.
- **Kept under settings.** The settings shell's "Closed days" panel (`/tenant/management/settings/closed-days`, group Staffing) lists closed days in one `ListView` (`ClosedDayList`: search by name; Today and later, or Past; sorted by first day or name). `SettingsView` reads the list and opens a closed day read-only; `SettingsEdit` adds, changes and deletes. No permission is added.
- **Read through the lookup cache.** `ClosedDayService.GetCalendarAsync` answers the tenant's closed days from `TenantLookupCache` (the catalogue "closed days") to a holder of `SettingsView` or `ViewCoordination`, and every save invalidates it. The coordination workspace reads it for the calendar rail and the day's title. Its rows carry the unit's name, so a unit renamed in the meantime shows its old name there until the cache entry's ten minutes expire.
- **Which staff a closed day applies to.** Without a unit, it applies to every staff member. With a unit, it applies to every staff member who belongs to that unit or a unit below it, by the one rule the codebase has for a staff member's units: the `ResourceUnitMembership` rows the linker keeps from the units where the staff member holds a staff role. A staff member of several units is closed when any of them lies in the closed unit's part of the tree. The Company root as the unit means the whole organization and is stored as no unit.
- **One availability rule judges it.** `StaffAvailabilityService.ComputeAsync`, the one read behind `IStaffAvailability` ([ADR 0026](0026-coordination-workspace-hour-timeline.md)), reads the closed days overlapping the range (one query, plus the memberships and the tenant's tree only when a closed day names a unit). `AvailabilityRules.Compute` turns each applicable closed day into a block from midnight to midnight in the tenant's zone, "Closed for {name}", with the closed day as its source, cut to the range read, and lists the closed days per date on the staff member's answer (`StaffAvailability.Closed`). The candidate list and its ranking, the assignment guard, the job guard's check of holders, the timeline's pairs and free counts and the priority list's at-risk reasons therefore all treat a closed day as they treat an absence that blocks.
- **Marked where coordinators look.** The calendar rail stripes a closed day and names it in the day's accessible label ("Closed for Founders' Day"), with a legend entry. The timeline's title names the shown day's closed days, and each row it applies to carries a striped band across the day with its name in words for a screen reader. Colours come from the existing tokens.
- **A unit with closed days is not deleted.** `ClosedDaysBlockUnitDelete` refuses the unit's delete step while a closed day names it.

## Rationale

A workflow would only add a Create state and a delete step that mean nothing more than the row's insert and delete, so a plain row in a form dialog is sufficient. Judging closed days in the one availability rule means every staffing surface agrees without changes of its own. A day off for one office is a day off for its people, which is why membership in any unit under the closed unit applies.

## Consequences

- One table, `ClosedDays`, with the tenant row's audit columns, a `TenantId` index, an index on `(TenantId, FirstDay, LastDay)` and a `NoAction` key to the unit, in the single `InitialCreate` ([ADR 0022](0022-data-model-conventions.md)).
- A closed day has no step history; its audit columns record who created it and who last changed it.
- The availability read takes one more query per call, the same for any number of staff members, and two more when a closed day names a unit.
- A change of closed days reaches an open coordination workspace when it next reads (a day chosen, a dialog closed, a reload), not live: the tenant change channel carries jobs and staff members, not the calendar.

## Related Decisions

- [ADR 0009](0009-state-entity-dialog-only-and-workflow-governed-attributes.md): form dialogs for entities without a lifecycle.
- [ADR 0021](0021-one-organization-tree-per-tenant.md): the unit tree.
- [ADR 0026](0026-coordination-workspace-hour-timeline.md): the one availability rule.
- [ADR 0045](0045-public-holidays-are-computed-from-a-tenant-setting.md): public holidays use the same closed-day path.
