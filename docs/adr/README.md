# Architecture decision records

Each record states one architectural decision: its context, the decision, its rationale, its consequences and the decisions it relates to. Numbers are never reused. A superseded record is kept and links to the record that replaced it; an amended record links to the records that changed it.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-workflow-engine-for-state-entities.md) | Workflow engine for state entities | Accepted |
| [0002](0002-workflowservice-orchestration.md) | WorkflowService orchestrates every state transition | Accepted |
| [0003](0003-separate-superadmin-layout.md) | Separate SuperAdmin layout, one router | Superseded by [0023](0023-one-app-shell-two-menus.md) |
| 0004 | | Withdrawn |
| [0005](0005-code-style-and-english-ui.md) | Code style and English UI | Accepted |
| [0006](0006-course-staffing-domain.md) | Customers order courses, coordinators staff them | Superseded by [0027](0027-generic-resource-staffing.md) |
| [0007](0007-cross-tenant-ordering-customer-portal.md) | Portal customers order across tenants | Accepted |
| [0008](0008-users-become-resources-through-a-role.md) | Users become resources through a resource role | Accepted |
| [0009](0009-state-entity-dialog-only-and-workflow-governed-attributes.md) | State entities live in StateEntityDialogs, custom fields obey the workflow | Accepted |
| [0010](0010-comment-threads-per-audience.md) | Comments are threads, one container per audience | Accepted, amended |
| [0011](0011-notifications-from-workflow-after-commit.md) | Notifications come from the workflow, after commit | Accepted, amended |
| [0012](0012-platform-owner-and-tenant-onboarding.md) | Platform owner, tenant onboarding and system owners | Accepted, amended |
| [0013](0013-tenant-isolation-in-the-data-context.md) | Tenant isolation is enforced in the data context | Accepted |
| [0014](0014-session-on-the-auth-cookie-audited-impersonation.md) | Session state lives on the auth cookie; impersonation is authorized and audited | Accepted |
| [0015](0015-dialogs-save-through-a-detached-entity-updater.md) | Dialogs save through a detached entity updater | Accepted |
| [0016](0016-deletion-cascades-through-child-workflows.md) | Deletion cascades through child workflows | Accepted |
| [0017](0017-dialogs-load-through-one-service-call.md) | State-entity dialogs load through one service call and one include graph | Accepted |
| [0018](0018-test-strategy.md) | Test strategy | Accepted |
| [0019](0019-coordination-workspace.md) | Coordinators plan from one workspace; staged plans apply as workflow transitions | Superseded by [0026](0026-coordination-workspace-hour-timeline.md) |
| [0020](0020-dates-and-times.md) | Dates and times | Accepted |
| [0021](0021-one-organization-tree-per-tenant.md) | One organization tree per tenant | Accepted |
| [0022](0022-data-model-conventions.md) | Data model conventions | Accepted |
| [0023](0023-one-app-shell-two-menus.md) | One app shell; the session chooses the menu | Superseded by [0025](0025-top-bar-shell-section-menus.md) |
| [0024](0024-visual-language-tokens-and-state-colour.md) | One visual language: measured tokens, a warm floor, state as colour, text chosen for contrast | Accepted |
| [0025](0025-top-bar-shell-section-menus.md) | A top bar with section menus; the session still chooses the menu | Superseded by [0029](0029-left-sidebar-shell.md) |
| [0026](0026-coordination-workspace-hour-timeline.md) | The coordination workspace is a full-viewport tool with an hour timeline; one availability rule judges every surface | Accepted, amended |
| [0027](0027-generic-resource-staffing.md) | Jobs with slots staff any service business; a crew is one slot per person | Accepted, amended |
| [0028](0028-the-product-is-called-staffora.md) | The product is called Staffora | Accepted |
| [0029](0029-left-sidebar-shell.md) | A left sidebar and a top row with the breadcrumb; the session still chooses the menu | Accepted |
| [0030](0030-platform-owner-corrects-a-users-sign-in-details.md) | The platform owner corrects a tenant user's name, address and password from the console | Accepted |
| [0031](0031-dialogs-open-locked-until-edit.md) | A saved record's dialog opens locked until Edit | Accepted |
| [0032](0032-platform-owner-makes-custom-fields-for-a-tenant.md) | Custom fields are the platform owner's, global or for one chosen tenant | Accepted, amended |
| [0033](0033-custom-fields-have-a-lifecycle.md) | Custom fields have a lifecycle: Draft, Active, Inactive, never deleted | Accepted, amended |
| [0034](0034-no-self-service-account-pages.md) | No self-service account pages: nobody changes their own account | Accepted |
| [0035](0035-absence-questions-are-workflow-steps.md) | Absence questions are workflow steps, and their words travel with the notice | Accepted, amended |
| [0036](0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md) | A record's history is for owners, and the workflow inspector is gone | Accepted |
| [0037](0037-request-questions-are-workflow-steps.md) | Request questions are workflow steps on the assignment | Accepted, amended |
| [0038](0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md) | A question from staff is marked handled, and its notices settle for every coordinator | Accepted |
| [0039](0039-opentelemetry-is-the-telemetry-standard.md) | OpenTelemetry is the telemetry standard | Accepted |
| [0040](0040-global-search-reads-each-kind-through-its-view-permission.md) | Global search reads each kind through its view permission, by name, in SQL | Accepted |
| [0041](0041-a-request-expires-by-a-system-step.md) | A request expires by a system step at its answer-by time | Accepted |
| [0042](0042-retention-and-the-tenant-purge.md) | Retention removes rows by age, and a tenant past its grace period is purged and archived | Accepted |
| [0043](0043-a-closed-day-is-a-row-without-a-lifecycle.md) | A closed day is a row without a lifecycle that blocks staffing through the one availability rule | Accepted |
| [0044](0044-taking-off-for-absence-and-open-again.md) | Taking a staff member off for absence is a step, and "open again" is read from its history | Accepted |
| [0045](0045-public-holidays-are-computed-from-a-tenant-setting.md) | Public holidays are computed from a tenant setting, never stored | Accepted |
| [0046](0046-the-database-reads-committed-rows-by-row-versioning.md) | The database reads committed rows by row versioning (READ_COMMITTED_SNAPSHOT) | Accepted |

## Format

Every record has the sections Status, Context, Decision, Rationale, Consequences and Related Decisions. The Status is Accepted, Superseded by a named record, or Accepted with the records that amend it. A new record takes the next free number.
