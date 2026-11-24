# ADR 0035: Absence questions are workflow steps, and their words travel with the notice

## Status

Accepted. Amends [ADR 0010](0010-comment-threads-per-audience.md). Amended by [ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md).

## Context

A requested absence often needs clarification before it is decided: the coordinator asks which days a planned leave covers, or the staff member asks whether a half day is acceptable. An absence with only an internal thread, read by approvers and used only for a rejection's reason, gives neither side a way to ask the other anything on the absence itself. Every word exchanged must go through a workflow step and be carried in the notice.

## Decision

### Question states

`ResourceAbsenceState` has Question ("Question to the staff member", 4) and StaffQuestion ("Question to the coordinator", 5), appended to the enum. Each is Requested with a person's turn attached: the absence is undecided, and every reader that counts, lists or blocks requested absences reads them as requested (`AbsenceRules.UndecidedStates`; a sick leave blocks in them as it does while requested). Question is editable like Requested, by the staff member alone. StaffQuestion is read-only for everyone, so the dates do not change under a question asked about them.

### Steps

- **The coordinator's side** (a holder of `ApproveResourceAbsence`, never on their own absence):
  - "Send a question" (Requested to Question);
  - "Send another question" (Question to Question);
  - "Continue without an answer" (Question to Requested);
  - "Answer the staff member" (StaffQuestion to Requested): an answered question no longer waits on the coordinators, so it leaves the Priorities, and the absence waits for its decision again;
  - "Mark as handled" (`absence-question-handled`, icon `done_all`, StaffQuestion to Requested), which settles the question without words ([ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md)).
- **The staff member's side** (`ReportOwnAbsence`, on their own absence only):
  - "Write a question" (Requested to StaffQuestion);
  - "Write another question" (StaffQuestion to StaffQuestion);
  - while the question is with them, Save and "Answer" (Question to Requested), which may carry corrected dates instead of words.
- Cancel absence is offered from both question states. Approve and Reject remain on Requested alone, so every way out of a question leads back to where the decision is made.

### Every word through a step

Each step that carries words asks for them in its comment prompt, which writes them into the absence's staff thread in the step's own transaction. The prompts are required except for the staff member's Answer, which may be sent empty and then writes nothing: `TransitionCommentPrompt` has `Required` (true by default) and `RequiredMessage`, the transition button passes them to the prompt, and the server refuses a blank answer only for a required prompt.

### One thread, no composer

The absence has one thread, the staff thread, read by the absence's own staff member and by the approvers. It has no composer for anyone, and nothing is shown where the composer would be (the thread access's `ShowsClosedReason` is off). A free post is refused with its `ClosedReason`, `ResourceAbsenceService.WrittenThroughSteps` ("Questions and answers are written in the window that opens from a step below."). The dialog's side column shows the thread only once it holds a comment. A rejection's reason is written there too, so the staff member reads why, and the rejection's notice says that the reason is on the absence.

### The words in the notice

- "Send a question" and "Send another question" notify the staff member ("New question about your absence").
- "Answer the staff member" notifies the staff member ("New answer about your absence", `absence-answer-to-staff`), carrying the answer and stating that the absence again waits for an approver.
- "Write a question" and "Write another question" notify every approver ("New question about an absence", `absence-question-from-staff`), and "Answer" notifies them too ("New answer on an absence").
- Each notice carries the words typed in its prompt, appended as a separate paragraph (`NotifyAction`'s `withComment`). For these absence notices only, this replaces the rule that a notice names who wrote and never the words.
- "Continue without an answer" and "Mark as handled" notify nobody. A cancellation notifies the other side: the staff member when someone else cancels, the approvers when the staff member cancels.
- "Answer the staff member" and "Mark as handled" mark read, in their own transaction, every approver's unread `absence-question-from-staff` notice about the absence; the open bells are told and count again ([ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md)).

### No reminders

A question that waits is shown by its state, nothing more.

## Rationale

Writing every word through a step keeps the conversation inside the workflow, with its access rules, history and notices, and keeps the absence's state an accurate statement of whose turn it is. Carrying the words in the notice lets the reader act without opening the absence.

## Consequences

- For the absence, this replaces [ADR 0010](0010-comment-threads-per-audience.md)'s rule that a comment saves at once through the thread service: the absence's thread is written only by steps. Other threads keep their composers.
- Existing absences need no migration: the states are stored as their numbers and no check constraint names them. A reason written into an absence's former internal thread stays in the database and is not shown.
- An approver who is also a staff member writes questions about their own absence as a staff member and is notified of them as an approver, as with their own report.
- A notice holds a staff member's or a coordinator's own words, truncated to the notice's length; the thread keeps the whole text.

## Related Decisions

- Amends [ADR 0010](0010-comment-threads-per-audience.md).
- [ADR 0037](0037-request-questions-are-workflow-steps.md): the same pattern on a request.
- [ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md): "Mark as handled" and notices settled for every coordinator.
- [ADR 0044](0044-taking-off-for-absence-and-open-again.md): absence that blocks and its affected jobs.
