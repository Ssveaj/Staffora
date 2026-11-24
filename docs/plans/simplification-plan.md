# Code Simplification & Refactoring

A phase that makes `Staffora.Services` simpler to read and maintain without changing what the product does ([PRODUCT.md](../../PRODUCT.md)) or how it is built. It keeps the four projects (Staffora.Blazor, Staffora.Core, Staffora.Data, Staffora.Services) and the `LayeringTests` layer map exactly as they are. It is a refactoring, not an architecture change: every step leaves the product's behaviour, its security checks and its schema as they were, and every test level green.

The plan follows the format the [build plan](build-plan.md) asks of work outside it (Why, Decisions, Out of scope, steps with **Done when**, Open points). The [Definition of done](../../.github/copilot-instructions.md) applies unchanged to every step.

## Why

The build plan delivered the product one feature at a time, and `Staffora.Services` grew with it: 146 files, about 27,300 lines and 34 services. Most services are cohesive and well tested. A first reading still finds work worth doing:

- **Large services with more than one responsibility.** `WorkflowService` (2,314 lines), `TenantService` (1,060: tenants, roles and role holdings), `SlotAssignmentService` (1,023: assignments and the staff conversations), `UserService` (947, with 15 constructor dependencies).
- **Overlap.** Role holdings are listed by three services (by role, by unit and by user) with the same row, the same search, sort and page shape and three copies of the query.
- **Duplicated code.** The delete-step dispatch is copied into 8 services, the name-and-description check into 5, the same `NoTenant` refusal into 19 files, and the read's catch-log-fail filter appears 89 times.
- **Forwarding methods.** Some public methods only check a permission and hand the call to another service that checks the same permission again.
- **Code that only tests call.** Six public service methods have no caller left in the product.

Simplifying this now, together with the code style sweep, means every service is rewritten once.

## Decisions

- **D1. Structure stays.** The projects, their references and the `LayeringTests` map do not change. Code moves only within Staffora.Services, plus the small pure checks named in the steps, which go to Staffora.Core beside its other rules.
- **D2. A service is judged by its responsibility, not its size.** Keep is a valid, good outcome. It is not necessary to remove every small service: a small service with a clear, self-contained responsibility can be entirely correct (`PortalService`, `ErrorLogService`, `SetupChecklistService` are examples).
- **D3. The security surface does not move silently.** A permission check, a platform-reach check, a lease or an allow-list entry moves only together with the test that proves it, and a scan whose allow-list changes is proven to turn red when its rule is broken (a switch-off).
- **D4. The dialog contracts stay.** Each state-entity dialog keeps its id-only opener over its entity's own concrete service (`DialogRouteRegistry`), its one load ([ADR 0017](../adr/0017-dialogs-load-through-one-service-call.md)) and its updater save ([ADR 0015](../adr/0015-dialogs-save-through-a-detached-entity-updater.md)).
  A dialog that needs a child entity's service reaches it through its parent service's property, the pattern `JobService.Slots`, `CustomerService.Contacts` and `UserService.Absences` already use.
- **D5. It runs together with the code style sweep** (see "When it runs"). Every file a step touches follows [ADR 0005](../adr/0005-code-style-and-english-ui.md) when the step ends.

## Out of scope

- A project migration, a new project or a new architecture layer.
- New features, changed behaviour or changed UI text; pages and components change only at the call sites a step moves.
- Schema changes: the migrations stay exactly one `InitialCreate`, and the model has no pending changes.
- Refactoring Staffora.Data, Staffora.Core or Staffora.Blazor for their own sake.
- Performance work, except where a merged query happens to be the faster one.
- Use cases are not part of this phase; one may appear only where there is a specific, clearly motivated need that is agreed first.

## When it runs

After the build plan's remaining feature steps (6.12, 6.13 and 7.1), **together with [build-plan step 7.2](build-plan.md#phase-7-finish)**:

1. S.0, the reviewed inventory, comes first.
2. The refactoring steps S.1 to S.11 follow, in order. Each step rewrites the files it touches in the ADR 0005 style (ordinary constructors, `private readonly` camelCase fields reached through `this.`, no primary constructors), so every simplified service is converted once. Within a step, convert the style first and then refactor, so a reviewer can read the refactoring diff on its own.
3. The 7.2 sweep then converts the files that remain, adds its scan and closes the known issue on the old field style. S.11 hands over to it.
4. Step 7.3 (README) comes last and describes the simplified services.

Each step is green at every level before the next one starts.

## Principles

1. Reduce unnecessary services. Check whether an existing service can be used before creating a new one.
2. Reuse existing functionality. Don't create new classes for things we already solve well.
3. Merge services whose responsibilities overlap or naturally belong together.
4. Split large services only when that actually improves readability, maintenance or testability.
5. Reduce duplicated code, and gather shared logic in suitable places.
6. Avoid unnecessary abstractions, interfaces and intermediate layers.
7. Keep working code when there is no concrete reason to change it.
8. Prioritise clear responsibility, simplicity and a consistent code style over the number of classes and files.

In practice:

- Remove a service or method that only forwards calls, unless it adds UI state, coordination, security or other concrete value. A forward that checks a permission the target does not check is a security check, not a forward.
- Create a new interface only when it keeps a boundary or solves a real problem. The workflow plug-in interfaces in Staffora.Core (`IJobGuard`, `IStepLock`, `IDeleteBlocker`, `IStepFollowUp`, `IStaffAvailability`, `IUserPasswordIssuer` and their peers) are such a boundary: Staffora.Core cannot reference Staffora.Services. They stay.
- Database queries keep their filtering, sorting and paging in SQL (the `ListQuery` / `ListPage` pattern). Only sorting of data already fetched for display may move to the Blazor side.
- One shared implementation for one rule. Where two copies differ, the step names the difference and keeps it or removes it on purpose.
- Fewer classes is not a goal. Do not trade a clear responsibility or a testable unit for a lower count.

## Invariants that hold at every step

- **Tenant isolation** ([ADR 0013](../adr/0013-tenant-isolation-in-the-data-context.md), the [tenant-isolation skill](../../.claude/skills/tenant-isolation/SKILL.md)). The query filter on `TenantId` and the `SaveChanges` write checks are untouched. Contexts come from `IApplicationDbContextFactory<ApplicationDbContext>` and are disposed with `using` or `await using`.
  `BeginPlatformScope`, `BeginSystemScope`, `CreateSystemDbContext`, `IgnoreQueryFilters` and raw SQL stay in allow-listed files only, each lease with its reason.
