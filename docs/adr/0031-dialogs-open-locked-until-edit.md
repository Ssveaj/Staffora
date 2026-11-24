# ADR 0031: A saved record's dialog opens locked until Edit

## Status

Accepted

## Context

A state-entity dialog that shows a saved record's fields ready for input, as the state allows, with Save in the footer beside the state's other steps, lets a reader change a field by accident in a dialog opened only to look, and makes every dialog look like a form waiting to be filled in.

## Decision

- A saved record's dialog opens with every field locked, and the footer offers **Edit** in place of Save, beside the state's other steps.
- Edit unlocks the fields and leaves only **Cancel** and **Save** in the footer. Cancel discards the edits and locks the fields again. Every load (Save, any other step, a discard) shows the record as stored, locked.
- A new record, in create mode, is open from the start and offers its saves.
- A state without a step that stores edits keeps its fields as the workflow sets them.
- The step that Edit stands for is the state's edit authority (`Workflow.GetEditAuthority`): its designated save or, where the engine designates none because only a platform owner takes it (a tenant's saves, a custom field's saves, [ADR 0033](0033-custom-fields-have-a-lifecycle.md)), the state's one self-transition that a user takes with edits. Those dialogs open locked too.
- Edit is offered only to a reader whom the server lets save, so a reader who may not save sees no Edit and every field locked.

## Rationale

The edit mode is built once, in the dialog's own machinery, rather than as an Edit transition declared in each workflow and filtered in each dialog. The workflow already marks the state's save (`Transition.IsSave`), so the dialog knows what Edit stands for in every state without a step of its own, and every dialog behaves the same. Cancel beside Save lets a person leave the edit without closing the dialog.

## Consequences

- `StateEntityDialogContext` holds the edit mode (`Editing`, `IsUnlocked`, `LocksUntilEdit`, `BeginEditing`, `CancelEditingAsync`). `StateEntityDialogContextBase` makes every field and area read-only while the record is locked. The footer draws Edit and Cancel as its own buttons (`data-dialog-action`), never as steps, so Save and the access gate remain the workflow's.
- The leave rule (moving between views saves the edits through the state's save) and the guards (closing or navigating away with edits asks first) hold as before; there are edits only after Edit.
- `OpensLocked` is true for every entity's dialog. Only the test entities of the dialog machinery's own tests answer false, so those tests keep driving the machinery on open fields while the edit mode is verified on the user dialog.

## Related Decisions

- [ADR 0009](0009-state-entity-dialog-only-and-workflow-governed-attributes.md): state entities in StateEntityDialogs.
- [ADR 0015](0015-dialogs-save-through-a-detached-entity-updater.md): saving through the updater.
- [ADR 0033](0033-custom-fields-have-a-lifecycle.md): custom field dialogs, whose saves are the platform owner's.
