# ADR 0016: Deletion cascades through child workflows

## Status

Accepted

## Context

Deleting or canceling an object affects everything below it. A silent database cascade of state entities would bypass their workflows, locks and history; leaving dependants behind would create orphans.

## Decision

- A hard delete is a transition into a `[DeleteState]` and is allowed only before other records depend on the entity. Afterwards the entity moves to a terminal state instead (Archived, Inactive, Canceled).
- The delete state's enter actions cascade each child state entity through its own workflow, so children (and their children) are checked, locked, transitioned and recorded in history in the same transaction. `WorkflowService` removes every entity left in a delete state.
- Owned rows without a lifecycle cascade in the database. Every other dependant is removed by a registered cleanup in tenant scope, or blocks the delete through a described condition.

## Rationale

Deleting an object takes care of everything below it, with no orphans, no silent database cascade of state entities and one delete path per entity.

## Consequences

- Cascades are declared on the workflow: the delete or cancel state names the child navigation and the child's target state. `WorkflowService` loads the navigation itself and refuses the transition if it cannot, rather than skipping children.
- Cancel cascades (job to slots to assignments) use the same mechanism, so they are locked and recorded too.
- A thread with comments blocks a hard delete, since comments are never deleted ([ADR 0010](0010-comment-threads-per-audience.md)).
- History rows have no foreign key to the entity and survive it. Each cascaded entity gets its own history row, correlated with the parent's.
- A model test proves that every foreign key into an entity with a delete state is an owned cascade, a declared workflow cascade, a registered cleanup, or blocked by a named condition.
- `Cascade` delete behaviour is used only for owned rows without a lifecycle; every other relationship is `NoAction` ([ADR 0022](0022-data-model-conventions.md)).
- A transition may opt out of cascade registration with `Transition.NeverCascaded` when it leads into the same state as a cascade's own step ([ADR 0044](0044-taking-off-for-absence-and-open-again.md)).

## Related Decisions

- [ADR 0002](0002-workflowservice-orchestration.md): the orchestration that executes cascades.
- [ADR 0022](0022-data-model-conventions.md): relationship conventions.
- [ADR 0044](0044-taking-off-for-absence-and-open-again.md): `NeverCascaded`.
