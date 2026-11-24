# ADR 0010: Comments are threads, one container per audience

## Status

Accepted. Amended by [ADR 0035](0035-absence-questions-are-workflow-steps.md), [ADR 0037](0037-request-questions-are-workflow-steps.md) and [ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md).

## Context

Staff members, coordinators and customers need to communicate about jobs and assignments, but each audience may read only its own conversations: staff never see customer conversations or internal notes, and customers see nothing of the staffing company beyond their own orders. Visibility decided per comment is easy to get wrong.

## Decision

- Communication is stored as **comments** in a `CommentContainer` (one thread) that an entity points to. Who may read a thread is decided by which container it is, never by a flag on each comment.
- A job has an internal thread (coordinators only) and a customer thread (coordinator and customer, mirrored across tenants through the integration outbox). Each assignment has its own staff thread (the coordinator and that staff member). An absence has one staff thread (the staff member and the approvers).
- Staff members and customers never share a thread; the coordinator relays.
- Threads are shown with one shared comment block (`CommentBubbleList`) in the StateEntityDialog side column, one tab per audience. A transition's comment prompt writes its text into the entity's thread inside the transition's transaction.
- A free comment saves at once through the thread service, never through the dialog's save-before-transition hook. Threads that are written only through workflow steps take no free comment and show no composer:
  - an absence's thread, written only by its steps' prompts ([ADR 0035](0035-absence-questions-are-workflow-steps.md));
  - an assignment's staff thread while its request is open (Requested, or StaffQuestion while the staff member's question waits): the questions and answers are written by the request's question steps' prompts ([ADR 0037](0037-request-questions-are-workflow-steps.md)). Once the staff member has accepted, the thread works with its composer, whose-turn timestamps and notices.
- Whose turn it is lives on the owning entity as timestamps (last staff member message, last coordinator message, seen), not in a per-user read table; tabs show an attention badge from them. A coordinator's "Mark as handled" on an assignment whose staff member's message waits stamps the coordinators' side as a reply does, through a workflow step with its history and without a comment ([ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md)).
- **The customer thread** is one thread in each tenant, mirrored through the integration outbox ([ADR 0007](0007-cross-tenant-ordering-customer-portal.md)). A copy is written by its own tenant's system account and names its author as that tenant knows them (`CommentAuthorType.Contact`, a contact of the customer; `CommentAuthorType.Supplier`, the portal's supplier party), never a user of the other tenant. Its attention is judged from its last comment by side (a copy from the other side awaits a reply; a colleague's message on one's own side does not) rather than from whose-turn timestamps.

## Rationale

Making the audience a property of the container rather than of each comment makes per-comment visibility mistakes impossible.

## Consequences

- Comments are never edited or deleted.
- New comments trigger notifications ([ADR 0011](0011-notifications-from-workflow-after-commit.md)).
- A thread with comments blocks a hard delete of its owner ([ADR 0016](0016-deletion-cascades-through-child-workflows.md)).

## Related Decisions

- [ADR 0006](0006-course-staffing-domain.md) and [ADR 0007](0007-cross-tenant-ordering-customer-portal.md): this decision brought messaging into scope.
- [ADR 0011](0011-notifications-from-workflow-after-commit.md): notifications for new comments.
- [ADR 0035](0035-absence-questions-are-workflow-steps.md), [ADR 0037](0037-request-questions-are-workflow-steps.md), [ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md): threads written through workflow steps.
