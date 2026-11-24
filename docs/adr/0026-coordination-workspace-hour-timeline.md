# ADR 0026: The coordination workspace is a full-viewport tool with an hour timeline; one availability rule judges every surface

## Status

Accepted. Supersedes [ADR 0019](0019-coordination-workspace.md). Amended by [ADR 0044](0044-taking-off-for-absence-and-open-again.md).

## Context

A coordinator plans by the hour. They need one screen that shows who is free when, what is uncovered and what is waiting for them, without a page to scroll, and every staffing surface must agree on who is available.

## Decision

### The workspace

Coordinators work from one workspace, `/coordination`, which fills the window under the shell's top row and never scrolls as a page. It has:

- **A header row** with the day shown, Today and New job. There is no week view, no list mode, no planning mode and no filter button.
- **A calendar rail** on the left: three months with one dot per day marking where the day's slots stand, and arrows naming the month they show. A week number is a label, not a button. Closed days and public holidays are marked ([ADR 0043](0043-a-closed-day-is-a-row-without-a-lifecycle.md), [ADR 0045](0045-public-holidays-are-computed-from-a-tenant-setting.md)).
- **An hour timeline** of one day from 00:00 to 24:00, wider than its area, which scrolls sideways inside it. A day opens scrolled to now when it is today, otherwise to its first booking or 07:00, whichever is earlier.
  - An "Open slots" row comes first; it opens a slot's dialog on its Staffing view.
  - Staff members follow as a flat list by name, the name alone, which opens the person's schedule. "Find staff" and a sort sit in the timeline's corner.
  - A staff member's track shows working time as a tint with no text, absence as a band (dashed while it waits for a decision, solid once approved or handled), and the times of their live assignments in their state's colour.
  - On today a red now line (the danger dot's colour) stands at the current minute on the tenant's clock, with its time at the top, moved by a timer of the page once a minute.
- **The Priorities dock** under the timeline, which folds to a bar that keeps only the counts per tab.

The address keeps the shown day, the timeline's staff search, sort and page, and the dock's tab, search, day and fold.

### The Priorities dock

- The dock lists everything waiting on the coordinator in one flat list, most urgent first, read by `CoordinationService.ReadPrioritiesAsync` over the workspace's snapshot, each kind behind its own permission. There are no groups, no group headings or folds, and no focus card.
- Each row names its kind in a reason pill in the kind's colour (At risk, Uncovered, Unanswered, Waiting for a pick, Question, Message, Order, Behind, Absent, Absence), beside what it is about, its time, how long it has waited or how soon it starts, and its action.
- The order is one rule in the service (`PriorityList.Order`). Each item is due when its work starts, or one day after a reply was asked for when it waits for one (the earlier of the two when it has both). Everything already due counts as due now. At the same time, a fixed kind order decides: at risk, absence to settle, uncovered, orders to answer, unanswered, questions, messages, waiting for a pick, absence to approve, jobs behind.
- The tabs (All, Orders, Slots, Questions, Absence) and the search filter the list, which shows 50 items at a time with "Show more".
- The dock lists a job, not a slot. The four slot kinds (at risk, to request or assign, unanswered requests, waiting for a pick) each list one row per job with a short count of what waits ("2 of 3 open"); a job with slots of several kinds appears once in each kind, and the counts count jobs. Jobs behind are listed under the Orders tab.
- Every row about a job, its slots or its conversations has one action, Open (Answer for a message), and a click on the row runs it: it opens that job's dialog. An absence to approve opens the absence's dialog, and an absence to settle opens the absence on its Affected jobs view ([ADR 0044](0044-taking-off-for-absence-and-open-again.md)).

### Staffing from the job dialog

The job is the coordinator's one dialog. Its Info view's Staff section names who works each slot ("Open" where nobody does), with "Find staff", which shows the job's Staffing view on that slot. There, as in the slot dialog's Staffing view, the ranked candidates carry Request and Assign, and the view offers Request several, with no candidate dialog or round form on top. Every step still runs through its workflow's first step.

### Rules that hold for every surface

- The timeline, the staffing dialog and views, and the priority reasons judge availability with one rule, `IStaffAvailability`, over all of a job's occurrences.
- The board's reads are a fixed number of queries per page of staff members, regardless of how many staff members a tenant has.
- Open workspaces refresh from the tenant change channel that `WorkflowService` publishes to after commit. The channel carries identifiers only; each page reloads its own data through its services.
- A parity test keeps every surface in agreement with the availability rule.

## Rationale

A coordinator plans by the hour across a day and needs one screen that shows who is free when, what is uncovered and what waits for them, without a page to scroll. A single flat list ordered by one rule shows the most urgent work first without the coordinator having to compare groups. Staffing from the job dialog keeps every step inside the workflow.

## Consequences

- The board's read covers the week of the shown day, Monday to Sunday (`BoardRules.Days`), in pages of staff members. Each staff row names the unit it belongs to, from the memberships the read already loads.
- The calendar rail's marks are answered from the workspace's snapshot in memory (`CoordinationSnapshot.DayMarks`), with no query of their own.
- Placing a bar is pure (`TimelineRules`): its start and length in hours from midnight, times the hour width.
- Every bar is a button with its full name; the timeline is a grid of rows of staff members; the rail is a table of buttons. The hover card is a picture of the same facts and is hidden from assistive technology.
- The workspace does not declare the shared `PageHeader`; its header row is its own (`PageChromeScanTests` allow-lists it).
- Staffing is done from the staffing dialog and views and from request rounds; there is no planning mode that stages moves.

## Related Decisions

- Supersedes [ADR 0019](0019-coordination-workspace.md).
- [ADR 0029](0029-left-sidebar-shell.md): the shell around the workspace.
- [ADR 0037](0037-request-questions-are-workflow-steps.md), [ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md): questions from staff in the Priorities.
- [ADR 0041](0041-a-request-expires-by-a-system-step.md): expired requests in the availability and the Priorities.
- [ADR 0043](0043-a-closed-day-is-a-row-without-a-lifecycle.md), [ADR 0045](0045-public-holidays-are-computed-from-a-tenant-setting.md): closed days and holidays judged by the same availability rule.
- [ADR 0044](0044-taking-off-for-absence-and-open-again.md): absence to settle and slots open again.
