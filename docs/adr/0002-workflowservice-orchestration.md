# ADR 0002: WorkflowService orchestrates every state transition

## Status

Accepted

## Context

A single transition can change several state entities, and in a multi-user Blazor Server application two users can act on the same entity at the same time. Concurrent transitions must not corrupt state.

## Decision

All transitions on `IStateEntity<TState>` entities run through `WorkflowService.TransitionEntityAsync`, which, in one database transaction:

1. checks that the transition is allowed;
2. locks the primary entity and every state entity that `Workflow.GetAllCandidatesForTransition` names, with one `ExecuteUpdate` per entity type that sets `IsInTransition` on the rows where it is still false;
3. executes the transition and saves.

The entry points are in `Staffora.Services/Workflow/WorkflowService.cs`; the lock statement and its change-tracker synchronisation are in `WorkflowService.Locks.cs`. The step-by-step sequence and the EF change-tracker synchronisation it depends on are described in the [transition orchestration guide](../../.claude/skills/workflow-state-entities/TRANSITION-ORCHESTRATION.md).

## Rationale

Locking all affected rows atomically, inside the same transaction that executes and saves the transition, prevents concurrent transitions from interleaving on the same entities.

## Consequences

- Direct mutation of `State` or `IsInTransition` outside `WorkflowService` is a defect.
- Transition actions must declare every entity they modify; an undeclared entity is changed without a lock.
- The lock is written outside EF's change tracker, so the original value of every locked entity's `IsInTransition` must be synchronised before saving. This is easy to regress silently and is documented in the transition orchestration guide.

## Related Decisions

- [ADR 0001](0001-workflow-engine-for-state-entities.md): the workflow model.
- [ADR 0011](0011-notifications-from-workflow-after-commit.md): notifications dispatched after the transition commits.
- [ADR 0016](0016-deletion-cascades-through-child-workflows.md): cascades executed by the same orchestration.
