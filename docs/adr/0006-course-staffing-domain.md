# ADR 0006: Customers order courses, coordinators staff them

## Status

Superseded by [ADR 0027](0027-generic-resource-staffing.md)

## Context

The product first targeted the staffing of teachers. It needed the smallest domain model that still covered the full flow of ordering, coordination and assignment for that case.

## Decision

A **Customer** (an education company, such as the fictional Brightpath Learning) needs teachers for a **Course**. The course is the staffed unit: it belongs to one customer and carries one or more **TeacherNeeds** (main teacher, assistant). A coordinator fills each need with a **TeacherAssignment**, either directly or by requesting teachers who accept or decline. Courses reach the supplier in two ways: staff register them for any customer, or a portal customer sends them across tenants ([ADR 0007](0007-cross-tenant-ordering-customer-portal.md)).

Request rounds with deadlines and automatic assignment, time reports, billing, pricing and contracts are out of scope. Comments and notifications are covered by [ADR 0010](0010-comment-threads-per-audience.md) and [ADR 0011](0011-notifications-from-workflow-after-commit.md).

## Rationale

This was the smallest model that still covered the full ordering, coordination and assignment flow for the teacher-staffing case.

## Consequences

- One customer request is one course; there is no order container. One would be added only if a request had to cover several courses.
- Coverage of a course (uncovered, partly covered, covered) is derived from its needs and assignments and is never stored as a state.
- A teacher sees an assignment as soon as it exists; there is no publication axis.
- Cancellation records who canceled (`CanceledByCustomer`, `CanceledBySupplier`), because the customer portal shows the difference.
- Per-session staffing (substitutes per lesson) would introduce a session entity between the course and the need, keeping the assignment model unchanged.

## Related Decisions

- [ADR 0027](0027-generic-resource-staffing.md) supersedes this decision with the same model under neutral names (jobs, slots, assignments, orders) for any service business, adding a place, single or recurring jobs, slots created by count, request rounds and a service catalogue. The rules above that still hold carry over there.