- **Permission before data.** Every public service method checks its permission (or platform reach) before it touches a context, Identity or another service's data. The UI's `PermissionView` gating is unchanged.
- **Lifecycles only through workflows.** Every state change runs through `WorkflowService` ([ADR 0002](../adr/0002-workflowservice-orchestration.md)); no step mutates `State` or `IsInTransition` or deletes a state entity elsewhere (`LifecycleCallScanTests`).
- **StateEntityDialog only.** State entities are shown and stepped only in their StateEntityDialog ([ADR 0009](../adr/0009-state-entity-dialog-only-and-workflow-governed-attributes.md)), loaded through one call and saved through the updater.
- **Session and impersonation** ([ADR 0014](../adr/0014-session-on-the-auth-cookie-audited-impersonation.md)). `IRuntimeExecutionContext` stays the single source of session state. `UserSessionService` stays the only claims reader, and `ImpersonationService` the only other cookie writer. Every impersonation stays audited.
- **No `UserManager.CreateAsync` from a session** (`UserCreationScanTests`). Users are created only through the user workflow's create step.
- **`Result` returns.** Service methods keep returning `Result` / `Result<T>`.
- **Security scans keep their strength.** `ServicePermissionScanTests`, `DataContextScanTests`, `UserCreationScanTests`, `LifecycleCallScanTests`, `IntegrationHandlerScanTests`, `PlatformConsoleScanTests`, `SessionStateScanTests` and `LayeringTests` stay green.
  Whenever a step changes one of their allow-lists, type lists or paths, the step proves the scan still fails when its rule is broken: a switch-off (break the rule in a scratch copy or a sample, see the scan go red, restore it), reported with the step.
- **Schema unchanged.** `Staffora.Data/Migrations` holds exactly one `InitialCreate`, and `dotnet ef migrations has-pending-model-changes --project Staffora.Data` reports none.

## Baseline

| Project | Files | Lines |
|---|---|---|
| Staffora.Services | 146 `.cs` | 27,285 |
| Staffora.Core | 230 | 18,095 |
| Staffora.Data (without the three migration files) | 82 | 8,406 |
| Staffora.Blazor (`.cs` and `.razor`) | 250 | 30,343 |
| Staffora.Test (`.cs` and `.razor`) | 358 | 83,829 |

- **Services.** 34 classes named `*Service` in Staffora.Services. The largest are `WorkflowService` (2,314 lines), `TenantService` (1,060), `SlotAssignmentService` (1,023), `UserService` (947), `OrganizationService` (825), `AttributeManagementService` (789) and `JobService` (752).
- **Context type.** 88 files in Staffora.Services name the concrete `ApplicationDbContext`. That is expected, because the factory's contexts are of that type, and it is not a target of this phase.
- **Style.** Two counts differed: one found 192 files with `_camelCase` fields and 112 classes with primary constructors, a text search 117 files declaring a `private readonly` underscore field and 140 product files declaring a primary constructor. S.0 fixes one counting rule, the same one 7.2's scan uses.
- **Scan coverage.** `ServicePermissionScanTests` names 18 of the 34 services (see F10).

## Step S.0: the inventory

S.0 turns the first-pass table below into the reviewed final table. Each row has one classification:

- **Keep and reuse.** It stays as it is, apart from the shared checks the steps introduce.
- **Adapt.** Rename it, move it within Staffora.Services, or trim it.
- **Merge** into a named target.
- **Split** into named parts. Only when that clearly improves readability, maintenance or testability.
- **Remove** as redundant, saying what replaces it.

"Keep" rows that need no work are closed by S.0 itself. Every other row is closed by the step named in it.

### Inventory

The first-pass table below is the final S.0 inventory, adopted together with every open point's recommendation. A later step may move a row only when the change is reviewed and recorded on that row.

The callers column counts Blazor files that inject or construct the service (excluding `Program.cs`), then classes in Staffora.Services that inject it.

