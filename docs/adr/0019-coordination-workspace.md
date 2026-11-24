# ADR 0019: Coordinators plan from one workspace; staged plans apply as workflow transitions

## Status

Superseded by [ADR 0026](0026-coordination-workspace-hour-timeline.md)

## Context

Coordinators need a fast, visual overview of who is available and what is uncovered, without any path that bypasses the rules, history and notifications that every other path obeys. Because an assignment then held for a whole term, availability had to be judged across the full term.

## Decision

- Coordinators work from one workspace: a priority list of needs that are uncovered or at risk, a teacher-by-week board showing each teacher's timetable slots, work pattern and absence, and a term-week navigator.
- The board, the staffing dialog and planning mode judge availability with one rule, `ITeacherAvailability`, over the course's full term: assigned and accepted teaching blocks, an open request is a warning, approved absence and sick leave block, and time outside the work pattern is a warning.
- Planning mode stages moves in memory and applies each one as an ordinary Assign, Request or Cancel transition in its own transaction, so a partly failed plan reports failures per row, keeps its history and notifies only through the workflow. A reassignment cancels the old assignment and creates a new one; it never edits the teacher in place.
- Open workspaces refresh from a tenant change channel that `WorkflowService` publishes to after commit.

## Rationale

Coordination becomes fast and visual without ever bypassing the rules, history and notifications that every other path obeys.

## Consequences

- Every staffing surface (board, staffing dialog, planning verdicts, priority reasons) reads the same availability rule; a parity test keeps them in agreement.
- The board loads in a fixed number of queries regardless of how many teachers a tenant has.
- Dragging has a click-and-keyboard equivalent.
- The live channel carries identifiers only; each page reloads its own data through its services.

## Related Decisions

- [ADR 0026](0026-coordination-workspace-hour-timeline.md) supersedes this decision: the board and the term-week navigator became an hour timeline and a calendar rail, planning mode was removed, and the one availability rule (now `IStaffAvailability`), the fixed query count, the change channel and the parity test carry over.
