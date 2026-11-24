# Jobs and staffing

A tenant describes what it sells as services, registers jobs for its customers, and staffs each job's slots with its own staff members: by a request the staff member answers, by a direct assignment, or by a request round sent to several staff members at once ([PRODUCT.md](../../PRODUCT.md), Core flows 3 and 4; [ADR 0006](../adr/0006-course-staffing-domain.md), [ADR 0027](../adr/0027-generic-resource-staffing.md)).

Every staffing surface judges a staff member by one availability rule ([ADR 0019](../adr/0019-coordination-workspace.md)); the availability itself is described in [Schedules and availability](schedules-and-absence.md#schedules-and-availability), and the verdict built on it in [Staffing dialog](#staffing-dialog). Permissions are defined in [authorization.md](../authorization.md#rolepermission); tenant isolation in [tenant-isolation.md](../tenant-isolation.md).

## Service catalogue

A tenant describes what it sells as [services](../database.md#service): a name, a description, the qualifications a person doing it needs (each **Required** or **Preferred**, `RequirementLevel`), a default number of people (1 to 20) and whether the customer portal offers it. A service has no price.

- **Kept under settings.** The settings shell's **Services** panel (`/tenant/management/settings/services`, group "Staffing", `ServicesPanel`) lists the catalogue on the one list surface (`ServiceList`): searched by name or description, filtered by state and by the portal, its query in the address under the prefix `services`. A row opens `ServiceDialog` (token `service`). "New service" is drawn for a holder of `ServicesEdit` (the Administrator preset and the System owner, never the coordinator). The shell stays gated on `SettingsView`.
- **Lifecycle.** Create, Active, Archived (`ServiceWorkflowAttribute`). A service is archived rather than deleted, because slots name it. Every step takes the customer and job locks ([Application locks](../database.md#application-locks)).
- **A slot made from a service.** The job dialog's slots view draws a "Service" picker (`slot-service`) beside its add action, from the job's editor options (the active services, read from the cache behind `JobsEdit`). The picker is a list view's add choice (`StateEntityDialogListView.AddChoice`, drawn by `StateEntityDialogChildList`), so the body draws no input.
  - With a service chosen, the add action opens the slot dialog filled by `JobSlotService.NewJobSlotFromServiceAsync` through `ServiceSlotRules.Build`: the service's name and description, and each of its qualifications that is active now, at its level.
  - The slot keeps the service's id (`JobSlot.ServiceId`, never changed) and may still be edited; later changes to the service never reach it. The slot's save refuses a service that is not active.
- **Levels.** The qualification editor (`StateSlotQualifications<TRow>`, a slot's and a service's rows through `IQualificationRequirement`) adds a row as Required and switches it with a button beside the name; read-only, a preferred row is marked "(preferred)". The staffing ranking reads the levels ([Staffing dialog](#staffing-dialog)). A missing Required qualification is a warning; a missing Preferred one never warns, so the week board and the planning verdicts read the Required rows only. A candidate's qualifications read, for example, "The required one, 1 of 2 preferred".
- **Offered in the portal.** A service step that brings the service into the portal (Active and "Offered in the customer portal"), changes its name, description or default number of people there, or takes it out (archived, or no longer offered) queues one message per portal of the staffing company (`ServiceOfferFollowUp`, in the step's transaction under its locks; kinds `service-offered`, `service-changed`, `service-withdrawn`; `ServiceOfferSync`).
  - A change of the qualifications alone sends nothing: qualifications never cross.
  - Enabling a portal queues the offers of every service already offered (`ServiceOfferQueue.QueueCatalogueAsync`, in the "Enable portal" step's follow-up).
  - Each portal keeps a read-only copy ([OfferedService](../database.md#offeredservice)); the transport is the [integration outbox](operations.md#integration-outbox).

Verified by: `ServiceCatalogueTests`, `ServiceRankingTests`, `ServiceCatalogueDialogsTests`, `OrderLinesTests`, `OfferedServiceIsolationTests`, `ServiceOfferSyncTests`.

## Jobs, timetable and slots

A job is a [Job](../database.md#job) registered for a customer. A recurring job's weekly times are its timetable ([JobTimeSlot](../database.md#jobtimeslot)). The staff members it needs are its slots, each a [JobSlot](../database.md#jobslot) with its own dialog and the qualifications it requires ([JobSlotQualification](../database.md#jobslotqualification)). Dates and times follow [ADR 0020](../adr/0020-dates-and-times.md).

### Registering a job

"New job" on the Jobs page opens the job's dialog in create mode, which shows only Info: its name, its customer (an active one), the customer's contact (an active one, optional), its unit (the Company root unless another unit in use is chosen), its schedule and its period (from today for three months, both days changeable). The dialog's other views are `StoredOnly`, and the comment column waits for the stored row too.

Register creates the job in Registration. The stored job then shows Place, Timetable, Slots (and Staffing, once it applies) and the comment column.

- Weekly times are kept in the Timetable view ("Add time" per weekday, each time's start and end typed; what the rules refuse is listed as it is typed) and saved with the registration's Save. The place is kept in the Place view the same way. Hand over and Start need a time.
- Slots are added in the Slots view once the job exists: "Add slot" opens a slot's own dialog in create mode on top, whose Save adds the slot with the qualifications it requires.
- Choosing another customer clears a contact of the previous one.

### Single and recurring jobs

The job dialog's Info view chooses the kind ("Schedule": Single, the default of a new job, or Every week).

- A single job has a day ("Day") and its start and end ("Starts", "Ends"; "Ends the next day." under the end when it is at or before the start); its last day follows from them.
- Choosing Single for a job with weekly times asks before it removes them. Choosing Every week clears the times and proposes three months from the day.
- The Timetable view of a single job says its day and times are under Info. The portal's order dialog chooses the kind the same way.
- Every reader of time reads a job's occurrences through `JobRules.Occurrences(JobTimes, …)`: `IStaffAvailability`, the staffing verdicts, the assignment and job guards, the timeline and its cards, the calendar rail's marks, the priority list's next time, a staff member's own week, the job clock and the portal.
- A single job past midnight is one occurrence, judged whole, found on either of its days and drawn on both (`JobRules.DaySegments`); its card names the whole occurrence ("2026-12-03 18:00-02:00 (+1 day)").
- The job clock moves a single job in progress at its start time and completes it at its end (`JobRules.Window`). Lists and dialogs say when a job happens with `JobRules.WhenText`.

Verified by: `JobOccurrenceRulesTests`, `SingleJobTests`, `tests/e2e/single-job.spec.ts`.

### A place

The job dialog's Place view (the workflow's Place area) holds where the job takes place: the place's name, street address, postal code and city, and its access notes, each optional. The notes' helper says they reach only the assigned staff. The place is editable wherever the description is, and read-only once the job is completed, canceled or deleted.

- A staff member reads the place in their own assignment's dialog ("Where?", `[data-place]`) whatever its state, and the access notes (`[data-access-notes]`) only once Assigned.
- "My assignments", "My work" and the timeline's cards ("Place", on a booking and on an open slot's bar) name it in a word (`JobPlace.ShortText`: its name, otherwise its city).
- A portal's order carries the four parts of a place, never notes; its send copies them onto the job, and the order's page shows them ("Place", `[data-order-place]`).

Verified by: `JobPlaceTests`, `JobPlaceDialogsTests`, `tests/e2e/job-place.spec.ts`.

### Handing over, coordinating and confirming

- **Hand over** moves a registered job to Awaiting coordination; "Back to registration" returns it.
- A coordinator **starts coordination** from either state, which names them its coordinator (`Job.CoordinatorUserId`, set by the step's action, never a field of the dialog). Hand over and start both need the customer, contact and unit in use, a time and an active slot.
- **Confirm** moves the job to Confirmed regardless of coverage, and fixes its period, timetable and slots.
- Staff are requested and assigned while the job is Coordinating, Confirmed or InProgress ([Assignments](#assignments)).
- The [job clock](#the-job-clock) moves it to InProgress and Completed.

### Deleting and canceling

A job in registration that no staff member was asked to work is deleted from its dialog (Delete, `JobsDelete`, confirmed).

- Its slots go with it, each through its own system-driven step into Deleted with a history row correlated with the job's. Its times and custom-field values go with its row, which the delete removes, so the job no longer blocks its customer's, contact's or unit's deletes ([ADR 0016](../adr/0016-deletion-cascades-through-child-workflows.md)).
- A job is a draft only in registration (Create is the dialog's entry and is never stored), so no other state offers the delete.
- A job a staff member was asked for is kept for the record and canceled instead (`JobWorkflowAttribute.NoStaffAskedReason`; the slot's own blocker refuses it too). A job with a comment is not deleted, as no commented record is.

Any job not yet completed is canceled (`JobsCancel`, confirmed, taking no edits): "Cancel job" (canceled by us, `CanceledBySupplier`) or "Canceled by the customer" (`CanceledByCustomer`, which the customer portal also takes).

- Both cancel the job's active slots through their own Cancel, as part of the job's step whatever its state (`WorkflowContext.IsTakenWith`). Each slot's Cancel cancels its live assignments (requested, said yes, assigned) through their own steps, each with its history correlated with the job's and a notice to its staff member ("Algebra is canceled for you", naming no other staff member). A declined or not selected request stays.
- The access gate judges the whole plan: a holder of `JobsCancel` alone cancels no job with slots (the slot's Cancel needs `JobsEdit`, an assignment's needs `PerformCoordination`; the Coordinator preset holds all three).
- Each cancel asks for a reason: its prompt is its confirmation, and the answer is a note in the job's internal thread, which only the coordinators read. Closing the prompt cancels nothing.
- A canceled job's staff members are no longer booked (their assignments are canceled), its coverage reads uncovered, the job clock leaves it alone, and every field is read-only.

### The job clock

`JobClock` and its hosted service `JobClockService` (`Staffora.Services/Jobs/`) move jobs through the steps their period implies.

**A pass.** A pass goes through every Active tenant. The platform tenant and tenants in Setup, Suspended, Pending deletion or Archived are skipped; their ids are listed on a system context under a lease with its reason. In each tenant the pass acts as that tenant's system account: a service scope of its own whose fresh runtime takes the account's session, so every context it opens is filtered to the tenant and every row it writes is stamped with that account ([tenant-isolation.md](../tenant-isolation.md)).

- A confirmed job goes in progress at the start of its period's first day (`job-period-starts`), and a job in progress is completed once its period's last day is over (`job-period-ends`).
- Both are judged on the tenant's own calendar, its time zone read through the settings' reader of a named tenant (`TenantClock.GetTimeZoneOfAsync`). A day starts at its own local midnight across a daylight-saving change, or at its first instant when the clock skips midnight (`JobRules.LocalDay`, `JobRules.ClockTarget`).
- The steps are system-driven: no button offers them and every public path of `WorkflowService` refuses them. Their conditions judge the period again on the tenant's calendar (`JobWorkflowAttribute.PeriodStartedReason`, `PeriodEndedReason`), so even a clock that misjudged the day moves no job early.
- Each step runs in a transaction of its own: the job's step locks first (structure, customer, job), the job read under them and judged again, then `WorkflowService.RunSystemStepInTransactionAsync` and the commit.
- A missed pass (the app was down) is caught up by the next: a job whose period ended while it was still confirmed goes in progress and then is completed, two history rows in that order. Two passes at once (two instances of the app) move a job once; the second finds it moved.
- A job still being coordinated when its period starts is left alone and not flagged by the clock; its coordinator confirms it, and the coordination workspace lists it among the jobs behind ([Coordination workspace](coordination-and-homes.md#coordination-workspace)).
- No notice is sent: the coordinator and the staff members already know the job's period.
- Every step the clock commits is published on the tenant change channel after its commit.
- A tenant's new time zone counts at the next pass. The settings' save invalidates the cache entry the clock reads; a zone saved by another instance of the app counts within the cache's ten minutes.

**Expiring requests.** After the jobs, the pass expires the tenant's requests whose answer-by time passed (`JobClock.Expiry.cs`; [Assignments](#assignments)): first its open rounds with an unanswered request (`round-expire`, whose cascade expires them together), then each Requested or questioned request outside an open round (`assignment-expire`, `assignment-expire-question`). Each runs in a transaction of its own under the job's step locks, judged again under them, its notices dispatched after the commit. A refusal is logged as a warning, a failure as an error, and the pass goes on. A job that ends in the same pass has already closed its open requests as Not selected.

**Failures and backing off.** A job that cannot be moved is logged and the pass goes on with the next job and the next tenant: an exception as an error (kept in the platform's error log), a step the workflow refuses (a tenant held after it was listed) as a warning with its reasons.

- A job that could not be moved waits before it is tried and logged again: five minutes after its first failure, doubling with each failure in a row, at most six hours (`JobClock.RetryAfter`). A change to its row meanwhile (a coordinator saved it) tries it at the next pass. A job that moves or is no longer due is forgotten.
- What the clock remembers belongs to its process: a restart tries every job at once, and a second instance tries on its own (KI-69).
- The coordination workspace reads it (`JobClock.FailureOf`) to say why a job it lists as behind did not move: a refusal's words, or that an error kept in the error log stopped it, with when the clock tries again.

**Configuration.**

- `JobClock:Enabled` gates the clock: on in the app's `appsettings.json`, off when the section does not say so, and switched off by the end-to-end server (`scripts/e2e-server.mjs`) and the test hosts (`AppHost`, `ServiceHost`), so no job moves under a journey or a test that did not ask for it. When off, the hosted service says so once in the log and ends.
- `JobClock:Interval` is the wait between passes: a second to an hour, five minutes by default, checked at startup. The first pass runs as the app starts, after the startup work of `Program.cs`.

### The rules

- The period's first day is not after its last, and a period lasts at most a year.
- Each weekly time is on a weekday, starts before it ends and overlaps no other time of its day (one may start where another ends). A job has at most 50 weekly times (`JobRules.MaxOccurrences`), which keeps the staffing surfaces' repetition over the period bounded.
- Times are the tenant's wall clock. `JobRules.Occurrences` repeats the timetable over the period's days for the staffing surfaces, which turn them into instants through the tenant's zone.
- The contact is always one of the job's customer's.
- A customer, contact or unit given to a job anew must be in use. One the job names already stays when it goes out of use later: **a customer or contact deactivated while its job is registered or coordinated leaves the job as it is**; the job is kept and staffed to the end. It is then neither handed over nor started until the customer, contact and unit are in use again; a save of anything else goes on.
- Every one of these steps takes the tenant's customer lock, so no job is registered, handed over or started on a customer or contact whose deactivation committed before it, and a deactivation that commits after it finds the job as it was.

### Slots

- A slot is added, changed and canceled while its job is in Registration, Awaiting coordination or Coordinating; from Confirmed on, its slots are fixed.
- A slot no longer wanted is canceled (kept as canceled, its live assignments canceled with it, each by its own step: [Assignments](#assignments)), never deleted by hand.
- A qualification is required anew only while it is active in the catalogue. An archived one stays on the slots that required it before, marked "(archived)". The slot's save judges this itself (`JobSlotWorkflowAttribute.QualificationActiveReason`, over the qualifications its graph loads; the slot guard tells a kept one from one required anew), under the job lock that a qualification's archive also takes, so an archive and a slot's save never cross.
- An archived qualification a slot still names still counts as required: the archive only stops it from being given to anyone new, and its holders keep it on their record. A candidate who holds it meets that requirement.
- A staff member without a required qualification is staffed with a warning, not refused ([Staffing dialog](#staffing-dialog)).

### Coverage

Coverage is derived from a job's active slots and never stored ([ADR 0006](../adr/0006-course-staffing-domain.md)): uncovered while no active slot is held (a job without active slots included), partly covered, or covered (`JobCoverageRules`, read for many jobs in two queries by `JobCoverageReader`).

- A slot is held by an Assigned or Accepted assignment of a staff member in service (`SlotAssignmentWorkflowAttribute.Holds`, `StaffServiceRules.InService`, read in one query), however many staff members said yes.
- A staff member taken out of service keeps their assignment, but the slot reads uncovered and the job as needing re-staffing; the holder is named "(assigned, out of service)".
- The Jobs page shows coverage ("Uncovered (2 slots)"), and so does each row of the job dialog's Slots view. The Slots view also names each slot's assigned staff member and counts those asked and those who said yes ("Ada Example (assigned)", "2 asked, 1 said yes", `JobSlotRow.StaffMembersLabel`). The dialog's Info view names no coverage; its Staff section shows who works each slot instead.

### Deleting what a job names

A customer jobs are registered for, a contact a job names and a unit that runs a job are not deleted. Their delete steps are refused, and drawn greyed, with the reason (`JobsBlockCustomerDelete`, `JobsBlockContactDelete`, `JobsBlockUnitDelete`, each a registered delete blocker counting the jobs), so they are kept inactive or archived instead.

The reason says what can be done: choose another contact or unit while every job can still change it, or keep the contact inactive or archive the unit once a job keeps it for good (its unit from Confirmed on, its contact from Completed and the cancel states on). A deleted job never blocks, because its delete removes its row.

### Locks

Every job step takes the tenant's structure lock, its customer lock and its job lock, in that order. Every slot step takes the job lock, and so does every qualification step (`). The order among a tenant's application locks is fixed ([Application locks](../database.md#application-locks)).

### Who

- A permission of the Jobs or Coordination area reads the jobs and their slots: the Jobs page and the job and slot dialogs, read-only without the permission a step needs.
- `JobsCreate` registers and hands over. `JobsEdit` saves, takes a job back to registration and keeps its slots ("Add slot", a slot's save and cancel). `PerformCoordination` starts and confirms. `JobsDelete` deletes a draft and `JobsCancel` cancels.
- A step carries the dialog's edits only for a user who may save them (`JobsEdit`, the engine's edit-authority rule). A registrar who holds `JobsCreate` alone hands a job over as it is stored, and a staffer who holds `PerformCoordination` alone starts and confirms it as it is; for them the fields are read-only and a step is greyed while edits are pending.
- The Coordinator preset holds them all; the Administrator and the Staff member none; the System owner every one. The platform console keeps no tenant's jobs ([authorization.md](../authorization.md#rolepermission)).

### Pages

- **The Jobs page** (`/staffing/jobs`, Staffing in the navigation): `JobList` on the one list surface, searched by name or customer, filtered by state, sorted by period (the latest first) or name. Each row shows its customer, period, unit, coordinator and coverage; the query is kept in the address.
- **The job dialog** (`JobDialog`, token `job`): Info (name, description, customer, contact, unit, period, the job's custom fields, and the Staff section), Staffing (for a coordinator), Place, Timetable (`StateJobTimetable`, the workflow's Timetable area) and Slots (`JobSlotList`, rows opening each slot's dialog on top). Info names no coordinator (set by the step that starts coordination, the recipient of the job's notices, and listed on the Jobs page), no coverage and no explanatory note.
- **The Staff section** (`JobStaffSection`, read with the dialog's editor options `JobEditorOptions.Staff`, `JobStaffEntry`, by whoever reads the job) lists each active slot group in the order its slots were made: its name, the staff assigned by name (one out of service says so), "Open" for each slot nobody is assigned to, and quietly who is asked and who said yes ("Asked: Bo Example"). A slot with an open place gives a coordinator **Find staff**, which shows the Staffing view on that slot.
- **The Staffing view** (`staffing`, `ViewCoordination`; `JobStaffingView`) is offered while the job is Coordinating, Confirmed or In progress and has a slot (`JobEditorOptions.StaffingOpen`). It is the coordinator's one place to staff the job: the slots as a row of choices (the first with an open place unless Find staff chose another), and under them the chosen slot's staffing in place ([Staffing dialog](#staffing-dialog)). Requests, assignments and request rounds are made in the job dialog itself, with no dialog on top.
- **The slot dialog** (`JobSlotDialog`, token `slot`): Info (whose slot it is, its name and description, and its required qualifications, `StateSlotQualifications`, the workflow's Qualifications area); Staff (the staff members asked for or assigned to it, rows opening each assignment's dialog on top through the assignment service the body injects); and, for a coordinator, Staffing.
- The job's custom fields are described in [Job fields](custom-fields-and-settings.md#job-fields).

### Cross-tenant links

`Job.CrossTenantId` and `Job.LinkedCrossTenantId` are written only by the portal's order send ([Customer portal](customers-and-portal.md#customer-portal), [ADR 0007](../adr/0007-cross-tenant-ordering-customer-portal.md)). No state opens them to a dialog. Registration refuses a new job that sets them or a coordinator (`JobWorkflowAttribute.NothingSetAheadReason`, judged by the step, so a create that bypasses the service is refused too).

Verified by: `JobWorkflowTests`, `JobRulesTests`, `JobClockTests`, `JobLifecycleTests`, `JobDialogsTests`, `JobCancelWorkflowTests`, `JobDeleteCancelTests`, `JobCustomFieldTests`, `DeleteCoverageTests`, `ReadSweepTests`, `tests/e2e/jobs.spec.ts`, `tests/e2e/job-staffed.spec.ts` and the accessibility sweep.

## Assignments

A [SlotAssignment](../database.md#slotassignment) is one staff member asked for or assigned to one slot of a job. Staff members accept or decline their own requests ([PRODUCT.md](../../PRODUCT.md), Core flow 4; [ADR 0010](../adr/0010-comment-threads-per-audience.md), [ADR 0011](../adr/0011-notifications-from-workflow-after-commit.md)).

### Asking and assigning

A coordinator (`PerformCoordination`) opens a slot's dialog. Its Staff view (`SlotAssignmentList`) lists each staff member asked or assigned, filtered by state and sorted by name or by when they were asked (`SlotAssignmentService.QuerySlotAssignmentsAsync`, reached through `JobSlotService.Assignments`). Its Staffing view ranks the candidates in place (`SlotStaffingPanel`, the same component as in the job dialog's Staffing view; [Staffing dialog](#staffing-dialog)). Each row offers "Request" (the staff member accepts or declines) and "Assign" (at once), while the slot is active and its job is Coordinating, Confirmed or InProgress.

A slot a staff member is assigned to takes nobody new (`SlotAssignmentService.SlotHeld`): the Staffing view says "Filled by" and names its staff, each with a Change link to the assignment's dialog, where the assignment is canceled first.

### Answering

The staff member answers their own request in the assignment's dialog (`SlotAssignmentDialog`, token `assignment`), reached from the notice's link in the bell, from a row of "My assignments" and from a time of "My schedule" ([Staff pages](coordination-and-homes.md#staff-pages)).

- The steps are Accept, Decline (with a reason, asked through the step's comment prompt) and, until the coordinator picks, Take back my yes (Accepted to Declined, with a reason).
- For its own staff member, the dialog opens on the request in words (where it stands and what to do next, `SlotAssignmentEditorOptions.Status` and `StatusHint`) over what the job contains: the job, its customer and place, the service, the slot and the qualifications it asks for, and the times. It names no other staff member.
- Only the staff member asked answers. The gate asks for the staff member's own `RespondToAssignment`, and a condition applies the owner rule (the step's effective user is the staff record's user), so neither a coordinator nor another staff member answers, through the service or past it. A platform owner impersonating the staff member answers as them, audited.

### Questions

While asked, the staff member may ask the coordinators before answering ([ADR 0037](../adr/0037-request-questions-are-workflow-steps.md)).

- "Write a question" (Requested to StaffQuestion, "Question to the coordinator") and "Write another question" (StaffQuestion to StaffQuestion) need `RespondToAssignment` on their own request.
- A coordinator answers with "Answer the staff member" (StaffQuestion back to Requested, `PerformCoordination`, never on their own request: `NotOwnQuestionReason`), which hands the request back to the staff member; the coordinator has nothing more to click.
- A question that needs no words is settled with "Mark as handled" (`assignment-question-handled`, StaffQuestion back to Requested, the answer's permission and conditions, no prompt and no notice; [ADR 0038](../adr/0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md)). Both the answer and "Mark as handled" mark every coordinator's unread notices of the question read ([Notifications](notifications-and-comments.md#notifications), "Settled for everyone").
- Each question and answer step asks for its words in a required prompt, written into the staff thread in the step's transaction.
- The staff member accepts or declines from StaffQuestion as from Requested (`assignment-accept-question`, `assignment-decline-question`: the same conditions, claims and notices; a first-to-accept round's yes takes a slot at once). The coordinator withdraws it (`assignment-cancel-question`).
- The system closes a questioned request as Not selected as it closes a request (`assignment-close-question`: the slot group filled by a pick or a first-to-accept round, or the job clock completing the job). A closed request leaves the staff member's open requests ("To answer", "Questions sent", "My work"); only "Show closed" under All open lists it, as Not selected. A slot's cancellation and a round's close cancel it.
- StaffQuestion is an open request wherever Requested is (`SlotAssignmentWorkflowAttribute.IsAsked`, `IsLive`, `NotSelectedStepFor`): a warning in the availability, an unanswered request in the counts, the coordination workspace and the request round, and one of the staff member's requests on "My work".
- The dialog's footer offers the staff member exactly Decline, Write a question (Write another question while one waits) and Accept; it offers the coordinator Withdraw request, then Mark as handled and Answer the staff member while a question waits.

### Picking

- A coordinator picks a staff member who said yes (Assign, Accepted to Assigned) or assigns one straight away. Either way the slot's other live requests (Requested, StaffQuestion and Accepted) close as Not selected, each by its own system-driven step with its history and its notice.
- A slot has at most one Assigned assignment and takes no new request or yes while it has one. To replace its staff member, the coordinator cancels the assignment first.
- A staff member has at most one live assignment on a slot. One who declined, was not selected or was canceled is asked again by a new assignment.
- Both rules are judged in the step under the tenant's job lock and held by filtered unique indexes. Two coordinators assigning at once are put in one order, and the second is refused with the reason.

### Canceling

- A coordinator withdraws a request, closes a yes or cancels an assignment (confirmed).
- A slot's Cancel cancels its live assignments, each by its own cancel step with its history (correlated with the slot's) and a notice to its staff member; a Declined or Not selected one stays. A job's cancellation reaches them through its slots the same way.
- Once the job is no longer staffed (completed or canceled), no answer and no cancel is taken: a no, taking back a yes and every cancel need the job staffed, so the Assigned staff member of a completed job is never rewritten.
- As the job clock completes a job, it closes every request and yes nobody picked as Not selected, each by its own system step with its history (the tenant's system account as actor) and no notice, so nothing is left open.

### Taking off for absence

While an absence of the staff member blocks a remaining time of the job, a coordinator takes them off with "Take off for absence", from any live state into Canceled, with an optional word and a notice naming the absence. The slot is then open until a new assignment holds it ([Absence impact](schedules-and-absence.md#absence-impact)).

### Answer-by time and expiry

A request carries the instant it must be answered by (`RespondBy`), set when it is sent: from the tenant setting "Time to answer a request" ([Tenant settings](custom-fields-and-settings.md#tenant-settings); 48 hours by default, none when empty) or from the time the coordinator chooses in the Staffing view ("Answer within"). A direct assignment carries none ([ADR 0041](../adr/0041-a-request-expires-by-a-system-step.md)).

- Once the time passes, the job clock takes a request still Requested or with a question into **Expired** by the system-driven `assignment-expire` or `assignment-expire-question` (no user takes them and no footer offers them), as the tenant's system account. It tells the job's coordinator and whoever sent the request (`assignment-expired`).
- The staff member is not told, and the expired request leaves their lists.
- Expired is not live: the staff member is a candidate again, marked "No answer before", and a slot left with no request is open again in the Priorities, its reasons saying how many requests expired.
- The staff member's request (its dialog's status, "My work" and "My assignments") and the coordinator's dialog show the deadline in the tenant's local time ("Answer by Tue 24 Nov 2026, 14:00", `ResponseWindow.AnswerByText` through `TenantClock`).

### What it books

An Assigned or Accepted assignment holds its slot (coverage) and books the staff member's time in the availability; an open request is a warning there ([Schedules and availability](schedules-and-absence.md#schedules-and-availability)). A request, an assignment, a yes and a pick need the staff member free for the job's remaining occurrences: not absent, not out of service and not booked on another job at the same time, including a booking on another slot of the same job ([Staffing dialog](#staffing-dialog)). Qualifications are not judged: a staff member without a required one is staffed with a warning.

### The staff thread

The assignment's one comment thread is the conversation between the coordinators and that staff member (`CommentAudience.Staff`; [Comment threads](notifications-and-comments.md#comment-threads), "Staff conversations").

- A no's reason is written there by the step. The staff member (on their own assignment) and the coordinators read and post in it.
- While the request is open (Requested or StaffQuestion) it takes no free comment from anyone (`StaffConversationService.WrittenThroughSteps`, shown in the composer's place): questions and answers are the steps' prompts. The composer comes back once the staff member said yes.
- It closes for the staff member once their request or assignment is no longer live (declined, not selected, canceled), and for everyone once the job is completed or canceled. A coordinator may still post while the job is staffed after the staff member's request closed. A closed thread is still read, without a composer, and a post is refused with the reason (`StaffConversationService.ThreadClosedForStaff`, `ThreadClosedWithJob`).
- Whose turn it is is kept on the assignment: each new comment tells the other side, the job's Staff tab lists the threads that wait for the coordinators, and "My assignments" marks a new reply.

### Notices

See [Notifications](notifications-and-comments.md#notifications) for delivery.

- To the staff member: requested, assigned, not selected, canceled, and taken off for absence (`assignment-taken-off`, naming the absence).
- To the job's coordinator and whoever made the assignment: accepted, declined and taken back.
- A question ("New question about a request", `assignment-question-from-staff`) goes to the job's coordinator and whoever asked, or to every holder of `PerformCoordination` when neither still coordinates. An answer ("New answer about your request", `assignment-answer-to-staff`) goes to the staff member. Each carries the typed words.
- Named coordinators receive each notice only while they hold `ViewCoordination` as it is delivered (`OutgoingMessage.NamedRecipientsHold`), so someone who no longer coordinates reads nothing of another staff member's answer.
- A decline's reason is never in a notice (the coordinator reads it in the thread), and no notice names another staff member.

### A staff member sees only their own

A reader of jobs (`JobService.Readers`) reads every assignment. A staff member with `ViewOwnAssignments` reads their own only; another staff member's assignment reads as missing in its load, history and thread. A staff member's dialog holds no other assignment of the slot, even in memory, and their history lists no notice the steps sent (whose counts would reveal others). No list of a slot's staff members is theirs to read. The assignment's dialog shows the job's custom fields the reader may read ([Job fields](custom-fields-and-settings.md#job-fields)).

### Loads never throw

Every dialog's one load (`Load{Entity}UpdaterAsync`) reads through `DeadlockRetry` (`Staffora.Services/Diagnostics/`). A load chosen as a deadlock victim (SQL Server error 1205) by a step writing the rows it reads is read once more; any other failure, or a second deadlock, is logged and answered as the service's read failure, never thrown at the dialog. The database reads committed rows by row versioning (`READ_COMMITTED_SNAPSHOT`, [Isolation level](../database.md#isolation-level)), so a plain read does not wait on a writer; the retry covers the deadlocks that remain possible.

Verified by: `SlotAssignmentWorkflowTests`, `AssignmentLifecycleTests`, `AssignmentConcurrencyTests`, `AssignmentQuestionTests`, `AssignmentDialogsTests`, `AssignmentReviewTests`, `RespondByDeadlineTests`, `tests/e2e/job-staffed.spec.ts`, `tests/e2e/assignments.spec.ts` and the accessibility sweep. The availability's bookings and requests are verified with [Schedules and availability](schedules-and-absence.md#schedules-and-availability).

## Slot groups

Slots that are alike form one row wherever staff are planned, and staffing a group fills any open slot of it ([ADR 0027](../adr/0027-generic-resource-staffing.md)).

- **Adding by count.** The slots view's add opens the slot dialog in its Create state, which asks "How many" (1 to 20, `JobSlot.Count`, `[NotMapped]`, shown only in Create) beside the name, description and qualifications. Its one step, Add (`slot-add`), makes that many rows alike in one call and one transaction (`JobSlotService.CreateJobSlotAsync` → `WorkflowService.CreateManyAndTransitionAsync`). Each row is judged, stepped and given its history as a create alone would be, the history rows sharing one correlation id; a refused row refuses them all. A slot made from a service opens the add with the service's default number of people as its count.
- **Copying.** "Copy slot" on a slot's Staff view makes a new slot of the same job with its name, description and qualifications (`JobSlotDialog.OpenCopyAsync`), which joins its group.
- **The rule** (`SlotGroups`, `Staffora.Core/Jobs/SlotGroups.cs`): active slots of one job (the job stands for their times) alike in trimmed, case-insensitive name, service and qualifications at their levels form a group. Members are ordered by `Created` then `Id`; the first is the representative. A canceled slot is a row of its own. Held is one rule for every surface: `min(Count, Assigned + Accepted)`.
- **Where it shows.**
  - The job dialog's slots view (`JobService.QueryJobSlotsAsync`, grouped in memory over one job's slots: `JobSlotRow.SlotCount`, `Held`, `HeldLabel` "2 of 3" in the coverage column, and the holders' names).
  - The slot dialog's Staffing view ("Slots: 2 of 3 held"; the candidates exclude anyone live on any slot of the group; `SlotAssignmentService.GroupFull` once every slot has a holder) and its Staff view (the group's assignments).
  - The priority list and the week strip (`CoordinationService`: one entry per group, `SlotFacts.Count`/`Holders`, "2 of 3: Ada Example, Bo Example").
  - Job coverage (`JobCoverageRules.OfGroups`).
  - A group of one reads exactly as a single slot. The timeline stays per slot.
- **Staffing a group.** A request or an assignment names the representative. The step into Assigned takes the row's own slot when it is open, otherwise the first open one (`TakeOpenSlot`), so three assignments to a group of three land on three slots. Once the group is full, its other live requests close as Not selected (`SlotGroupFollowUp`).
- **Settling groups.** A slot canceled or saved can also leave a group of its job full (its open slot canceled while a round asks for it); the slot's step settles every such group in its own transaction the same way (`SlotStepsSettleTheirGroups`).
- **Privacy.** A staff member's own assignment never holds another staff member's request, including its round's, and its history names no actor of a step the system took (`SlotAssignmentService.SystemStepActor`), since a first-to-accept yes closes the others' requests.

Verified by: `SlotGroupsTests`, `SlotGroupTests`, `JobSlotGroupViewTests`, `SlotAssignmentWorkflowTests`.

## Request rounds

A coordinator asks several staff members at once for a slot group ([ADR 0027](../adr/0027-generic-resource-staffing.md)).

- **Sending.** A Staffing view (the job dialog's or the slot dialog's) offers "Request several" (`PerformCoordination`, while the group has an open slot), in place:
  - The group's candidates in rank order (`StaffingService.ReadRoundCandidatesAsync`) turn into check boxes; a blocked candidate is shown, not offered.
  - A bar that stays at the bottom of the view says how many are chosen, with "First to accept gets a slot" (on by default), Cancel and Send requests. The view says how many slots are held.
  - Send (`RequestRoundService.SendRoundAsync`, 1 to 30 staff members) creates the round and one request per staff member in one transaction (`WorkflowService.RunBatchAsync`). A request the workflow refuses refuses the round ("{Name} cannot be asked: {reason} Nothing was sent.").
- **Answering.** Each staff member gets `AssignmentRequestedInRound` with the round's rule and answers in the assignment's dialog, which shows the rule.
  - With first to accept on, a yes takes an open slot at once (`assignment-claim` in the yes's transaction, telling the staff member and the coordinators, `AssignmentTaken`). Once the group is full, the round's other requests close as Not selected and the round is Filled (`RoundFilled` to its sender).
  - With it off, yeses wait for the coordinator's pick.
  - A yes arriving after the group filled is answered "Every slot was taken before your yes arrived, so you were not selected." Two yeses for the last slot are put in one order by the tenant's job lock.
- **Closing.** The round's dialog (`RequestRoundDialog`, token `round`; Info: job, slot, "2 of 3", rule, sender, counts; Requests view) offers Close round, which cancels its unanswered requests with notices and leaves yeses.
- **Expiry** ([ADR 0041](../adr/0041-a-request-expires-by-a-system-step.md)). A round carries one answer-by time (`RespondBy`, chosen in the Request several bar's "Answer within" or the tenant's default), which each of its requests carries too. Once it passes while a request is still unanswered, the job clock takes the round from Open to **Expired** (`round-expire`, system-driven). Its cascade expires every unanswered request together, each by its own step and history under one correlation, leaving answers alone (a yes still waits for the pick), and sends the coordinator one `round-expired` notice naming who did not answer. The round counts expired requests among its closed ones.
- **Who.** Rounds are read with `ViewCoordination` (the slot dialog's Request rounds view, `RequestRoundService.QueryGroupRoundsAsync`). A staff member reads only their own request.

Verified by: `RequestRoundTests`, `RequestRoundConcurrencyTests`, `tests/e2e/request-round.spec.ts` and the staffing sweep.

## Staffing dialog

The coordinator opens a slot, sees a ranked list of candidates with their qualifications, schedule, absence and other bookings, and requests or assigns ([PRODUCT.md](../../PRODUCT.md), Core flow 4). Every staffing surface uses the one availability rule described here ([ADR 0019](../adr/0019-coordination-workspace.md)).

### Where

The slot dialog (`JobSlotDialog`, token `slot`) offers a Staffing view (`staffing`, after Info, before Staff) to a coordinator (`ViewCoordination`, which its service checks too) for a stored slot. The same component, `SlotStaffingPanel`, shows a slot's staffing in the job dialog's Staffing view. It is read in one call (`JobSlotService.ReadSlotStaffingAsync`: `ViewCoordination`; the staffing context with whether the reader may staff the slot and why not; the group's candidates in rank order; and how many slots are open).

- **The context** (`StaffingService.SummaryOfSlotAsync`, read with every load): the slot's job, period, weekly times, occurrences left and required qualifications (an archived one marked "(archived)"), and, when the reader may not staff it now, why (no `PerformCoordination`, the slot canceled or its job not being staffed, a staff member assigned).
- **The strip.** The view opens with the slot's facts in one strip (times, the required qualifications as chips with the preferred ones marked, and how many of the group are held, `SlotStaffingSummary.StaffedLabel`) and the staff holding it (`Holders`).
- **The slot's people** are its Staff view (each request and assignment with its state, opening the assignment's dialog), and its **history** is the header's (the slot's) and each assignment's own. A candidate's earlier conversation on the slot is read in the assignment's own dialog.
- **Candidate rows.** Each candidate is one dense row: a quiet rank, the name, the slot's qualifications they hold (a check) and those missing ("Missing: …"), what became of an earlier request on the slot, the jobs in the period as a number, and a verdict badge (Free, Warning or Blocked, with its text and a token tone).
  - A badge with findings is a disclosure ("1 clash", "2 notes") that unfolds each block and warning as a short line: its reason, the booking's job and slot, and its dates (`StaffingCandidateRow.BlockLines`, `WarningLines`).
  - The rows keep the rank order. A blocked row is muted, its Request and Assign disabled with the verdict as their tooltip.
  - A filled slot hides its candidates behind "Show candidates". A search by name shows from eight candidates on, and the first twelve show until "Show N more".
- **Steps.** While the reader may staff the slot and nothing blocks the candidate, the row offers **Request** and **Assign**, which make the assignment through the assignment's first steps (`SlotAssignmentService.RequestStaffAsync`, `AssignStaffAsync`), and the view's toolbar offers **Request several** ([Request rounds](#request-rounds)); otherwise the view says why. A refusal stays in the view with its reason. Once a step is made the dialog reloads (its edits settled first), so its footer, Staff section, views and candidates follow.
- **Structure.** `SlotStaffingPanel` is a component of its own, not the body (`DialogArchitectureScanTests`), and is listed with the shared pieces of `StateFieldScanTests`. Its steps make records through their workflows' first steps, which take no transition key, so `LifecycleCallScanTests` holds nothing against it.
- **Opening on the view.** The slot dialog opens on Info (its first view) by default. An opener may name the view a state-entity dialog opens on (`StateEntityDialogOptions.View`, shown when the reader is offered it, otherwise the first view). The coordination workspace opens a slot's dialog on its Staffing view (`JobSlotDialog.OpenStaffingAsync`); a page that does so names it (`[DialogViewOnPage]`), so a reload reopens the dialog there too ([URL state](lists-and-dialogs.md#url-state)).

### Who is a candidate

The tenant's staff members in service (`StaffServiceRules`) without an open request or assignment on the slot (those are its people). One asked before whose request closed (declined, not selected, canceled, expired) is a candidate again, marked "Declined before" and the like; an expired request is marked "No answer before", counts as not answered and changes nothing in the ranking.

### The times judged

`StaffingRules.RemainingOccurrences`: the job's occurrences over its period, from the later of its first day and the tenant's today, each turned into instants in the tenant's zone through `WallClock` ([ADR 0020](../adr/0020-dates-and-times.md)), keeping those not yet over at the injected clock's now. A time already worked judges nobody, so re-staffing a running job is judged on what is left of its period. A time a clock change leaves without length counts as none.

### The verdict

`StaffingRules.Judge` works over one staff member's answer of `IStaffAvailability`, read for every candidate at once over the times' days.

- **Blocks:** each block of the availability that meets a time: absence (an approved one, or a reported sick leave, "On sick leave (reported)"), out of service, or a booking (Assigned or Accepted) on another job or another slot of the same job at the same time.
- **Warnings:** an open request on another job at the same time, and each time outside the staff member's work pattern (`StaffAvailability.IsWithinWorkingTime`) where nothing blocks it. The assignment's own booking is left out by its source.
- **The roll-up:** a block on any one occurrence blocks the staff member for the slot, since an assignment holds the whole period; a staff member absent in week six is not available for the period. The dialog says on how many times and when ("Blocked on 1 of 40 occurrences", and a line "On sick leave (reported) 2027-03-08"), grouped by reason and source.
- Warnings never refuse.
- An absence shows only its kind and whether it is reported or approved. An absence carries no reason, and a rejection's reason is read by approvers alone ([Absence](schedules-and-absence.md#absence)).

### Qualifications

`StaffingRules.Match` reads `QualificationService.ReadQualificationsOfStaffMembersAsync`, which answers every qualification a staff member holds, archived ones included, against every one the slot requires, archived ones included. An archived qualification a slot still names counts as required, and a staff member who holds it meets it. A staff member without a required one is staffed with a warning ("1 of 2 required (missing: Physics)"), never refused.

### Held jobs in the period

The jobs being staffed (Coordinating, Confirmed, InProgress) on whose active slots the staff member's assignment is Assigned or Accepted and whose period overlaps the slot's job's. A yes counts, as coverage counts it as held, and the job's own other slots count too.

### The ranking

`StaffingRules.Rank` (`MatchAtLevels`):

1. Staff members holding every Required qualification first.
2. Then by the number of Required qualifications held.
3. Then by the number of Preferred qualifications held.
4. Then the fewest held jobs in the period.
5. Then by name, then by record, so the order is stable.

Blocks and warnings do not reorder the ranking; they are shown beside it, and the availability filter keeps the free or the blocked ones. The search, the filter, the sort and the page are applied after the ranking, so a staff member's rank is the same whatever the list shows.

### One page, a fixed number of queries

`StaffingService.QuerySlotCandidatesAsync` (reached from the slot dialog through `JobSlotService.Staffing`) reads: the slot with its job's period and times, its required qualifications and its assignments (one query each, split); the staff members in service; the availability's own queries (at most five); the qualifications; the candidates' live bookings in the period with their jobs and slots; and the tenant's zone through the lookup cache.

This is an exception to the list surface's rule of paging in SQL ([List view](lists-and-dialogs.md#list-view)): the ranking needs every candidate's availability, so the page is cut in memory after it, over the tenant's staff members in service, in that fixed number of queries (the same count for 5 staff members and for 50).

### The workflow judges the same rule

The assignment's Request, Assign, Accept and Pick need the staff member free for the job's remaining occurrences (`SlotAssignmentWorkflowAttribute.StaffFreeReason`), by every path, the service's and `WorkflowService`'s alike. They are refused with the times and dates that block: "The staff member is not free for 1 of the job's 40 remaining occurrences: On sick leave (reported): 1 occurrence, 2027-03-08." A no, taking back a yes and a cancel never ask.

- The assignment's guard (`SlotAssignmentGuard`, `ISlotAssignmentGuard`, an `IStepPreparation`) reads the job's period and times through the slot and the staff member's availability on the step's own context, inside its transaction under the tenant's job lock, by the same rule (`StaffAvailabilityService.ComputeAsync`, `StaffingRules.Judge` with the assignment's own booking left out).
- For a request or an assignment, which create the row, the guard runs through the engine's `IStepPreparation.PrepareNewAsync`, once the new row's references are read; a create that names no tenant yet (its save gives it the runtime's) is judged on its slot's.
- A read that fails (the tenant's zone unreadable) leaves no verdict: the four steps are refused as steps that cannot be checked, while a no, a cancel and the job clock's closing of a request, which ask nothing, go ahead.
- The footer reads the verdict too (the staff member's Accept and the coordinator's Assign greyed with the reason), without the lock; the step judges again under it.

### Concurrency

- Every absence step takes the tenant's job lock, as every assignment step does, so an absence reported, registered or approved and an assignment of the same staff member are judged in one order. An assignment judged after the absence committed is refused; an absence that commits after the assignment finds it made, and the job then needs re-staffing (the coordination workspace's at-risk list).
- The job lock is the tenant's, so two coordinators assigning one staff member to two overlapping jobs at once are put in one order too, and the second is refused for the first's booking. No race window is left.
- **A staffed job's own edit.** Every job step that stores edits (the save, and Confirm with the dialog's edits) judges, once the edits are saved inside its transaction and under the same job lock, each staff member in service who is assigned to the job or said yes, over the times the edit adds, moves or lengthens (those still to come that no stored time of their day holds whole; a shortened time adds nothing). The same rule applies, with the staff member's own bookings on the job left out.
  - The step refuses with each such staff member's blocks: "Ada Example is not free for 8 of the 8 occurrences the change adds: Assigned to work a job: 8 occurrences, 2027-02-02 to 2027-03-23."
  - The check is `JobWorkflowAttribute.HoldersFreeReason` over `IJobGuard.HolderClashes`, read by the job guard through the engine's `IStepPreparation.PrepareEditedAsync`. It runs after a step's edits are saved, since preparations before them see the job as it was.
- A time the job already had is not judged again: a holder absent on it is the workspace's at-risk case, so an at-risk job can still be edited. The dialog judges its edits in memory against the stored row's answers, so it does not grey the Save for a clash; the server's refusal names it.
- The schedule lock is not taken for this: the assignment steps would need it before the job lock, and a job's cancellation, which already holds the job lock as it cascades into its assignments' steps, could not take it in the fixed order ([Application locks](../database.md#application-locks)).
- A work pattern's steps take no job lock: time outside the pattern is a warning, never a refusal.

### Asked and answer by

Under the holders, the view lists the group's open requests ("Asked": Requested or with a question, soonest deadline first, `SlotStaffingSummary.Asked`) with each one's answer-by time in the tenant's local time ("Answer by Wed 25 Nov 2026, 09:00", or "No deadline to answer") and an Open link to the request's dialog. Beside "Request several", "Answer within" (a drop-down named by `FieldLabel`, the tenant's default chosen, `SlotStaffingRead.RespondWithin`, its choices `ResponseWindow.Choices`) sets the deadline of the next request and of the next round sent from the view.

### Who

Only a coordinator: `ViewCoordination` reads the candidates and the context, `PerformCoordination` requests and assigns. A staff member, who holds no permission of the Coordination area, reaches neither the view nor its service, and another tenant's slot reads as not found.

Verified by: `StaffingRulesTests`, `SlotAssignmentWorkflowTests`, `StaffingTests`, `StaffingDialogsTests`, `AssignmentConcurrencyTests`, `tests/e2e/staffing.spec.ts` and the accessibility sweep.