| Id | Service | Lines | Responsibility | Callers (Blazor / services) | Classification | Reason | Closed by |
|---|---|---|---|---|---|---|---|
| I-01 | `AttributeManagementService` | 789 | Custom field definitions: the settings list, editor, create, change, delete; the fields a dialog renders; the console's read | 2 / 4 | **Adapt**: rename `CustomFieldService` and move with the five `CustomField*` files to `Staffora.Services/CustomFields/` | The product, the docs, the UI and its own tests (`CustomFieldServiceTests`) call them custom fields; its helpers sit in the catch-all `Services/Services/` | S.5 |
| I-02 | `CommentService` | 348 | Entity-agnostic thread read and write, given each audience's access | 0 / 4 | **Keep** | The one shared thread implementation every commented entity reuses | S.0 |
| I-03 | `CoordinationService` | 288 | The coordination workspace's snapshot | 1 / 0 | **Keep** | One read, one responsibility (ADR 0019) | S.0 |
| I-04 | `OrderService` | 596 | A portal's orders: list, lifecycle, threads, editor options | 5 / 0 | **Keep** (adopts the shared checks) | Cohesive; portal area, reviewed | S.9 |
| I-05 | `JobService` | 752 | Jobs: list, needs list, lifecycle, comments, editor options | 7 / 0 | **Adapt**: trim | Its three staff-conversation methods forward to `SlotAssignmentService` (F6); it adopts the shared checks | S.3, S.4 |
| I-06 | `CustomerContactService` | 265 | Customer contacts' lifecycle and editor options | 2 / 1 | **Keep** (adopts the shared checks) | Own entity, own dialog | S.2 |
| I-07 | `CustomerService` | 586 | Customers: list, contacts list, lifecycle, enabling the customer portal | 3 / 0 | **Keep** (adopts the shared checks) | Cohesive; enabling a portal crosses tenants, so it is reviewed with the portal | S.9 |
| I-08 | `ErrorLogService` | 106 | The console's error-log reads under the platform lease | 1 / 0 | **Keep** | Small and self-contained | S.0 |
| I-09 | `ImpersonationService` | 198 | Start, switch and end impersonation on the cookie; eligible targets | 3 / 0 | **Keep** | Security core with its authorizer and audit writer; clear | S.0 |
| I-10 | `NoticeService` | 214 | The bell: one's own notices, unread count, mark read | 1 / 0 | **Keep** | Small and self-contained | S.0 |
| I-11 | `OrganizationService` | 825 | The unit tree, unit lifecycle, a unit's children and role holdings, one's own holdings | 5 / 0 | **Adapt**: trim | Its two holding reads merge into `RoleHoldingService` (F1); `DeleteOrganizationUnitAsync` is test-only (F7); adopts the shared checks | S.7 |
| I-12 | `PatternAssignmentService` | 325 | Work-pattern assignments to staff members | 2 / 1 | **Keep** (adopts the shared checks) | Own entity, own dialog | S.2 |
| I-13 | `PermissionService` | 459 | Permission checks, the cached permission set, edition | 10 / 32 | **Adapt**: trim | Two `HasAnyPermissionAsync` overloads do the same; the token-first `params` one has 3 callers (F9) | S.8 |
| I-14 | `PlanningService` (later removed with planning mode) | 567 | Planning mode: judge, check and apply staged moves | 2 / 0 | **Keep** | Clear coordination value; applies through `SlotAssignmentService` and the workflow | S.0 |
| I-15 | `PlatformConsoleService` | 407 | The console's reads: overview, tenant list, one tenant's pages | 6 / 0 | **Keep** | Its forwards add the reach and tenant-exists checks (security value) | S.0 |
| I-16 | `PortalService` | 114 | The portal home's overview | 1 / 0 | **Keep** | Small and self-contained | S.0 |
| I-17 | `QualificationService` | 600 | The qualification catalogue's lifecycle; giving and taking staff members' qualifications; bulk reads | 5 / 4 | **Keep** (adopts the shared checks) | One catalogue and its holdings; the bulk read is shared by staffing, board and planning | S.2 |
| I-18 | `ResourceAbsenceService` | 549 | Absences: lists, own status, lifecycle, comments, editor options | 7 / 1 | **Keep** (adopts the shared checks) | Own entity, own dialog | S.2 |
| I-19 | `ResourceService` | 214 | The Staff list, a staff member's units, the first candidate query | 1 / 1 | **Adapt**: trim | `FindCandidatesAsync` has no product caller; `StaffingService` ranks the candidates (F7) | S.2 |
| I-20 | `SetupChecklistService` | 163 | The administrator's setup checklist | 1 / 0 | **Keep** | Small and self-contained | S.0 |
| I-21 | `StaffingService` | 291 | The slot dialog's ranked candidates | 0 / 1 | **Keep** | A distinct, tested ranking; reached through `JobSlotService` (F6) | S.3 |
| I-22 | `StateHistoryService` | 261 | A state entity's history page | 0 / 13 | **Keep** | Shared by every dialog; it checks nothing by design, because the entity's service gates it | S.0 |
| I-23 | `SlotAssignmentService` | 1,023 | Assignments: lists, own work, request, assign, cancel, lifecycle; **and** the staff conversations | 7 / 3 | **Split** into `SlotAssignmentService` and `StaffConversationService` | Two responsibilities; about 460 lines of conversation rules (whose turn, seen marks, notices) with two consumers, the job's Staff tab and the assignment's side column | S.4 |
| I-24 | `StaffAvailabilityService` | 358 | The one availability rule over the database (`IStaffAvailability`) | 1 / 1 (+6 via the interface) | **Keep** | The interface is the Core boundary and the tests' fake | S.0 |
| I-25 | `StaffBoardService` | 458 | The staff week board's two reads | 1 / 0 | **Keep** | One read surface (ADR 0019) | S.0 |
| I-26 | `JobSlotService` | 349 | Slots: lifecycle, editor options, the dialog's Staff and Staffing views | 4 / 1 | **Adapt**: trim | Its two view methods forward and check again what the targets check (F6); adopts the shared checks | S.3 |
| I-27 | `TenantOnboardingService` | 259 | Onboards a tenant: the tenant, its first system owner, presets, in one move | 1 / 1 | **Keep** | Real coordination and a security operation, already a single business operation | S.0 |
| I-28 | `TenantService` | 1,060 | Tenants **and** their roles **and** role holdings | 13 / 3 | **Split** into `TenantService` (tenant record), `TenantRoleService` (roles) and `RoleHoldingService` (holdings) | Three responsibilities; `FetchAllTenantsAsync` and `DeleteTenantRoleAsync` are test-only (F7) | S.6 |
| I-29 | `TenantSettingsEnsureService` | 193 | Pins every tenant's time zone at startup and onboarding | 0 / 2 | **Keep** | A singleton in system scope with its own lease; merging it would put a system-scope allow-list entry on a user-facing service | S.0 |
| I-30 | `TenantSettingsService` | 403 | Tenant settings: read, save, console read | 1 / 2 | **Keep** | The only writer of settings, by rule | S.0 |
| I-31 | `UserService` | 947 | Users: lists, create, lifecycle, own name, custom fields, the user dialog's views | 8 / 3 | **Adapt**: trim | Its role-holding view merges into `RoleHoldingService` (F1); its view forwards are reviewed (F6); `FetchTenantUsersAsync` and `FetchUsersByUnitAsync` are test-only (F7). Not split: user lifecycle and credentials stay together | S.8 |
| I-32 | `UserSessionService` | 520 | Resolves the session from cookie claims, once per circuit | 8 / 0 | **Keep** | The only claims reader, by rule | S.0 |
| I-33 | `WorkPatternService` | 306 | Work patterns: list, lifecycle, copy, editor options | 3 / 0 | **Keep** (adopts the shared checks) | Own entity, own dialog | S.2 |
| I-34 | `WorkflowService` | 2,314 | Every transition, create and save; the footer's judgement; the inspector | 3 / 14 | **Split** into partial files of the one class: entry points and `RunAsync`; judgement and inspection; new-record copying and references; locks and cascades; refusals | 70 members in one file. Partial files keep the single entry point ADR 0002 requires, add no public surface and follow the `ApplicationDbContext.*.cs` pattern | S.10 |

