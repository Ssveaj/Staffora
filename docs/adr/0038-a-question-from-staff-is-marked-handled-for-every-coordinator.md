# ADR 0038: A question from staff is marked handled, and its notices settle for every coordinator

## Status

Accepted. Amends [ADR 0011](0011-notifications-from-workflow-after-commit.md), [ADR 0035](0035-absence-questions-are-workflow-steps.md) and [ADR 0037](0037-request-questions-are-workflow-steps.md).

## Context

A staff member's question or message waits on the coordinators until one of them writes back. A request's question ([ADR 0037](0037-request-questions-are-workflow-steps.md)) and a message in an assigned staff member's conversation ([ADR 0010](0010-comment-threads-per-audience.md)) leave the coordinator only the answer, so a question that needs no words keeps its item in the Priorities and its notice in every coordinator's bell until someone writes something. A coordinator must be able to settle such a question without writing, through the workflow: an optional step without text, after which the item leaves the Priorities for every coordinator, the related notices are read for every coordinator, and the history records who did it and when.

## Decision

### One step, "Mark as handled", in each flow

The step has the icon `done_all`, no prompt, no confirmation and no notice to anyone; it is never offered on one's own record and takes no edits.

- **A request with a question:** `assignment-question-handled`, StaffQuestion to Requested, with `PerformCoordination` and the answer's conditions (the job in staffing, `NotOwnQuestionReason`). The request is the staff member's again to accept or decline, as after an answer. The answer remains available.
- **An absence with a question:** `absence-question-handled`, StaffQuestion to Requested, labelled "Mark as handled", so both flows read the same.
- **A staff member's message on an accepted request or an assignment:** `assignment-accepted-message-handled` (Accepted) and `assignment-assigned-message-handled` (Assigned), self-transitions with `PerformCoordination`, offered only while the staff member's last message waits for a reply (`AwaitsReplyReason`, the same rule as the thread's "Awaits your reply") and the job is in staffing. The step stamps the coordinators' side of the whose-turn timestamps (`LastCoordinatorCommentAt`, one millisecond after the staff member's when the clock has not passed it), so the conversation no longer waits, and moves the staff member's seen mark with it unless a real reply is still unseen, so they are not shown a new reply that does not exist. No schema change is needed.

### The notices settle for everyone

These steps, "Answer the staff member" on a request and on an absence, and a coordinator's reply in an assignment's conversation mark read, in their own transaction, every unread copy of the notices that the staff member's question or message raised about that record (`assignment-question-from-staff` and `assignment-staff-wrote` on an assignment, `absence-question-from-staff` on an absence), for every recipient, in the record's tenant only.

- A step does this through a workflow action (`MarkNoticesReadAction`, resolving `IHandledNotices` from the step's scope, with a `BeforeSave` write and an after-commit push); the reply does it through the same service inside the comment's transaction.
- After the commit each reader whose copies were marked is told on the notice bus (`NoticeArrived.Read`), so an open bell reads its count again without announcing a new notice.

### The Priorities follow by themselves

The Priorities' questions from staff are read from the state (StaffQuestion) and from the whose-turn timestamps, with nothing stored per reader, so a step that any coordinator takes removes the item for all.

## Rationale

A step without text lets a coordinator settle a question that needs no words while still going through the workflow, with its access gate and history. Settling the notices and the Priorities item for every coordinator at once means one coordinator's action ends the question for the whole team.

## Consequences

- For these notices, this changes [ADR 0011](0011-notifications-from-workflow-after-commit.md)'s rule that reading a notice is a UI fact: a coordinator's step settles them for the other coordinators too. Their read time and editor stamp record the step's moment and actor. Every other notice is still read only by its own reader.
- A message that arrives after a coordinator opened the assignment and before they mark it handled is handled with it: the step is judged on the row under its lock, and the whose-turn timestamps are not part of the version a step compares.
- An absence's "Answer the staff member" takes the absence back to Requested: the question leaves the Priorities as soon as it is answered, since the Priorities list what the workflow leaves with the coordinators, and the absence then waits for its decision like any requested absence.

## Related Decisions

- Amends [ADR 0011](0011-notifications-from-workflow-after-commit.md), [ADR 0035](0035-absence-questions-are-workflow-steps.md) and [ADR 0037](0037-request-questions-are-workflow-steps.md).
- [ADR 0010](0010-comment-threads-per-audience.md): whose-turn timestamps.
- [ADR 0026](0026-coordination-workspace-hour-timeline.md): the Priorities dock.
