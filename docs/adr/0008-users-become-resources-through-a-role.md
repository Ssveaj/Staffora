# ADR 0008: Users become resources through a resource role

## Status

Accepted

## Context

Administrators, coordinators and staff members all sign in, hold permissions and have a profile. Only staff members are assignable to jobs, with a schedule, qualifications and assignments. The model must decide whether staffability is a separate person record or a property of the user.

## Decision

Everyone starts as an ordinary `ApplicationUser`. A user becomes a **Resource** (assignable, with a schedule, qualifications and job assignments) by holding a tenant role flagged `ApplyResourceMembershipToUser`. The seeded "Staff" role carries the flag and the own-work permissions. A single `ResourceLinker`, called by the services that change role assignments or a user's active flag, creates the user's `Resource` or moves it between Active and Inactive.

## Rationale

Deriving staffability from a role rather than from a separate person record means one login, one profile and one permission system cover administrators, coordinators and staff members alike.

## Consequences

- Every resource is backed by exactly one user (`Resource.ApplicationUserId`, unique). Non-person resources (rooms, equipment) are out of scope.
- Schedules, absences, qualifications and assignments attach to `ResourceId`, never to the user, and survive the loss of the role: the resource becomes Inactive and keeps its history.
- Only Active resources appear as candidates. Deactivating a user deactivates the resource.
- The linker runs inside the role-assignment service, not in UI components.
- The user dialog shows the resource's views (qualifications, schedule, absence, assignments) when a resource exists. There is no separate resource dialog and no "create resource" button (`DialogArchitectureScanTests` records the exception).

## Related Decisions

- [ADR 0012](0012-platform-owner-and-tenant-onboarding.md): role presets, including the Staff role.
- [ADR 0021](0021-one-organization-tree-per-tenant.md): staff membership of organization units derived from where the Staff role is held.
