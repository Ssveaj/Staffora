# ADR 0041: A request expires by a system step at its answer-by time

## Status

Accepted. Amends [ADR 0027](0027-generic-resource-staffing.md).

## Context

Without a deadline, a request waits for its staff member indefinitely: a coordinator who asked someone who never answers has to notice it and withdraw the request by hand, and a request round stays open until someone closes it. Every request needs a deadline after which it runs out by itself.

## Decision

- **The deadline is an instant on the request.** `SlotAssignment.RespondBy` and `RequestRound.RespondBy` (UTC, nullable) are set when the request or the round is sent, never afterwards. The value comes from the tenant setting `General.RespondWithin` (whole hours; 48 when the tenant has saved none; empty for no deadline) or from the time the coordinator chooses in the Staffing view ("Answer within": the offered hours, including the tenant's own, and "No deadline"). A round's requests carry the round's instant. The request step refuses an instant that has passed (`RespondByAheadReason`) and a round's request whose instant is not the round's (`RoundTimeReason`). An assignment made directly asks for no answer and carries no deadline (`AssignAsksNoAnswerReason`).
- **Expired is a state of its own, reached only by the system.** `SlotAssignmentState.Expired = 8` (category Canceled, not live) is entered from Requested (`assignment-expire`) and from StaffQuestion (`assignment-expire-question`). Both steps are system-driven, require no permission, and are judged by one condition: the answer-by time has passed (`RespondByPassedReason`, read from the step's `TimeProvider`). No user takes them and no footer offers them, as for every system-driven step. Nothing leaves Expired: the coordinator asks the staff member again with a new request.
- **A round expires as a whole.** `RequestRoundState.Expired = 4` (category Canceled) is entered from Open by the system step `round-expire`, due when its answer-by time has passed and at least one of its requests is still unanswered. The state cascades into its requests' Expired steps, leaving every answered request alone (Accepted, Assigned, Declined, Not selected, Canceled), so the round's unanswered requests expire in one transaction with one correlation, and acceptances waiting for a pick remain as a closed round leaves them. A round whose requests are all answered stays open.
- **The job clock runs it.** Each pass of `JobClock`, after it moves the tenant's jobs, expires the tenant's due rounds and then its due requests outside an open round, each in a transaction of its own under the job's step locks, as the tenant's system account (`SystemAccountSession`), judging the row again under the locks. A refusal or a failure is logged and the pass continues. A job that ends closes its open requests as Not selected first, so a request of a finished job never reads as expired.
- **The coordinator is told, the staff member is not.** A single request's expiry sends `assignment-expired` (for example "Ada Example did not answer for Office cleaning") to the job's coordinator and whoever sent the request. A round's expiry sends one `round-expired` naming the staff members who did not answer, and its requests send none of their own (`WorkflowContext.IsTakenWith` the round). The staff member gets no notice, and an expired request leaves their lists ("My work", "My assignments" with closed ones shown, and its status filter).
- **What follows reads it as not answered.** Expired is not live, so the staff member is a candidate again, ranked as before and marked "No answer before". The availability rule no longer warns of the request. A slot whose requests have all expired is open again in the Priorities' existing open-slot item ("No staff member is asked yet." and "1 request expired with no answer."). A round counts its expired requests among the closed ones.

## Rationale

A deadline fixed when the request is sent, and an expiry taken only by the system under the same locks as every other step, frees coordinators from tracking unanswered requests by hand while keeping every change in the workflow, its history and its notices.

## Consequences

- The schema has two enum values and two nullable columns with their filtered indexes, in the single `InitialCreate` ([ADR 0022](0022-data-model-conventions.md)).
- A request without a deadline never expires; a tenant that wants no deadline saves the setting empty.
- `IX_SlotAssignments_OneLivePerStaff` still names only Requested, Accepted and Assigned ([known issue KI-70](../known-issues.md)). Expired is not live, so it adds nothing to that gap.

## Related Decisions

- Amends [ADR 0027](0027-generic-resource-staffing.md), which had left deadlines out.
- [ADR 0037](0037-request-questions-are-workflow-steps.md): requests with a question also expire.
- [ADR 0026](0026-coordination-workspace-hour-timeline.md): the availability rule and the Priorities.
