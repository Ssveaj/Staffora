# ADR 0001: Workflow engine for state entities

## Status

Accepted

## Context

Many entities have a lifecycle: a set of states, the transitions between them, the conditions that guard each transition and the fields that may be edited in each state. When these rules are expressed as state checks spread across services and components, they are hard to find, easy to contradict and hard to audit.

## Decision

Entities with a lifecycle derive from `StateEntityBase<TState>` (tenant-owned ones from `TenantStateEntityBase<TState>`) and take their states, transitions, transition guards and per-state field access from a `Workflow<TState, TEntity>` declared in a `WorkflowAttribute` on the entity.

## Rationale

A declarative workflow keeps every lifecycle rule in one place. The UI renders whatever the current state allows, and transitions are predictable and auditable.

## Consequences

- Services and components never set `State` directly and never hardcode which fields are editable in which state.
- Every state entity carries an enum, a workflow attribute and a dialog context, even when its lifecycle is small.

## Related Decisions

- [ADR 0002](0002-workflowservice-orchestration.md): how transitions are executed.
- [ADR 0009](0009-state-entity-dialog-only-and-workflow-governed-attributes.md): how state entities are shown.
- [ADR 0016](0016-deletion-cascades-through-child-workflows.md): how deletion and cancellation cascade.
