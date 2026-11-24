# ADR 0009: State entities live in StateEntityDialogs, custom fields obey the workflow

## Status

Accepted

## Context

If some state entities are edited in ad hoc pages or components, or if custom field values follow rules of their own, the statement "the workflow decides" stops being true for some of the fields a user can see. Lifecycle rules must apply uniformly to every field and every action.

## Decision

- Every entity with a lifecycle is a state entity driven by its workflow, and its only UI is a **StateEntityDialog**. The shared host renders the header, the state, the history and the footer of transition buttons; the entity's component renders fields only.
- Saving, creating and deleting are transitions. A button is hidden by a condition, never by component code.
- Custom field values (entity attributes) are governed by an `Attributes` field area that every state configures, so their visibility, editability and required status follow the state exactly like the entity's own properties. The server enforces the same rules on save.
- The rule has no exceptions among entities with a lifecycle. Entities without a lifecycle get a simple form dialog of their own.

## Rationale

With one dialog type and one rule for every field, "the workflow decides" is true for every field a user can see, and the client and the server cannot disagree about what is allowed.

## Consequences

- Catalogue-like entities with a lifecycle (qualifications, customer contacts) are state entities too, with their own dialogs.
- When an entity becomes stateful, its dialog is converted to a StateEntityDialog in the same change.
- Required custom fields block a transition through a described condition, shown on the disabled button.
- No page or component outside the dialog's context runs a step or deletes a state entity (`LifecycleCallScanTests`).
- The rules and the dialog recipe are in the `workflow-state-entities` skill ([DIALOGS.md](../../.claude/skills/workflow-state-entities/DIALOGS.md), [ATTRIBUTES.md](../../.claude/skills/workflow-state-entities/ATTRIBUTES.md)).

## Related Decisions

- [ADR 0001](0001-workflow-engine-for-state-entities.md): the workflow model.
- [ADR 0015](0015-dialogs-save-through-a-detached-entity-updater.md) and [ADR 0017](0017-dialogs-load-through-one-service-call.md): how dialogs save and load.
- [ADR 0031](0031-dialogs-open-locked-until-edit.md): a saved record's dialog opens locked.
- [ADR 0033](0033-custom-fields-have-a-lifecycle.md): custom field definitions are state entities.
- [ADR 0043](0043-a-closed-day-is-a-row-without-a-lifecycle.md): an example of an entity without a lifecycle in a form dialog.
