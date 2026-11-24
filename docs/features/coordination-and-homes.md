# Coordination workspace, homes and staff pages

This document describes where each session lands after sign-in (the homes), the coordinator's workspace with its hour timeline and Priorities dock, and the staff member's own pages.

The staffing rules these pages use are described elsewhere: candidates and their judgement in [Staffing dialog](jobs-and-staffing.md#staffing-dialog), request rounds in [Request rounds](jobs-and-staffing.md#request-rounds), working time and absence in [Schedules and availability](schedules-and-absence.md#schedules-and-availability) and [Absence](schedules-and-absence.md#absence), and the permissions in [authorization.md](../authorization.md). The architectural basis is [ADR 0026](../adr/0026-coordination-workspace-hour-timeline.md), which supersedes [ADR 0019](../adr/0019-coordination-workspace.md) and keeps its rules.

## Homes

After sign-in each session lands on its own home. Sign-in and the start of an impersonation land on the start page (`/`, `Pages/Home.razor`), which renders nothing, sends the session on and replaces itself in the history.

The rule is `AppHome` (`Layout/`), a pure function of the session and the permissions it holds. It is read for the same runtime snapshot the menu reads (`PermissionService.GetUserPermissionsAsync(RuntimeSnapshot)`).

- **Platform owner.** A platform owner who is not impersonating lands on the platform console (`/superadmin/overview`), the only pages open to them (`SessionPolicies`). The sign-in page sends the `Admin` role there directly.
- **Tenant users.** Anyone else, including a platform owner impersonating a user (who lands as that user through the impersonation endpoint's redirect to `/`), lands on the first home their permissions open, strongest first:
  1. the administrator's home, the administration overview (`/tenant/management`, `TenantView`);
  2. the coordinator's staffing overview (`/staffing/overview`, `ViewCoordination`); the [coordination workspace](#coordination-workspace) keeps its own link;
  3. the staff member's "My work" (`/me/work`, `ViewOwnAssignments`);
  4. otherwise their own overview (`/me/overview`).
- **Customer portal.** A session of a customer portal ([Customer portal](customers-and-portal.md#customer-portal)), an impersonating platform owner's included, lands on the portal's home (`/portal`) whatever it holds. No home of a staffing company is part of a portal.
- **Several roles.** A user holding several roles lands on the strongest role's home: administrator before coordinator before staff member. A system owner, who holds everything outside own work, lands on the administration overview; a coordinator who also works lands on the staffing overview.
- **No refusals.** Each home is opened by exactly the permission its page gates on and its navigation link names, so a home never lands a user on a refusal.

The administration overview is the administrator's home rather than a separate page, because it is already gated on `TenantView`. It stays the home once setup is complete, so a system owner keeps landing there. A user of none of the homes keeps their own overview.

### Shared hero look

The administrator's home, the coordinator's home and "My work" share the platform overview's look, tokens and rules of tone. The pieces live in `Components/Common/Ui/`, each with its own stylesheet:

- `HomeHero`: the ink band, the head and figures on the left, the ring on the right, room at its foot for the lifted cards.
- `HomeHeroFigures` and `HomeHeroFigure`: a figure light on the ink, its value and a dot before its label amber or red by tone, the tone also said in words to assistive technology (`data-figure`, `data-tone`).
- `HomeRing`: segments sized by value with a gap, halos, a glow, a centre title and caption, a legend whose dots glow in their tones (`data-segment`, `data-legend`).
- `HomeFloor`: the warm floor, its first row lifted over the band.
- `HomeSheet`: a white card with its `h2`, a note, a link onwards and a staged rise.

The head is `HomeHead` with `Hero`: an overline with the date and time on the tenant's clock (`TenantClock.NowAsync`; the administrator's home puts the tenant's name first, "Clearshine Cleaning Services · Thursday 19 November · 15:12"), and the page's name as its one `h1`. There is no greeting, no status sentence and no summary.

`HomeRules` (`Staffora.Core/Models/TenantHomeModels.cs`, pure) holds the words and tones:

- the open slots figure is amber only when an open slot's job has a time within seven days;
- a count waiting for the reader is amber above zero;
- the slot ring's and the week ring's titles and captions;
- the reader's next times: an assigned time not over yet, a time in progress counting, a time past midnight counted once.

The hero and the cards stack on a phone. The parts rise in once, and stay still under reduced motion.

### Banded home (customer portal)

The portal's home opens with its own head instead of the page header (`HomeHead`, allow-listed in `PageChromeScanTests`, which checks that each such home opens with it):

- An eyebrow ("Customer portal"), the title as the page's one `h1` with the "?" popover holding its description, and on the right today and "Updated hh:mm" on the tenant's clock with a "Refresh" button that reads the page again.
- Under it a white "To do" band and a muted "Situation" band that runs to the window's foot, each at most 82.5 rem wide and centred.
- The parts, laid out by their frame `HomeBoard`: figures as type (`HomeFigure`, the leading one large over an accent line, a zero quieter but never below the subtle text colour), a check list (`HomeCheck`: a mark, a bold name and its status in words) and rows that are one button or link each (`HomeRow`: a date block, the title over one line, what it asks on the right, a left edge in its tone).
- To do holds the reader's requests as figures over their latest requests, beside the supplier. Situation holds what the reader can do.

### My work

"My work" (`Pages/Me/MyWork.razor`, `ViewOwnAssignments`) is first under My work in the navigation after the overview. Each part is read from the service that owns it, and only the reader's own rows:

- **Assignments** (`SlotAssignmentService.ReadOwnWorkAsync`, filtered, counted and cut in the database): the open requests (asked, not answered) and the next assignments by period (assigned, the job's period not ended on the tenant's calendar), earliest period first, at most five each (`OwnWork.Shown`) with the count of all; and the live assignments whose conversation holds a coordinator's reply the reader has not opened (`OwnWork.Unread`, `UnreadCount`, newest reply first).
- **This week and the next** (`StaffAvailabilityService.ReadOwnWeekAsync`, `ViewOwnSchedule`): each day's working time, absence and the times of the reader's live requests and assignments, each time with where its job takes place (`OwnOccurrenceEntry.Place`), never its access notes.
- **Absence ahead**, for a holder of `ReportOwnAbsence` (`ResourceAbsenceService.ReadOwnAbsenceStatusAsync`, `OwnAbsenceStatus`): for example "Sick leave today, approved, until 2026-11-10", "Next: planned leave on 2027-03-10" or "No absence ahead", with how many wait for approval.

The hero's figures:

- **Next assignment**: the first assigned time of this week and the next that is not over ("Today 15:00", "Tomorrow 06:00", "Thu 26 Nov · 06:00") with its place, or its job when it names none. Without one in those two weeks, the first assignment ahead by period; otherwise "None ahead".
- **Waiting for your answer**: the open requests, amber above zero.
- **Assignments this week**: the assigned times of this week, with how many are still to come.
- **Your absence**: "Absent today" with its kind, state and last day; or the next absence's first day with its kind and whether it is approved or waiting; or "None ahead".

The ring shows this week's times by day: each day's assigned times a segment (past days muted, today in the glow, days ahead in the light accent) and the times asked of the reader (requested or said yes to) in amber. Its centre reads "3 to go / of 5 this week", "All done" or "Free week"; its legend lists each day with a time.

The cards:

- **Waiting for your answer** (the open requests) and **Next up** (the next five assigned times with their times and place, or the assignments ahead by period when none falls in the two weeks), lifted over the band.
- **This week**: Monday to Sunday, today ringed, each day with its absence and its times. A time asked of the reader is amber with "You are asked" or "You said yes". A day without working time says "Day off". On a phone each day is one line.
- **Absence**: the status line, the absence ahead and "Report absence" for a reader the absence service accepts it from (`MayAddAbsenceForUserAsync`), which opens the absence dialog in create mode.
- **Unread**: shown only while a coordinator's reply waits.

A request, a time or an assignment is one button that opens the assignment's own dialog (`SlotAssignmentDialog`), where the staff member accepts or declines. A step runs only in its state entity's dialog (`LifecycleCallScanTests`), so the cards offer no Accept or Decline of their own. An absence opens `AbsenceDialog`. The page reads again once a dialog has closed and when the tenant change channel reports a change to the reader's own work (`LiveRefresh`, `ChangeInterest.OwnWork`). Each card links to the full page: "My assignments", "My schedule" and the absence page, which keep the search, the filters and the closed rows. A coordinator who works reads their own work here, never a colleague's.

### Administrator's home

The administrator's home is the administration overview (`Pages/TenantAdmin/TenantManagementOverview.razor`, `TenantView`). Its figures, lists and name come from `TenantHomeService.ReadAdministrationAsync` (`Staffora.Services/Home/`, `TenantView`, the session's own tenant, refused to the platform console, at most nine queries, each naming the tenant). Each part is shown only to a reader of its rows:

- **Jobs this week** and **Open slots**, for a reader of jobs (`JobService.Readers`), counted as the platform overview counts the tenant's row: jobs from Registration to Completed with a time in the tenant's week; the active slots of live jobs nobody holds, with how many have a time within seven days.
- **Absent today**, for a reader of absence (`ResourceAbsenceService.Readers`): absences that block staffing on the tenant's today (approved, or sick leave reported), with first names.
- **Waiting for approval**, for a reader of absence: absence requested and not ended.
- **Staff in service**, in their place for a reader of staff members who reads neither jobs nor absence.

The ring shows this week's active slots of those jobs, held (calm) and open (amber). Its centre reads "13 of 20 / slots held this week", "All held" or "No slots"; its legend names held, open and the jobs.

The cards:

- **Short of staff** (for `ViewCoordination`): the slot groups of the jobs being staffed that are at risk, uncovered, waiting for a pick or unanswered, as `CoordinationService.ReadAsync` reads them, soonest time first, five shown, each opening its job's dialog, with "Open coordination". The UI word rules keep "need" off labels, so the card is not called "Needs staffing".
- **People**: the staff in service, who is absent today and the absence waiting for approval, each opening its absence's dialog, with "Manage users" for `UsersView`. Under its figures it shows the [staff without a work pattern](#staff-without-a-work-pattern).
- **Setup checklist**, while setup is open ([below](#setup-checklist)).
- **Customers & portal**, for a reader of customers: the active customers, those with a portal and, for a reader of jobs too, the live jobs a portal sent that wait for a coordinator or are being staffed.
- **Recent activity**, for a reader of jobs: the newest five steps of the tenant's jobs from their state history, with who took them ("System" for the tenant's system account), each opening its job's dialog.

A card none of whose parts the reader may read is left out, and the first row present is the lifted one.

### Setup checklist

`TenantHomeService.ReadSetupChecklistAsync` (`Staffora.Services/Home/TenantHomeService.Checklist.cs`) answers a card of the administration overview while setup is open: how far the tenant is set up for staffing ([PRODUCT.md](../../PRODUCT.md), "Setting up"). Each item comes from the tenant's own rows, is counted in the database and is shown only to a reader of those rows, as the page its link opens is gated. In setup order:

| Item | Shown to | Done when |
|---|---|---|
| People | `UsersView` | more than the one person onboarding created |
| A staff member | `ResourcesView` | a staff member in service |
| Qualifications | `ResourcesView` or `QualificationsManage` | an active qualification in the catalogue, held by a staff member in service |
| Work patterns | `ManageWorkPatterns` or `AssignWorkPatterns` | a published pattern, assigned to a staff member in service from today on |
| Customers | `CustomersView` or `CustomersManage` | an active customer |

Each row has its mark, what to do, its status in words ("1 qualification in the catalogue; 2 of 2 staff members hold one"), Done or To do in words and a link to its page. The card says how many are done ("2 of 5 done") and is left out once every item it shows is done.

The checklist opens with `TenantView`, in the session's own tenant only: every count names the tenant and runs under the tenant filter. The platform console is refused; an impersonating platform owner reads the impersonated administrator's. The Administrator preset reads every item except customers, which belong to the coordinator.

### Staff without a work pattern

The administrator's home's People card states how many staff members have no work pattern ("3 staff without a work pattern", amber while there are any and said in words; "Every staff member has a work pattern" otherwise), with a link to the staff list filtered to them (`/staffing/staff?pattern=none`). `TenantHomeService` reads it with the staff in service (`HomePeople.WithoutWorkPattern`, `ResourcesView`) through the filter's own rule (`StaffPatternCoverage.WithoutPattern`), so the count and the list agree.

The coordinator's home shows the same line, words, tone and link (the shared `HomePatternLine`) at the foot of **Waiting for you**, for a reader of staff members (`CoordinatorHome.WithoutWorkPattern`, `ResourcesView`, one count query through the same rule).

### Coordinator's home

The coordinator's home is the staffing overview (`Pages/Staffing/CoordinatorOverview.razor`, `/staffing/overview`, `ViewCoordination`), built from the shared hero parts. The head has the overline with the tenant's name, today and the time on the tenant's clock, and "Staffing overview" as the one `h1` and browser tab title.

Figures and lists come from `TenantHomeService.ReadCoordinatorAsync` (`ViewCoordination`, the session's own tenant, refused to the platform console, at most fifteen queries whatever the tenant's size, each naming the tenant); the slot groups come from `CoordinationService.ReadAsync`.

Figures:

- **Open within 7 days**: open slots of live jobs whose job has a time still to come within seven days, counted as the administrator's open slots figure, amber above zero, with all open slots beside it.
- **Without an answer**: requests sent and not answered (Requested) on the active slots of the jobs the workspace reads (being staffed, confirmed or in progress, their period not over).
- **Waiting for your pick**: the yeses on those slots (Accepted), which the coordinator picks or lets go; amber above zero.
- **Waiting for approval**: absence requested and not ended, amber above zero; only for a holder of `ApproveResourceAbsence`.

The ring shows this week's slots held and open with its legend, as on the administrator's home (`HomeStaffingParts`).

Cards:

- **Short of staff**: the administrator's home's card, with the same read and rows (`CoordinationSnapshot.ShortOfStaff`).
- **Waiting for you**: the yeses to pick, earliest period first, each opening its assignment's dialog where the coordinator picks or lets go; the absence to approve, each opening its absence's dialog; and, for a tenant with a portal, the jobs whose customer thread waits for a coordinator's reply (its newest comment a customer contact's, judged as the job's dialog judges it), oldest question first, each opening its job's dialog. Five of each, with the count of all.
- **Today**: jobs from Registration to Completed with a time today on the tenant's clock (one started the evening before included), earliest first, each with its times, place, customer and slots held ("1 of 2 held"), the row's edge amber while one is open, each opening its job's dialog.
- **From the portals**, only while the tenant has an active customer with a portal: the live jobs the portals sent, those waiting for a coordinator first, then those being staffed, by first day, each opening its job's dialog.

No card offers a step. The page reads again once a dialog has closed and when the tenant change channel reports a coordination change (`LiveRefresh`, `ChangeInterest.Coordination`). A staff member without `ViewCoordination` gets "Access denied".

### Platform console overview

The platform owner's home is `Pages/SuperAdmin/SuperAdminOverview.razor` (`/superadmin/overview`). A deep ink hero band under the console's bar uses the ink tokens (`--color-ink`, `--color-ink-strong`, `--color-on-ink`, `--color-on-ink-muted`, `--color-ink-accent`, `--color-ink-glow`, `--color-ink-warning`, `--color-ink-danger`; each text pair held at 4.5:1 by `TokenContrastTests`). It holds `HomeHead` as its hero: the date and time on the platform's clock (`PlatformOptions.TimeZone`) as an overline led by a calm pulsing dot, and "Platform overview" as the page's one `h1` and browser tab title. There is no greeting and no health ring, since the Tenants card lists the companies.

The four figures, light on the ink:

- **Errors, last 24 h**: error-log entries of the last 24 hours, with the count of the last seven days and how long ago the newest entry was written.
- **Failed messages**: the integration outbox's dead letters, with the messages delivered since the platform's day began.
- **Active users**: active people of the staffing companies, "in N tenants" counting the companies alone. System accounts, the platform's own people and customer portals are excluded. No sign-in time is stored, so the overview counts no sign-ins.
- **Acting as a user**: "None", or the target of today's newest impersonation with no end recorded ("no end recorded" rather than "running", since a lapsed session reports no end either), with how many impersonations started today.

On the warm floor, two cards lifted over the band's edge:

- **Tenants**: every staffing company as one row; customer portals and the platform's own tenant are neither listed nor counted by state, nor are their people. Each row links to the company's console with its initials, name, description, state pill (its state's own tone, no health colour), people, jobs this week and open slots, and no word of its errors. The head links to "All tenants".
- **Act as a user**: a search field whose Enter opens the impersonation picker with what was typed (`ImpersonateUserDialog.InitialSearch`); the users acted as lately, at most three, each one click posting the start as the picker does, offered only while the user is active in an Active tenant; and "Impersonate User", the picker unfiltered.

Then:

- **Onboard a company**: its three steps on a connected track, "Start onboarding" opening the onboarding dialog as the tenant list's "New Tenant" does, the tenants waiting in Setup with how long ago each was created (or a dashed empty box), and the tenants by state as figures without links.
- **Today**: today's impersonations as a timeline among the newest five, and a foot with whether a session has no end recorded, the error log's last entry and a link to it.

One read answers all of it: `PlatformConsoleService.GetOverviewAsync`, under the platform lease ("read the platform overview"), in ten queries whatever the platform holds, refused before any read without platform reach. A company's **jobs this week** are its jobs from Registration to Completed with an occurrence in the platform's week, Monday to Sunday: a single job whose day falls in it, a recurring job with a weekly time on a weekday its period holds. Its **open slots** are the active slots of its live jobs (Registration to In progress) that nobody holds (no Assigned assignment).

A bad day shows through the figures alone, never through a headline sentence (`PlatformOverviewRules`, pure; a tenant's health, `HealthOf`, is kept for the rules' own tests): the errors figure turns amber on any error of the last day, the failed messages figure red on any dead letter, and "Acting as a user" amber while a session of today has no end recorded, each also said in words. The parts rise in once and the dot breathes, both still under reduced motion. At phone width the hero and the cards stack and each tenant row puts its labelled figures under its name.

Verified by: `AppHomeTests`, `HomeTests`, `HomeRulesTests`, `HomePagesTests`, `HomePartsTests`, `WorkPatternCoverageTests`, `OrderSyncTests`, `PlatformOverviewRulesTests`, `SuperAdminOverviewPageTests`, `PlatformConsoleTests`, `tests/e2e/home.spec.ts`, `tests/e2e/platform-overview.spec.ts`, `tests/e2e/job-staffed.spec.ts` and the accessibility sweep.

## Coordination workspace

`/coordination` (Coordination, first under Staffing in the navigation) is where coordinators staff jobs ([PRODUCT.md](../../PRODUCT.md), Core flow 4, "Staffing"). It opens with `ViewCoordination`, which the Coordinator preset and the system owner hold. Every staffing surface reads one availability rule, and open workspaces refresh from the tenant change channel ([ADR 0026](../adr/0026-coordination-workspace-hour-timeline.md)).

It is a full-viewport tool on white under the top bar: a header row across it, the calendar rail on the left and, on the right, the [hour timeline](#hour-timeline) above the Priorities dock. The workspace always shows the hour timeline of one day, from 00:00 to 24:00. It has no week view, no planning mode, no filter button and no list mode; an old address naming `week`, `mode`, `view=list`, `board.unit`, `board.customer` or `board.qual` shows the day it names, unfiltered, on the timeline.

### One read

`CoordinationService.ReadAsync` (`ViewCoordination` checked first; the session's own tenant, never the platform console) reads a snapshot of the tenant's staffing (`CoordinationSnapshot`):

- every active slot of the jobs being staffed (Coordinating, Confirmed, InProgress) that has a time still to come on the tenant's calendar;
- its live assignments;
- its assigned staff member, judged over the remaining occurrences by the one availability rule (`IStaffAvailability`, read for every holder at once; `StaffingRules.Judge` with the assignment's own booking left out);
- the jobs behind.

It runs a fixed number of queries whatever the tenant's size: the jobs and their times, their slots with the qualifications they require, the slots' live assignments, the holders' availability in at most five, and the jobs behind; the time zone comes from the lookup cache. The calendar rail, the timeline's Open slots row and the dock's slot items are answered from the snapshot in memory. As with the staffing dialog's candidates, every row is judged by availability before it is filtered, so filtering happens in memory ([List view](lists-and-dialogs.md#list-view)). The page reads a new snapshot when a dialog it opened closes and when the change channel reports a change.

### Where a slot stands

`PriorityRules.Classify` (`PriorityKind`) puts each slot in one kind:

- **At risk**: it has an assigned staff member whom availability blocks on a time still to come (absent, out of service, booked elsewhere at the same time), with each block's times and dates ("Bo Example, the assigned staff member, is not free for 1 of its 8 remaining occurrences." "On sick leave (reported): 1 occurrence, 2027-03-08.").
- **Covered**: its assigned staff member is free. A warning never puts a slot at risk.
- Without an assigned staff member: **waiting for a pick** when a staff member said yes, **unanswered** when staff members are asked and none said yes, **uncovered** when nobody is asked.

A staff member of the slot who wrote last in their assignment's conversation (`StaffConversation.AwaitsCoordinatorReply`) raises it: a covered slot becomes **awaits your reply**, and any other kind lists it last among its reasons ("Ada Example wrote and waits for your reply.").

### Header row and calendar rail

The header row (`CoordinationTimeline`, 56 px on white) holds ‹ the day shown ("Wednesday 25 November") with "Week 48 · 2026" under it ›, Today (today, with its now line scrolled into view) and New job for a holder of `JobsCreate`. Every tool is a button with its name. The arrows, Today and the rail move the shown day.

The calendar rail (`CalendarRail`, 252 px) stacks three months from the shown day's month:

- Buttons above and below ("Show one month earlier, October", "Show one month later, February") move the stack one month; the stack follows the shown day when the day leaves it.
- Each month is a table of day buttons with a week-number column. A day's button shows that day. A week number is a label ("Week 47"), not a button, since the timeline shows one day.
- Today has a ring; the shown day is filled teal and its week tinted.
- Under a day's number one dot says where its slots stand, read from the snapshot (`CoordinationSnapshot.DayMarks`, `PriorityRules.MarkDays`, `CalendarRail.DotOf`): **at risk** (amber, a holder blocked that day) before **open slots** (red: uncovered, unanswered or waiting for a pick) before **all staffed** (teal). A key at the rail's foot names them, and each day's button says them in words ("Tuesday 24 November 2026, 1 open, 2 staffed").

The shown day is kept in the address (`day`, left out for today).

### Opening dialogs and the address

An open slot's time on the timeline and a slot item in the dock open the slot's dialog on its Staffing view (`JobSlotDialog.OpenStaffingAsync`, [Staffing dialog](jobs-and-staffing.md#staffing-dialog)). The page names that view for slot dialogs (`[DialogViewOnPage]`), so F5 reopens it there.

F5 and a link keep, in the address ([URL state](lists-and-dialogs.md#url-state)): the day, the timeline's search, order and page (`board.*`), the dock's tab, search and day (`prio.tab`, `prio.q`, `prio.day`), its fold (`dock`) and the open dialog.

### Priorities dock

The Priorities dock (`PrioritiesDock`, under the timeline, 330 px with a lift above it) lists everything that waits on the coordinator, most urgent first.

- **Head**: "Priorities" with the count and "Everything waiting on you, most urgent first"; the tabs with their counts (All, Orders, Slots, Questions, Absence; a tab list that the arrow keys move; an address naming a former Behind tab shows All); a search, behind its icon until used; the day chip while the Open slots row's link narrows the dock to a day ("Wed 25" with its count; a click shows every day again); and the fold.
- **Fold**: folds the dock to a 44 px bar keeping the count and each tab's count with something waiting ("Orders 1", "Slots 3", "Questions 2", "Absence 1"), nothing more. The fold is page state in the address (`dock=closed`), not a per-browser preference.
- **Body**: one flat list across the dock's width, without groups, headings, folds or per-group "Show all", ordered by [the service's one rule](#the-order), in a region that scrolls on its own. The tabs and the search filter it. It shows the first 50 (`PriorityList.PageSize`), and "Show more" adds 50 each time, with how many of the tab's items are shown, up to 500 (`PriorityList.MaxShown`); beyond that the search or a tab narrows it.
- **Item**: a reason pill naming its kind in its kind's colour (At risk, Uncovered, Unanswered, Waiting for a pick, Question, Message, Order, Behind, Sick, Absence); when ("Today 08:30", "Tue 24 13:00", red when today or past); what (bold, then its line); how long it waited or how soon it starts; and its action.
- **Action**: each item has one action, **Open** (**Answer** for a question or a message), and a click on the row runs it. Everything about a job, its slots or its conversations opens that job's dialog through its id-only opener (`JobDialog.OpenAsync`), where its slots are staffed on its Staffing view; an absence opens the absence's dialog. A sick report has a second action, Sick leave, which opens its absence. The dock has no Request, Assign, Staff, Pick or Replace of its own, and no step runs in it (`LifecycleCallScanTests`). The page reads again once the dialog closes.

The dock covers the tenant's whole work and is not narrowed by the shown day unless its day chip is set.

### The kinds

`CoordinationService.ReadPrioritiesAsync` (`PriorityItemKind`, `PriorityQuery`) needs `ViewCoordination` in the session's own tenant and a snapshot of it. A kind the reader may not see is left out, never refused. The pill is in brackets.

- **Absence to settle** (Sick or Absence; a reader of absences, [ADR 0044](../adr/0044-taking-off-for-absence-and-open-again.md)): one item per absence that blocks (approved, or sick leave still undecided, `AbsenceRules.Blocks`) and still falls on a time, not yet ended, of a live request or assignment of its staff member (Requested, a question, Accepted or Assigned), judged by the one rule of a job's occurrences (`AbsenceImpactRules.AffectedOccurrences`): "Sick leave on 2026-11-11, Lead, Algebra" (or "2 jobs") with "1 assignment to settle", due at its first affected time.

  Open opens the absence's dialog on its Affected jobs view ([Absence impact](schedules-and-absence.md#absence-impact)). It leaves the dock only once every affected assignment is settled (taken off for the absence, canceled, declined, not selected, expired), whether or not the sick report is marked Handled; nothing is dismissed outside the workflow.
- **Orders to answer** (Order; `ViewCoordination`): jobs a customer's portal sent (`LinkedCrossTenantId`) waiting in Awaiting coordination, with when they start and how long they have waited. A job awaiting coordination has no slot being staffed, so it appears here and not on the timeline. Open opens the job.
- **At risk**, **To request or assign** (Uncovered: slots nobody is asked for yet), **Unanswered requests** (Unanswered) and **Waiting for a pick**, from the snapshot: one row per job and kind, with its customer, the job with a short count ("Algebra, 2 of 3 open", "1 asked, no answer", "1 said yes", "1 at risk") and the soonest time of those slots (on the asked day when one is set). The tabs count jobs. Open opens the job's dialog. A slot whose staff member was taken off for absence says so until a new assignment holds it ("Algebra, 1 of 1 open, open again", `PriorityItem.IsOpenAgain`).
- **Questions from staff** (Question):
  - absences in StaffQuestion ("Question to the coordinator", `ApproveResourceAbsence`, the reader's own left out), Answer opening the absence;
  - requests in StaffQuestion, where the staff member asked before answering ([ADR 0037](../adr/0037-request-questions-are-workflow-steps.md)), "Question waiting for …" since the step that left it there, until a coordinator answers;
  - live assignments whose staff conversation waits for a reply (`ViewCoordination`), Answer opening the job that holds them.

  Each is read from the state or the whose-turn timestamps alone, nothing per reader, so one coordinator's answer, reply or "Mark as handled" ([ADR 0038](../adr/0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md)) takes it off every coordinator's dock.
- **Messages from customers** (Message; `ViewCoordination`, which reads the customer thread): live jobs whose customer thread's newest comment is a customer contact's, judged as the job's dialog and the homes judge whose turn it is. Answer opens the job.
- **Absence to approve** (Absence; `ApproveResourceAbsence`, the reader's own left out): planned leave in Requested only. Sick leave is reported sick and never counted twice. It leaves once approved or rejected.
- **Jobs behind** (Behind; under the Orders tab), from the snapshot, one row per job: a job still being coordinated after its period started, and a job the job clock has not moved on time, with what the clock recorded of why ([Jobs, timetable and slots](jobs-and-staffing.md#jobs-timetable-and-slots), "The job clock"). Jobs behind are derived from each job's state and period, not stored. Open opens the job.

Each read is counted whole and cut in SQL where its filter can be expressed in SQL (orders, messages, questions, absences; for absences to settle the candidates, with their overlap judged after the read). Each kind is cut to its most urgent page, and a kind outside the asked tab is not read, so the tabs count everything while the merged list is cut to its page. The search narrows by the names an item shows. A day (`prio.day`) narrows the dock to what happens on it: slots with a time that day, absences covering it and orders whose period holds it. Messages, staff conversations and jobs behind have no day and are left out.

### The order

`PriorityList.Order` orders the dock by one deterministic rule in the service:

1. Each item is **due** at one instant (`PriorityList.DueUtc`): when its work starts (a slot's next time, or its time on the asked day; an absence's first affected time; an absence's first day; an order's first time), or, for an item that waits for a reply, one day after it was asked (`PriorityList.ReplyWithin`: a staff member's question, a customer's message). An item with both (an order a portal sent, a question about an absence) is due at the earlier. A job behind is overdue and so due now.
2. The list runs by when each item is due, everything already due counting as due now, so overdue items come first and items without a time of their own fall among the dated ones by how long they have waited.
3. At the same due time a fixed kind order decides: at risk, absence to settle, uncovered, orders to answer, unanswered, questions, messages, waiting for a pick, absence to approve, jobs behind.
4. Then the item due earlier (the longest overdue), then the item's key.

This is distinct from `PriorityRules.Order`, which orders one slot's kinds.

### Tenant change channel

`TenantChangeBus` (one per process, [ADR 0019](../adr/0019-coordination-workspace.md)) carries committed changes to open pages.

- **What is collected.** `WorkflowService` collects from every save of a step (a create's insert, a dialog's edits, the step's own save, its follow-ups; the context's `SavingChanges`) the jobs and staff records it touched: a job, its times and slots name the job; an assignment names its job and staff member; a staff record, its absences and pattern assignments name the staff member.
- **Who is named.** Before the commit, in the step's transaction, `TenantChangeCollector.NameAffectedAsync` adds the staff members of the live assignments (asked, said yes, assigned) of every job the step touched, so a coordinator's edit of a job reaches the own pages of exactly the staff members involved; a closed assignment's staff member sees it at their next load. The same read names a user's staff record for a user's step (a rename), the staff members whose active assignments take hours from a work pattern for a pattern's step, and a customer's jobs (and so their live staff members) for a customer's step.
- **When it is published.** Only after the step has committed, with its tenant. A refused step, a step rolled back after its edits were saved, and a step whose save failed publish nothing. A write that names no job and no staff member publishes nothing.
- **Other publishers.** `QualificationService`, which writes outside the workflow, publishes the staff record after its commit when a qualification is given or taken away. The job clock publishes each step after its own commit, and the role-holding service each holding given or taken, each naming the jobs' staff members the same way.
- **Failure and isolation.** A failure to publish is logged and never fails the write. A change carries ids only. Each subscriber is told on the thread pool without the publisher's execution context, so a slow, blocking or throwing page never holds up the write or another page.

**Who hears what** (`TenantChangeFeed`, one per circuit): the tenant is the session's (a tenant's user, or a platform owner impersonating one of its users); the platform console and a session without a user or tenant get no subscription. The workspace's interest (`ChangeInterest.Coordination`) needs `ViewCoordination`, checked as it subscribes, and hears every change of its tenant. The staff member's own pages (`ChangeInterest.OwnWork`) hear only a change naming the reader's own staff record and are told only that one came. Whatever is heard, the page reads again through its own permission-checked services, so an id reveals nothing and a revoked permission shows as the service's refusal.

**At most every five seconds** (`RefreshThrottle`, `LiveRefresh`): the first change after a quiet spell reloads at once; later changes within five seconds of the last reload are folded into one reload once the five seconds have passed, on the injected clock. The subscription and any pending reload end with the page, so no callback reaches a page that is gone.

**Nothing missed, nothing stale.** A page takes the tenant's change count before its first read (`TenantChangeFeed.Mark`) and hands it to `LiveRefresh` (`Since`), which reads once more after subscribing when a change came in between. The workspace shows only the newest of two overlapping snapshot reads (a live reload and a dialog's close), so an older read never overwrites a newer one; the dock likewise shows only the newest of its own reads.

**One instance.** The channel, like the notification bell's, reaches only the circuits of the instance where the step committed (KI-69).

### Layout

The workspace fills the window under the shell's top row and beside its sidebar (`calc(100dvh - var(--app-header-height))`, [ADR 0029](../adr/0029-left-sidebar-shell.md)) on white, and the page itself never scrolls: the header row across, the rail at 252 px with a border on its right, the timeline as its own scroll area sized to what the dock leaves (it scrolls sideways through the day), and the dock at the foot. It is the one page without the page header band (`PageChromeScanTests`); a hidden heading names it. Below 1000 px the page scrolls and the parts stack: header, timeline (60 % of the window), dock, rail.

Verified by: `PriorityRulesTests`, `PriorityListTests`, `RefreshThrottleTests`, `TenantChangeBusTests`, `CoordinationTests`, `PrioritiesTests`, `BoardChangeChannelTests`, `AssignmentQuestionTests`, `AbsenceQuestionTests`, `StaffConversationTests`, `CoordinationPageTests`, `CalendarRailTests`, `JobDialogsTests`, `StaffingDialogsTests`, `AssignmentDialogsTests`, `StaffBoardTests`, `tests/e2e/coordination.spec.ts`, `tests/e2e/coordination-showcase.spec.ts` and the accessibility sweep.

## Hour timeline

The hour timeline is the coordination workspace's frame and timeline (`CoordinationTimeline`, `Components/Coordination/`: the header row, the rail's place, the timeline and the dock's place). It is read by `CoordinationService.ReadStaffMembersAsync` and `ReadBookingsAsync` (`Staffora.Services/Coordination/CoordinationService.StaffBoard.cs`) with `ViewCoordination`, in the session's own tenant, never on the platform console. It shows candidates' schedules, absence and other bookings for one day ([PRODUCT.md](../../PRODUCT.md), Core flow 4).

### Axis and scrolling

- One hour axis for the shown day (`TimelineRules`), from 00:00 to 24:00, its marks labelled 00 to 24. Every time sits at its hour from midnight, so nothing early or late is cut.
- An hour is about a fourteenth of the width beside the names, at least 56 px and at most 96 px (at least 48 px below 1000 px), so the day is wider than the area and bars keep their words.
- The timeline is a scroll area of its own on white that scrolls sideways (and down) while the page never scrolls. The hour head, the Open slots row's label and the 176 px name column stay in place. A pill at its foot says how many staff rows are below the fold ("16 more staff below", counted on scroll by `scripts/coordination-timeline.js`, `watchMore`); a click scrolls on.
- As a day opens (and after Today) the area scrolls so the working day is in view (`scrollToHour`): on today the now line a third into the visible hours; on another day its first booking or open slot, or 07:00 when that is earlier (`TimelineRules.OpeningHour`), at the start. A reload of the same day keeps the coordinator's scroll position.
- The head's corner holds **Find staff** (a search box: part of a name narrows the rows, `BoardFilter.Name`, in the address as `board.q`) and a quiet **sort** button (by name A to Z or Z to A, `board.sort`). The search narrows rows and never hides a shown staff member's times.

### Open slots row

The first row, on the danger tint, shows the shown day's times, still to come, of the slots nobody holds: uncovered or unanswered, as coverage reads them. A yes counts as held, and an at-risk slot has its holder, so it stays in the dock. The times are dashed (red on white for nobody asked, amber for asked without an answer), each naming the slot, its job and how many of the staff members shown are free for every remaining occurrence ("1 of 4 free"). Under the row's name a link ("5 on Wednesday") narrows the dock to the shown day.

### Staff rows

One flat list of the staff in service by name, nothing grouped and nothing hidden, each row 30 px unless bars stack in lanes. The name cell shows the name alone, as a button that opens the person's schedule (`PersonScheduleDialog.OpenAsync`, [Staff pages](#staff-pages)), where absence is registered. On the track:

- **Working time** from the one availability rule, as a faint teal tint with nothing written.
- **Absences** as a striped band over the row's height behind the times, with a label ("Sick leave, reported", "Sick leave, handled", "Planned leave, approved", "Planned leave, waiting for approval"): dashed while it waits for a decision (Requested or a question about it), solid once approved or handled. Requested planned leave is shown although it blocks nothing. A rejected or canceled absence is not shown; the same rule that draws an absence blocks staffing (`AbsenceRules.Blocks`).
- **Times of live assignments** (asked, said yes or assigned, on an active slot of a job being staffed or completed, inside its period) as bars: assigned teal with white text, asked dashed amber, said yes outlined blue, a question waiting for the coordinator's reply with an amber foot, and a time over an absence or another time ringed in red. A bar's caption fits its width (job and customer, job alone, or none on a bar too narrow for a word); its full name is always in its hidden text.
- **Now line** on today only: a red line (`--color-danger-dot`, 3:1 on the track) at the minute on the tenant's clock (`TimelineRules.NowOn`, the injected `TimeProvider` through `TenantClock.ToLocal`, [ADR 0020](../adr/0020-dates-and-times.md)), from the head through every row, with its time on a red plate at its top ("09:40", white at 4.5:1) and in the hour head's name. A timer of the page, disposed with it, renders it again at the turn of every minute.
- **Hover card** (also on focus): job, customer, slot, when and where it stands, and an overlap in danger.

### One rule and parity

The working time is the availability rule's; the Open slots row is the workspace snapshot's own slots; and each slot and staff pair is judged as the staffing dialog judges a candidate (`BoardRules.Pair`), so the board never computes availability its own way ([ADR 0026](../adr/0026-coordination-workspace-hour-timeline.md)). `StaffBoardTests` holds the board's verdict and match equal to the staffing dialog's row for every slot of the Open slots row and every staff member.

### Loading

- **Two steps, newest shown** (`TwoStepLoad`). The board reads the staff members, their working time and absences first (`ReadStaffMembersAsync`) and shows them, then the times, the Open slots row and the pairs (`ReadBookingsAsync`, over the first step and the page's snapshot, each refused when it was read for a tenant other than the session's). A load overtaken by a newer one is cancelled and shows nothing, whichever step it was in.
- **Fixed queries.** The board runs a fixed number of queries whatever the number of staff members, and reads the shown day's week once, so moving within the week reads nothing again.
- **Paging.** Find staff narrows every staff member in service by name, and the board shows them `BoardRules.PageSize` (25) at a time, the page in the address (`board.page`; a search or a new order returns to page 1), with "Previous staff" and "Next staff" at the timeline's end beside "Staff 26-50 of 212 · sorted by name" ("30 staff · sorted by name" on one page). Only the page's staff members are read and judged, so a reload's cost follows the page, not the organization.
- **Live.** The board is part of the workspace's reload on the [tenant change channel](#tenant-change-channel).

### Clicks and focus

A click opens a dialog through its own opener: an open slot's time opens its slot's dialog on the Staffing view, a time its job's dialog, an absence its own dialog, a name the person's schedule. The page reads again once the dialog has closed; once that read is shown in full, the focus returns to the item that opened it, or to its row's name when the read removed the item (`scripts/coordination-timeline.js`).

### Accessibility and phone layout

The timeline is a grid named by the header's title: a row of column heads (the corner with Find staff and the sort, then the hours), the Open slots row, then each staff member's row, with a row header (the name) and a cell (the track) whose bars are buttons named in full ("Algebra, Lead, Monday 9 November 10:00-11:30, Assigned, Ada Example, Brightpath Learning"). The working time, the now line and the hover card are visual only; their facts are in the names (the hour head says "00:00 to 24:00, now 09:40"). Colour is never the only sign. Below 1000 px the timeline scrolls inside its own box, with a 128 px name column and hours 48 to 64 px wide.

Verified by: `BoardRulesTests`, `TwoStepLoadTests`, `StaffBoardTests`, `BoardChangeChannelTests`, `TimelineRulesTests`, `TimelineContrastTests`, `StaffBoardPageTests`, `PrioritiesTests`, `tests/e2e/coordination-timeline.spec.ts` and the accessibility sweep.

## Staff pages

A staff member sees their own schedule, absence and assignments and answers their requests, and never sees other staff members' assignments, internal notes or customer conversations ([PRODUCT.md](../../PRODUCT.md), Actors). Two pages of one's own work sit under My work in the navigation (`Pages/Me/`). They are open to a tenant's users and to a platform owner impersonating one of them, and each is gated on a `Personal` permission that the Staff preset holds.

### My assignments

`/me/assignments` (`ViewOwnAssignments`) lists the reader's own requests and assignments on the shared list surface (`OwnAssignmentList`):

- **Finding.** Searched by the job's or the customer's name, filtered by state, sorted by period or job, its query in the address. Closed rows (declined, not selected or canceled) are hidden and counted until "Show closed" shows them; a state chosen in the filter shows whatever the toggle says.
- **Rows** (`OwnAssignmentRow`) name the job, the slot, the customer, the period, the state in its strip and what the state asks of the staff member ("Your answer is needed", "You said yes; the coordinator picks who works", "You are assigned"), and nothing of another staff member's. A row whose conversation holds a coordinator's comment the staff member has not read says "New reply" (`OwnAssignmentRow.HasUnseenReply`, from the assignment's whose-turn timestamps in the same query), never what was written.
- **Read.** `SlotAssignmentService.QueryOwnAssignmentsAsync` checks `ViewOwnAssignments` and reads the effective user's own staff record's assignments in the session's tenant only (one snapshot of both), whatever else the reader may read. A coordinator who also works lists their own assignments here, never a colleague's.
- **Section navigation** beside the list's cards: "To answer" (Requests; Questions sent, `?status=staffquestion`, a request whose staff member asked the coordinator first, [ADR 0037](../adr/0037-request-questions-are-workflow-steps.md)) and "My assignments" (Assigned, Answer sent, All open). Each is a link to the page with the list's own status filter (`?status=requested`, `assigned`, `accepted`; All open is the bare address), with its count from the same service (one read per state, again after a dialog closes and on a live change). The section the address names is marked as the current page, with its title and one sentence over the cards. A closed row is reached through All open's Show closed.
- **Dialog.** A row opens the assignment's dialog (`SlotAssignmentDialog.OpenAsync`). Its footer offers the staff member's own steps: Accept, Decline and Take back my yes. Accept is greyed with the availability's reason while the staff member is not free, judged from their own absences and bookings only ([Staffing dialog](jobs-and-staffing.md#staffing-dialog)). Once assigned, the dialog shows the job's fields for the assigned staff member's audience ([Job fields](custom-fields-and-settings.md#job-fields)).

  Its body is in sections (What, When, Where, Who, About the job), led for the assignment's own staff member by their request in words and for coordinators by who answers and who picks. The assignment's own staff member reads its thread as "Questions to the coordinator" (staff as "Staff"). The list reads its page again once the dialog has closed.

### My schedule

`/me/schedule` (`ViewOwnSchedule`) shows a calendar month at a time.

The page is a frame: its header and the month in the address (`month`, `yyyy-MM`, through `UrlStateKey.Month`, so F5 and a link keep it). No month, or a month outside the calendar's readable range (`OwnScheduleMonth.IsReadable`), means this month on the tenant's calendar. Everything else is the schedule view it hosts, `PersonSchedule` (`Components/Common/Schedule/`), the same view a coordinator opens for a staff member in [`PersonScheduleDialog`](#a-staff-members-schedule-for-a-coordinator). The view reads one month in one call, `StaffAvailabilityService.ReadMonthAsync` (`OwnScheduleMonth`).

- **The bar**: previous month, Today and next month (icon buttons named "Previous month" and "Next month"), the month's name and its ISO weeks, and at its end "Report absence" for one's own (exactly when `ResourceAbsenceService.MayAddAbsenceForUserAsync` answers true: `ReportOwnAbsence` and a staff member in service) or "Register absence" for another's (`ManageResourceAbsence`), opening the absence dialog in create mode ([Absence](schedules-and-absence.md#absence)). A month that could not be shown keeps Today offered, so the reader is never stranded.
- **Two slim white cards**:
  - the work pattern: the active pattern that holds today when the month holds today, else on the month's first day, or the first that starts in it, with its name, period, week of its rotation and "Ends in N days", and the pattern registered after it ("not confirmed yet" while it is being prepared);
  - the month's hours: working, assignments, absence and open, with a thin bar of the three. The hours count minute by minute over the working time: absence covers first, then an assignment, and the rest is open (`OwnHours`; wall-clock minutes of the tenant's zone).
- **The days**: one row per day of the month, grouped under week headings (the ISO week, the days shown, "Pattern week N" for a rotation of more than one week, the week's hours), on one hour axis the month shares (`OwnDayAxis`: whole hours around the month's working time, assignments and timed absences, 08:00–17:00 when it holds none; a stretch of two hours or more in which nothing lies on any day is folded to a narrow hatched band, named in the axis's corner). Each row has three layers:
  - the working time the work pattern gives, as the base band (through the one availability rule);
  - the staff member's assignments as bars inside it (the job, its times and its customer; a button opening the assignment's dialog for a reader of assignments);
  - absence on top, covering, with its kind and part of the day ("Planned leave from 15:30") and its status ("Approved", "Waiting for approval", "Reported"). A whole-day absence spans the day's working time and lets the assignments under it show through faintly. Each is a button opening the absence's dialog.

  Today's row is tinted with the now line. Times are the organization's local time, named under the cards' key. Below 760 px the rows scroll sideways inside their own focusable region (named, for example, "Days of November 2026"), the day's name staying at the left; the page never scrolls sideways.
- **The notes**: a quiet note at the end of a day's row says what changes that day: a work pattern starting ("Work pattern Winter evenings starts", with ", not confirmed yet" for one being prepared) or ending, the rotation's next week on each Monday it continues into ("Pattern week 2 starts"), a recurring job's last time for the staff member when its period ends in the month ("Last time on Office cleaning"), or an absence starting ("Planned leave starts, until 17 Dec"). There is no separate "Coming up" list.
- **The weeks the pattern gives**: a table of the pattern's rotation, each week's times per weekday and its total, this week marked, and the pattern before it ("Before this: Day cleaning, 2 Mar 2026 to 30 Aug 2026").
- **What is shown**: only assignments in the Assigned state. A request asked (Requested) or said yes to (Accepted, waiting for the coordinator's pick) is answered on "My assignments". Every absence that is not rejected or canceled is shown (`AbsenceRules.ScheduledStates`: Requested, the two question states and Approved), whether it blocks or not.
- **The read**: `ReadMonthAsync(resourceId, month)`.
  - With no record given, the effective user's own staff record (`ViewOwnSchedule`; a holder who is not a staff member is not found).
  - With a record given, a staff member of the session's tenant (one snapshot of tenant and user, the tenant named in every query): anyone's with `ViewCoordination`, the permission of every staffing surface; otherwise only one's own with `ViewOwnSchedule`, so a staff member who names another's record is refused whether it exists or not. A record of another tenant is not found.
  - Assignments on an active slot of a job being staffed or completed, inside its period, each occurrence cut at midnight. A completed job keeps its assigned staff member's times on its past days, so a staff member looking back sees what they worked.
  - `ReadOwnWeekAsync` (the week "My work" counts, with the live requests) shares the same day read.
- The view reads again once a dialog it opened has closed, and on the tenant change channel ([Live](#live) below; for another's schedule, the coordination's changes).

The absence page keeps serving a staff member as the list of all their own absences, beside "My schedule", which shows a month of them.

### A staff member's schedule for a coordinator

`PersonScheduleDialog` (`Components/Common/Dialogs/`) shows the same `PersonSchedule` for a given staff record, titled "Schedule of Ada Example". It is a form dialog without a lifecycle, opened only through `OpenFormDialogAsync` by its static opener (`PersonScheduleDialog.OpenAsync(dialogService, resourceId, name)`). Its month is the dialog's own, never the address's. "Register absence" shows only for a reader who may register the staff member's absence. The coordination workspace opens it from a staff member's name on the [hour timeline](#hour-timeline). The accessibility sweep lists it under `unsweptDialogs`, since the journeys check it with axe.

### Answering from the bell

A request's notice links to the assignment's dialog over the page the staff member is on (`?dlg=assignment-{id}`), so the bell, "My assignments" and "My schedule" open the same dialog. The staff member's yes and no reach the job's coordinator and whoever made the assignment, and a reported absence reaches the approvers, each as a notice ([Assignments](jobs-and-staffing.md#assignments), [Absence](schedules-and-absence.md#absence)).

### Live

Both pages listen on the tenant change channel for their reader's own work (`LiveRefresh`, `ChangeInterest.OwnWork`). A committed step that names the reader's own staff record (a coordinator asked, assigned or canceled them; an absence of theirs was decided; a job they are asked for, said yes to or assigned to was edited: its times, period, name or slots) reads the page again through its own service, at most every five seconds. A change of anyone else's is never told to them, and they are told nothing but that a change came ([Tenant change channel](#tenant-change-channel)).

Verified by: `OwnWorkTests`, `ScheduleMonthTests`, `StaffPagesTests`, `OwnScheduleModelTests`, `OwnScheduleMonthTests`, `UrlStateKeyTests`, `CoordinationPageTests`, `StaffBoardPageTests`, `tests/e2e/staff-pages.spec.ts`, `tests/e2e/coordination-timeline.spec.ts` and the accessibility sweep.
