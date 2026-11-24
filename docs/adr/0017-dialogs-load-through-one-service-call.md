# ADR 0017: State-entity dialogs load through one service call and one include graph

## Status

Accepted

## Context

A dialog that assembles its data from many independent calls costs an unpredictable number of round trips, is hard to test, and can disagree with the server about what a transition needs if the dialog and the transition load different data.

## Decision

- A dialog's context loads everything its body needs in one service call: `Load{Entity}UpdaterAsync(id, ct)` checks access, loads the entity with its `{Entity}WorkflowQueries.WithWorkflowGraph()` plus display includes and custom field rows, and returns the detached updater ([ADR 0015](0015-dialogs-save-through-a-detached-entity-updater.md)). Lookups for drop-downs come from the same call or from one `Get{Entity}EditorOptionsAsync`.
- The same `WithWorkflowGraph()` is the context's `QueryConfigurator`, so every transition reloads exactly what the dialog loaded, and conditions see the same data in the dialog and on the server.
- The host owns the loading, failure and cancellation states. The footer asks for the allowed transitions in one batched call per entity version. Views, history and comment threads load on demand. Nothing writes during a load.

## Rationale

Opening a dialog costs a small, predictable number of round trips, every load path is testable, and a dialog and the server can never disagree about what a step needs.

## Consequences

- `EntityId` is a `Guid?`; null means create, and create mode returns a new entity in its entry state from `init` inputs.
- There is one dialog per entity. Different audiences get different views or field configurations, not a second context.
- The body never calls a service in `OnInitialized*` (lazy view tabs and side-column threads excepted), never fires loads without awaiting them, and runs independent secondary loads together behind the open form.
- A failed load shows "could not open" with its reason (not found, no access, error) and never throws into the circuit. Closing the dialog cancels a load in flight.
- Reloading after a child dialog closes saves or asks first when the updater has changes.
- Architecture tests enforce the rules: each context's `QueryConfigurator` is the graph its load method uses, and no body loads in `OnInitialized*`.

## Related Decisions

- [ADR 0009](0009-state-entity-dialog-only-and-workflow-governed-attributes.md): state entities are shown only in their dialog.
- [ADR 0015](0015-dialogs-save-through-a-detached-entity-updater.md): the updater returned by the load.
- [ADR 0036](0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md): who may load a record's history.
