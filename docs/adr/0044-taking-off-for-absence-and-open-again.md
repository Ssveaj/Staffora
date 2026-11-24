# ADR 0044: Taking a staff member off for absence is a step, and "open again" is read from its history

## Status

Accepted. Amends [ADR 0026](0026-coordination-workspace-hour-timeline.md).

## Context

When someone calls in sick early in the morning, the coordinator must see the affected jobs, take the person off and request or assign someone else in a few clicks. Showing a sick report only while it is undecided leaves the coordinator to find each affected job by hand; an ordinary cancel says nothing about why the assignment ended; and a slot emptied that way looks like a slot that was never staffed. The Priorities list what the workflow leaves waiting on the coordinator ([ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md)), so an absence must stay there until the workflow has settled every assignment it affects.

## Decision

- **What an absence affects is one read.** `ResourceAbsenceService.ReadAbsenceImpactAsync` answers, for one absence, its staff member's live requests and assignments on jobs in staffing whose times the absence overlaps and that have not ended, by the one rule of a job's occurrences ([ADR 0020](0020-dates-and-times.md)) met by the absence's instants as the availability meets them ([ADR 0026](0026-coordination-workspace-hour-timeline.md)). It requires `ViewCoordination`. An absence waiting for approval is read too, as what it would block; only one that blocks (`AbsenceRules.Blocks`: approved, or a sick leave undecided) waits in the Priorities or lets a coordinator take anyone off. The absence's dialog shows the result as a view, Affected jobs, whose rows open the assignment's dialog or the job's Staffing view; rows act through dialogs only ([ADR 0009](0009-state-entity-dialog-only-and-workflow-governed-attributes.md)).
- **Taking off is a step of the assignment, not a new state.** "Take off for absence" leads from every live state (Requested, StaffQuestion, Accepted, Assigned) into the existing Canceled. It is a coordinator's step (`PerformCoordination`) with four durable keys (`assignment-take-off`, `-accepted`, `-request`, `-question`). It is offered only while an absence of the staff member blocks a remaining time of the job (`AwayReason`), judged by the assignment's guard from the availability (`ISlotAssignmentGuard.BlockingAbsenceOf`, which also judges an Assigned assignment), so it is a different step from Cancel and its key records why the assignment ended. Its optional prompt carries a message for the staff member into the staff thread, and its notice (`assignment-taken-off`) names the absence. The history shows the step with that notice, which is where the absence is referenced, since a history row has no note of its own.
- **A cascade never takes it.** A cascade into a state needs exactly one transition from each state (registration refuses two), and a slot's or a round's cancellation must keep canceling through the cancel steps. A transition can declare this: `Transition.NeverCascaded` (on `ITransition` too) leaves it out of a cascade's registration check, of its plan in the engine and of the access gate's plan in `WorkflowService`. Only the four take-off steps carry it.
- **"Open again" is derived, not stored.** A slot group is open again while it has an open slot and at least one take-off step on one of its assignments that no assignment has filled since. Walking the group's take-off instants and the instants its current holders entered Assigned in time order, each take-off opens one and each later holder closes one, never more than the group's open slots (`AbsenceImpactRules.OpenAgain`, read by `OpenAgainReader` from `StateHistoryEntries` by step key). A request or an acceptance does not hold the slot; the first assignment after the take-off clears it. The Priorities' slot items, the job's Staffing view and the slot's staffing panel show it. Reading history to derive this is a service's own read, not the history view, which remains an owner's ([ADR 0036](0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md)).
- **The Priorities item is the absence, until settled.** "Absence to settle" (pill "Absent") is one item per blocking absence while an affected assignment is still live, ordered by the one rule at its first affected time, and opens the absence on its Affected jobs view. Marking a sick report Handled does not clear it; settling the assignments does.

## Rationale

A distinct step records why an assignment ended without adding a state, and keeps the change inside the workflow with its notice and history. Deriving "open again" from the history needs no new column, index or state. Keeping the absence in the Priorities until its assignments are settled follows the rule that the Priorities list what the workflow leaves with the coordinator.

## Consequences

- No column, index or state is added, so the model and `InitialCreate` are unchanged. The open-again mark costs two history reads per snapshot, the second only for slot groups with a take-off.
- A take-off from Assigned notifies a portal's customer of the staff members, as a cancel does.
- An assignment taken off for one absence shows as settled in the impact of any absence that overlaps the same times.
- A workflow that needs a step beside a cascade's own into the same state marks it `NeverCascaded`; nothing else changes for existing workflows.

## Related Decisions

- Amends [ADR 0026](0026-coordination-workspace-hour-timeline.md): the sick-report item in the Priorities becomes "Absence to settle".
- [ADR 0016](0016-deletion-cascades-through-child-workflows.md): cascades through child workflows.
- [ADR 0035](0035-absence-questions-are-workflow-steps.md): the absence workflow.
- [ADR 0036](0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md): the history view is for owners.
