# Schedules and absence

This document describes when a staff member can work: work patterns given over a period, the availability rule every staffing surface reads, closed days and public holidays, absence with its reporting, decisions and questions, and the impact of an absence on jobs already staffed. One availability rule serves every staffing surface ([ADR 0019](../adr/0019-coordination-workspace.md), [ADR 0026](../adr/0026-coordination-workspace-hour-timeline.md)); dates and times follow [ADR 0020](../adr/0020-dates-and-times.md). Permissions are defined in [authorization.md](../authorization.md).

## Schedules and availability

A staff member works by the [work pattern](../database.md#workpattern) given to them from a date ([ResourcePatternAssignment](../database.md#resourcepatternassignment)). The availability turns that into working time.

### Giving a pattern

- **The Schedule view.** The user dialog's Schedule view (for a staff member, `ResourcesView`) lists the patterns given to the staff member (`StaffScheduleList`): latest start first, filtered by state, each with its pattern (marked "(archived pattern)" once that pattern is archived) and its dates. It is read through `UserService.PatternAssignments` from `PatternAssignmentService.QueryAssignmentsOfUserAsync`, which checks its own readers (`ResourcesView` or `AssignWorkPatterns`).
- **The assignment dialog.** A row opens the assignment's own state-entity dialog on top (`PatternAssignmentDialog`, token `patternassignment`). "Assign work pattern" is the view's add action, shown exactly when `PatternAssignmentService.MayAssignToUserAsync` answers true (`AssignWorkPatterns` and a staff record that is not inactive). It opens the dialog in create mode for the staff member, from today until further notice, its cycle starting on this week's Monday.
- **Fields:** the pattern (a drop-down of the published patterns, plus a stored assignment's own; the choice sets the reference the "published" condition reads in memory), From, To (empty: until further notice) and "Cycle starts (a Monday)". A note says when the pattern is archived.
- **Steps.** Save creates it in Registration; Activate puts it into effect. A create takes the prepared assignment's Save only (`PatternAssignmentService.CreateBySaveOnly`), and an end before the start is refused with its rule's words before anything is written.
- **Several staff members at once.** A pattern is given to several staff members at once, created and activated together, from the pattern's own dialog (its Staff view's "Assign to staff", [Dialogs](lists-and-dialogs.md#dialogs)). The staff list's Work pattern column and filter say who has one.

### Rules

- **One active assignment per date.** A staff member has at most one active assignment on any date (`ScheduleRules`, pure). The workflow judges it on Activate, Reactivate and the active assignment's save against the staff member's other active assignments. The step's guard reads them inside the step's transaction under the tenant's schedule lock (`AppLock.Schedules`, which every assignment and pattern step takes; [Application locks](../database.md#application-locks)), and refuses with each overlapping assignment named ("It overlaps the active assignment of Full time from 2026-08-17 to 2026-12-31: ..."). The dialog draws Activate greyed with that reason. A prepared assignment (Registration) may overlap: it gives no hours until it is activated.
- **The editing model.** Once active, only the end date changes.
  - A staff member's pattern is replaced by ending the current assignment on a day and activating a new one from the day after (`ScheduleRules.DayAfter`): no gap and no overlap the user did not choose.
  - An end moved later into the next assignment is refused.
  - An assignment that was a mistake is withdrawn (Inactive: no hours on any of its dates, its history kept) and can be reactivated where it shares no date.
  - One that never took effect is deleted.
- **Archiving a pattern in use** is allowed. The assignments that have it keep it as it is, active, with the same hours (a published pattern never changes), save their end date and are withdrawn as any other. An archived pattern is given to nobody new (not offered, not created, not activated, not reactivated). Every pattern step takes the schedule lock too, which orders a pattern's steps with the assignments' steps.
- **A staff member out of service.** Only a staff member in service is given or keeps hours.
  - The create, Activate, Reactivate and the active assignment's save are refused for a staff member whose record is inactive (`StaffSetUpOpenReason`, the setting-up rule `StaffServiceRules.IsSetUpOpen`, which the qualification service reads too). A staff member in Registration, being set up, is given patterns and qualifications ahead of service; being in service is the availability's question.
  - The Schedule view offers no "Assign work pattern" for an inactive staff member.
  - The availability gives a staff member whose record is not Active, or whose account is not, no working time and one block over the whole range ("The staff member is inactive", `AbsenceRules.NotInServiceReason`), whatever assignments they keep.

### IStaffAvailability

`IStaffAvailability` (`Staffora.Services/Scheduling/`, registered with `StaffAvailabilityService`) requires `ViewCoordination`.

**`ReadAsync(resourceIds, from, to)`** answers, for each staff record of the runtime's tenant among the ids (another tenant's id and a missing one are left out alike), their working time over the days (both included, at most `MaxDays`, 731), as an `AvailabilityAnswer` of `StaffAvailability`:

- **`Working`:** each `WorkingTime` with its calendar day, its start and end as UTC instants and as the wall clock reads them, its real `Duration`, and the assignment and pattern it comes from.
- **`Blocks`:** a staff member out of service over the whole range; each blocking absence over its instants, cut to the range, with its reason and the absence as its source; each closed day it reaches ([Closed days](#closed-days)); and the staff member's bookings (below).
- **`Warnings`:** open requests (below).
- **`Closed`** and `ClosedOn(date)`: the closed days per date.

Blocks of one staff member may overlap (two absences over one day): a surface asks whether any block meets the stretch it judges. `IsWithinWorkingTime(startUtc, endUtc)` says whether a stretch lies wholly in one run of working time (the "outside the work pattern" warning of the staffing surfaces).

**Bookings.** A query reads the staff members' live assignments (Requested, Accepted, Assigned) on an active slot of a job being staffed whose period touches the range, each with its job's period and weekly times. The core repeats the times over the period's days inside the range (`JobRules.Occurrences`, each start and end through `WallClock`):

- as **blocks** for an Assigned or Accepted assignment ("Assigned to work a job", "Said yes to working a job");
- as **warnings** for a Requested one ("Asked to work a job, not answered yet");

each with the assignment as its source. A surface judging a slot the staff member already holds leaves its own blocks out, and a staff member's other overlapping assignment is a double booking. A staff member out of service gets the one block over the range and nothing else.

**Queries.** A read issues a fixed number of queries, one per kind of data, whatever the number of staff members: the staff records with whether each is in service, their active assignments that touch the range, the patterns those name with their segments, their blocking absences on a day of the range, and their live job assignments; the closed days add one query, and two when a closed day names a unit. The ids go to SQL Server as one JSON parameter, so 2,500 at once take the same queries.

**The pure core.** `AvailabilityRules.Compute` decides:

- Each day takes the active assignment that applies on it (`ScheduleRules.AssignmentOn`; should rows written past the rules share a date, the latest start wins, then the lowest id).
- Its pattern repeats from the assignment's Monday anchor (`WorkPatternRules.Expand`).
- Each segment becomes instants in the tenant's zone.
- `AbsenceRules` turns each absence that blocks into instants.

**One staff member's schedule.** `StaffAvailabilityService.ReadScheduleOfUserAsync` (`StaffSchedule`) reads one staff member's schedule by week through the same rule, for a reader of staff members, a coordinator, or the staff member themselves with `ViewOwnSchedule`. It refuses a number of weeks it does not cover (1 to 104) before it computes a date from it. The user dialog's Schedule view opens the staff member's month in the person schedule dialog (`PersonScheduleDialog`, the same `PersonSchedule` as My schedule) and lists their work patterns.

### Daylight saving

Every conversion goes through the tenant's zone ([ADR 0020](../adr/0020-dates-and-times.md)); the pure rule is `WallClock`, handed on by `TenantClock.ToUtc`.

- **A skipped time** (Europe/Stockholm's last Sunday of March, 02:00 to 03:00) is the instant the clock jumps to: 02:00 and 02:30 both become 03:00 local (01:00 UTC). The mapping never runs backwards:
  - a segment across the gap (01:00-04:00) ends as it should and lasts an hour less than its wall clock says;
  - one starting in the gap (02:30-05:00) starts at 03:00;
  - one wholly inside it (02:15-02:45) has no length and is no working time.
- **A repeated time** (the last Sunday of October, 03:00 back to 02:00) is its first pass, the earlier instant (the daylight-saving offset), for a start and an end alike: 02:15-02:45 lasts 30 minutes (00:15-00:45 UTC), and 01:00-04:00 runs across the repeated hour and lasts four hours.
- **Displayed times.** `WorkingTime.LocalStart` and `LocalEnd` are what the wall clock reads at the instants, not the pattern's own times. A start or an end in the gap reads 03:00, so a pattern's 01:00-02:30 shows as 01:00-03:00 on that Sunday and lasts an hour: the schedule shows what the staff member's clock says rather than a time the day does not have.
- **Weekly hours** count real time: a week with the spring change works an hour less on a segment across it.
- Other zones follow their own dates (America/New_York changes on the second Sunday of March), and a zone without a change converts every day alike.

Verified by: `ScheduleRulesTests`, `AvailabilityRulesTests`, `PatternAssignmentWorkflowTests`, `PatternAssignmentLifecycleTests`, `PatternAssignmentConcurrencyTests`, `StaffAvailabilityTests`, `ScheduleDialogsTests`, `tests/e2e/schedules.spec.ts` and the accessibility sweep.

## Closed days

A tenant keeps the days it does not work: public holidays, a closed office ([ADR 0043](../adr/0043-a-closed-day-is-a-row-without-a-lifecycle.md)). A [closed day](../database.md#closedday) has a name, a first and a last day (one day to a year) and, optionally, an organization unit. Nobody it applies to is staffed on it.

- **Kept under settings.** The settings shell's **Closed days** panel (`/tenant/management/settings/closed-days`, group "Staffing", `ClosedDaysPanel`) lists them on the one list surface (`ClosedDayList`: searched by name, filtered by "Today and later" or "Past", sorted by first day or name, its query in the address under the prefix `closed`). Each row shows its period, its number of days and what it applies to ("The whole organization" or the unit).
  - `SettingsView` reads it; "New closed day" is drawn for a holder of `SettingsEdit`.
  - A row opens `ClosedDayDialog`, a form dialog (no lifecycle, so no state-entity dialog): Name, First day, Last day (moved along with the first day while they were the same) and "Applies to" (the whole organization or one unit in use), a line saying whom it reaches, and Save, Cancel and Delete (after a confirmation) for a holder of `SettingsEdit`. A reader of settings sees it read only.
- **The service.** `ClosedDayService` (`Staffora.Services/Scheduling/`):
  - `QueryClosedDaysAsync` (`SettingsView`);
  - `GetCalendarAsync`: the cached rows as `ClosedDayFact`, to `SettingsView` or `ViewCoordination`;
  - `GetEditorAsync(id)`: the draft and the unit choices, editable with `SettingsEdit`;
  - `CreateClosedDayAsync`, `UpdateClosedDayAsync`, `DeleteClosedDayAsync` (`SettingsEdit`), each judged by `ClosedDayRules.Problem`: a name, the last day not before the first, at most 366 days, inside the calendar.
  - The unit must be the tenant's own and in use; the Company root means the whole organization and is stored as none. The platform console is refused, as for every tenant setting.
- **Whom it reaches.** Without a unit, every staff member. With a unit, every staff member who belongs to it or to a unit below it, through their unit memberships (`ResourceUnitMembership`, the units where they hold a staff role); one unit of several is enough (`ScheduledClosedDay.AppliesTo`, the tree from `OrganizationStructure.SelfAndDescendants`).
- **The one availability rule.** `StaffAvailabilityService.ComputeAsync` reads the closed days overlapping the range, and `AvailabilityRules.Compute` blocks each staff member it reaches from midnight to midnight in the tenant's zone ("Closed for Founders' Day", the closed day as the block's source) and lists them per date (`StaffAvailability.Closed`, `ClosedOn(date)`). Every staffing surface agrees, as for an absence:
  - the candidate list and its ranking ("Blocked on 1 of 3 occurrences", "Closed for Founders' Day: 1 occurrence, 2027-03-10");
  - the assignment and request steps' refusal;
  - a job edit that adds times on a closed day for a holder;
  - the timeline's pairs and its "None of 3 free";
  - the Priorities' at-risk item for a holder.
- **Marked on the workspace.**
  - The calendar rail stripes a closed day (`mini-month__day--closed`, `data-closed-day`), names it in the day's accessible label ("Monday 9 November 2026, Closed for Founders' Day, 2 open") and its tooltip, and its legend has "Closed day".
  - The timeline's title names the shown day's closed days ("Closed for Founders' Day, Closed for Study day in Team North", `data-closed-day-note`).
  - Each staff member's row it reaches carries a grey striped band across the day (`timeline-closed`, `data-closed`), its name kept in view as the day scrolls and in words for a screen reader; a time held on it is marked as overlapping.
  - Closed days are read from the cache once per workspace read (`Coordination.razor`), so a change shows when the workspace next reads.
- **A unit with closed days** is not deleted (`ClosedDaysBlockUnitDelete`): delete its closed days first, or archive the unit.

Verified by: `ClosedDayRulesTests`, `ClosedDayTests`, `StaffBoardTests`, `CalendarRailTests`, `StaffBoardPageTests`, `tests/e2e/closed-days.spec.ts`.

## Public holidays

A tenant's public holidays are computed, never typed in ([ADR 0045](../adr/0045-public-holidays-are-computed-from-a-tenant-setting.md)).

- **The rule.** `PublicHolidays` (`Staffora.Core/Scheduling/PublicHolidays.cs`) answers the holidays and eves of a year (`OfYear`) or a date range (`Of`) for a `HolidayCalendar` (Sweden, or None), each a `PublicHoliday` with its date, English name and kind (`Holiday` or `Eve`).
  - Easter follows the anonymous Gregorian computus (`EasterSunday`).
  - Sweden's holidays: New Year's Day, Epiphany, Good Friday, Easter Sunday, Easter Monday, May Day, Ascension Day, Whit Sunday, National Day, Midsummer Day (the Saturday 20 to 26 June), All Saints' Day (the Saturday 31 October to 6 November), Christmas Day and Boxing Day.
  - Sweden's eves: Midsummer Eve, Christmas Eve and New Year's Eve.
- **The settings.** `General.PublicHolidays` chooses the calendar and `General.ClosedOnPublicHolidays` makes its days block staffing ([Tenant settings](custom-fields-and-settings.md#tenant-settings)). The General panel has a **Public holidays** section: the calendar (Sweden or None), the "Closed on public holidays" switch, the next holiday of the chosen calendar, and its own "Save public holidays" button. `TenantClock.GetHolidaysAsync` (and `GetHolidaysOfAsync(tenantId)` for the guards) reads both as a `TenantHolidays` from the lookup cache.
- **Blocking.** While the tenant is closed on its holidays, `StaffAvailabilityService.ComputeAsync` adds each holiday and eve of the range as a closed day of the whole organization (`TenantHolidays.ClosedDays`), so `AvailabilityRules.Compute` blocks it from midnight to midnight ("Closed for Christmas Day") on every staffing surface, as a [closed day](#closed-days) does, and the timeline's rows carry its closed band. No query is added.
- **Marked**, whether it blocks or not:
  - the calendar rail colours a holiday (`mini-month__day--holiday`, `data-holiday`, `--color-holiday` on `--color-holiday-soft`) and names it in the day's accessible label and tooltip beside the day's closed days ("Friday 25 December 2026, Christmas Day, Closed for Founders' Day"), with "Public holiday" in the legend;
  - the timeline's title names the shown day's holidays (`data-holiday-note`);
  - the staff member's schedule (`PersonSchedule`) names them on the day (`data-holiday`, `OwnScheduleDay.Holidays`).
  - While the tenant is closed on them, each reads "Closed for …".

Verified by: `PublicHolidaysTests`, `TenantSettingDefinitionTests`, `StaffBoardTests`, `CalendarRailTests`, `SettingsPageTests`, `TokenContrastTests` (the holiday colour in both schemes), `tests/e2e/public-holidays.spec.ts`.

## Absence

A staff member's absence is a [ResourceAbsence](../database.md#resourceabsence): planned leave or sick leave, over whole days with an optional time on the first and the last. Absence is reported by the staff member and approved by a coordinator, or registered directly by a coordinator ([PRODUCT.md](../../PRODUCT.md), "Setting up").

### Which absence blocks

`AbsenceRules.Blocks` is the one rule the availability reads:

- an approved absence of either kind blocks;
- a sick leave blocks from the moment it is reported while it is undecided (Requested, or a question about it, `AbsenceRules.UndecidedStates`);
- a planned leave that is undecided does not block;
- a rejected or canceled absence never blocks.

Every reader of "requested" absences (the availability, the staff board, the own schedule and status, the homes' counts, the lists' filters) reads the question states as requested.

### Period and instants

- From its first day to its last, both included. A from time moves the start from 00:00 of the first day; an until time moves the end from 00:00 of the day after the last.
- The availability turns them into instants in the tenant's zone through `WallClock`, by the rules of working time: a whole day lasts 23 hours on the spring change and 25 on the autumn one, a time the clock skips is the instant it jumps to, one it shows twice is its first pass, and a period wholly inside the skipped hour blocks nothing.
- Refused, with each reason (`AbsenceRules.Problems`, by the workflow, by the service before anything is written, and by the database's checks):
  - a last day before the first;
  - a single day whose until time is not after its from time;
  - an until time of 00:00 (end on the day before instead);
  - a kind the app does not declare (`AbsenceRules.UnknownKind`; the workflow's `KindKnownReason`; `CK_ResourceAbsences_KnownKind`).

### Reporting and registering

- **Reporting.** The staff member reports their own (`ReportOwnAbsence`; the workflow's Report, only their own record) from the absence page's "Report absence" or from "My schedule" ([Staff pages](coordination-and-homes.md#staff-pages)).
- **Registering.** A coordinator registers one on another staff member's behalf (`ManageResourceAbsence`; Register), from the page's "Register absence" (the staff member chosen first, `ChooseAbsenceStaffDialog`) or from the Absence view of the staff member's user dialog.
- **Decided at once.** With `ApproveResourceAbsence` too, the registration is decided at once: the service takes the workflow's Approve (a planned leave) or Handled (a sick leave) as the create, one step and one history row.
- One's own absence is reported, never registered, and an approver never decides their own (`NotOwnDecisionReason`). The dialog says beforehand what its step does.

### Editing and canceling

- A requested absence's period is saved by the staff member or a holder of `ManageResourceAbsence` (the absence guard, `IResourceAbsenceGuard`, an `IStepPreparation` that reads the stored kind and the session's absence permissions).
- Its kind never changes once reported (`KindUnchangedReason`), so a planned leave waiting for approval is never turned into a sick leave that blocks at once.
- A decided absence is kept as decided: to change one, cancel it and report it anew.
- The same people cancel a requested or an approved one (confirmed).

### A decision is not an edit

- Approve, Reject and the cancellations take no edits (`Transition.TakesNoEdits`, the workflow skill). With the dialog's edits pending, each is refused (`TransitionRefusals.PendingEdits`) by the service and by `WorkflowService` for any caller, so the save's rules (who changes the period, never the kind) are never bypassed by pressing a decision instead.
- Each is refused on an absence someone saved after the decider opened it (`TransitionRefusals.ChangedSinceOpened`, read under the step's lock), so nobody approves a period they never saw.
- In the dialog the decisions stay in the footer, greyed with that reason, while it holds edits: save them, or undo them, first.
- Only Report, Register, Approve and Handled (registered decided at once) create an absence; Reject, Save and the cancellations take a stored one only.

### Deciding

A holder of `ApproveResourceAbsence` decides, only from Requested:

- a planned leave is approved (Approve, `absence-approve`);
- a sick leave is marked handled once the report is taken in (Handled, `absence-handle-sick`: "Mark the sick report as handled. The staff member stays off staffing for that time");
- either goes to Approved and blocks as approved.

Each kind shows only its own step (a condition each, `PlannedLeaveOnlyReason` and `SickLeaveOnlyReason`; neither is drawn greyed, so an approver's own absence shows neither).

Either kind may be rejected with a reason through the step's comment prompt. The reason is written into the absence's staff thread ([ADR 0035](../adr/0035-absence-questions-are-workflow-steps.md)), where the staff member reads it, and the rejection's notice says it is there. No history step or list row carries it; a reader who reads no thread of the absence (neither its staff member nor an approver) is told the reason is kept between the staff member and the approvers.

### Questions

While an absence is requested either side may ask the other, and every word is written through a step ([ADR 0035](../adr/0035-absence-questions-are-workflow-steps.md)).

- **The coordinator's side** (`ApproveResourceAbsence`, never on one's own absence, `NotOwnQuestionReason`):
  - "Send a question" (Requested to Question, "Question to the staff member");
  - "Send another question" (stays in Question);
  - "Continue without an answer" (Question to Requested);
  - "Answer the staff member" (StaffQuestion back to Requested, so the item leaves the Priorities and the absence waits for its decision again);
  - "Mark as handled" (StaffQuestion to Requested, `absence-question-handled`, icon `done_all`, no prompt; [ADR 0038](../adr/0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md)).
  - "Answer the staff member" and "Mark as handled" mark every approver's unread question notices about the absence read ([Notifications](notifications-and-comments.md#notifications), "Settled for everyone").
- **The staff member's side** (`ReportOwnAbsence`, their own absence only, `OwnQuestionReason`):
  - "Write a question" (Requested to StaffQuestion, "Question to the coordinator", read-only for everyone while it waits);
  - "Write another question" (stays in StaffQuestion);
  - while the question is with them, Save (the Question state's save, theirs alone) and "Answer" (Question to Requested), which may carry corrected dates instead of words.
- **Prompts.** Each step that carries words opens a prompt (title, a line of text, a four-row box with a placeholder). A required one says why under the box only after an empty send, and Cancel takes no step. Only the staff member's Answer is optional ("You can send it without writing anything"), and then nothing is written. The words go into the staff thread in the step's own transaction (`TransitionCommentPrompt.Required`, `RequiredMessage`).
- **No composer.** The staff thread is read by the staff member (`ResourceAbsenceService.OwnThread`) and the approvers (`ApproverThread`). It is closed to free comments: a post is refused with `WrittenThroughSteps`, and nothing is shown where a composer would be (`CommentThreadAccess.ShowsClosedReason` false). The side column shows only once the thread holds a comment.
- **Leaving a question.** Approve, Handled and Reject wait for Requested, and Cancel absence is offered from both question states. Every question step but the staff member's Answer takes no edits.

### In service and readers

- **In service.** A new or approved absence needs a staff member in service, the availability's definition (`StaffServiceRules`: the staff record and the user's account both active), refused with `StaffInServiceReason`; a rejection or a cancellation does not. `MayAddAbsenceForUserAsync` and the staff members "Register absence" lists (`GetRegistrableStaffMembersAsync`) read the same rule.
- **Who reads what.** A reader of absences (`ViewResourceAbsence`, `ManageResourceAbsence` or `ApproveResourceAbsence`) reads every absence of the tenant. A staff member with `ReportOwnAbsence` reads their own only, and another staff member's reads as missing, by every read.

### Notices

Notices are raised by `NotifyAction` with its `when` ([Notifications](notifications-and-comments.md#notifications)). Each links to the absence's dialog over the page the reader is on (`?dlg=absence-{id}`).

| Event | Key | Recipients |
|---|---|---|
| Planned leave requested | `absence-leave-requested` | Holders of `ApproveResourceAbsence`, whoever reported or registered it, an approver who reported their own included (the notice is the request's record; they cannot decide it) |
| Sick leave reported | `absence-sick-reported` | As above |
| Registered on the staff member's behalf (saying whether it waits, blocks or is approved) | `absence-registered` | The staff member |
| Approved / rejected (the rejection says the reason is written on the absence) | `absence-approved`, `absence-rejected` | The staff member |
| Canceled by someone else | `absence-canceled` | The staff member |
| Canceled by the staff member | `absence-canceled-by-staff` | The approvers |
| "Send a question", "Send another question" ("New question about your absence") | `absence-question-to-staff` | The staff member |
| "Answer the staff member" ("New answer about your absence"; the absence waits for an approver again) | `absence-answer-to-staff` | The staff member |
| "Write a question", "Write another question" ("New question about an absence") | `absence-question-from-staff` | The approvers |
| "Answer" ("New answer on an absence") | `absence-answer-from-staff` | The approvers |

The question and answer notices append the words typed in the step's prompt as a paragraph of their own (`NotifyAction`'s `withComment`). "Continue without an answer" and "Mark as handled" tell nobody.

### Overlaps and concurrency

- Overlapping absences of one staff member are allowed: each blocks for its own stretch, so one inside another changes nothing for staffing.
- A decision and a cancellation at once are put in one order by the step's own lock: exactly one goes through.
- Every absence step takes the tenant's job lock, under which an assignment's request, assignment, yes and pick judge whether the staff member is free ([Staffing dialog](jobs-and-staffing.md#staffing-dialog)): an absence and an assignment of one staff member are judged in one order.

### Pages

- **My schedule** ([Staff pages](coordination-and-homes.md#staff-pages)): a staff member reports their own absence from it, and it shows each absence that is on the schedule (neither rejected nor canceled: `AbsenceRules.ScheduledStates`) on its days.
- **The absence page** (`/staffing/absence`, Staffing in the navigation, for a reader of absences or a staff member): `AbsenceList` on the one list surface, searched by the staff member's name or email address, filtered by state and kind, sorted by first day (latest first) or staff member, its query in the address. A staff member is filtered to their own by the service; the page is their one list of all their own absences.
- **The user dialog's Absence view** (`ViewResourceAbsence`, for a staff member): `StaffAbsenceList`, rows opening each absence's dialog on top, and "Register absence" exactly when `ResourceAbsenceService.MayAddAbsenceForUserAsync` answers true.
- **The absence dialog** (`AbsenceDialog`, token `absence`): the staff member, a note, the kind and the period as two fields, From and Until, each a day and an optional time in one frame (`StateFieldDayAndTime`: the time is the browser's own time input, "All day" while empty).

Verified by: `AbsenceRulesTests`, `AbsenceLifecycleTests`, `AbsenceQuestionTests`, `AbsenceDialogsTests`, `StaffAvailabilityTests`, `tests/e2e/absence.spec.ts` and the accessibility sweep.

## Absence impact

When someone calls in sick in the morning, the coordinator sees the affected jobs, takes the person off and requests or assigns someone else ([PRODUCT.md](../../PRODUCT.md), core flow 4; [ADR 0044](../adr/0044-taking-off-for-absence-and-open-again.md)).

- **What an absence affects.** `ResourceAbsenceService.ReadAbsenceImpactAsync` is one call, requiring `ViewCoordination` in the session's own tenant; it is refused on the console, and another tenant's absence reads as missing. It returns:
  - every request or assignment of its staff member (Requested, a question, Accepted, Assigned) on a job being staffed (coordinating, confirmed or in progress) and an active slot, with the job's times that the absence overlaps and that have not ended (`AbsenceImpactRules.AffectedOccurrences`: the job's occurrences by the one occurrence rule, met by the absence's instants as the availability meets them, [ADR 0026](../adr/0026-coordination-workspace-hour-timeline.md));
  - the ones taken off for it (Canceled by the take-off step, settled).
  - An absence waiting for approval counts too, so an approver sees what a planned leave would block ("This planned leave blocks nothing until it is approved"). Only one that blocks (`AbsenceRules.Blocks`) waits in the Priorities or lets a coordinator take anyone off. A rejected or canceled absence affects nothing.
- **The Affected jobs view.** Once stored, the absence's dialog has a second view for holders of `ViewCoordination` (`AbsenceDialogContext.ImpactView`, `AbsenceImpactView`, a component of the shared pieces like the Staffing view):
  - one row per affected assignment, unsettled first, with the job, the slot and the customer, the assignment's state (or "Taken off") and its affected times ("2026-11-11 13:00-14:00", the first three and "and N more"), and "Open again" while its slot is;
  - two actions: **Open** opens the assignment's dialog on top, whose footer offers "Take off for absence"; **Staffing** opens the job's dialog on its Staffing view with the slot chosen (`JobDialog.OpenStaffingAsync`), to request or assign someone else;
  - the view reads again after either closes;
  - the Priorities item opens the absence on this view (`AbsenceDialog.OpenImpactAsync`; the coordination page names it, `[DialogViewOnPage]`, so F5 keeps it).
- **Taking off for absence** (`SlotAssignmentWorkflowAttribute.TakeOffSteps`). A coordinator's step (`PerformCoordination`) from every live state into Canceled (`assignment-take-off-request`, `-question`, `-accepted`, and `assignment-take-off` from Assigned).
  - It is offered only while an absence of the staff member blocks a remaining time of the job (`AwayReason`, judged by the assignment's guard from the availability, `ISlotAssignmentGuard.BlockingAbsenceOf`) and the job is being staffed.
  - It asks an optional word for the staff member (`TakeOffPrompt`, which the staff thread keeps) and tells them which absence it is for (`assignment-taken-off`: "Hello Kim, because of your sick leave on 2026-11-11 you no longer work Algebra as Lead. The coordinator asks someone else to work it."). From Assigned it tells a portal's customer of the staff members, as a cancel does.
  - A cascade never takes it (`Transition.NeverCascaded`): a slot's or a round's cancellation still cancels through the cancel steps.
- **Open again.** A slot group whose holder was taken off this way, and which no assignment has held since, is open again (`AbsenceImpactRules.OpenAgain` over the group's take-off steps and the steps into Assigned of its current holders, read from the history, `OpenAgainReader`; never more than its open slots).
  - A request or a yes does not hold it; the first assignment after the take-off does.
  - It shows in the Priorities' slot items, in the job's Staffing view (the slot's button "Open again", `JobStaffEntry.OpenAgain`) and in the slot's staffing panel ("Open again: its staff member was taken off for absence", `SlotStaffingSummary.OpenAgain`).

Verified by: `AbsenceImpactTests`, `AbsenceImpactRulesTests`, `SlotAssignmentWorkflowTests`, `JobCancelWorkflowTests`, `tests/e2e/absence-impact.spec.ts`.
