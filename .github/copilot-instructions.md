# Project Instructions

## Overview

**Read [PRODUCT.md](../PRODUCT.md) first**: it defines what the product is. When a request or a change conflicts with it, stop and ask the user before proceeding.

Staffora is a multi-tenant staffing SaaS for service businesses: each tenant is an independent company that plans jobs for its customers and assigns its own staff to them ([ADR 0027](../docs/adr/0027-generic-resource-staffing.md)); a platform owner operates all tenants. Code and UI use the same domain terms: `Job` (a job), `JobSlot` (a slot), `SlotAssignment` (an assignment), `Order` (an order) and `Resource` (a staff member). Blazor Server, .NET 10, EF Core on SQL Server, Radzen UI. Single database, shared schema, tenant isolation through global query filters.

The architecture overview is [docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md). Known deviations from the rules below are listed in [docs/known-issues.md](../docs/known-issues.md); read it before "fixing" something that looks wrong.

## Solution structure

| Project | Layer | Purpose |
|---------|-------|---------|
| `Staffora.Core` | Domain | Entities, enums, base classes, workflow engine (`Workflow/`, `Annotations/`), execution context (`Execution/`), `Result` / `Result<T>` and DTOs (`Models/`) |
| `Staffora.Data` | Infrastructure | EF Core `ApplicationDbContext`, `ApplicationDbContextFactory`, configurations, migrations, tenant isolation, seeding |
| `Staffora.Services` | Application | Domain services, one folder per area (`Tenancy/`, `CustomFields/`, `Jobs/`, `Coordination/`, `Customers/`, `Portal/`, `Resources/`, `Scheduling/`, `Settings/`, `Workflow/` and others), authorization (`Authorization/`: `PermissionService`, `UserSessionService`, `AppRoles`) |
| `Staffora.Blazor` | Web | Pages, components, layouts (`Layout/`), helpers (`Helper/`) |

## Commands

