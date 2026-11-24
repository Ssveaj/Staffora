# ADR 0027: Jobs with slots staff any service business; a crew is one slot per person

## Status

Accepted. Supersedes [ADR 0006](0006-course-staffing-domain.md). Amended by [ADR 0041](0041-a-request-expires-by-a-system-step.md).

## Context

The teacher-and-course model of [ADR 0006](0006-course-staffing-domain.md) already held almost everything a general service business needs: resources through a resource role, qualifications, work patterns, absence, several needs per staffed unit, the request and accept flow, and coverage. What bound it to schools were its names, a timetable that only knew terms and weekly lessons, the lack of a place, a portal request that could not state how many people it wanted, and a coordinator who had to pick every person by hand.

## Decision

- A company sends its own staff out on **Jobs** for its **Customers**, whatever it sells: a cleaning crew of three to an office, several teachers to a school, two carers to a home.
- A job belongs to one customer, happens once or recurs weekly over a period, takes place at a location, and carries one **JobSlot** per person it needs.
- A coordinator fills each slot with a **SlotAssignment**: directly, by a request to one staff member, or by a **request round** that asks several staff members for a group of interchangeable slots.
- A tenant describes what it sells in a **service catalogue**. A service names the qualifications its slots require (Required or Preferred) and prefills a slot made from it.
- Jobs reach the company in two ways: staff register them for any customer, or a portal customer sends an **Order** whose lines name a service the company offers and how many people it wants ([ADR 0007](0007-cross-tenant-ordering-customer-portal.md)).
- The code and the UI use neutral words: Job, JobSlot, SlotAssignment, Order, and Resource in code with "Staff" in the UI.
- A slot remains one place for one person. A count is a way to create slots, and interchangeable slots are grouped by what they ask for (service, qualifications, times and name), never stored as a group.

## Rationale

Keeping a slot as one place for one person leaves the one-holder rule, the unique indexes and the availability rule unchanged, while neutral names, a place, single or recurring jobs, slots created by count and request rounds remove what tied the model to schools.

## Consequences

- The rules of ADR 0006 that still hold carry over: coverage is derived, never stored; staff see an assignment as soon as it exists; cancellation records who canceled (by the customer, by the supplier); per-occurrence staffing would add a session between job and slot and keep the assignment model.
- The entities of ADR 0006 map to the current ones as follows: Course to `Job`, CourseTimeSlot to `JobTimeSlot`, TeacherNeed to `JobSlot`, TeacherNeedQualification to `JobSlotQualification`, TeacherAssignment to `SlotAssignment`, CourseRequest to `Order`, CourseRequestTeacher to `OrderStaff`. Permission values keep their numbers.
- No page, notice, display name or workflow step says teacher, course, lesson or term, and no label says need (`UiWordScanTests`).
- A job is either one occurrence (a day, a start and an end) or recurring (a period of at most a year and weekly time slots). Everything that judges time (availability, conflicts, the timeline, the job clock) reads the occurrences, so both kinds are judged by the same rule ([ADR 0020](0020-dates-and-times.md)).
- A request round asks its candidates for a slot group. With "first to accept" on, each acceptance takes an open slot in the group until it is full and the rest become Not selected; with it off, the coordinator picks among those who accepted. Requests and rounds carry an answer-by time and expire by a system step ([ADR 0041](0041-a-request-expires-by-a-system-step.md)).
- A service has no price; billing, pricing and contracts are out of scope.
- The portal sees only the services its supplier offers in the portal, sent across as messages through the integration outbox like every other cross-tenant change.
- Words a tenant chooses for its own staff and jobs, material resources, on-call schedules and remote meetings are out of scope ([PRODUCT.md](../../PRODUCT.md), Scope). A tenant's own vocabulary is planned ([roadmap](../roadmap.md)).

## Related Decisions

- Supersedes [ADR 0006](0006-course-staffing-domain.md).
- [ADR 0007](0007-cross-tenant-ordering-customer-portal.md): orders and services offered in the portal.
- [ADR 0008](0008-users-become-resources-through-a-role.md): staff members as resources.
- [ADR 0028](0028-the-product-is-called-staffora.md): the product's name.
- [ADR 0041](0041-a-request-expires-by-a-system-step.md): request deadlines.
