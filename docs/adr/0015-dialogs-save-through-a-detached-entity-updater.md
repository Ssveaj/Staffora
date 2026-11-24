# ADR 0015: Dialogs save through a detached entity updater

## Status

Accepted

## Context

A dialog that sends back its whole entity on save overwrites every field, including fields that someone else changed after the dialog loaded. Large saves are also hard for the per-state field access checks and the tenant write checks to judge precisely.

## Decision

- A dialog edits the entity its service loaded and hands the service a `DetachedEntityUpdater<T>`, created by the service with a snapshot taken at load.
- Saving applies only the members that differ from the snapshot: changed scalars and foreign keys on the root, and the child collections declared in the updater's options, diffed by key. New keys are inserted, changed children updated, missing keys removed explicitly, and children added meanwhile by someone else are kept.
- Two people editing different fields both keep their edits; for the same field the last save wins.
- `State` and `IsInTransition` remain concurrency tokens, so a dialog whose entity changed state since loading is refused and asked to reload.

## Rationale

A dialog never overwrites what it did not change, and every save is small enough for the field access and tenant checks to judge precisely.

## Consequences

- Only services create updaters (`Load{Entity}UpdaterAsync`) and save them, together with a workflow step (`Update{Entity}AndTransitionAsync(updater, transitionKey, …)`). A dialog never sends a whole entity for an update; create still sends the new entity.
- The dialog context holds `Updater`. Its `HasChanges()` drives the unsaved-changes warning, and a reload after a transition replaces it.
- Reference navigations are never written through an updater, and many-to-many edges change only through dedicated service commands.
- Every updater save is a transition: the service passes the updater and the step to `WorkflowService.SaveAndTransitionAsync`, which applies the edits and takes the step in one transaction (plain edits go through the state's designated save step). The step's field rules and edit authority, and the tenant write checks ([ADR 0013](0013-tenant-isolation-in-the-data-context.md)), judge the edits; a concurrency, field-lock or step refusal rolls back both and returns a failed `Result`.

## Related Decisions

- [ADR 0017](0017-dialogs-load-through-one-service-call.md): how the updater is loaded.
- [ADR 0031](0031-dialogs-open-locked-until-edit.md): edits start only after Edit.
- [ADR 0043](0043-a-closed-day-is-a-row-without-a-lifecycle.md): a form dialog without a lifecycle sends a draft of its fields instead.
