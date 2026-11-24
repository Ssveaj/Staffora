# ADR 0021: One organization tree per tenant

## Status

Accepted

## Context

A staffing company is one business. It needs to record where its people work and which staff members belong to which part of the company, and it needs a place to hold roles. A model of several organizations per tenant, each holding a graph of shared units, adds rules that a single business does not need.

## Decision

- Each tenant has one tree of `OrganizationUnit`s under a business root of type `Company`, created at onboarding and never deleted or archived.
- Every unit has at most one parent (`ParentId`). Unit types are `Company`, `Region`, `Branch`, `Team` and `Grouping`; a grouping holds no roles or members.
- A role is held on a unit (the root means the whole company) and grants its permissions tenant-wide. Role holdings are `OrganizationUnitUserRole` rows with a required unit.
- The unit records where a person works and which staff members belong to it. This drives membership and default filters, not access.
- Jobs belong to a unit. Customers are a separate entity, and a portal customer's jobs land on its default unit.

## Rationale

A single tree with one parent per unit matches how one business is organised. A second organization level and shared units would only add rules that nobody needs.

## Consequences

- There is no organization level between the tenant and its units, and no relation table between units.
- Structure rules (one root, one parent, no cycles, groupings without roles) live in one policy used by every path that writes units.
- A unit is deleted only while it has no units below it and nothing uses it; otherwise it is archived, children first.
- Staff members' unit membership is derived from the units where they hold the Staff role (`ResourceUnitMembership`).
- Unit-scoped coordination (a coordinator who sees only part of the company) is not implemented; it would be a product change, not a defect.

## Related Decisions

- [ADR 0012](0012-platform-owner-and-tenant-onboarding.md): onboarding creates the `Company` root; the System owner role is held on it.
- [ADR 0008](0008-users-become-resources-through-a-role.md): staff members through a resource role.
- [ADR 0043](0043-a-closed-day-is-a-row-without-a-lifecycle.md): closed days that apply to a unit and the units below it.