**First-pass counts.** Keep 24 (16 close at S.0 with no work; 7 adopt the shared checks in their area's step; `StaffingService` is confirmed with its forward in S.3). Adapt 7. Merge 0 whole services (one overlap is merged: the role-holding reads of three services, F1). Split 3. Remove 0 services (six test-only methods are removed, F7).

**New classes the first pass proposes**, each for a stated responsibility:

- `TenantRoleService` and `RoleHoldingService`, from the `TenantService` split.
- `StaffConversationService`, from the `SlotAssignmentService` split.
- One small pure text check in Staffora.Core, replacing five copies (F3).

No new interface.

### Duplication and pass-through findings (first pass)

| Id | Finding | Where | Resolution | Step |
|---|---|---|---|---|
| F1 | Role-holding lists built three times: same `UserRoleHoldingRow`, same search, sort and page; only the filter differs (by role, by unit, by user). One's own holdings is a fourth view. The writes (assign, remove, choices, may-manage) live in a fourth place | `TenantService.QueryTenantRoleHoldersAsync`, `OrganizationService.QueryOrganizationUnitRoleHoldingsAsync`, `UserService.QueryApplicationUserRoleHoldingsAsync`, `OrganizationService.FetchOwnRoleHoldingsAsync`; writes in `TenantService` | One `RoleHoldingService` with one query and three entry points; the writes move with it. Each entry keeps its own permission and platform-lease behaviour | S.6, S.7, S.8 |
| F2 | Delete-step dispatch (`TryGetTransition` then `IsDeleteState()`, then `TransitionEntityAsync` instead of `SaveAndTransitionAsync`) copied | `JobService`, `JobSlotService`, `CustomerContactService`, `CustomerService`, `PatternAssignmentService`, `WorkPatternService`, `OrganizationService` (its private `IsDeleteStep`), `TenantService` | One `IsDeleteStep(transitionKey)` on the workflow in Staffora.Core, beside `TryGetTransition`. Each service still chooses its call | S.2 (adds), S.3, S.6, S.7, S.9 |
| F3 | Name-and-description check (empty name, name too long, description too long) copied | `CustomerService`, `QualificationService`, `WorkPatternService`, `OrganizationService`, `TenantService` (roles) | One pure check over the entity's own length constants and messages, in Staffora.Core beside the other rules. The entity-specific checks of `JobService`, `JobSlotService`, `CustomerContactService` and `OrderService` stay, since they check different fields | S.2 (adds), then each area's step |
| F4 | Catch filter `when (exception is not OperationCanceledException \|\| !cancellationToken.IsCancellationRequested)`, then log, then a failed `Result`: 89 occurrences. `DeadlockRetry.ReadAsync` already does this for reads in 14 files | All services | Reads a step already rewrites use `DeadlockRetry.ReadAsync` where its words fit; writes keep their own catch, since their words differ. Not a sweep (open point 3) | Areas' steps |
| F5 | `private const string NoTenant = "No tenant is selected."` copied, with the same `runtime.TenantId is not { } tenantId` guard | 19 files | One shared constant, chosen at S.0; the remaining copies are replaced in S.11 | S.2, S.11 |
| F6 | Gate-and-forward methods. The forward checks the dialog's permission and then calls a public method of another service, which checks its own permission again | `JobService.GetStaffConversationsAsync`, `GetStaffConversationAsync`, `AddStaffConversationCommentAsync`; `JobSlotService.QuerySlotAssignmentsAsync`, `QuerySlotCandidatesAsync` (checks `ViewCoordination` twice); `UserService.QueryApplicationUserAbsencesAsync`, `QueryApplicationUserPatternAssignmentsAsync` | Remove the forward only where the target's check is equal to or stricter than the forward's, proven by a permission test; otherwise keep it, with its reason in the pass-through scan's allow-list | S.3, S.4, S.8 |
| F7 | Public methods with no caller in the product, only in tests | `UserService.FetchTenantUsersAsync`, `UserService.FetchUsersByUnitAsync`, `TenantService.FetchAllTenantsAsync`, `TenantService.DeleteTenantRoleAsync`, `OrganizationService.DeleteOrganizationUnitAsync`, `ResourceService.FindCandidatesAsync` | Remove each one. Its tests move to the product path that does the same (the list's `Query…Async`, the delete step through `Update…AndTransitionAsync`, `StaffingService`), and docs that name it are updated | S.2, S.6, S.7, S.8 |
| F8 | Similar private helpers `MayOpenAsync` / `TenantOfAsync` (a reader of the tenant or the platform console) | `UserService`, `OrganizationService` | Justified unless S.0 finds the bodies identical; then one shared helper | S.7, S.8 |
| F9 | Two `HasAnyPermissionAsync` overloads with the same meaning | `PermissionService` | Keep the `IEnumerable` one; move the 3 callers | S.8 |
| F10 | Scan gaps. `ServicePermissionScanTests` names 18 of 34 services; tenant-data services outside it: `SlotAssignmentService`, `StaffingService`, `PlanningService`, `StaffBoardService`, `ResourceService`, `PortalService`, `OrderService`, `SetupChecklistService`. It also finds a service's source by namespace and class name (`Staffora.Services/<namespace>/<Name>.cs`), so a moved file or a partial file breaks it (loudly, but it breaks) | `Staffora.Test/Unit/Architecture/ServicePermissionScanTests.cs` | Extend the type list and make the source lookup read every file of a type, before any service moves | S.1 |

Also checked and kept:

- The `Get{Entity}HistoryAsync` / `Inspect{Entity}Async` pairs in 13 services are gate-and-forward too. Their targets (`StateHistoryService`, `WorkflowService.InspectTransitionsAsync`) check no read permission by design, so the forward is the read gate. They stay as the dialog contract.
  - **Note:** the `Inspect{Entity}Async` half of these pairs is gone with the workflow inspector ([ADR 0036](../adr/0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md)); the `Get{Entity}HistoryAsync` forwards stay, and `StateHistoryService.GetHistoryAsync` now itself refuses everyone but a system owner of the record's organization or a platform owner on the console.
- The `{Entity}WorkflowQueries` files (one per entity, about 16 lines each) are each dialog's include graph (ADR 0017). They stay.
- The workflow plug-ins (guards, step locks, delete blockers, follow-ups) implement Staffora.Core's interfaces. They stay.

## Steps

Each step is one implementation run, ends with the Definition of done (build, fast tests, database tests, journeys, Done-when proofs, bookkeeping) and leaves every level green. Every step's Done-when includes that the files it touches follow ADR 0005, and that no test was deleted without a named test that proves its behaviour elsewhere. Steps marked **reviewed** get an independent review (a fresh reviewer given the diff and the invariants, not this plan's conclusions) before they are ticked.

- [x] **S.0 Final inventory.** Re-measure the baseline with one counting rule, the same one 7.2's scan uses. Confirm or correct every row and finding above: callers by constructor dependency, method bodies of F6 and F8 compared, the test-only methods of F7 checked against tests and docs. Present the table for review. *Size: S.*
  - **Done when** the table is reviewed and this section holds the final version (the draft label removed), and the "Keep" rows with no work are closed with their outcome.
  - **Built:** The inventory above is the final S.0 table.

- [x] **S.1 Safety net first.**
  - `ServicePermissionScanTests` reads every source file that declares a service type, wherever it sits and whether it is partial, and adds the tenant-data services of F10, each new exception allow-listed with its reason.
  - A new fast scan, `ServicePassThroughScanTests`, flags a public method of a Staffora.Services service whose body (comments removed) is a single call on an injected service with no permission or reach check before it, or a permission gate followed by a single call to a public method of a service that `ServicePermissionScanTests` already holds to its own check (a double gate). Each current hit is allow-listed with its reason, and the allow-list must match exactly, as the permission scan's does.
  - **Done when** both scans are green; a sample of an ungated forward, a double gate and a method in a second partial file each turn them red (switch-offs); the files it touches follow ADR 0005.
  - **Rows closed:** none (it closes F10 and is the prerequisite of every later step). *Size: M.*
  - **Built:** F10 closed. Both scans read a service's source through `ServiceSources` by type: every file of the service's project that declares it, wherever it sits and whether it is partial.
  - **Verified by:** `ServicePermissionScanTests`, `ServicePassThroughScanTests`.

- [x] **S.2 Shared checks; Scheduling, Resources and contacts.**
  - Add `IsDeleteStep` on the workflow (F2), the pure name-and-description check (F3) and the shared `NoTenant` refusal (F5), each with unit tests.
  - Adopt them in `WorkPatternService`, `PatternAssignmentService`, `ResourceAbsenceService`, `QualificationService` and `CustomerContactService`.
  - Remove `ResourceService.FindCandidatesAsync` (F7), moving `CandidateQueryTests` and `StaffLinkTests` coverage to `CandidateRulesTests` and `StaffingTests`.
  - **Done when** the lifecycle tests of those entities (`WorkPatternLifecycleTests`, `PatternAssignmentLifecycleTests`, `AbsenceLifecycleTests`, `QualificationTests`, `CustomerLifecycleTests`) pass unchanged; the copies of F2, F3 and F5 are gone from the touched files; `docs/authorization.md` no longer names the removed method; the files it touches follow ADR 0005.
  - **Rows closed:** I-06, I-12, I-17, I-18, I-19, I-33. *Size: M.*
  - **Built:** Rows I-06, I-12, I-17, I-18, I-19 and I-33 closed; each service kept its responsibility and adopted the shared checks `Workflow.IsDeleteStep` (F2), `NameAndDescriptionRule` (F3) and `IRuntimeExecutionContext.NoTenant` (F5). `ResourceService.FindCandidatesAsync` (F7) is removed and its tests moved to the product's paths.
  - **Verified by:** `CandidateQueryTests`, `StaffLinkTests`, `CandidateRulesTests`, `StaffingTests`, `WorkPatternLifecycleTests`, `PatternAssignmentLifecycleTests`, `AbsenceLifecycleTests`, `QualificationTests`, `CustomerLifecycleTests`, `StaffReadTests`, `StaffBoardTests`, `TransitionTests`, `NameAndDescriptionRuleTests`, `SharedCheckScanTests`.

- [x] **S.3 Jobs and slots.**
  - `JobService` and `JobSlotService` adopt the shared checks.
  - The forwards of F6 in `JobSlotService` are resolved. `QuerySlotCandidatesAsync` is removed when the slot dialog reaches `StaffingService` through the existing service-property pattern and `StaffingService`'s own `ViewCoordination` check is the same; `QuerySlotAssignmentsAsync` is kept or removed by the same rule.
  - **Done when** `JobLifecycleTests`, `StaffingTests`, `AssignmentLifecycleTests` and the slot dialog's Web tests pass; a permission test proves a reader without `ViewCoordination` still reads no candidate; the pass-through allow-list shrank by the forwards removed; the files it touches follow ADR 0005.
  - **Rows closed:** I-21, I-26 (I-05 in part). *Size: M.*
  - **Built:** Rows I-21 (`StaffingService`, kept unchanged and now reached directly) and I-26 (`JobSlotService`) closed, I-05 (`JobService`) in part. Both forwards of F6 in `JobSlotService` are removed.
  - **Verified by:** `JobLifecycleTests`, `StaffingTests`, `AssignmentLifecycleTests`, `DialogArchitectureScanTests`, `StaffingDialogsTests`, `AssignmentDialogsTests`, `JobDialogsTests`, `CoordinationPageTests`, `StaffBoardPageTests`, `StaffConversationDialogsTests`.

- [x] **S.4 Staff conversations out of the assignment service.**
  - Move the conversation half of `SlotAssignmentService` (job conversations, candidate conversation, the assignment's threads, post effects, seen marks) into `StaffConversationService` (`Staffora.Services/Jobs/`).
  - `JobService` depends on it instead of the whole assignment service, and its three forwards (F6) call it or are removed by S.3's rule.
  - The assignment dialog reaches it through `SlotAssignmentService.Conversations`.
  - **Done when** `StaffConversationTests`, `CommentThreadTests`, `OwnWorkTests` and the job and assignment dialogs' Web tests pass; `StaffConversationService` is in the permission scan's type list; the job-staffed journey is green; the files it touches follow ADR 0005.
  - **Rows closed:** I-05, I-23. *Size: M.*
  - **Built:** Rows I-05 and I-23 closed: the conversation half of `SlotAssignmentService` moved unchanged into `StaffConversationService` (`Staffora.Services/Jobs/`), and F6's last forwards on `JobService` are gone.
  - **Verified by:** `StaffConversationTests`, `CommentThreadTests`, `OwnWorkTests`, `ServicePermissionScanTests`, `SharedCheckScanTests`, `DataContextScanTests`, `AssignmentLifecycleTests`, `AssignmentReviewTests`, `JobCustomFieldTests`, `PlanningTests`, `StaffConversationDialogsTests`, `JobDialogsTests`, `AssignmentDialogsTests`, `StaffingDialogsTests`.

- [x] **S.5 Custom fields under their own name.**
  - Rename `AttributeManagementService` to `CustomFieldService` and move it with `CustomFieldValues`, `CustomFieldEdit`, `CustomFieldTargets`, `CustomFieldUsage` and `CustomFieldDefinitionRow` to `Staffora.Services/CustomFields/`.
  - Update the permission scan's lists, the console-read list and the `DataContextScanTests` path entry for its platform lease.
  - **Done when** `CustomFieldServiceTests`, `CustomFieldValueTests`, `JobCustomFieldTests` and the manages-custom-fields journey pass; a switch-off of the moved lease entry and of the console-read check turns their scans red; `docs/features.md` and the workflow skill's `ATTRIBUTES.md` name the new service; the files it touches follow ADR 0005.
  - **Rows closed:** I-01. *Size: M.*
  - **Built:** Row I-01 closed: `AttributeManagementService` is `CustomFieldService` in `Staffora.Services/CustomFields/`, with every method, check and text unchanged.
  - **Verified by:** `DataContextScanTests`, `CustomFieldServiceTests`, `CustomFieldValueTests`, `JobCustomFieldTests`, `SharedCheckScanTests`, `LookupCacheTests`, `PlatformConsoleTests`, `ServicePermissionTests`, `TenantLifecycleAccessTests`, `CustomFieldWorkflowTests`, `CustomFieldDialogsTests`, `CustomFieldsPanelTests`, `LookupCacheDialogTests`, `SettingsPageTests`, `TenantConsolePageTests`, `ServicePermissionScanTests`.

- [x] **S.6 Tenants, roles and holdings apart (reviewed).**
  - Split `TenantService` into `TenantService` (the tenant record: own tenant, the console's tenant dialog, history, inspection), `TenantRoleService` (roles: list, lifecycle, editor options, role holders, grantable permissions) and `RoleHoldingService` (assign, remove, by-unit list, choices, may-manage), all in `Staffora.Services/Tenancy/`.
  - Remove `FetchAllTenantsAsync` and `DeleteTenantRoleAsync` (F7); their tests move to `PlatformConsoleService.QueryTenantsAsync` and the role's delete step.
  - `ResourceLinker` and the step locks call `RoleHoldingService`.
  - Role dialog and pages follow D4.
  - **Done when** `RoleCeilingTests`, `TenantRoleLifecycleTests`, `TenantLifecycleAccessTests`, `SystemOwnerTests`, `PlatformReachTests`, `PlatformConsoleTests`, `TenantOnboardingTests` and `StaffLinkTests` pass; the grant ceiling is proven unchanged by a test that a role holder cannot grant a permission they lack; the scans' lists and the `DataContextScanTests` lease entries are updated with switch-offs; the onboarding and platform-owner journeys are green; the review found no loosened gate;
    the files it touches follow ADR 0005.
  - **Rows closed:** I-28. *Size: L.*
  - **Built:** Row I-28 closed: `TenantService` became `TenantService`, `TenantRoleService` and `RoleHoldingService` in `Staffora.Services/Tenancy/`, every member moved unchanged; F7's two `TenantService` methods are removed. The independent review of S.6 to S.9 found no loosened gate.
  - **Verified by:** `RoleCeilingTests`, `TenantRoleLifecycleTests`, `TenantLifecycleAccessTests`, `SystemOwnerTests`, `PlatformReachTests`, `PlatformConsoleTests`, `TenantOnboardingTests`, `StaffLinkTests`, `DataContextScanTests`, `ServicePermissionTests`, `DemoDataTests`, `TenantReferenceTests`, `ServicePermissionScanTests`, `SharedCheckScanTests`, `LifecycleCallScanTests`, `TenantCreateTests`.

- [x] **S.7 One role-holding query; organization trimmed (reviewed).**
  - Merge `OrganizationService.QueryOrganizationUnitRoleHoldingsAsync` and `FetchOwnRoleHoldingsAsync`, and `TenantRoleService`'s role-holders list, into `RoleHoldingService` over one shared query (F1).
  - `OrganizationService` adopts the shared checks.
  - Remove `DeleteOrganizationUnitAsync` (F7), its tests moved to the unit's delete step.
  - F8 is resolved for `OrganizationService`.
  - **Done when** `OrganizationStructureTests`, `OrganizationTreeTests`, `OrganizationUnitLifecycleTests`, `ServicePermissionTests` and the unit and role dialogs' Web tests pass; one query builds every holding list; the platform lease for the console's holding reads stays (scan green, switch-off shown); `docs/authorization.md` and `docs/features.md` updated; the files it touches follow ADR 0005.
  - **Rows closed:** I-11. *Size: M.*
  - **Built:** Row I-11 closed and F1 merged: the role's Holders, the unit's Role holdings and the user's Roles views list `UserRoleHoldingRow` from one query in `RoleHoldingService`; `OrganizationService` adopts the shared checks and `DeleteOrganizationUnitAsync` (F7) is removed.
  - **Verified by:** `OrganizationStructureTests`, `OrganizationTreeTests`, `OrganizationUnitLifecycleTests`, `ServicePermissionTests`, `ServicePassThroughScanTests`, `RoleHoldingListTests`, `OrganizationDialogTests`, `UserDialogTests`, `StaffDialogsTests`, `DataContextScanTests`.

- [x] **S.8 Users and permissions trimmed (reviewed).**
  - `UserService`'s role-holding view moves to `RoleHoldingService` (done ahead in S.7, so one query builds every holding list).
  - Its F6 forwards are resolved by S.3's rule; the two internal reads (`QualificationsOfAsync`, `UnitsOfAsync`) keep `UserService`'s gate, since their targets check nothing.
  - Remove `FetchTenantUsersAsync` and `FetchUsersByUnitAsync` (F7), their tests moved to `QueryUsersAsync` and the console's users list.
  - F8 is resolved.
  - `PermissionService` keeps one `HasAnyPermissionAsync` overload (F9).
  - Credentials and the create step are not restructured.
  - **Done when** `UserLifecycleTests`, `UserAccountProtectionTests`, `SignInNameTests`, `ServicePermissionTests`, `PlatformReachTests`, `SystemAccountAccessTests` and the user dialog's Web tests pass; `UserCreationScanTests` is green with a switch-off; `UserService`'s constructor dependencies are reported before and after; the review found no loosened gate; the files it touches follow ADR 0005.
  - **Rows closed:** I-13, I-31. *Size: M.*
  - **Built:** Rows I-13 (`PermissionService`) and I-31 (`UserService`) closed; F6's last two forwards, F7's two `UserService` methods, F8 and F9 resolved. The review's one low finding was fixed in `DialogArchitectureScanTests`, with no product change.
- **Verified by:** `UserLifecycleTests`, `UserAccountProtectionTests`, `SignInNameTests`, `ServicePermissionTests`, `PlatformReachTests`, `SystemAccountAccessTests`, `UserCreationScanTests`, `DataContextScanTests`, `OrganizationTreeTests`, `ServicePassThroughScanTests`, `SharedCheckScanTests`, `ServicePermissionScanTests`, `AbsenceLifecycleTests`, `PatternAssignmentLifecycleTests`, `PortalEditionTests`, `QualificationTests`, `UserDialogTests`, `StaffDialogsTests`, `ListPagesTests`, `AbsenceDialogsTests`, `ScheduleDialogsTests`, `DialogArchitectureScanTests`.

- [x] **S.9 Portal and cross-tenant paths (reviewed).** `CustomerService` and `OrderService` adopt the shared checks; nothing else in `EnableCustomerPortalAsync`, `OrderSender`, the integration handlers or `TenantOnboardingService` changes.
  - **Done when** `CustomerPortalTests`, `OrderTests`, `OrderSendTests`, `OrderSyncTests`, `PortalEditionTests`, `PortalUserTests` and `IntegrationOutboxTests` pass; `IntegrationHandlerScanTests` and the `DataContextScanTests` system-scope entries are unchanged and green; the portal round-trip journey is green; the review confirms that the tenant promise's only link (ADR 0007) is untouched; the files it touches follow ADR 0005.
  - **Rows closed:** I-04, I-07. *Size: M.*
  - **Built:** Rows I-04 (`OrderService`) and I-07 (`CustomerService`) closed; F2 and F3 have no copy left, and `EnableCustomerPortalAsync`, the order send and the integration handlers are unchanged member for member.
  - **Verified by:** `CustomerPortalTests`, `OrderTests`, `OrderSendTests`, `OrderSyncTests`, `PortalEditionTests`, `PortalUserTests`, `IntegrationOutboxTests`, `IntegrationHandlerScanTests`, `DataContextScanTests`, `SharedCheckScanTests`, `CustomerLifecycleTests`, `NameAndDescriptionRuleTests`, `UserCreationScanTests`, `ServicePassThroughScanTests`, `ServicePermissionScanTests`.

- [x] **S.10 WorkflowService in partial files (reviewed).**
  - Move `WorkflowService`'s members, unchanged, into partial files of the one class: `WorkflowService.cs` (constructor, public entry points, `RunAsync`), `WorkflowService.Judgement.cs` (access, conditions, available transitions, inspection, graph paths), `WorkflowService.NewRecords.cs` (copy for insert, carried state entities, references), `WorkflowService.Locks.cs` (begin locked, locks, cascades) and `WorkflowService.Refusals.cs` (refusal texts and exception mapping).
  - The `IsInTransition` original-value sync and its comment stay together with ADR 0002's text.
  - `DataContextScanTests`' path entry follows the lease to its file.
  - **Done when** a script shows every member body is identical before and after (move only); `WorkflowServiceTests` (all parts), `WorkflowDeleteTests`, `WorkflowInspectionTests`, `SystemStepTests`, `AssignmentConcurrencyTests`, `TenantLockOrderTests` and `LifecycleCallScanTests` pass; the lease entry's switch-off turns its scan red; every journey is green; the review found no change in behaviour; the files it touches follow ADR 0005.
  - **Rows closed:** I-34. *Size: L.*
  - **Built:** I-34 closed: `WorkflowService` is one `public sealed partial class` in five files of `Staffora.Services/Workflow/` (`WorkflowService.cs`, `.Judgement.cs`, `.NewRecords.cs`, `.Locks.cs`, `.Refusals.cs`), every member body unchanged.
  - **Verified by:** `DataContextScanTests`, `WorkflowServiceTests`, `WorkflowDeleteTests`, `WorkflowInspectionTests`, `SystemStepTests`, `AssignmentConcurrencyTests`, `TenantLockOrderTests`, `LifecycleCallScanTests`, `SystemStepProblemTests`.

- [x] **S.11 Close-out and hand-over to 7.2.**
  - Replace the remaining `NoTenant` copies (F5).
  - Record the final table and the findings' outcomes in this plan.
  - Report the counts below.
  - Update `.github/copilot-instructions.md` (the Staffora.Services folders) and any doc or skill that names a moved service.
  - List the files not yet in the ADR 0005 style for [step 7.2](build-plan.md#phase-7-finish), whose scan then holds the rule.
  - **Done when** every inventory row and finding is closed with its outcome; `ServicePassThroughScanTests` holds only reasoned entries; a search for each removed or renamed service and method name in `docs/`, `.claude/skills/` and `.github/` finds none; the files it touches follow ADR 0005; 7.2 can start with its list.
  - *Size: S.*
  - **Built:** The phase is closed: the remaining Staffora.Services files follow the ADR 0005 style, the last `NoTenant` copies (F5) are replaced, the stale names in docs and skills are gone, and the hand-over to build-plan step 7.2 is complete.
  - **Verified by:** `CandidateRulesTests`, `ServicePassThroughScanTests`, `SharedCheckScanTests`, `StaffReadTests`, `CandidateQueryTests`, `StaffLinkTests`, `StaffingTests`, `StaffingRulesTests`, `CodeStyleScanTests`, `ServicePermissionScanTests`, `DataContextScanTests`.

**Estimate.** S = about half an hour of agent run time, M = about an hour, L = one and a half to two hours, each including the style conversion of the touched files and one run of every test level.

| Step | Size | Hours |
|---|---|---|
| S.0 | S | 0.5 |
| S.1 | M | 1 |
| S.2 | M | 1 |
| S.3 | M | 1 |
| S.4 | M | 1 |
| S.5 | M | 1 |
| S.6 | L | 1.5 |
| S.7 | M | 1 |
| S.8 | M | 1 |
| S.9 | M | 1 |
| S.10 | L | 2 |
| S.11 | S | 0.5 |
| Five independent reviews (S.6 to S.10) | | about 1.5 |
| **Total** | | **about 14 hours** |

That is about 12 implementation runs and 5 review runs, about 13 to 15 hours in all. Step 7.2 shrinks by the files these steps convert.

## Quality goal: simplify, not just tidy

The phase is done when these hold, and S.11 reports each one:

1. **Every inventory row closed**, with its outcome (kept, adapted, merged, split, removed) and the step that closed it. Keep counts as a good outcome.
2. **Every finding resolved or justified**: for each of F1 to F10, the copies or forwards before and after, or the reason it stays.
3. **No pass-through left without a reason.** `ServicePassThroughScanTests` is green with an allow-list whose every entry names the value the forward adds (a gate the target lacks, coordination, UI state). Reviewers also check each step against this list:
   - Does the method do anything but call one other service?
   - Does its check differ from the target's?
   - Would the caller lose a permission check, a lease or a refusal text if it called the target directly?
   - Is the class still needed once the method is gone?
4. **Overlaps merged.** The role-holding lists come from one query; any further overlap S.0 finds is merged or justified.
5. **Duplication removed**, counted per finding (for example, delete-step dispatch 8 copies before, 1 shared method after).
6. **No new interface** without a stated boundary or problem. The first pass proposes none.
7. **No test deleted** without its behaviour proven elsewhere, named in the step's report.
8. **Security scans as strong as before**, each allow-list change proven by a switch-off.
9. **Information, not targets.** Reported before and after:
   - the number of services, classes and files in Staffora.Services, and its lines;
   - the lines and constructor dependencies of the largest services;
   - the pass-through allow-list's size.

   A higher class count is acceptable when each new class has one clear responsibility. A lower count earns nothing by itself.

## Outcome (step S.11)

The quality goal's report. Every inventory row and every finding is closed.

### Rows

| Outcome | Rows | Closed by |
|---|---|---|
| **Keep**, no work | I-02 `CommentService`, I-03 `CoordinationService`, I-08 `ErrorLogService`, I-09 `ImpersonationService`, I-10 `NoticeService`, I-14 `PlanningService`, I-15 `PlatformConsoleService`, I-16 `PortalService`, I-20 `SetupChecklistService`, I-22 `StateHistoryService`, I-24 `StaffAvailabilityService`, I-25 `StaffBoardService`, I-27 `TenantOnboardingService`, I-29 `TenantSettingsEnsureService`, I-30 `TenantSettingsService`, I-32 `UserSessionService` | S.0 (the reviewed inventory); the five of them that still copied the "No tenant" text took the shared one in S.11 |
| **Keep**, adopting the shared checks | I-06 `CustomerContactService`, I-12 `PatternAssignmentService`, I-17 `QualificationService`, I-18 `ResourceAbsenceService`, I-33 `WorkPatternService` | S.2 |
| | I-04 `OrderService`, I-07 `CustomerService` | S.9 |
| **Keep**, reached directly | I-21 `StaffingService` (the slot dialog reaches it through `JobSlotService.Staffing`) | S.3 |
| **Adapt** | I-19 `ResourceService` (`FindCandidatesAsync` removed) | S.2 |
| | I-26 `JobSlotService` (two forwards removed, the shared checks adopted) | S.3 |
| | I-05 `JobService` (three forwards removed, the shared checks adopted) | S.3, S.4 |
| | I-01 `AttributeManagementService`, now `CustomFieldService` in `Staffora.Services/CustomFields/` | S.5 |
| | I-11 `OrganizationService` (its holding reads to `RoleHoldingService`, `DeleteOrganizationUnitAsync` removed, the shared checks adopted) | S.7 |
| | I-13 `PermissionService` (one `HasAnyPermissionAsync`), I-31 `UserService` (its holding view, two forwards and two test-only methods removed) | S.8 |
| **Split** | I-23 `SlotAssignmentService` into it and `StaffConversationService` | S.4 |
| | I-28 `TenantService` into it, `TenantRoleService` and `RoleHoldingService` | S.6 |
| | I-34 `WorkflowService` into five partial files of the one class | S.10 |
| **Merge** | none of the 34 services whole; the overlap of F1 (the holding lists of three services) became one query | S.6 to S.8 |
| **Remove** | no service; six test-only methods (F7) | S.2, S.6 to S.8 |

Keep 24, Adapt 7, Split 3: as the inventory planned, no row moved.

### Findings

| Id | Before | After | Step |
|---|---|---|---|
| F1 | role-holding lists built by three copies of one query, in three services | one query (`RoleHoldingService`), three entry points, each with its own permission, lease and filter | S.6 to S.8 |
| F2 | delete-step dispatch copied into 8 services | 0 copies, one `Workflow.IsDeleteStep` | S.2, S.3, S.6, S.7, S.9 |
| F3 | name-and-description check copied into 5 services | 0 copies, one `NameAndDescriptionRule` | S.2, S.6, S.7, S.9 |
| F4 | the read's catch-log-fail filter, 89 times; `DeadlockRetry.ReadAsync` in 14 files | kept (open point 3: no sweep); 91 filters and the retry in 15 files, as the services grew and split | areas' steps |
| F5 | the "No tenant" text in 22 files (19 constants, 3 literals) | 0 copies, one `IRuntimeExecutionContext.NoTenant` | S.2 to S.9, S.11 |
| F6 | 7 gate-and-forward methods | 0: each removed where the target's check was equal or stricter, proven by a permission test | S.3, S.4, S.8 |
| F7 | 6 public methods only tests called | 0: removed, their tests moved to the product's paths | S.2, S.6, S.7, S.8 |
| F8 | `MayOpenAsync` / `TenantOfAsync` in `UserService` and `OrganizationService` | kept: their bodies differ (the user's own account and the user viewers, against the unit's permissions) | S.7, S.8 |
| F9 | two `HasAnyPermissionAsync` overloads | one | S.8 |
| F10 | the permission scan held 18 services, found by path | 29 held by type, wherever their files sit and however many parts; the other 10 named with their reason | S.1, S.4, S.6 |

### The quality goal

1. Every row closed, above.
2. Every finding resolved or justified, above.
3. No pass-through without a reason: `ServicePassThroughScanTests` holds 3 entries (10 at S.1), each naming its value: `ResourceAbsenceService`'s comment read and post (a gate on the absence itself, which the entity-agnostic `CommentService` cannot judge) and `TenantSettingsEnsureService.ForgetCachedSettings` (names the cache entry).
4. Overlaps merged: the role-holding lists come from one query (F1); S.0 found no other overlap.
5. Duplication removed, per finding above.
6. No new interface.
7. No test deleted without its behaviour proven elsewhere, named in each step's Built line.
8. The security scans as strong as before, each allow-list change proven by a switch-off in its step.
9. As information, Staffora.Services:

| | Baseline | S.1's start | Before S.11 | After S.11 |
|---|---|---|---|---|
| `.cs` files | 146 | 150 | 157 | 157 |
| Lines | 27,285 | 28,408 | 28,733 | 29,366 |
| Classes and records | | 275 | 278 | 278 |
| Services (classes named `*Service`, the two hosted ones included) | 36 | 36 | 39 | 39 |
| Pass-through allow-list | | 10 | 3 | 3 |

The lines grew by the ADR 0005 constructors and fields (S.11's style pass added 645 lines, its close-out took 12 away). The largest services after the phase, in lines and constructor dependencies: `WorkflowService` 2,412 and 12 (five partial files;
2,314 in one before), `UserService` 852 and 16 (947 and 15), `CustomFieldService` 800 and 4 (789 as `AttributeManagementService`), `JobService` 763 and 11 (752), `OrganizationService` 748 and 8 (825), `RoleHoldingService` 621 and 6, `SlotAssignmentService` 577 and 9 (1,023 and 10), `StaffConversationService` 548 and 7, `PermissionService` 462 and 3, `TenantRoleService` 368 and 7, `TenantService` 241 and 7 (1,060 and 9).
Three new classes, each with one responsibility: `StaffConversationService`, `TenantRoleService` and `RoleHoldingService`.

The tables in this section record the services as the phase left them. Later merges changed the list: `SetupChecklistService` and `PortalService` are part of `TenantHomeService` (`ReadSetupChecklistAsync`, `ReadPortalAsync`), `StaffBoardService` is part of `CoordinationService` (`CoordinationService.StaffBoard.cs`), `ErrorLogService` is part of `PlatformConsoleService` (`QueryErrorLogAsync`), and `PlanningService` is removed with planning mode.
The per-domain lock classes (`JobLock`, `CustomerLock`, `ScheduleLock`, `TenantCreateLock`) and the per-entity step-lock classes are one `AppLock` and one generic `TenantStepLock<TEntity>`. The ADR 0005 field style is now enforced by the build (`.editorconfig`, `EnforceCodeStyleInBuild` and warnings as errors), and `CodeStyleScanTests` checks only primary constructors and Razor code.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| A moved or removed check loosens a permission gate | S.1's scans first; a forward goes only when the target's check is equal or stricter, proven by a permission test; independent review of S.6 to S.10 |
| Path-based allow-lists (`DataContextScanTests`, `UserCreationScanTests`) and the permission scan's source lookup break or weaken when files move | S.1 makes the lookup type-based; every allow-list change ships with a switch-off in the same step |
| The `WorkflowService` split changes behaviour (locks, the change-tracker sync of ADR 0002) | Partial files only, no logic change, bodies compared by script, full suite and every journey, last in order and reviewed |
| Style conversion and refactoring in one diff hide a change | Within each step, the style pass comes first and the refactoring second; reviewers read the second |
| A dialog contract breaks (opener shape, one load, `QueryConfigurator`) | D4; `DialogRouteRegistryTests`, `DialogArchitectureScanTests` and the Web tests run in every step |
| Coverage is lost when tests of removed methods are rewritten | A test is moved or rewritten, never just deleted; each step names where the behaviour is now proven |
| Docs and skills name old services or methods (`docs/authorization.md`, `docs/features.md`, the skills) | Bookkeeping in each step; S.11's search for every old name |
| Too many new classes | Principles 1, 2 and 6; S.0's review of every new class |
| Other work in the repository at the same time | The phase starts after the build plan's feature steps; one step at a time, each green before the next |

## Open points

Every recommendation below was adopted.

1. **Partial files or new classes for `WorkflowService`?** Recommendation: partial files. ADR 0002 wants one orchestrating entry point, and partial files keep it with no new surface, as `ApplicationDbContext` already does.
2. **Where the shared text check lives.** Recommendation: Staffora.Core, beside the other pure rules (`WorkPatternRules`, `StaffingRules`), since it judges an entity's own length constants and needs no I/O.
3. **Convert reads to `DeadlockRetry.ReadAsync` (F4)?** It removes duplicated catch blocks but adds a retry on deadlock, a small change in behaviour. Recommendation: only in reads a step already rewrites, and only where the failure words match; no sweep.
4. **Folder moves out of `Staffora.Services/Services/`.** Recommendation: move a service to its area folder (`Tenancy/`, `Users/`, `Organization/`, `CustomFields/`) only in the step that already rewrites it, so namespaces change once. Leave the guards and step locks where they are unless their step touches them.
5. **`RoleHoldingService` as its own class**, rather than inside `TenantRoleService`. Recommendation: its own class. Holdings are read by four views and written under the grant ceiling, and together with roles they would make another class of about 850 lines.
6. **Keep `ServicePassThroughScanTests` after the phase?** Recommendation: yes. It is a fast unit scan, and it stops new forwards from creeping back.
7. **Record the lasting rules as an ADR?** These are "a large service is split into partial files or by responsibility, never by size alone" and "a dialog reaches a child service through its parent's property". Recommendation: one short ADR in S.11.
