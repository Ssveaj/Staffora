# Staffora

**Read this first.** It defines what this product is. Every other document, plan and line of code serves it. When a request or a change conflicts with this file, stop and ask the maintainers. This file changes only in an approved change that comes with an ADR.

## Purpose

Staffora is a multi-tenant SaaS for **staffing jobs with a company's own people**. A company that sends its staff out on jobs for its customers buys the system to plan the jobs, keep track of its staff and their schedules, and put the right people on each job. The business does not matter: a cleaning company sends a crew of three cleaners to an office, a school authority sends several teachers to a school, a care company sends two carers to a home. Staff members answer every request themselves: they accept or decline.

Many companies use the platform side by side. Company A and Company B each buy the system for their own business; they do not know about each other and never see each other's data. A platform owner operates the whole platform and has oversight of every tenant.

The product's purpose is recorded in [ADR 0027](docs/adr/0027-generic-resource-staffing.md) and its name in [ADR 0028](docs/adr/0028-the-product-is-called-staffora.md).

## Actors

| Actor | Where | Can | Cannot |
|---|---|---|---|
| **Platform owner** | Platform level | Create, activate, suspend, reactivate and archive tenants; inspect any tenant from the platform console; correct a tenant user's name, email address and password from the console to help the tenant's owners ([ADR 0030](docs/adr/0030-platform-owner-corrects-a-users-sign-in-details.md)); make, change and delete every custom field, global or for one chosen tenant ([ADR 0032](docs/adr/0032-platform-owner-makes-custom-fields-for-a-tenant.md)); impersonate an eligible user in any tenant to see and act exactly as that user (audited); see platform health (error log, integration outbox); give a new tenant its first system owner | Work inside a tenant as itself beyond those corrections and fields; be a staff member; show one tenant's data in another tenant's context |
| **System owner** | One tenant | Everything in its tenant except own work; appoints other system owners | Leave the tenant without an owner |
| **Tenant administrator** | One tenant | Users, roles (up to the permissions they hold themselves), organization units, settings, the service catalogue | Change the tenant's own lifecycle state; make or change custom fields, which the platform owner sets up |
| **Coordinator** | One tenant | Customers, jobs and their slots; request or assign staff, alone or in a request round; register absence on a staff member's behalf, and approve or reject reported absence; talk with staff and customers | Grant roles beyond their own permissions |
| **Staff member** | One tenant | Own schedule, absence and assignments; accept or decline requests; talk with the coordinator about their own assignments | See other staff members' assignments, internal notes or customer conversations |
| **Customer administrator / Orderer** | A customer portal | Order jobs (which services, how many people, when and where), follow and cancel orders, talk with the coordinator; administrators manage orderers | See any page or data of the staffing company beyond their own orders |
| **System account** | One tenant | Stamps rows written on the tenant's behalf (cross-tenant sync, background work) | Sign in or be impersonated |

A staff member is an ordinary user who holds a role marked as a resource role; that makes the user a **resource** with a schedule, qualifications and assignments ([ADR 0008](docs/adr/0008-users-become-resources-through-a-role.md)).

## Core flows

1. **Onboarding.** The platform owner creates a tenant for a company: its Company root unit, its system owner, role presets (administrator, coordinator, staff) and system account. The tenant becomes active once its system owner can sign in.
2. **Setting up.** The tenant's administrators add users; giving a user the staff role makes them a resource. Staff get qualifications and a work pattern. The administrators describe the services the company sells (for example "Office cleaning" or "Mathematics teaching") and the qualifications each needs. Absence is reported by the staff member (and approved by a coordinator) or registered directly by a coordinator, for example when someone calls in sick.
3. **Ordering.** A job reaches the company two ways: staff register it for a customer, or a customer with a portal sends an order ([ADR 0007](docs/adr/0007-cross-tenant-ordering-customer-portal.md)). A job happens once or recurs weekly over a period, at a place, and has one slot per person it needs: a crew of three is three slots.
4. **Staffing.** A coordinator opens the job, sees each slot and a ranked list of candidates (qualifications, schedule, absence, other bookings), and assigns staff, sends requests, or sends one request round to several candidates for several slots, where the first to accept fill the slots. Staff accept or decline; the coordinator confirms the job.
5. **Following up.** Everyone involved is notified in the app; staff and coordinator, and coordinator and customer, talk in threads on the job. The customer sees the confirmed staff.

## The tenant promise

- A tenant never sees another tenant's data and never learns that another tenant exists.
- The only link between tenants is between a staffing company and the customer tenants it manages for its portal. Sending an order writes the job straight into the staffing company's tenant in system scope, stamped with that tenant's system account; everything after that, the services the company offers in the portal included, crosses as messages through the integration outbox ([ADR 0007](docs/adr/0007-cross-tenant-ordering-customer-portal.md)).
- Rows written into a tenant on its behalf are stamped with that tenant's system account, never with a user from elsewhere.
- The platform owner sees tenants from the platform console; inside a tenant it only acts by impersonating a user, and every impersonation is audited. The exceptions are help with signing in (from the console it lifts a lockout and corrects a user's name, email address and password, each recorded in the user's history) and the custom fields it sets up for a tenant, stamped with that tenant's system account.

## Tenant lifecycle

| State | Meaning |
|---|---|
| Setup | Created by the platform owner; only the system owner may sign in to finish setup |
| Active | In normal use |
| Suspended | Nobody signs in and nobody may be impersonated; data and integrations are held |
| Pending deletion | Scheduled for removal; read-only for the platform owner |
| Archived | Kept for the record; no access |

Only the platform owner moves a tenant between states. A tenant scheduled for deletion can be taken off the schedule; it returns to Suspended.

## Scope

**In scope:** tenants and the platform console; users, roles and permissions; one tree of organization units per tenant; staff as resources with qualifications, work patterns and absence; customers and contacts; a service catalogue per tenant; jobs with a place, a single time or a weekly timetable, slots and assignments; request rounds; comment threads; in-app notifications; custom fields, set up by the platform owner; the customer portal.

**Out of scope:** time reports, payroll, billing, pricing and contracts; students, grades and enrollment; self-registration; a portal for subcontracting staffing companies; external calendar sync; material resources (vehicles, equipment, rooms); on-call schedules; remote meetings. Words a tenant chooses for its own staff and jobs ("Cleaner", "Visit") come later ([roadmap](docs/roadmap.md)).

## Words

The domain vocabulary (tenant, organization unit, customer, service, job, slot, assignment, request round, resource, staff member) is defined where it is used: [docs/database.md](docs/database.md) for entities, [docs/authorization.md](docs/authorization.md) for roles and permissions. Example names in docs and seed data are fictional (Company A, Company B, a customer called Brightpath Learning).

## Where to go next

- How to build it, step by step: [docs/plans/build-plan.md](docs/plans/build-plan.md)
- Decisions and their reasons: [docs/adr/](docs/adr/)
- Rules for code and agents: [.github/copilot-instructions.md](.github/copilot-instructions.md)
