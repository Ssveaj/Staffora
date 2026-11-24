# Known issues

Places where the code deviates from the documented rules or design. The rules in [.github/copilot-instructions.md](../.github/copilot-instructions.md) and the documents under `docs/` describe the intended behaviour; each entry here is a gap to close in the code.

Documents reference an entry by its ID. When an entry is fixed, its entry and every reference to its ID are removed. An ID is never reused: a new entry takes the next number after the highest ever given (82 so far, written without its prefix here because every ID in the documents must name an open entry).

## Security and data integrity

**KI-70 The one-live index does not cover a request with a question.**
The filtered unique index `IX_SlotAssignments_OneLivePerStaff` (one live assignment per staff member and slot) names Requested, Accepted and Assigned. StaffQuestion ([ADR 0037](adr/0037-request-questions-are-workflow-steps.md)) is live too.

- The workflow's one-live rule (`OneLive`, evaluated under the tenant's job lock by every assignment step) counts StaffQuestion, so the application never creates a second live row beside one with a question.
- The index is the rule's backstop against a write that bypasses the workflow, and it does not hold for StaffQuestion until its filter is widened.
- Closing it changes the model and needs a migration.

Evidence: `Staffora.Data/Configurations/Jobs/SlotAssignmentConfiguration.cs`, `Staffora.Core/Workflow/WorkflowAttributes/SlotAssignmentWorkflowAttribute.cs` (`IsLive`, `OneLive`).

## Reliability

**KI-69 Notices and tenant changes reach live pages only on the same app instance.**
Three in-process mechanisms assume a single instance of the app:

- **Notices.** The bell's live push is an in-process bus (`NoticeBus`, a singleton). The in-app sender publishes to the recipients' circuits on the instance that delivered the notice. A recipient whose circuit runs on another instance sees the notice only when their bell next reads its count (a new page or a reload).
- **Tenant changes.** The tenant change channel (`TenantChangeBus`) is in-process the same way. A step committed on one instance reloads the coordination workspace and the staff member's own pages open on that instance only; a page on another instance reads again only when it next loads (a dialog closed, a filter changed, a reload).
- **Job clock failures.** The job clock's memory of the jobs it could not move (`JobClock.FailureOf`) belongs to each instance, so the workspace names the clock's last failure only on the instance whose clock saw it. Every instance lists the job as behind, from its state and period alone.

The app runs as one instance. Running a second requires a backplane (a message broker, or SQL Server query notifications) behind the `Subscribe` and `Publish` of both buses. See also [Running more than one instance](ARCHITECTURE.md#running-more-than-one-instance).

Evidence: `Staffora.Services/Messaging/NoticeBus.cs`, `Staffora.Services/Coordination/TenantChangeBus.cs`, `Staffora.Services/Jobs/JobClock.cs`.

## Housekeeping

None open.