- **Build:** `dotnet build Staffora.slnx`
- **Run:** `dotnet run --project Staffora.Blazor`. Startup applies migrations and seeds the database; in Development it also seeds the platform owner and builds the demo data through the services (credentials: [docs/features/demo-data.md](../docs/features/demo-data.md#demo-data)).
- **First platform owner outside Development:** `dotnet run --project Staffora.Blazor -- bootstrap-platform-owner <email>`. Runs once, prints the initial password to the console only, and refuses when a platform owner exists ([docs/authentication.md](../docs/authentication.md#first-platform-owner)).
- **Platform owner recovery:** `dotnet run --project Staffora.Blazor -- unlock-user <email>` lifts a lockout and keeps the password. `dotnet run --project Staffora.Blazor -- reset-platform-owner <email>` gives a platform owner a new password, printed to the console once, ends their earlier sessions and clears Identity tokens and lockout; it refuses anyone who is not a platform owner. Both exit 0 when done, 1 on a refusal or failure (one line, no stack trace) and 2 on a malformed address ([docs/authentication.md](../docs/authentication.md#a-platform-owner-who-lost-their-password)).
- **Add migration:** `dotnet ef migrations add <Name> --project Staffora.Data`
- **Update database:** `dotnet ef database update --project Staffora.Data`
- `dotnet ef` builds only Staffora.Data and creates the context through its design-time factory: the local development database (`stafforadb` on LocalDB) by default, `ConnectionStrings__DefaultConnection` when set, or `-- --connection "<connection string>"` appended to the command.
- **Tests:** `dotnet test --project Staffora.Test` runs everything on Microsoft.Testing.Platform (`global.json`). The fast loop is `dotnet test --project Staffora.Test --filter-not-trait "Category=Integration" --filter-not-trait "Category=E2E"` ([ADR 0018](../docs/adr/0018-test-strategy.md)).
- **Database tests:** `dotnet test --project Staffora.Test --filter-trait "Category=Integration"` runs against LocalDB by default. `STAFFORA_INTEGRATION_CONNECTION` points them at another SQL Server; every database they use must have "test" as a separate word in its name ([Staffora.Test/Integration/README.md](../Staffora.Test/Integration/README.md)).
- **Browser journeys:** require `STAFFORA_E2E_CONNECTION` pointing at a database whose name contains "e2e" (each of the parallel workers, `STAFFORA_E2E_WORKERS`, default 4, gets a database `<name>.w<n>` and an app of its own) and the integration pool's SQL Server, from which the browser host of the state-entity dialogs leases its database ([Staffora.Test/E2E/README.md](../Staffora.Test/E2E/README.md)). Per change: `npm run test:e2e:smoke` and `npm run test:e2e:affected -- <changed paths>`. One area: `npm run test:e2e:area -- <tag>` (`@shell`, `@dialogs`, `@staffing`, `@coordination`, `@portal`, `@platform`, `@homes`, `@admin`, `@lists`, `@a11y`, `@visual`). Everything, including the accessibility sweeps and visual baselines: `npm run test:e2e`, at merge. Screenshots of UI changes go to `output/playwright/<area>/` (the full run, or `--shots`).

## Architecture rules

- RULE: The contracts are interfaces: tenant-owned rows implement `ITenantEntity`, entities with a lifecycle implement `IStateEntity<TState>`, usually through `TenantEntityBase`, `TenantStateEntityBase<TState>` or the tenant-free `StateEntityBase<TState>` (`Tenant` and custom field definitions). `ApplicationUser` derives from `IdentityUser<Guid>` and implements the interfaces directly. Keys are `Guid` everywhere.
- RULE: Data model conventions follow [ADR 0022](../docs/adr/0022-data-model-conventions.md): audit columns `Created`/`CreatedById`, `Modified`/`ModifiedById` and `ImpersonatedById` stamped in `SaveChanges`, a `TenantId` index and `NoAction` foreign keys on every tenant row, explicit enum values, a maximum length on every string, additive migrations. Each tenant has one organization tree under a `Company` root; roles are held on a unit and grant permissions tenant-wide ([ADR 0021](../docs/adr/0021-one-organization-tree-per-tenant.md)).
- RULE: Dates and times follow [ADR 0020](../docs/adr/0020-dates-and-times.md): UTC instants, `DateOnly` for calendar days, `TimeOnly` for times of day, the tenant's time zone for conversions, and the current time only from an injected `TimeProvider` (`TimeSourceScanTests`).
- RULE: Tenant isolation is enforced in the data context ([ADR 0013](../docs/adr/0013-tenant-isolation-in-the-data-context.md)): a query filter on `TenantId` that fails closed, and write checks in `SaveChanges`. Read the `tenant-isolation` skill before touching tenant data, queries or the tenant boundary. The rules are in [docs/tenant-isolation.md](../docs/tenant-isolation.md).
- RULE: `IRuntimeExecutionContext` is the single source of truth for tenant, user and impersonation state per Blazor circuit. Services inject it. `UserSessionService` is the only class that reads claims to populate it, and `ImpersonationService` the only other, to re-issue the cookie behind the impersonation endpoints. Hosted services without a circuit (`JobClock`, `IntegrationOutboxDispatcher`, `Retention`) act for a tenant only through `SystemAccountSession`, which gives a fresh runtime in a service scope of its own that tenant's system-account session (the `tenant-isolation` skill, "Acting for a tenant with no circuit").
- RULE: Create contexts with the singleton `IApplicationDbContextFactory<ApplicationDbContext>` and dispose each with `using` or `await using`: `CreateDbContext(runtime)` in services, `CreateSystemDbContext()` (no runtime and no scope until a lease) only in allow-listed code. Cross the tenant boundary only through a lease with a reason: in services the platform lease `context.BeginPlatformScope(runtime, reason)`, which opens only with platform reach (`PlatformScope.HasPlatformReach`), elsewhere `BeginSystemScope(reason)`. A directly injected `ApplicationDbContext` is for Identity's stores only, and components never touch a context (`DataContextScanTests`).
- RULE: Session state (impersonation) lives on the auth cookie, set only by the POST endpoints that re-issue it, and is re-authorized on every circuit ([ADR 0014](../docs/adr/0014-session-on-the-auth-cookie-audited-impersonation.md)); never keep it in browser storage (`SessionStateScanTests`). Pages open by a session policy (`SessionPolicies`): the platform console only to a platform owner who is not impersonating, tenant pages to everyone else. There are no self-service account pages ([ADR 0034](../docs/adr/0034-no-self-service-account-pages.md)).
- RULE: Service methods return `Result` or `Result<T>`. Callers check `IsSuccess`, then read `Value`, `Error` or `Message`.
- RULE: A tenant catalogue that pages and dialogs read repeatedly (custom field definitions, tenant settings, the qualification catalogue) is read through `TenantLookupCache` by the service that owns it. The service checks the permission and the tenant the caller may read on every call before it asks the cache, the catalogue's load names its tenant, its rows are immutable, and every save invalidates the entries it touched once it has returned. Writes and their checks read the database ([docs/features/custom-fields-and-settings.md](../docs/features/custom-fields-and-settings.md#lookup-cache)).
- RULE: A tenant setting is declared in `TenantSettingDefinitions` and written only by `TenantSettingsService`; a missing row means its default. Conversions to a tenant's local time go through `TenantClock` ([docs/features/custom-fields-and-settings.md](../docs/features/custom-fields-and-settings.md#tenant-settings)).
- RULE: Permissions are `RolePermission` values grouped by `[PermissionArea]` and checked through `PermissionService` (`ServicePermissionScanTests`). Gate UI with the `PermissionView` component ([docs/authorization.md](../docs/authorization.md)).
- RULE: The platform owner holds `AppRoles.Admin` and belongs to the platform tenant ([ADR 0012](../docs/adr/0012-platform-owner-and-tenant-onboarding.md)). It manages tenants from the platform console and acts inside a tenant only by impersonating one of its users. Tenant roles use `TenantRole` / `TenantRolePermission`.
- RULE: Users are created by a holder of `RolePermission.UsersCreate` in their own tenant, through the user workflow's create step (`UserService.CreateApplicationUserAsync` from the user dialog, `CreateUserAsync` with a chosen password); the platform owner gives a new tenant its first system owner through tenant onboarding. There is no self-registration and no self-service account management: only a system owner changes their own name, address, user name or password, and one's own profile (`/me/profile`) is read-only ([ADR 0034](../docs/adr/0034-no-self-service-account-pages.md)). Identity's `UserManager.CreateAsync` is refused from any signed-in session and called only by the setup paths (`UserCreationScanTests`). A user is a state entity: deactivated and reactivated through its workflow, never deleted. Credentials, lifecycle steps, role holdings and role permissions stay within the grant ceiling ([docs/authorization.md](../docs/authorization.md)).
- RULE: Set `.OnDelete(DeleteBehavior.NoAction)` explicitly on every relationship. Use `Cascade` only for owned rows without a lifecycle; child state entities are deleted through their workflows ([ADR 0016](../docs/adr/0016-deletion-cascades-through-child-workflows.md)).
- RULE: Dialogs load through one service call that returns a `DetachedEntityUpdater<T>` ([ADR 0017](../docs/adr/0017-dialogs-load-through-one-service-call.md)) and save edits through it, never by sending a whole entity ([ADR 0015](../docs/adr/0015-dialogs-save-through-a-detached-entity-updater.md)).
- RULE: Reuse an existing pattern when one exists; introduce a new one only when nothing fits.

## Key patterns

Each pattern is specified in the linked document; this list names the entry points.

- **Tenant isolation:** query filter on `TenantId`, write checks in `SaveChanges`, system scope as a logged lease. Rules and exceptions: [docs/tenant-isolation.md](../docs/tenant-isolation.md); recipes and tests: the `tenant-isolation` skill.
- **Permission checks:** `PermissionService.HasPermissionAsync(RolePermission)`, or the synchronous `HasPermission` after `LoadPermissionsAsync()`. UI gating through `PermissionView`.
- **Session start:** one session gate component, hosted by both layouts, resolves the session from cookie claims once per circuit and renders the page only when it is ready ([ADR 0014](../docs/adr/0014-session-on-the-auth-cookie-audited-impersonation.md)). Nothing reads browser storage.
- **Lists:** every list page uses `ListView` (`Components/Common/List/`) over a service method that takes a `ListQuery` and returns a `ListPage` filtered, sorted and paged in SQL. One `ListDefinition` per list (`Staffora.Core/Lists/`) declares what it offers; the service normalizes every query with it; the list keeps its query in the address. Rows carry no actions and expand nothing: a row click (or Enter on its name) calls the dialog's static opener, and the list reloads when the dialog closes and returns focus to the row. A list whose rows are pages of their own (the platform console's tenant list) passes `RowLink` instead. A second address-bound list on a page takes an `AddressPrefix`; a list in a dialog binds nothing by default ([docs/features/lists-and-dialogs.md](../docs/features/lists-and-dialogs.md#list-view)).
- **Dialogs:** a state entity opens only in its StateEntityDialog (recipe: the `workflow-state-entities` skill, `DIALOGS.md`). A dialog without a lifecycle (confirmation, error, prompt, form) opens only through `DialogServiceExtensions` (`ConfirmAsync`, `ShowErrorAsync`, `ShowMessageAsync`, `PromptAsync`, `OpenFormDialogAsync`), never through Radzen's `OpenAsync`, `Confirm` or `Alert` (`DialogUsageScanTests`; [docs/features/lists-and-dialogs.md](../docs/features/lists-and-dialogs.md#dialogs)).
- **URL state:** page filters bind through `UrlStateService.Bind` with keys from `UrlStateKeys` (built with the `UrlStateKey` factories), bound in `OnInitialized` and disposed with the page; a page that reloads when a link changes its filters handles the binding's `Changed`. `dlg` is reserved for the dialog stack. Never use `NavigateTo` or `[SupplyParameterFromQuery]` for page state ([docs/features/lists-and-dialogs.md](../docs/features/lists-and-dialogs.md#url-state)).
- **Coordination:** one full-viewport workspace: a header row, a calendar rail, a one-day hour timeline (00:00 to 24:00, horizontally scrollable, with a now line) and the Priorities dock, which lists everything waiting on the coordinator in one list ordered by one urgency rule in the service and opens only the existing dialogs. Every staffing surface uses one availability rule ([ADR 0026](../docs/adr/0026-coordination-workspace-hour-timeline.md); [docs/features/coordination-and-homes.md](../docs/features/coordination-and-homes.md#coordination-workspace)).
- **Across tenants:** a write into another tenant is stamped through `ApplicationDbContext.ActorIn` (`StampOnOtherTenantsBehalf`, inside a lease). Anything a tenant sends a linked tenant after a portal request is queued with `IntegrationOutbox.Enqueue` in the sender's own step and handled by a handler registered per kind (`AddIntegrationHandler`), which the hosted dispatcher runs once, in order per correlation, as the target tenant ([ADR 0007](../docs/adr/0007-cross-tenant-ordering-customer-portal.md); [docs/features/operations.md](../docs/features/operations.md#integration-outbox)).
- **Telemetry:** OpenTelemetry over OTLP, registered only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (`AddTelemetry`). The application's own activities and instruments live only in `StafforaTelemetry` (one `ActivitySource` and one `Meter`, both named `Staffora`); their tags are ids and keys, never a person's name or address ([ADR 0039](../docs/adr/0039-opentelemetry-is-the-telemetry-standard.md); [docs/features/operations.md](../docs/features/operations.md#telemetry)).
- **State entities:** lifecycle, transitions and per-state field access come from the entity's workflow. Read the `workflow-state-entities` skill before adding or changing one.
- **Custom fields:** dynamic fields through generic definition and value base classes, with no schema change per field ([docs/features/custom-fields-and-settings.md](../docs/features/custom-fields-and-settings.md#entity-attributes-dynamic-custom-fields)).
- **Settings:** `/tenant/management/settings`, one panel per category at its own address (General, Custom fields, Services, Closed days), gated on `SettingsView`. A new setting adds its definition and its editor to its category's panel ([docs/features/custom-fields-and-settings.md](../docs/features/custom-fields-and-settings.md#tenant-settings)).

## UI rules

- RULE: Write all code and UI text in English.
- RULE: No text the product shows contains a semicolon; use two sentences, "and" or a colon instead (`UiSemicolonScanTests`).
- RULE: The UI uses the terms job, slot, staff (a staff member), assignment, order, time and period ([ADR 0027](../docs/adr/0027-generic-resource-staffing.md)). No page, notice, display name or workflow step says teacher, course, lesson or term, and no label says need (`UiWordScanTests`).
- RULE: Every state entity is shown only in a StateEntityDialog with its transitions as footer buttons ([ADR 0009](../docs/adr/0009-state-entity-dialog-only-and-workflow-governed-attributes.md)). No page or component outside a dialog's context runs a step or deletes a state entity (`LifecycleCallScanTests`). Entities without a lifecycle get a form dialog of their own.
- RULE: A tenant's pages show that tenant's data only; another tenant's name or identifiers never appear outside the platform console.
- RULE: Every routable page declares the shared `PageHeader` (or gets it from its layout). The shell's top row is the page header: its title is the breadcrumb's last step and the page's one `h1`, with the "?" popover holding the page description beside it. The page's actions, facts and filters stay inside the page, at its top, with actions at the end of the first row and never in the top row. Allow-listed exceptions: the start page, sign-in pages, system pages (`SystemPage` cards inside `MainLayout`), the coordination workspace and the homes, which use `HomeHead` (`PageChromeScanTests`; [docs/features/ui-shell.md](../docs/features/ui-shell.md#page-chrome-and-navigation)). One shell draws the chrome of every layout ([ADR 0029](../docs/adr/0029-left-sidebar-shell.md)).
- RULE: Colours, radii, shadows and type sizes come from the tokens in `app.tokens.css` ([ADR 0024](../docs/adr/0024-visual-language-tokens-and-state-colour.md)): no hex colour in a component stylesheet (`TokenUsageScanTests`), every text pair at 4.5:1 (`TokenContrastTests`), and text on a state-coloured fill chosen by `ColorContrast.OnFill`.
- RULE: Every icon-only button or link carries an `aria-label` (`IconButtonScanTests`). Every form field names its input (a Radzen field's `Component` matches its input's `Name`; drop-downs and check boxes also through `FieldLabel`). The browser journeys and the accessibility sweep fail on a serious or critical axe violation.

## Code style

- MUST: Inject dependencies through an ordinary constructor into `private readonly` fields named in camelCase without an underscore, and qualify every field access with `this.` (`this.userService`). No primary constructors ([ADR 0005](../docs/adr/0005-code-style-and-english-ui.md)). The build holds this (`.editorconfig`, `EnforceCodeStyleInBuild`, warnings as errors), and `CodeStyleScanTests` for primary constructors and Razor code.
- MUST: Keep method parameters on a single line unless that hurts readability.
- MUST: Services depend on `IApplicationDbContextFactory<ApplicationDbContext>` and `IRuntimeExecutionContext`.
- MUST: No code comments in any source file: no `//`, `///`, `/* */`, `@* *@` or `<!-- -->`. Names, small methods and the documentation (`docs/`, ADRs) carry the explanation.
- RULE: Shared build settings live in `Directory.Build.props` and package versions in `Directory.Packages.props` (central package management, one version per package); a project file names no version and repeats no shared property.
- RULE: Entity configurations go in `Staffora.Data/Configurations/`, by domain area.
- RULE: Pages go in `Staffora.Blazor/Pages/`, by audience (`SuperAdmin/`, `TenantAdmin/`, `Staffing/`, `Me/`, `Portal/`). Shared components go in `Staffora.Blazor/Components/`, dialogs in `Components/Common/Dialogs/`, layouts in `Staffora.Blazor/Layout/`.

## Workflow

1. **Analyze existing code** before writing new code: read the related files, services and patterns.
2. **Reuse first.** Extend the existing service, component or helper.
3. **Smallest change.** Keep the diff to the files the task needs.
4. **Verify before deleting.** Check all references before removing any symbol or file.
5. **Match conventions.** Check similar files in the same directory before creating new ones.
6. **Respect the domain.** Add, rename or remove entities only as [PRODUCT.md](../PRODUCT.md) describes, or when the user asks. Current entities are in [docs/database.md](../docs/database.md).
7. **Finish by the Definition of done** below; work is not complete until it holds.

## Definition of done

A change is done only when all of these hold, in this order. Run the commands yourself and report each command with its result; "should pass" is not a result.

1. **Build.** `dotnet build Staffora.slnx` succeeds with no errors and no new warnings. New services are registered in DI. When persistence changed, a migration exists and `dotnet ef migrations has-pending-model-changes --project Staffora.Data` reports none.
2. **Fast tests.** `dotnet test --project Staffora.Test --filter-not-trait "Category=Integration" --filter-not-trait "Category=E2E"` is green.
3. **Database tests**, when the change touches entities, configurations, migrations, services that save, workflow orchestration or tenant isolation: `dotnet test --project Staffora.Test --filter-trait "Category=Integration"` is green.
4. **Journeys**, when the change alters a flow a journey covers or adds a journey: `npm run test:e2e:smoke` and `npm run test:e2e:affected -- <changed paths>` are green (a new spec carries its area tag); the full run, `npm run test:e2e`, is green at merge. For visual changes, screenshots of the changed states are under `output/playwright/<area>/` (`--shots`).
5. **Acceptance.** Every acceptance criterion of the task is proven by a named test (or, for a visual criterion, a screenshot); list them. When the change implements a step of a plan in `docs/plans/`, that step's **Done when** line is the acceptance criterion.
6. **Documentation.** In the same change, update the permanent documentation and the skills whose rules changed, and delete the [docs/known-issues.md](../docs/known-issues.md) entries the change closes.

If a level cannot run (no SQL Server, no browser), say which, report the change as not done, and stop there.

## References

- What the product is: [PRODUCT.md](../PRODUCT.md)
- Architecture overview: [docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md)
- Sign-in, sessions, platform owner recovery: [docs/authentication.md](../docs/authentication.md)
- Roles, permissions, impersonation: [docs/authorization.md](../docs/authorization.md)
- Tenant isolation rules: [docs/tenant-isolation.md](../docs/tenant-isolation.md)
- Entity schemas, relationships, indexes: [docs/database.md](../docs/database.md)
- Features and shared UI components: [docs/features.md](../docs/features.md) (an index of `docs/features/`)
- User and role assignment flow: [docs/features/user-creation-flow.md](../docs/features/user-creation-flow.md)
- Planned, not yet built: [docs/roadmap.md](../docs/roadmap.md)
- Architectural decisions: [docs/adr/](../docs/adr/)
- Code that deviates from these rules: [docs/known-issues.md](../docs/known-issues.md)
- Implementation plans (history of how the product was built): `docs/plans/`
