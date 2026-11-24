# ADR 0037: Request questions are workflow steps on the assignment

## Status

Accepted. Amends [ADR 0010](0010-comment-threads-per-audience.md). Amended by [ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md).

## Context

A staff member asked to work a job often needs information before answering: where to park, what to bring, whether the times can move. A free comment in the assignment's thread leaves the request itself showing nothing, so the coordinator sees an open request and a conversation and must read both to know that a staff member is waiting on them. Staff need their own view of what the job contains, where they can ask a question, ask another, and accept or decline. The coordinator should not have to do anything further after answering: the decision to accept or decline stays with the staff member, and a request whose slot group was filled by someone else closes for this staff member.

## Decision

### Question state

`SlotAssignmentState` has StaffQuestion ("Question to the coordinator", 7), appended to the enum. It is Requested with the coordinators' turn attached: the request is open and live and books nobody, and every reader that counts, lists, closes or warns about open requests reads it as Requested (`SlotAssignmentWorkflowAttribute.IsAsked`, `IsLive`; the availability's warning, the unanswered counts, the request round's counts, the staff member's own requests). It is read-only for everyone, as every assignment state is.

### Steps

- **The staff member's side** (`RespondToAssignment`, on their own request only): "Write a question" (Requested to StaffQuestion) and "Write another question" (StaffQuestion to StaffQuestion).
- **The coordinators' side** (`PerformCoordination`, never on their own request):
  - "Answer the staff member" (StaffQuestion to Requested), after which the request is the staff member's again and the coordinator has nothing more to do;
  - "Mark as handled" (`assignment-question-handled`, StaffQuestion to Requested, no prompt, no notice, with the answer's permission and conditions), which settles a question without words ([ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md)).
- **Accept and Decline** are offered from StaffQuestion as from Requested, so the staff member never waits on the coordinator, with the same conditions, claims and notices (a first-to-accept round's acceptance takes a slot at once from either state), each its own step (`assignment-accept-question`, `assignment-decline-question`).
- **Closing a request with a question.** The coordinator withdraws it as one without a question (`assignment-cancel-question`). The system closes it as Not selected when the slot group fills, by a pick or by a first-to-accept round, or when the job clock completes the job (`assignment-close-question`); a closed request leaves the staff member's open requests ("To answer", "Questions sent", "My work"; only the list's Show closed shows it). A slot's cancellation and a round's close cancel it through its own cancel step. A request with a question also expires at its answer-by time ([ADR 0041](0041-a-request-expires-by-a-system-step.md)).
- Every question step requires the job to be in staffing.
- While a question waits, the dialog's footer offers the coordinator Withdraw request, Mark as handled and Answer the staff member.

### Every word through a step

Each question step asks for its words in a required comment prompt ("Write a question to the coordinator": "The coordinator sees your question on the request."), written into the assignment's staff thread in the step's own transaction as the step's note. The answer's prompt states that the request goes back to the staff member.

### No free comment while the request is open

While the assignment is Requested or StaffQuestion, its staff thread takes no free comment from anyone (`StaffConversationService.WrittenThroughSteps`, with `ShowsClosedReason` off, so nothing is shown where the composer would be). Once the staff member has accepted, the conversation works as described in [ADR 0010](0010-comment-threads-per-audience.md): both sides write in it, with its whose-turn timestamps and notices. A staff member's message in that conversation can be marked as handled by a coordinator ([ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md)).

### The words in the notice

- A question notifies the job's coordinator and whoever sent the request, while they still coordinate, otherwise every holder of `PerformCoordination` ("New question about a request", `assignment-question-from-staff`).
- An answer notifies the staff member ("New answer about your request", `assignment-answer-to-staff`) and asks them to open the request and accept or decline it.
- Each notice carries the typed words appended (`NotifyAction`'s `withComment`).

### The staff member's own view

The assignment's dialog opens, for its own staff member, on their request in words (where it stands and what to do next) over what the job contains: the job, its customer and place, the service, the slot and the qualifications it asks for, and the times; the conversation in the side column; and in the footer Decline, Write a question (or Write another question while one waits) and Accept. It names no other staff member ([PRODUCT.md](../../PRODUCT.md)). The coordinator reads the same dialog with their own steps. The view is chosen by what the service reads with every load (`SlotAssignmentEditorOptions.ForStaff`, `Status`, `StatusHint`), never by the body reading the state.

### Questions in the Priorities

The coordination workspace's Priorities list the requests in StaffQuestion among the questions from staff, beside the absences in StaffQuestion and the conversations that wait for a reply.

## Rationale

A question as a state makes a waiting question visible in the request itself and in the Priorities, without the coordinator having to read the conversation. Writing every word through a step keeps the exchange inside the workflow, with its access rules, history and notices. Keeping Accept and Decline available during a question leaves the decision with the staff member.

## Consequences

- For an open request, this replaces [ADR 0010](0010-comment-threads-per-audience.md)'s rule that a comment saves at once through the thread service: while the request is open its thread is written only by steps.
- Existing assignments need no migration: the state is stored as its number and no check constraint names it.
- The filtered unique index of one live assignment per staff member and slot (`IX_SlotAssignments_OneLivePerStaff`) still names only Requested, Accepted and Assigned. The workflow's one-live rule (`OneLive`, judged under the job lock) counts StaffQuestion as live, so no second live assignment is made beside one with a question; the index, the rule's backstop, does not see it until its filter is widened by a migration (`docs/known-issues.md`, KI-70).
- A request's questions do not move the conversation's whose-turn timestamps: a request with a question is shown by its state, in the Priorities and on the staff member's own pages ("Questions sent").

## Related Decisions

- Amends [ADR 0010](0010-comment-threads-per-audience.md).
- [ADR 0035](0035-absence-questions-are-workflow-steps.md): the same pattern on an absence.
- [ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md): "Mark as handled" and notices settled for every coordinator.
- [ADR 0041](0041-a-request-expires-by-a-system-step.md): request deadlines.
