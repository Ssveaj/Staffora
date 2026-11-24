# ADR 0011: Notifications come from the workflow, after commit

## Status

Accepted. Amended by [ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md).

## Context

Users must be told about the changes that concern them. A notification sent for a change that was later rolled back, or sent by a service outside the workflow, would be misleading and would not appear in the record's history.

## Decision

- Every notification is queued by a workflow action (or by the comment service for a new comment) as an `OutgoingMessage` on the transition context.
- `WorkflowService` dispatches the batch only after the transaction commits, and links each delivered message to the transition's history entry, so the dialog's history shows who was told and on which channel.
- Delivery goes through a channel router and `IChannelSender` implementations. The only channel is in-app: a `StoredMessage` with one `MessageRecipient` per user, pushed live to open circuits through an in-process per-user bus and shown in a bell with an unread badge.
- Messages are built from a code catalogue of templates with placeholders, one per `MessageType`.
- No service calls the notifier directly.

## Rationale

With the workflow as the only route, no notification can escape the history or fire for a rolled-back change.

## Consequences

- A notification for a rolled-back transition is never sent; a test guards this.
- `MessageRecipient` read state is a plain flag, not a workflow: reading a notice is a UI fact, not a business lifecycle (a deliberate exception to [ADR 0009](0009-state-entity-dialog-only-and-workflow-governed-attributes.md)). The exception is the notices raised by a staff member's question or message, which a coordinator's step or reply marks read for every coordinator ([ADR 0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md)).
- In-app messages stay inside one tenant. The customer is notified by the order steps that the integration outbox handler runs in the customer tenant ([ADR 0007](0007-cross-tenant-ordering-customer-portal.md)).
- E-mail, SMS, digests, per-type preferences and tenant-edited templates are not implemented; they would be added behind the same router and catalogue.

## Related Decisions

- [ADR 0002](0002-workflowservice-orchestration.md): the transaction after which notifications are dispatched.
- [ADR 0010](0010-comment-threads-per-audience.md): comments that trigger notifications.
- [ADR 0035](0035-absence-questions-are-workflow-steps.md) and [ADR 0037](0037-request-questions-are-workflow-steps.md): notices that carry the words typed in a step's prompt.
