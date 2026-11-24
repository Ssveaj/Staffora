# Authorization and roles

Access is decided by **permissions**, never by role names. A user's permissions come from the `RolePermission` values attached to the tenant roles they hold. Role names ("Coordinator", "Staff") are labels tenant administrators choose; code never reads them.

Related documents:

- Signing in, the session on the auth cookie, sign-in hardening and security headers: [authentication.md](authentication.md).
- Tenant isolation in the data context (query filters, write checks, leases, stamping): [tenant-isolation.md](tenant-isolation.md).
- Entity shapes and relationships: [database.md](database.md).
- Creating users and giving roles, step by step: [features/user-creation-flow.md](features/user-creation-flow.md).

## Two layers

| Layer | Mechanism | Scope |
|-------|-----------|-------|
| Application role | ASP.NET Identity role `AppRoles.Admin` | System-wide |
| Tenant roles | `TenantRole` → `TenantRolePermission` → `RolePermission` | One tenant |

The application role only marks the **SuperAdmin** (the platform owner). Only `Admin` is persisted; `AppRoles.StandardUser` is an in-memory fallback label `UserSessionService` assigns to a user with no Identity roles. Everything inside a tenant is driven by tenant role permissions.

## Rules

- A user starts with no permissions; each one is granted through a `TenantRolePermission`.
- A user's effective permissions are the union of the permissions of the roles they hold, evaluated in the current tenant.
- A role is held on an organization unit (`OrganizationUnitUserRole`; the `Company` root means the whole company) and grants its permissions tenant-wide ([ADR 0021](adr/0021-one-organization-tree-per-tenant.md)). The unit records where the person works and, for a staff role, which units the staff member belongs to; it does not narrow access: `PermissionService` reads every role the user holds on any unit of the tenant. A grouping holds no roles (`OrganizationStructurePolicy`).
- Queries see only the current tenant ([tenant-isolation.md](tenant-isolation.md)). Only the platform owner reaches other tenants: through the platform console, and inside a tenant only by impersonating one of its users ([SuperAdmin](#superadmin-platform-owner)).
- **Signing in.** Who may sign in, and how a refused user is answered, is in [authentication.md](authentication.md#signing-in). What each tenant state means for access is in [Tenant states and access](#tenant-states-and-access).

### Creating users and system accounts

- **Who creates users.** A holder of `UsersCreate` in their own tenant, through the user workflow's create step (`UserService`); a new tenant's first system owner is created by tenant onboarding ([System owners](#system-owners), "Onboarding").
- **No self-service.** There is no self-registration and no self-service of any kind: nobody changes their own account, its name, address, user name, phone or password, and nobody deletes it; a user is deactivated, never deleted ([ADR 0034](adr/0034-no-self-service-account-pages.md)). A system owner is the one exception for their own details and password ([System owners](#system-owners), "Owners appoint owners").
- **Identity's own create** (`UserManager.CreateAsync`) is refused from any session with a signed-in user (`ApplicationUserStore.CreateOutsideSetupRefused`). Only the setup paths call it, before any session: the seeding, the platform bootstrap and the browser journeys' preparation.
- **Entry state.** A user written without a state is in the entry state, `Create` (0), which signs in nowhere, is not impersonated and holds nothing, so such a write fails closed.
- **System accounts.** Each tenant has one system account, which stamps rows written on its behalf ([database.md](database.md#applicationuser), "System accounts"). It is kept out of every person's path:
  - `ApplicationSignInManager` refuses it at sign-in, password or not, and fails the security-stamp check of any cookie that names it, which the circuit's revalidation uses too;
  - the session gate (`UserSessionService`) opens no session for it;
  - `RoleHoldingService.AssignUserToRoleAsync` refuses to give it a role (and refuses a holder from another tenant);
  - the impersonation authorizer refuses it as a target, at start and on every circuit whose cookie names it;
  - `UserService` leaves it out of every user list, never creates a user with a caller's id or system-account flag, and refuses a user name starting with `system:`;
  - the user workflow's create step refuses a new user set as a system account, or with two-factor sign-in, a lockout or failed sign-ins set ahead (`ApplicationUserWorkflowAttribute.OrdinaryNewAccountReason`), so a create past the service is refused too.
- **Acting as a system account.** The work with no circuit (the job clock, the integration outbox's dispatcher, the retention pass, and a portal send's delivery after its commit, `OrderSender`) gives the fresh runtime of a service scope of its own a tenant's system-account session (`SystemAccountSession`; refused on a runtime that already holds a session, allow-listed in `DataContextScanTests`), so every context it opens is filtered to the tenant and every row it writes is stamped with the account.

  The account holds no permission and its steps ask none: they are system-driven, which every public path refuses to a user. A direct write to the database is not stopped; stamping accepts a flagged user only under the id derived for the row's tenant, and the ensure accepts the row only as it wrote it.

### Services check

**Services check.** Every user-facing service operation that reads or writes tenant data checks its permission through `PermissionService` before it touches data, and refuses with a failed `Result` that names what is missing. `ServicePermissionScanTests` holds this and lists each exception with its reason: reads of the caller's own data (their notices, role holdings and first name, and the tenant settings every user works with), platform-console operations gated by platform reach instead of a tenant permission, and infrastructure that checks no tenant permission itself (session resolution, the workflow engine behind the entity services' own checks, state history read through those services, and the hosted services acting as a tenant's system account). The UI gates on the same permissions, but only the service check holds against a crafted call.

- **Users.** `UsersView` to read, `UsersCreate` to create, `UsersEdit` to edit, deactivate, reactivate, unlock or give a new password, each within the [grant ceiling](#grant-ceiling).
- **Organization units.** `OrganizationUnitsView` to read the tree or a unit, `OrganizationUnitsCreate` to add a unit, `OrganizationUnitsEdit` to change, move, deactivate, reactivate, archive or restore one, `OrganizationUnitsDelete` to delete one. The obsolete `Organizations*` values open nothing.
- **Roles.** `RolesView` to read a role and its holders, `RolesCreate` to create one, `RolesEdit` to change one or its holdings, `RolesDelete` to delete one.
- **Deletes.** A dialog's Delete runs the step through the entity's service (`OrganizationService.UpdateOrganizationUnitAndTransitionAsync`, `TenantRoleService.UpdateTenantRoleAndTransitionAsync`), which checks the `*Delete` permission and not `*Edit`, and runs the step on the row as stored, without the dialog's edits: a reader who may view and delete but not edit deletes from the dialog and saves no edit. The workflow's delete step requires the same permission of the access gate. The dialog's footer is the one place a unit or a role is deleted (`LifecycleCallScanTests`).
- **Service catalogue** (`ServiceCatalogueService`). The settings shell's Services panel and a service's dialog are read with `SettingsView` or `ServicesEdit` (the shell itself stays gated on `SettingsView`). A service is created, saved, archived and restored with `ServicesEdit`, every step's own gate; a reader without it sees the dialog read-only. A coordinator reads the active services only through the job dialog's options and makes a slot from one with `JobsEdit` (`JobSlotService.NewJobSlotFromServiceAsync`). The platform console changes no tenant's catalogue.
- **Custom fields** ([ADR 0032](adr/0032-platform-owner-makes-custom-fields-for-a-tenant.md), [ADR 0033](adr/0033-custom-fields-have-a-lifecycle.md)). Custom fields are the platform's:
  - Only the platform owner with platform reach creates a field and takes its steps (save, activate, deactivate; a field is never deleted), global ones and a chosen tenant's, on the platform console. Each step is marked `RequiresPlatformOwner` and needs `SettingsEdit` with platform reach; no tenant permission writes a field (`CustomFieldService`, `FieldsAreThePlatforms`), whatever the tenant's permissions, an impersonating platform owner included.
  - The Custom fields panel and a field's state-entity dialog are read with `SettingsView` (in a tenant, its active fields only).
  - The fields a user or unit dialog renders are read with `SettingsView` or any permission in their entity's area, for the runtime's own tenant. Another tenant's are read only with platform reach, and only for a tenant that exists: an id that names none is refused with `UnknownTenant` before the cache is asked. Entries come from the lookup cache only after that check, so a cached entry never answers a refused call.
  - Values are saved with the view and edit permissions of their entity.
- **Tenant settings** (`TenantSettingsService.LoadAsync`, `SaveAsync`). The settings shell reads every setting with `SettingsView` and saves one with `SettingsEdit`, for the runtime's own tenant only. The platform console never changes or shows a tenant's settings; it changes them only by impersonating one of the tenant's users.

  The startup ensure that pins every tenant's time zone (`TenantSettingsEnsureService`) acts on no one's behalf but the tenant's, outside any session, stamped with the tenant's system account. The value in force (the tenant's time zone, through `TenantClock`) is read by every user of the tenant without a permission, since every time shown converts through it, and never for a tenant the caller names.
- **One's own account.** Without a permission a user still reads their own profile (`/me/profile`, read only), their own role holdings and their tenant's settings in force. Nobody changes their own name, address, user name or password: those are changed only by a holder of `UsersEdit` in the user dialog and by the platform owner from the console ([ADR 0030](adr/0030-platform-owner-corrects-a-users-sign-in-details.md), [ADR 0034](adr/0034-no-self-service-account-pages.md)).
- **Creating a tenant** checks platform reach, which no tenant role grants.
- **Staff.** `ResourcesView` lists the tenant's staff members (`ResourceService.QueryStaffMembersAsync`) and reads a user's staff record and units for the user dialog (`UserService`). `ViewCoordination` reads a slot's candidates (`JobSlotService.ReadSlotStaffingAsync`, of the staff members in service). A staff record is written by nobody's request, only as the consequence of a role holding, a user step, a role step or a unit step its caller was allowed to take (`ResourceLinker`, [database.md](database.md#resource)). The startup ensure of the System owner roles (`SystemOwnerPermissionsEnsure`) acts on no one's behalf but each tenant's, outside any session.

Two source scans hold the rule:

- `ServicePermissionScanTests` fails on a public service method without a check before its first data access, outside an allow-list of the exceptions above, each with its reason. A check is a `permissionService.Has…` question, asked in the method or in a method of the same class it calls first (a private helper, transitively, or another public method or overload that checks first); data is a context, Identity or any other service the class holds.

  It finds the services by type, not by path: every class of Staffora.Services whose name ends in `Service` is held by the scan or named with the reason it is not (the permission check itself, the session and impersonation services, the platform console's reads, which are held to platform reach, the state history and the workflow, which their entity services gate, and the startup and hosted services). A service's source is every file that declares its type, partial or not, checked against the files the compiler built it from.
- `ServicePassThroughScanTests` fails on a public service method whose body is only a call on another service it holds: with no check before it (a forward), or after a check when the target holds a check of its own in the permission scan (a double gate). Each such method is allow-listed with the value it adds (a gate the target lacks).

### Grant ceiling

**Grant ceiling.** Nobody grants a permission they do not hold themselves, except permissions in the `Personal` area (own work: answering requests, own schedule, own absence), so whoever gives roles can give someone the Staff role without working ([ADR 0012](adr/0012-platform-owner-and-tenant-onboarding.md)).

- **The primitive.** `PermissionService.MissingAsync` returns the permissions of a set the session does not hold (none for a platform owner on the console, who holds them all), never a `Personal` one (`RolePermissions.IsPersonal`). So the exemption holds wherever the ceiling does: any `RolesEdit` holder gives the Staff role and puts own-work permissions on a role or takes them off, and a user administrator gives a staff member a new password, without doing that work themselves.
- **The staff flag.** A role's "Holders are staff members" (`TenantRole.ApplyResourceMembershipToUser`) is no permission and outside the ceiling: giving a role that carries it needs only what giving the role needs. Turning it on or off, on a role's create or save, needs `ResourcesManage` besides the role permission, since it makes or unmakes staff members of everyone holding the role (the role workflow's Save, `ResourceFlagReason`, asked of the role guard; `TenantRoleService.ResourceFlagRequiresResourcesManage` for a create).
- **The System owner role** is a grant of its own: only an active system owner of the tenant gives or takes it, or changes a system owner's credentials and access ([System owners](#system-owners)).

The ceiling holds in four places, each in the service (fail closed) and, where a screen offers the change, in the UI:

- **Role holdings.** `RoleHoldingService.AssignUserToRoleAsync` and `RemoveUserFromRoleAsync` refuse a holding of a role that grants a permission the session does not hold (`RoleBeyondCeilingNotGiven`, `RoleBeyondCeilingNotTaken`), one's own included, so a `RolesEdit` holder neither raises themselves nor strips a stronger colleague. The pick lists leave such a role out (`RoleHoldingChoices.Roles`), and `RoleHoldingChoices.RolesBeyondCeiling` names them, so the user dialog's Roles view, a unit's role holdings and the role dialog offer neither their holding nor its removal.
- **Role permissions.** A role is a state entity (`TenantRoleWorkflowAttribute`).
  - `TenantRoleService.CreateTenantRoleAsync` refuses a new role with a permission the session does not hold (`RolePermissionsBeyondCeiling`).
  - The role workflow's Save refuses an edit that adds or takes away one (`ChangesWithinCeilingReason`), and its Save and Delete refuse a role that already grants one (`StoredWithinCeilingReason`): such a role is not the session's to change at all, its name, description and the permissions the editor does hold included, so a weaker role editor neither strips, renames nor deletes a stronger colleague's role.
  - The facts come from the role guard (`TenantRoleGuard`, an `IStepPreparation` read by `WorkflowService` inside the step's transaction before a dialog's edits are saved, so it compares the edit with the role as stored), which fails closed.
  - The role dialog offers only the permissions the reader holds (`TenantRoleService.GetGrantablePermissionsAsync`; the others disabled and marked "you do not hold it") and shows a role beyond the reader read-only with the reason (`TenantRoleService.RoleBeyondCeilingReadOnly`), its Save not offered and its Delete greyed.
- **Units in use.** Deactivating, archiving, reactivating or deleting a unit takes away or gives back what the roles held on it grant ([Role holdings and inactive units](#role-holdings-and-inactive-units)), so each is a change of those holdings.
  - The unit workflow's Deactivate, Reactivate, Archive (of an active unit) and Delete steps (`OrganizationUnitWorkflowAttribute.WithinCeiling`) are refused when a role held on the unit grants a permission the session does not hold (`WithinCeilingReason`).
  - A deactivation takes every active unit below the unit out of use with it, each through its own deactivate step, so a role held on any of those units counts too and refuses the whole step. A unit below that is already out of use is not moved and does not count.
  - The facts come from the unit guard (`OrganizationUnitGuard`, an `IStepPreparation` that reads, under the tenant's structure lock, the permissions every role held on the unit and on each unit below it grants), which fails closed.
  - A rename or a move of an active unit changes no holding and is not refused, nor is a restore from the archive (which leaves the unit out of use) or the archive of an inactive unit (whose holdings grant nothing already).
- **Credentials and lifecycle.** The user workflow's New password, Deactivate, Reactivate and Unlock, and a Save that changes the user name or email address, are refused:
  - on a user who holds, through any role they hold (whatever their own state and their units'), a permission the session does not hold (own work aside);
  - on a system owner, from a session that is no system owner of the tenant (`OwnerByOwnerReason`);
  - on a platform owner (the `Admin` role), from every session, the platform console included.

  The conditions ask the step's account guard (`IUserAccountGuard`, read by `WorkflowService` before a user's steps are judged, an `IStepPreparation`); a question it cannot answer fails the step closed. The footer greys New password, Deactivate and Reactivate with the reason; a Save that would change a stronger account's sign-in name is not offered. A locked-out platform owner is let in with `unlock-user` on the server, a lost platform-owner password with `reset-platform-owner` ([authentication.md](authentication.md#a-platform-owner-who-lost-their-password)).

Verified by: `SystemOwnerTests`.

## Holding a role: OrganizationUnitUserRole

| Field | Required | Meaning |
|-------|----------|---------|
| `UserId` | yes | The user holding the role |
| `TenantRoleId` | yes | The role, and through it the permissions (granted tenant-wide) |
| `OrganizationUnitId` | yes | Where the person works; the `Company` root means the whole company. Not a limit on access |

Unit-scoped coordination, where a coordinator sees only part of the company, is not supported.

### Role holdings and inactive units

**In use.** A unit is in use while its state is Active (`OrganizationUnitState`; the Company root always is). Inactive and Archived units are out of use, and so is a unit written without a state (the entry state, Create), which fails closed. A role is in use while its state is Active (`TenantRoleState`).

**Giving a role.** A role is given only to an active user, of an active role, on a unit in use that is no grouping. `RoleHoldingService.AssignUserToRoleAsync` refuses the others with `InactiveUserTakesNoRoles`, "The role was not found in this tenant.", `InactiveUnitTakesNoRoles` or the grouping reason, and the pickers offer only those (`RoleHoldingService.GetRoleHoldingChoicesAsync`).

**Who manages holdings.** One check, made by the service before an add or a remove and read by every screen that offers them: `RoleHoldingService.CanManageRoleHoldingsAsync` (`RolesEdit`, in the session's own tenant, never the platform tenant). It gates the user dialog's Roles view, a unit's role holdings on the organization page and the role dialog, so none offers what the service refuses. The pick lists come with that permission alone, not with `UsersView` or `OrganizationUnitsView`, and hold only the roles within the reader's [grant ceiling](#grant-ceiling).

**Inactive users.** An inactive user keeps their holdings, listed in the user dialog, which grant nothing until they are reactivated. They take no new ones: the user workflow's Roles area offers no "Add role" while the user is inactive. This fails closed: a holding on a unit out of use would grant nothing now and everything the moment the unit is reactivated, without anyone deciding so then.

**Units out of use.** A unit with holdings may be deactivated or archived (within the grant ceiling). Its holdings stay stored and are listed, marked "(inactive)" or "(archived)", but grant nothing while it is out of use: `PermissionService` counts only holdings on units in use, of active roles, of active users, so they grant again when the unit is reactivated.

- A deactivation takes every active unit below the unit out of use with it, and a unit is reactivated only below a unit in use, so a unit in use never sits below one that is not.
- The data context holds the same rule on every unit it writes, and refuses a holding written on, or moved to, a unit out of use (`OrganizationStructurePolicy.OutOfUseHoldsNoRoles`), whoever writes it.
- The Company root is never deactivated, archived or deleted (`OrganizationStructurePolicy.RootStaysActive`, `RootIsNeverDeleted`; its workflow's steps are greyed with the reasons), and `PermissionService` counts a holding on it whatever its stored state, so a holding on the whole company always counts.

**Deletes.** A unit somebody holds a role on is not deleted (`RoleHoldingsBlockUnitDelete`), nor is a role somebody holds (`RoleHoldersBlockRoleDelete`): each refuses the delete with its reason, so permissions are never taken away by an unseen delete. The role's reason counts its holdings; the unit's names no count ("Roles are still held on this unit"), since its footer shows it to every reader of the unit and the holdings are the Role holdings view's to list, behind `RolesView`.

**Open circuits.** The user who takes a unit step has their own permissions re-read at once (`OrganizationService.UpdateOrganizationUnitAndTransitionAsync` drops their cache after every step but the save). Every other circuit keeps the set it loaded until its five-minute time-to-live ends ([Resolution](#resolution)).

**The platform tenant.** Holdings in the platform tenant are refused altogether: its tree is its root alone, and the platform owner holds the `Admin` role, not tenant roles.

## Tenant roles

A `TenantRole` is a named, tenant-scoped set of permissions with a `Name`, an optional `Description`, and a `Permissions` collection of `TenantRolePermission` (deleted with the role). Users receive it through `OrganizationUnitUserRole`, held on a unit.

A role is a state entity (`TenantRoleWorkflowAttribute`: Create, Active, Deleted): it is created, saved and deleted only through its workflow's steps, in its state-entity dialog (`TenantRoleDialog`: Info, Permissions, Holders), each kept in its history. It has no inactive state: taking away what it grants means removing its holdings. A role somebody holds is not deleted: the holders blocker (`RoleHoldersBlockRoleDelete`, an `IDeleteBlocker`) refuses the delete with the number of holdings, inside the step's transaction under the tenant's structure lock, which every new holding takes too.

Onboarding gives each new tenant the role presets (`RolePresets`):

| Preset | Permissions | Holders are staff members |
|---|---|---|
| **System owner** (locked) | Every grantable permission outside `Personal` and `Portal` (the customer portal's, which a staffing company never grants: [The edition ceiling](#the-edition-ceiling)) | No |
| **Administrator** | The `Tenant`, `Users`, `Roles`, `OrganizationUnits` and `Settings` areas (`ServicesEdit` included: the administrator keeps the service catalogue), and the staff members' setting up ([PRODUCT.md](../PRODUCT.md), "Setting up"): `ResourcesView`, `QualificationsManage`, `ManageWorkPatterns`, `AssignWorkPatterns` | No |
| **Coordinator** | `UsersView` (a staff member opens in the user dialog), the `Resources` and `Scheduling` areas, `CustomersView`, `CustomersManage`, the `Jobs` and `Coordination` areas; not `CustomerPortalEnable`, not `ServicesEdit` (the coordinator makes slots from the active services through the job dialog, `JobsEdit`, and keeps no catalogue), and no user or role administration | No |
| **Staff** | The `Personal` area (own work) | Yes |

A customer portal's tenant gets the portal presets instead (`RolePresets.Portal`), written as its portal is enabled, and no System owner role: its staffing company manages it.

| Portal preset | Permissions | Holders are staff members |
|---|---|---|
| **Customer administrator** | The `Portal` area: `OrdersView`, `OrdersCreate`, `OrdersSend`, `OrdersCancel`, `CustomerUsersManage`; the portal's first user holds it on the portal's Company root | No |
| **Orderer** | The `Portal` area except `CustomerUsersManage` | No |

- **Editing presets.** The presets are the tenant's to edit like any role once it is onboarded: a release that changes a preset changes it for tenants onboarded after it and never rewrites a tenant's own Coordinator or Staff role. The System owner role is not the tenant's to edit, so every start brings it up to date instead ([System owners](#system-owners), "Kept complete").
- **Under the grant ceiling** an Administrator does not give the Coordinator role (it grants permissions the Administrator does not hold, `ResourcesManage`, absence, customers, jobs and coordination among them); a system owner does. Since the Administrator holds `QualificationsManage`, `ManageWorkPatterns` and `AssignWorkPatterns`, it may put those three on a role it edits, or take them off, and edit a role that grants them; it may not change a role that grants anything else of the staffing areas, nor turn a role's "Holders are staff members" on or off (`ResourcesManage`).

Verified by: `TenantRolePresetTests` (the exact sets on a new tenant), `RolePresetTests`.

## System owners

A system owner may do everything in its tenant except own work, appoints other system owners, and cannot leave the tenant without an owner ([PRODUCT.md](../PRODUCT.md), Actors).

- **The role.** Each tenant has one **System owner** role (`TenantRole.IsSystemOwner`, at most one per tenant by a filtered unique index), written once, by onboarding. It grants every grantable permission outside the `Personal` and `Portal` areas (`RolePresets.SystemOwner`). A customer portal's tenant has none: its staffing company manages it, and its customer administrators its people.

  The role is locked: the role workflow's Save and Delete refuse it whoever asks (`TenantRoleWorkflowAttribute.NotLockedReason`), the flag is read-only in every state, and the role dialog shows it read-only with the reason (`TenantRoleService.SystemOwnerRoleReadOnly`). Its holders are the tenant's **system owners**; an **active system owner** is an active user who holds it while it is active (`SystemOwners` in Staffora.Data, the one definition the checks below share).
- **Kept complete.** A permission a release adds outside `Personal` belongs to the System owner role of every tenant, and no tenant can add it to the locked role itself. So the app adds it at every start (`SystemOwnerPermissionsEnsure.EnsureForEveryTenantAsync`, after the system accounts and the time zones, `Program.cs`):
  - one query finds the tenants in Setup, Active or Suspended whose System owner role misses a permission of `RolePresets.SystemOwner` (a tenant scheduled for deletion or archived is kept as it is, for the record), and only those are opened;
  - for each, in a transaction of its own under the tenant's structure lock, every missing permission is added, each row stamped with the tenant's system account and logged;
  - it only adds: a start never takes a permission from the role, and a second run adds nothing and opens no tenant;
  - a role holds each permission once (a unique index); a write the index refuses rolls back that tenant's transaction alone, and the tenant is read and ensured once more;
  - a tenant that fails is logged and keeps its role as it was; only failing to list the tenants stops the start.
- **Held on the Company root only.** The role is given only on the Company root, which is never out of use (`OrganizationStructurePolicy.SystemOwnerOnRootOnly`: refused by `RoleHoldingService.AssignUserToRoleAsync` and, for every other writer, by the data context's structure check). So no unit step takes an owner's holding away, and no edit of another role touches what the System owner role grants: if a sole administrator locks themselves out through a unit step or an edit of their own role, the tenant's owner can put it right.
- **Owners appoint owners.** Only an active system owner of the tenant gives the role or takes it away (`RoleHoldingService.OnlyOwnersAppointOwners`); a role editor who holds every permission an owner holds is still no owner, and the pick lists leave the role out for them (`RoleHoldingChoices.RolesBeyondCeiling`).

  Likewise only a system owner changes another system owner's password, lockout or access (New password, Unlock, Deactivate, Reactivate) or sign-in name (`ApplicationUserWorkflowAttribute.OwnerByOwnerReason`); the platform console's Unlock is the exception it is for every user. A system owner also changes their own details and gives themselves a new password, which nobody else does on their own row (`NotOwnDetails`, `NotOwnPassword`; [ADR 0034](adr/0034-no-self-service-account-pages.md)).
- **At least one active owner remains.** The holding of the tenant's last active system owner is not removed (`RoleHoldingService.LastOwnerKeepsTheRole`), and the last active system owner is not deactivated (`ApplicationUserWorkflowAttribute.LastOwnerReason`). A customer portal has no system owner; its last active customer administrator (an active holder of a role granting `CustomerUsersManage`) is not deactivated either (`LastCustomerAdministratorReason`), since neither its staffing company nor the platform owner, who impersonates no inactive user, could give it one again.
- **Serialized.** Both rules are judged inside one transaction under the tenant's structure lock, which every holding given or taken away, every unit and role step and every user step take, so two owners removing each other's holding, or deactivating each other, at once run one after the other and the second is refused. The lock holds the tenant's row first, in every scope, the platform console's steps included, so a console step, a tenant's session and a step moving the tenant never deadlock ([lock order](database.md#application-locks)).
- **Setup.** A tenant in Setup admits only its active system owners: the sign-in manager (every sign-in path), the per-request cookie check, the session gate and the impersonation authorizer read whether the user is one in the same query as the tenant's state (`TenantStates.AdmitsUser`). The tenant workflow's Activate needs an active system owner (`TenantWorkflowAttribute.ActiveOwnerReason`, read by `TenantOwnerGuard` inside the step), so Setup ends only once someone can sign in to keep the tenant.

Verified by: `SystemOwnerTests`, `SystemOwnerPermissionsEnsureTests` (starts over tenants whose role lacks permissions, four concurrent ensures, the states and the index), `RolePresetTests` (the preset holds every grantable permission outside `Personal`, derived from the enum), `TenantLockOrderTests`.

### Onboarding

"New Tenant" on the platform console opens tenant onboarding (`TenantOnboardingDialog`, a form: the company's name and description, and the first system owner's first and last name and email address, which is their user name).

- **Checks.** `TenantOnboardingService.OnboardTenantAsync` checks platform reach first, so a session without it learns nothing from the answer (not even that an address is held). It judges every text against its column up front with a reason of its own (tenant name 200 characters, description 500, first or last name 100, address 256; the form takes no more). It judges the owner by Identity's rules exactly as the user workflow's create judges a user (`UserService.NewUserRowAsync`: allowed characters, an address no other user of any tenant holds, answered in the one message, and a fresh initial password of 16 characters, `UserPasswords.Generate`).
- **What it creates,** in one transaction through `TenantService.CreateTenantAsync` and its first step: the tenant in Setup with its system account, Company root and pinned time zone, the role presets with the System owner role, and the owner holding it on the Company root (`TenantOnboardingWriter`, internal to Staffora.Data and visible to Staffora.Services; `UserCreationScanTests` allows its calls only from `TenantOnboardingService` and the development seeder, and a user row added to the context directly, `Users.Add(` and its kin, only from it and the system accounts' ensure).
- **Stamping.** Every row it writes into the tenant is written on the tenant's behalf, stamped with the tenant's system account, never with the platform owner; each row's history starts with its workflow's create step, the platform owner as the actor.
- **Atomic.** A refusal or a failure anywhere stores nothing, and two onboardings of one address at once store one.
- **The initial password** is shown once to the platform owner, in the form, and nowhere else (never logged, never in the address, a history or the dialog's answer; `OnboardedTenant`'s `ToString` leaves it out). Nobody can show it again, so until the platform owner ticks "I have copied the password", Done, the header's close button and Esc first ask whether to close without it (`FormDialogOptions.CanClose`); once ticked, Done opens the new tenant's dialog.
- The platform owner may impersonate the owner of a Setup tenant to help finish its setup.

Verified by: `TenantOnboardingTests`, `TenantLifecycleSignInTests`, `SystemOwnerTests`, `TenantDialogTests`, `UserCreationScanTests`.

## Permissions

### RolePermission

Every value except `None` carries `[PermissionArea]`, `[Display(Name)]` and `[Description]`. Names follow `{Area}{Action}`.

| Value | Name | Area |
|------:|------|------|
| 0 | `None` | (none) |
| 1 | `UsersView` | Users |
| 2 | `TenantView` | Tenant |
| 3 | `UsersCreate` | Users |
| 4 | `UsersEdit` | Users |
| 5 | `UsersDelete` | Users |
| 6 | `RolesView` | Roles |
| 7 | `RolesCreate` | Roles |
| 8 | `RolesEdit` | Roles |
| 9 | `RolesDelete` | Roles |
| 10 | `OrganizationsView` (obsolete) | Organizations |
| 11 | `OrganizationsCreate` (obsolete) | Organizations |
| 12 | `OrganizationsEdit` (obsolete) | Organizations |
| 13 | `OrganizationsDelete` (obsolete) | Organizations |
| 14 | `OrganizationUnitsView` | OrganizationUnits |
| 15 | `OrganizationUnitsCreate` | OrganizationUnits |
| 16 | `OrganizationUnitsEdit` | OrganizationUnits |
| 17 | `OrganizationUnitsDelete` | OrganizationUnits |
| 18 | `SettingsView` | Settings |
| 19 | `SettingsEdit` | Settings |
| 20 | `ViewOwnAssignments` | Personal |
| 21 | `RespondToAssignment` | Personal |
| 22 | `ViewOwnSchedule` | Personal |
| 23 | `ReportOwnAbsence` | Personal |
| 24 | `ResourcesView` | Resources |
| 25 | `ResourcesManage` | Resources |
| 26 | `QualificationsManage` | Resources |
| 27 | `ManageWorkPatterns` | Scheduling |
| 28 | `AssignWorkPatterns` | Scheduling |
| 29 | `ViewResourceAbsence` | Scheduling |
| 30 | `ManageResourceAbsence` | Scheduling |
| 31 | `ApproveResourceAbsence` | Scheduling |
| 32 | `CustomersView` | Customers |
| 33 | `CustomersManage` | Customers |
| 34 | `CustomerPortalEnable` | Customers |
| 35 | `JobsView` | Jobs |
| 36 | `JobsCreate` | Jobs |
| 37 | `JobsEdit` | Jobs |
| 38 | `JobsDelete` | Jobs |
| 39 | `JobsCancel` | Jobs |
| 40 | `ViewCoordination` | Coordination |
| 41 | `PerformCoordination` | Coordination |
| 42 | `OrdersView` | Portal |
| 43 | `OrdersCreate` | Portal |
| 44 | `OrdersSend` | Portal |
| 45 | `OrdersCancel` | Portal |
| 46 | `CustomerUsersManage` | Portal |
| 47 | `ServicesEdit` | Settings |

`TenantRolePermission.Permission` is stored as an int. Add a permission by appending the next free value with all three attributes; existing values keep their numbers forever. With one organization tree per tenant ([ADR 0021](adr/0021-one-organization-tree-per-tenant.md)), values 10 to 13 (`Organizations*`) stay reserved and are marked `[Obsolete]`, each message naming the unit permission to check instead; the `OrganizationUnits*` permissions cover the tree.

`RolePermissions.Grantable` leaves them out, so the role dialog, a new tenant's administrator role and the seeded roles never offer them, `PermissionService`'s area lookups skip them, and `ObsoletePermissionScanTests` fails on any source that names one outside the enum. Each new permission reaches every tenant's System owner role at the next start ([System owners](#system-owners), "Kept complete").

### What the staffing permissions open

**Settings.** `SettingsView` and `SettingsEdit` also read and keep the closed days ([features/schedules-and-absence.md](features/schedules-and-absence.md#closed-days)); `ViewCoordination` reads them for the coordination workspace's calendar and title.

**Personal (own work).** The Staff preset holds them, and the grant ceiling exempts them.

- `ViewOwnAssignments` reads one's own assignments and opens "My assignments" (`SlotAssignmentService.QueryOwnAssignmentsAsync`: the effective user's own requests and assignments only, whatever else the reader may read, so a coordinator who also works lists their own and never a colleague's).
- `RespondToAssignment` answers them: a staff member accepts, declines and takes back a yes on their own assignment only. The step's gate asks the permission and a condition the owner rule (the step's effective user is the assignment's staff record's user; `SlotAssignmentWorkflowAttribute.OwnReason`), so a coordinator, who holds no `Personal` permission by the preset, answers nobody's request, and another staff member reads another's assignment as missing ([features/jobs-and-staffing.md](features/jobs-and-staffing.md#assignments)).
- `ViewOwnSchedule` reads one's own schedule (`StaffAvailabilityService.ReadScheduleOfUserAsync` for the effective user's own record) and opens "My schedule" (`StaffAvailabilityService.ReadOwnWeekAsync`: a week of one's own working time, requested and approved absences and live times, nothing of another staff member's). Without it one's own is refused for the permission, never answered with whether one is a staff member.
- `ReportOwnAbsence` reports, edits while requested and cancels one's own absence (the effective user's own staff record, never another's: another staff member's absence reads as missing by every read, and the workflow's Report and the absence guard refuse it past the service too), and adds "Report absence" to "My schedule".
- Both pages are linked under My work for their holders only ([features/coordination-and-homes.md](features/coordination-and-homes.md#staff-pages)).

**Resources (staff).**

- `ResourcesView` opens the Staff page and the user dialog's Staff, Qualifications and Schedule views, reads the qualification catalogue (with `QualificationsManage`: the Qualifications page, a qualification's dialog, the cached catalogue), reads a staff member's schedule by week (with `ViewCoordination`) and reads pattern assignments (with `AssignWorkPatterns`).
- `ResourcesManage` changes a role's staff flag, on a new role as on a stored one, and names the staff record's system-driven steps, which no public path of `WorkflowService` runs, whoever asks.
- `QualificationsManage` keeps the qualification catalogue (every step of a qualification) and gives staff qualifications or takes them away (`QualificationService`). The Coordinator and Administrator presets hold it.

**Scheduling (schedules and absence).**

- `ManageWorkPatterns` makes work patterns (every step of a pattern, and "Copy to new draft"; `WorkPatternService`). `ManageWorkPatterns` or `AssignWorkPatterns` reads them (the Work patterns page, a pattern's dialog, read-only without `ManageWorkPatterns`).
- `AssignWorkPatterns` gives staff work patterns (every step of a pattern assignment, the user dialog's "Assign work pattern"; `PatternAssignmentService`, read with `ResourcesView` or `AssignWorkPatterns`, the assignment's fields read-only without `AssignWorkPatterns`).
- Absence (`ResourceAbsenceService`, the absence workflow, [features/schedules-and-absence.md](features/schedules-and-absence.md#absence)):
  - `ViewResourceAbsence` reads every absence of the tenant (the absence page, the user dialog's Absence view);
  - `ManageResourceAbsence` registers one on another staff member's behalf and edits and cancels any (and reads them);
  - `ApproveResourceAbsence` approves a planned leave or marks a sick leave handled (each kind has one deciding step), or rejects with a reason only its holders read (and reads them);
  - a registration by a holder of both of the last two is decided at once, and nobody decides their own absence;
  - the steps the staff member or a manager takes (the requested absence's save, the two cancellations) name no permission of their own: the access gate takes one permission, so the rule "staff member with `ReportOwnAbsence`, or a holder of `ManageResourceAbsence`" is a condition asked of the absence guard (`IResourceAbsenceGuard`, prepared before the step is judged, fail closed), and the service checks one of the absence permissions before any of them.

**Customers** (`CustomerService`, `CustomerContactService`, [features/customers-and-portal.md](features/customers-and-portal.md#customers-and-contacts)).

- `CustomersView` or `CustomersManage` reads the customers and their contacts (the Customers page, a customer's dialog with its Contacts view, a contact's dialog, read-only without `CustomersManage`).
- `CustomersManage` keeps them (every step of a customer and of a contact, "New customer" and "Add contact").
- `CustomerPortalEnable` enables a customer's portal: the customer workflow's "Enable portal", in the session's own tenant, never on the platform console (`CustomerService.EnableCustomerPortalAsync`, and the step's own gate).
- The Coordinator preset holds both read and keep, the Administrator neither. The platform console keeps no tenant's customers (refused by the services before any write, and by every step's own-tenant condition).

**Jobs** (`JobService`, `JobSlotService`, [features/jobs-and-staffing.md](features/jobs-and-staffing.md#jobs-timetable-and-slots)).

- Any permission of the `Jobs` or `Coordination` area reads the jobs and their slots (the Jobs page, a job's dialog with its Timetable and Slots views, a slot's dialog; `JobService.Readers`), so a holder of one of them alone opens what they act on.
- `JobsCreate` registers a job and hands it over. `JobsEdit` saves it, takes it back to registration and keeps its slots (every step of a slot, "Add slot"). The fields show read-only without `JobsEdit` (`JobsCreate` in create mode).
- `JobsDelete` deletes a job in registration nobody was asked to work.
- `JobsCancel` cancels a job not yet completed (on the company's side, or as the customer's). The access gate judges the whole plan, so a cancellation that cancels slots needs `JobsEdit` too (each slot's Cancel) and one that cancels live assignments `PerformCoordination` (each assignment's cancel); a holder of `JobsCancel` alone cancels only a job without active slots.
- A job's custom fields are read with the job (a permission of the Jobs or Coordination area, or `SettingsView` for the fields' list) and kept by the platform owner. A staff member reads, in their own assignment while it is Assigned, only the job fields whose audience includes the assigned staff member (`CustomFieldService.ReadAssignedStaffJobFieldsAsync`, internal, behind `ViewOwnAssignments`, judging in the database that the assignment is theirs and Assigned and the audience), never a coordinators-only field or value ([features/custom-fields-and-settings.md](features/custom-fields-and-settings.md#job-fields)).
- The Coordinator preset holds every Jobs permission; the Administrator and Staff presets hold none. The platform console keeps no tenant's jobs (refused by the services before any write, and by every step's own-tenant condition).

**Coordination.**

- `PerformCoordination` starts a job's coordination, which names the user who takes it its coordinator, and confirms it. Every coordinator step on an assignment (request, assign, pick, cancel) asks it; every staffing step goes through the staffing dialog and `SlotAssignmentService`.
- `ViewCoordination` reads a slot's staffing context and its ranked candidates, with their absences' kinds and their other bookings (the slot dialog's Staffing view; `StaffingService`, reached through `JobSlotService.Staffing`); `PerformCoordination` requests and assigns a candidate in place (`SlotStaffingPanel`) ([features/jobs-and-staffing.md](features/jobs-and-staffing.md#staffing-dialog)).
- `ViewCoordination` asks the availability of many staff members (`IStaffAvailability.ReadAsync`, the question of every staffing surface).
- `ViewCoordination` opens the coordination workspace (`/coordination`, `CoordinationService.ReadAsync`: every slot of the tenant's jobs being staffed with where it stands, its assigned staff member's blocks and the jobs behind), whose rows open the slot's Staffing view, and which listens on the tenant change channel for every change of its tenant ([features/coordination-and-homes.md](features/coordination-and-homes.md#coordination-workspace)).
- `ViewCoordination` reads the workspace's staff timeline (`CoordinationService.ReadStaffMembersAsync` and `ReadBookingsAsync`, both its steps: the staff members in service with their working time and their requested or approved absences, the kind and whether reported, waiting or approved, never a reason; then their timetable slots and the slots nobody holds). Its "Add absence" on a staff member's row is offered to a holder of `ManageResourceAbsence` on every row but the reader's own (one's own is reported, never registered); the absence dialog's own steps decide as ever ([features/coordination-and-homes.md](features/coordination-and-homes.md#hour-timeline)).
- The workspace raises a staff member's unanswered question (their assignment's whose-turn timestamps, read with `ViewCoordination`) and marks their slot, naming only the staff member, never the words.
- A staff member, who holds no permission of the Coordination area, reaches none of it; the platform console reads none of it.

**Portal** ([ADR 0007](adr/0007-cross-tenant-ordering-customer-portal.md)). The `Portal` permissions are a customer portal's alone: the edition ceiling grants them in a portal's tenant only, and nothing else there ([The edition ceiling](#the-edition-ceiling)).

- Any of them opens the portal's home (`/portal`, `TenantHomeService.ReadPortalAsync`, which refuses the platform console: it acts inside a portal only by impersonating one of its people).
- Orders (`OrderService`, every method refusing the platform console first with `InsidePortalOnly`):
  - `OrdersView` reads them ("My orders", the order's page, the order dialog; the portal's home lists the reader's own latest). The order's history is read only by the platform owner, as a portal has no system owner ([ADR 0036](adr/0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md));
  - `OrdersCreate` writes and saves them (the dialog's create, "New order", the draft's Save; the order dialog's fields are read-only without it);
  - `OrdersSend` sends them (the order workflow's Send, whose gate asks it; the dialog's edits go with it only for a holder of `OrdersCreate`, by the edit-authority rule);
  - `OrdersCancel` cancels them.
- A staffing company's user holds none of them, whatever a role names, so every portal page shows them "Access denied" and every service refuses them.
- Sending writes into the staffing company's tenant on its behalf, which no permission of the portal's people opens there: they read none of its rows before or after.
- `CustomerUsersManage` manages the portal's own people. A portal grants none of the user permissions (`UsersView`, `UsersCreate`, `UsersEdit`), so `UserService` takes it in their place (`UserService.Viewers`, `Creators`, `Editors`: listing and opening the portal's users, the user dialog's create and steps). The user workflow has the portal's own steps, each needing it (`ApplicationUserWorkflowAttribute.PortalSteps`: "Create orderer", "Create administrator", New password, Deactivate, Reactivate). A staffing company's own user steps need `UsersCreate` or `UsersEdit`, which no portal grants, and a staffing company never holds `CustomerUsersManage`, so each edition reaches only its own steps.
- The portal's steps keep every rule of the staffing company's (the session in the user's own tenant, not one's own account, no system account or platform owner, the grant ceiling on credentials and lifecycle) and take no edits, so a customer administrator changes nobody's names, address or user name.
- A portal's create gives the new user the portal's Orderer or Customer administrator role on its Company root in the create's own transaction (`PortalUserRoleFollowUp`), within the grant ceiling: a role granting a permission the creator does not hold is not given, and the create is refused whole (`PortalUserRoleFollowUp.RoleBeyondCeiling`).
- The customer thread is read with `OrdersView` and written with `OrdersCreate` in the portal ([Comment threads](#comment-threads-the-permission-is-passed-in)).

Verified by: `OrderTests`, `PortalPagesTests`, `PortalUserTests`, `PortalUserWorkflowTests`.

### PermissionArea

`Tenant`, `Users`, `Roles`, `Organizations` (holds only the obsolete values, so no lookup returns it), `OrganizationUnits`, `Settings`, `Personal` (own work: the grant ceiling's one exemption), the staffing areas `Resources` (shown as "Staff"), `Scheduling` ("Schedules and absence"), `Customers`, `Jobs` and `Coordination`, and `Portal` (shown as "Customer portal"; a customer portal's only).

Areas are used for area-level checks and for grouping permissions in the UI (`PermissionService.GetPermissionsByArea`, `GetGroupedPermissions`): the role editor's Permissions view shows one group per area, in the order of each area's first permission, named by the area's `[Display(Name)]` (`StatePermissionMatrix`; verified by `StaffDialogsTests`).

### Resolution

`PermissionService.GetUserPermissionsAsync`:

1. Reads the tenant and the effective user (the impersonated user while impersonating) from one snapshot of the runtime (`runtime.Current`), and the platform-owner bypass from the same snapshot, so a check never pairs facts of two sessions ([One snapshot](authentication.md#one-snapshot)).
2. Collects the `TenantRoleId` of every active role the user holds on any unit in use in that tenant (Active, or the Company root) while the user is active (`OrganizationUnitUserRole`). Which unit holds it narrows nothing, but a holding on a unit out of use, of a role that is not active, or of a user who is not active grants nothing ([Role holdings and inactive units](#role-holdings-and-inactive-units)).
3. Loads the distinct `RolePermission` values of those roles.
4. Caps the set by the tenant's edition, read from the tenant's own row in the same read ([The edition ceiling](#the-edition-ceiling)); a tenant whose row is not found grants nothing.
5. Caches the set, with the edition, for the circuit per (tenant, effective user) with a time-to-live of five minutes (`PermissionService.TimeToLive`) on the injected clock. A change of any key reloads it, and so does its age, so a role another user removed is gone within five minutes. Concurrent callers share one read.

- **Priming.** The session primes the cache before it is ready (`PrimeAsync`, given the resolved session before it is applied), so the first page's checks read nothing.
- **Invalidation.** `InvalidateCacheAsync` drops it at once. `RoleHoldingService` calls it after a holding of the current (effective) user's own is added or removed, `TenantRoleService` after a role they hold is edited or deleted, and `OrganizationService` after the user takes a unit step other than the save. Anyone else's edit reaches them within the time-to-live.
- **Synchronous checks** need a fresh set (`IsLoaded`) and throw once the time-to-live has passed, which can happen between asking `IsLoaded` and checking. So `PermissionView` and the pages use the asynchronous checks, which answer from a fresh set without a read or an await and read it again once it expired.

### The edition ceiling

A tenant's edition is derived from the tenant, never stored ([ADR 0007](adr/0007-cross-tenant-ordering-customer-portal.md); `TenantEditions.Of`, `Staffora.Core/Entities/Tenancy/TenantEdition.cs`): a managed tenant (one that names a staffing company, `Tenant.ParentTenantId`, which only a portal's enabling writes) is a **customer portal**, every other tenant a **staffing company**. The rule reads the tenant's own row only, never the staffing company's customer, so it fails closed: a managed tenant is a portal even if no customer names it.

- **The ceiling.** Each edition grants only its own permissions, whatever a role names (`TenantEditions.Caps`, `Ceiling`): a customer portal only the `Portal` area, a staffing company everything but it. `PermissionService` caps every set it reads, so every check (synchronous and asynchronous), the grant ceiling (`MissingAsync`: what is capped away is not held, so it is never granted), `PermissionView`, the navigation and the homes see the capped set.

  No role names more than its tenant's ceiling through a service (the grant ceiling refuses it, and the role dialog lists only the permissions the role's tenant may grant, `TenantRoleEditorOptions.Offered`, besides any the role already names); a role that names more anyway (written past the services, or by a platform owner on the console on the platform tenant's own roles) grants nothing more.
- **Impersonation.** A platform owner impersonating a user of a portal holds what that user holds, capped the same, and is held to the portal's pages; only platform reach on the console bypasses it, as it bypasses every check.
- **The edition itself** is cached with the set (`PermissionService.GetEditionAsync`) and read by the portal's navigation (`NavMenu`), the homes (`AppHome`) and the page guard (`PortalPageGuard`): a portal's session sees only the pages marked part of the portal (`[CustomerPortalPage]`), and every other page shows it "not part of your portal" ([features/customers-and-portal.md](features/customers-and-portal.md#customer-portal)).

Verified by: `PortalEditionTests`.

### Checking in code

| Method | Use |
|--------|-----|
| `HasPermissionAsync(RolePermission, ct)` | One permission |
| `HasAnyPermissionAsync(IEnumerable<RolePermission>, ct)` (a collection expression for a few: `[RolePermission.A, RolePermission.B]`) | Any of |
| `HasAnyPermissionInAreaAsync(PermissionArea, ct)` | Any permission in an area |
| `HasPermission` | Synchronous, after `await LoadPermissionsAsync()`; throws when `IsLoaded` is false |

### Comment threads: the permission is passed in

A commented record's threads ([ADR 0010](adr/0010-comment-threads-per-audience.md)) are read and written through `CommentService` (`Staffora.Services/Comments/`), which knows no entity's audiences. The entity's own service passes, with every call, each thread's read and post permissions (`CommentThreadAccess(audience, readPermission, postPermission)`), and `CommentService` checks them through `PermissionService` before it reads anything.

- **Access.** A reader is answered only the threads whose read permission they hold, each with whether they hold its post permission (no composer otherwise); a post needs both.
- **Tenant.** The record is read under the tenant filter first, so another tenant's record reads as missing and takes no comment. There is no platform lease: the platform owner reads a tenant's threads only while impersonating. Authors are named only when they are users of the record's tenant.
- **Step notes.** A step's comment prompt writes its note through `WorkflowService` inside the step, judged by the step's own access gate, not by a thread permission.
- `ServicePermissionScanTests` holds `CommentService`'s public methods to a check before any data. The staff conversations have a reader by ownership (the assignment's own staff member) beside the permission, decided by the assignment's service as below.

**An assignment's thread** is its staff thread (`CommentAudience.Staff`), the conversation between the coordinators and that staff member.

- On their own assignment the staff member reads it with `ViewOwnAssignments` and posts with `RespondToAssignment`; anyone else reads it with `ViewCoordination` and posts with `PerformCoordination` (`StaffConversationService`, which judges the reader of the assignment as `SlotAssignmentService` does and passes the access that fits). Another staff member never reads it: the assignment reads as missing to them.
- A decline's or a withdrawal's reason, written by the step's comment prompt, is in it.
- It closes (`CommentThreadAccess.ClosedReason`) to the staff member once their request or assignment is no longer live, and to everyone once the job is completed or canceled; it is still read, and a post is refused with the reason.
- While the request is open (Requested or StaffQuestion) the thread takes no free comment from either side (`StaffConversationService.WrittenThroughSteps`): its words are written only by the question and answer steps below, each judged by its own gate.
- Every read path checks the same: the job's Staff tab needs `ViewCoordination` (`StaffConversationService.GetJobConversationsAsync`, checked first; the job dialog reaches the conversations through `JobService.Conversations` and the assignment dialog through `SlotAssignmentService.Conversations`), so a staff member reaches neither. Whose turn it is and the unseen-reply mark are answered only with a thread the reader may read; "My assignments" marks only the reader's own rows.
- The whose-turn stamp of a post is one statement under the tenant filter (another tenant's assignment stamps nothing and the post reads as not found).
- A post's notice names no comment's words and reaches the named coordinators only while they hold `ViewCoordination`, the staff member only while they hold `ViewOwnAssignments`, judged as it is delivered (`OutgoingMessage.NamedRecipientsHold`). The change a post publishes names the job and that staff member's record only, which another staff member's own pages never hear.
- A platform owner impersonating the staff member reads and posts as the staff member (the comment names the impersonation) and leaves a reply unseen.

**A request's questions** are steps on the assignment ([ADR 0037](adr/0037-request-questions-are-workflow-steps.md), [ADR 0038](adr/0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md)):

- the staff member's "Write a question" and "Write another question" need `RespondToAssignment` and their own request (the owner rule), as Accept and Decline do;
- the coordinator's "Answer the staff member" and "Mark as handled" (no words) need `PerformCoordination` and never one's own request, and hand the request back to the staff member;
- a staff member's message on a yes or an assignment is marked handled by the same gate (`PerformCoordination`, never one's own assignment, only while the message waits for a reply);
- each of these steps, the answers and a coordinator's reply in the conversation mark the staff member's question or message notices read for every recipient in the record's tenant, a write no reader's own permission is asked for: the step's gate is the permission.

**An absence's thread** is its staff thread ([ADR 0035](adr/0035-absence-questions-are-workflow-steps.md)), read by the absence's own staff member with `ReportOwnAbsence` (`ResourceAbsenceService.OwnThread`) and by a holder of `ApproveResourceAbsence` (`ApproverThread`), the service choosing by ownership. Any other reader of absences, a manager without the approval right included, gets no thread and no column.

Nobody posts in it: both accesses carry a `ClosedReason`, so every word in it (a question, an answer, a rejection's reason) is written by a step's prompt, judged by that step's gate. The coordinator's question steps and the absence's "Mark as handled" need `ApproveResourceAbsence` and never one's own absence; the staff member's need `ReportOwnAbsence` and their own absence (the workflow's owner rule, as for Report). While a question waits on the staff member the absence is theirs alone to save.

**What an absence affects** ([ADR 0044](adr/0044-taking-off-for-absence-and-open-again.md)) is read with `ViewCoordination` in the session's own tenant (`ResourceAbsenceService.ReadAbsenceImpactAsync`, refused on the console, another tenant's absence read as missing), which also offers the absence dialog's Affected jobs view; the Priorities' "Absence to settle" item needs a reader of absences as well. "Take off for absence" on an assignment needs `PerformCoordination`, as a cancel does.

**A job's internal thread** (`JobService.InternalThread`) is read with `ViewCoordination` and posted in with `PerformCoordination`, both through the job service, which first refuses anyone without a permission of the Jobs or Coordination area (a staff member reads `JobService.NoView`, and the comment service itself refuses them for the permission). A job's cancel steps write their reason into it, so only the coordinators read why a job was canceled. It is never notified and never published on the change channel, so no staff member learns even that a note was written.

**The customer thread** ([ADR 0007](adr/0007-cross-tenant-ordering-customer-portal.md)) is two threads, one per tenant, mirrored through the integration outbox.

- In the staffing company it is a job's Customer thread (`JobService.CustomerThread`), read with `ViewCoordination` and posted in with `PerformCoordination`, offered only on a job a portal sent, behind the job service's refusal of anyone without a permission of the Jobs or Coordination area: a staff member reads none of it by any path, the comment service included.
- In the portal it is a sent request's "Messages with supplier" (`OrderService.RequestThread`), read with `OrdersView` and posted in with `OrdersCreate`; a draft has none.
- A copy crossing from the other tenant is written by the receiving tenant's own system account inside the message's delivery and names its author as that tenant knows them (a contact of the customer, or the portal's supplier party), so no row of either tenant names a user of the other, and nobody in either tenant gains a permission by it.
- A customer's message tells the job's coordinator while their account is active and holds `ViewCoordination`, judged as the notice is delivered, and every holder of `PerformCoordination` otherwise (the notice's fallback permission). The staffing company's tells the request's orderer while they hold `OrdersView`, and the portal's customer administrators otherwise.

Verified by: `OrderSyncTests`.

### Notices

**Notices.** In-app notices ([features/notifications-and-comments.md](features/notifications-and-comments.md#notifications)) need no permission to read: everyone reads their own and only their own. `NoticeService` (`Staffora.Services/Messaging/`) reads and marks the copies (`MessageRecipient`) of the session's effective user in the runtime tenant, from one snapshot of the session, so an id sent from elsewhere reads as missing; `ServicePermissionScanTests` allow-lists its public methods with that reason.

- **Who receives a notice.** The people a step's `NotifyAction` names (when it names a permission they must hold, only those who hold it as the notice is delivered: a staff member's answer reaches the coordinators who asked only while they hold `ViewCoordination`), every holder of the permission it names, and, when the named people resolve to nobody as it is delivered, every holder of its fallback permission (a job's coordinator who left or no longer coordinates: every holder of `PerformCoordination`).

  Only active users of the entity's tenant receive one, never a system account; a holding counts as described in [Role holdings and inactive units](#role-holdings-and-inactive-units). An inactive user receives nothing, and a notice is never delivered into another tenant.
- **The platform console.** A platform owner's step on a tenant's entity, without impersonating, notifies that tenant's people only; a platform owner named as a recipient is no user of the tenant and receives nothing. The notice is delivered under the platform lease, on the tenant's behalf: its rows are stamped with the tenant's system account, as the step's history is.
- **A customer portal's send.** The job a portal's order becomes tells every holder of `PerformCoordination` in the staffing company ("{customer} ordered {job}", `job-requested`), never anyone of the portal. The notice is a row of the staffing company's tenant, written on its behalf in the send's transaction and stamped with its system account, and delivered after the commit as that account in a service scope of its own (`OrderSender.DeliverAsync`); a delivery that cannot start stays pending, and the integration outbox's dispatcher delivers it once it is a minute old.
- **No bell for the platform owner.** No notice is ever delivered to a platform owner, so a platform owner on the platform console has no bell: `NoticeService.HasNotices` is false for a session with platform reach, its reads and marks are refused, and the account menu hosts no bell for it.
- **Impersonation.** A platform owner impersonating a user sees that user's bell (their count and notices) but marks nothing read: a notice they open stays unread, Mark all read is not offered, and `NoticeService.MarkReadAsync` and `MarkAllReadAsync` refuse while impersonating. Both are one `UPDATE` over the reader's own unread copies in the tenant, so another person's copy named by id matches nothing. The person still finds unread what they have not seen.
- **Following a notice.** A notice's link opens a page through the router, whose own policy applies, or a dialog through its id-only opener, whose load checks the reader's permission and the tenant as a list row's open does; a notice grants nothing it links to.

### The tenant change channel

Open pages reload when a committed write of their tenant touched what they show ([features/coordination-and-homes.md](features/coordination-and-homes.md#coordination-workspace)). The channel tells them so, never what changed.

- **Ids only, after commit.** `WorkflowService`, the job clock and the role-holding service publish the tenant and the ids of the jobs and staff records a committed write touched (`TenantChangeBus`), only after their commit: a refused, rolled-back or failed write publishes nothing. No name, value or state is carried.
- **What a write names.** A write that touched a job also names, read in its own transaction before the commit, the staff members of that job's live assignments (asked, said yes, assigned), so they hear it and no other staff member does. A user's step names the user's staff record, a work pattern's step the staff members whose active assignments give its hours, and a customer's step the customer's jobs (and so their live assignments' staff members), each read the same way. A staff member's qualification given or taken away (`QualificationService`, outside the workflow) publishes the staff record after its commit too. A write that names no job and no staff member publishes nothing.
- **Isolation of subscribers.** Each subscriber is told on the thread pool, without the publisher's execution context, so nothing of the writing circuit reaches another and a slow or failing page never holds up the write.
- **Its own tenant only.** A subscription is keyed by its tenant, and a change reaches the subscriptions of its own tenant alone. The tenant is the session's, decided by the circuit's `TenantChangeFeed`, never by the page: a tenant's user's, or the impersonated tenant for a platform owner impersonating one of its users. The platform console (platform reach, not impersonating) and a session without a user or a tenant get no subscription, so no change of any tenant reaches the console.
- **What a page hears.** The coordination workspace needs `ViewCoordination` to subscribe (checked as it subscribes) and hears every change of its tenant, whose jobs and staff members such a reader reads anyway.

  A staff member's own pages ("My assignments", "My schedule") hear only a change that names their own staff record (read once as they subscribe; nothing for a user who is no staff member), including an edit of a job they are asked for, said yes to or assigned to, and are told only that one came, so a staff member never learns that anything of another staff member's, or of a job they have no live part in, changed. A page's mark (`TenantChangeFeed.Mark`, the session's tenant's change count, null for the console) is a count only and makes it read once more when a change came before it subscribed.
- **Re-read, never trusted.** A page that hears a change only reads again through its own services, which check the reader's permission and tenant as on any load; a permission taken away meanwhile shows as their refusal.
- **Gone with the page.** A subscription is disposed with its page (and a reload still waiting with it), so nothing reaches a circuit or component that is gone. At most one reload every five seconds per page, on the injected clock.
- **One instance.** The bus is in-process, as the bell's is (KI-69).

### Global search: who finds what

One service, `GlobalSearchService` (`Staffora.Services/Search/`), answers the search dialog ([features/ui-shell.md](features/ui-shell.md#global-search), [ADR 0040](adr/0040-global-search-reads-each-kind-through-its-view-permission.md)). It decides what the session may search from one snapshot and asks each kind only of a reader who may view it, so a hit is never a record its reader may not see in that kind's own list.

| Kind | Searched when the session holds | Hits | A hit opens |
|---|---|---|---|
| Staff members | `ResourcesView` (the Staff page's gate) | the tenant's staff records, by the person's name | the user dialog (`user`) |
| Jobs | any of `JobService.Readers` (the Jobs page's gate) | the tenant's jobs that are not deleted, by name | the job dialog (`job`) |
| Jobs, own only | `ViewOwnAssignments` without the readers above (a staff member) | the reader's own assignments whose job's name matches, open ones first | the assignment dialog (`assignment`), which shows a staff member only their own row |
| Customers | `CustomersView` or `CustomersManage` | the tenant's customers that are not deleted, by name | the customer dialog (`customer`) |
| Tenants | platform reach (the platform console, not impersonating), and then nothing else | every tenant but the platform's, customer portals named as such, read under the platform lease with its reason | the tenant's console page |

- **One tenant.** Each query runs on a context of the runtime, so the tenant filter applies, and names the runtime tenant in its `Where` as well. A platform owner impersonating a user searches exactly as that user, in that tenant, and never the tenants. A customer portal's people hold none of these permissions (the edition ceiling), so they get no search at all.
- **Nothing to search, no button.** A session that may search no kind gets no search button, and the "/" shortcut opens nothing (`GlobalSearchService.KindsAsync`).
- **Opening a hit** goes through the dialog's id-only opener (or the page's router), whose own load checks the reader's permission and the tenant again; a hit grants nothing.

### Gating UI: PermissionView

`PermissionView` (`Staffora.Blazor/Components/Common/PermissionView.razor`) evaluates **one** condition, in this order: `Area`, then `AnyOf`, then `Permission`. With none set it denies.

Fragments: `Authorized` (falls back to `ChildContent`), `NotAuthorized`, and `Loading`, shown until the session has a user. It re-evaluates when `UserSessionService.OnChanged` fires, so it follows impersonation start and end. It checks through the asynchronous methods only, so a set whose time-to-live passes mid-check is read again rather than throwing.

### Navigation

The navigation is built from the same permissions: each tenant link names the permissions its page gates on and shows only to a holder of one of them, so the menu never offers a page that would refuse. A session of a customer portal gets the portal's own links instead (`AppNavigation.Portal`). A platform owner who is not impersonating gets the platform console's links, which no permission gates; while impersonating, the menu is the impersonated user's. Details in [features/ui-shell.md](features/ui-shell.md#page-chrome-and-navigation).

## SuperAdmin (platform owner)

The platform owner ([PRODUCT.md](../PRODUCT.md), [ADR 0012](adr/0012-platform-owner-and-tenant-onboarding.md)) holds the Identity role `AppRoles.Admin` **and** belongs to the platform tenant (`IsRootTenant`); either alone grants no platform reach.

Both are checked against the database, never read from claims alone: when the session resolves, `UserSessionService` reads the signed-in user, whether their tenant is the platform tenant and whether they hold `Admin`, and sets them on the runtime (`IsSuperAdmin`, `IsInPlatformTenant`). `PlatformScope.IsPlatformOwner` is both together; `PlatformScope.HasPlatformReach` is a platform owner who is not impersonating, and it is the one check behind the platform lease, the permission bypass and the access gate of `WorkflowService`. It takes all three facts from one snapshot of the runtime ([One snapshot](authentication.md#one-snapshot)).

Creating the first platform owner and recovering a platform owner's access are server commands: [First platform owner](authentication.md#first-platform-owner), [A platform owner who lost their password](authentication.md#a-platform-owner-who-lost-their-password).

### Pages and policies

- **Policies.** Inside a tenant the platform owner acts only by impersonating one of its users: tenant pages refuse a platform owner who is not impersonating, and the platform console refuses one who is. Two policies (`Staffora.Services/Authorization/SessionPolicies.cs`) decide it from the cookie, at the request for the page and in the circuit's router.
  - A cookie is a platform owner's when it carries the `Admin` role **and** the platform-tenant claim (`SessionPolicies.PlatformTenantClaim`), which sign-in, the security-stamp refresh and `RefreshSignInAsync` put on the cookie of a user of the platform tenant (`ApplicationClaimsPrincipalFactory`), so the policies agree with `PlatformScope.IsPlatformOwner`.
  - `PlatformConsole` opens a page to a platform owner without an impersonation; `TenantPages` to every other signed-in user, a platform owner only while impersonating, and an `Admin` of any other tenant as the tenant user it is.
  - There are no pages of one's own account ([ADR 0034](adr/0034-no-self-service-account-pages.md)); one's own profile (`/me/profile`, read only) is a tenant page.
  - Pages name their policy through their folder's `_Imports.razor` (`Pages/SuperAdmin/`, `Pages/TenantAdmin/`, `Pages/Me/`). The start page, the error pages and the sign-in flow's pages name none, and `SessionStateScanTests` holds every other routable component to a policy, each exempt page listed with its reason.
  - A refused request is sent to `/Account/AccessDenied`, which tells an impersonating session to end the impersonation first and offers the end button.
  - The policies only open pages: what a page reads and writes is decided by the runtime the session resolved from the database (`PlatformScope.HasPlatformReach`), so a cookie whose roles went stale opens a page that shows nothing.
- **Platform pages** live under `/superadmin/*`; each declares `@layout SuperAdminLayout` (the shell and its menus: [ADR 0029](adr/0029-left-sidebar-shell.md)) and opens by `SessionPolicies.PlatformConsole`, which the folder's `_Imports.razor` applies. After sign-in without a return URL, a platform owner lands on `/superadmin/overview`.
- **The error log** (`/superadmin/errorlog`) is such a page. Its reads, `PlatformConsoleService.QueryErrorLogAsync` and `GetExceptionDetailAsync`, check platform reach first and read the platform's error log only under the platform lease, so they refuse everyone without platform reach, an impersonating platform owner included ([features/operations.md](features/operations.md#error-log-and-log-scopes)). The same page shows the retention pass's last run, which `PlatformConsoleService.LastRetentionPass` answers only with platform reach ([features/operations.md](features/operations.md#retention)).
- There is no tenant switcher; changing tenant means impersonating a user there.

### The platform console

- **Pages.** The overview (`/superadmin/overview`: the tenants by state, each count a link to the list filtered by it, the newest errors and the newest impersonations), the tenant list (`/superadmin/tenants`, on the one list surface), each tenant's console (`/superadmin/tenants/{id}` with its Overview, `/users`, `/roles`, `/settings` and `/dead-letters`, the integration messages to or from the tenant that were given up, read under the platform lease alone) and the platform settings (`/superadmin/settings`, every custom field: the global ones and every tenant's). The list sets each managed customer tenant under its staffing company, and each console links the other side.
- **Reads.** Every read is `PlatformConsoleService`'s (or, for a tenant's custom fields, `CustomFieldService.QueryTenantDefinitionsAsync`), which checks platform reach before it reads anything (`ServicePermissionScanTests` holds every one to it) and then reads under the platform lease. A tenant's user and an impersonating platform owner are refused with `PlatformConsoleService.ConsoleOnly`, and the overview is refused while impersonating twice over: the page by `SessionPolicies.PlatformConsole`, the read by the service.
- **Read-only inside a tenant** ([ADR 0012](adr/0012-platform-owner-and-tenant-onboarding.md)). The console shows the tenant's users, roles, organization and settings and changes none of them; a user's or a role's row opens its dialog read-only (the user dialog's Unlock, New password and sign-in corrections aside, [ADR 0030](adr/0030-platform-owner-corrects-a-users-sign-in-details.md)). A role's or a unit's save or delete sent from the console anyway is refused twice: by the service before anything is written (`TenantRoleService.RoleChangeInOwnTenantOnly`, `OrganizationService.ChangeInOwnTenantOnly`) and by every step's own-tenant condition.
- **History.** The console's history names a platform owner who acted from outside (the `Admin` role and the platform tenant both); any other outsider stays described.
- **Its writes** are the platform's own: the tenant's lifecycle steps in its dialog ("Tenant details" on its console), onboarding, the custom fields (global, or for a chosen tenant, stamped with its system account) and the sign-in help for a tenant's user (Unlock, New password and the corrections of [ADR 0030](adr/0030-platform-owner-corrects-a-users-sign-in-details.md)).
- **The tenant dialog** is the console's: `TenantService`'s dialog methods (`LoadTenantUpdaterAsync`, `UpdateTenantAndTransitionAsync`, `GetTenantHistoryAsync`) and tenant onboarding (`TenantOnboardingService.OnboardTenantAsync`, which creates a tenant; the dialog has no create mode) check platform reach rather than a `RolePermission` (listed with that reason in `ServicePermissionScanTests`), so a tenant's user, a holder of every tenant permission and an impersonating platform owner are refused alike, and `WorkflowService`'s gate refuses its steps to them again.

  On the console its history names the platform owner who took each step ("Ada Admin (platform owner)", `StateHistoryService.PlatformOwnerSuffix`), since the console reads the platform tenant's own people under the lease; inside a tenant such a step reads "Someone outside this organization". The platform tenant's own name and description are fixed: the service refuses any edit of it.

### Permissions and steps

- **The bypass.** `PermissionService` grants every permission to a platform owner who is **not** impersonating (`PlatformScope.HasPlatformReach`, the same check that opens the platform lease). While impersonating, the effective user is the target, so checks use the target's permissions.
- **Tenant lifecycle moves are not tenant permissions.** Every tenant transition a person takes is marked `RequiresPlatformOwner` and names no `RequiredPermission`, so no tenant role can grant it. The one other, `tenant-purged` (Pending deletion to Archived), is system-driven: no button offers it and `WorkflowService` refuses it to every caller; only the retention pass takes it, inside its purge of a tenant whose grace period is over ([features/operations.md](features/operations.md#retention)).
- **Platform steps.** `WorkflowService` runs a `RequiresPlatformOwner` transition only for a runtime with platform reach ([ADR 0014](adr/0014-session-on-the-auth-cookie-audited-impersonation.md)); everyone else is refused, the `Admin` role outside the platform tenant and a holder of every tenant permission included. For a runtime with platform reach `WorkflowService` works under the platform lease, so the owner moves a tenant (written only in system scope) or an entity of any tenant from the console.

  What such a step writes into another tenant, its state history and every row it adds or changes there, is stamped with that tenant's system account, while the history's `ActorId` keeps the owner. A record of a tenant that is Suspended, Pending deletion or Archived takes no step, the owner's included ([Tenant states and access](#tenant-states-and-access)).

### Transition access gate

Every transition runs through `WorkflowService` (`Staffora.Services/Workflow/WorkflowService.cs`, the gate in `WorkflowService.Judgement.cs`), which asks one gate before it locks anything. It answers a dialog footer's question "which steps may this user take" with the same gate (`GetAvailableTransitionsAsync`, one call per entity).

- A transition marked `RequiresPlatformOwner` is open only to platform reach, as above.
- One with a `RequiredPermission` is open only to a holder of that permission, checked through `PermissionService` (so a platform owner who is not impersonating passes, and an impersonating one has the target's permissions).
- One with neither is open to every signed-in user of the entity's tenant.
- **The whole plan.** Every transition the step's cascades will run on a child or grandchild is judged the same way, so an open delete that would carry the user through a child's step they may not take is refused as a whole, and the footer shows it as not allowed, naming that child's step. A refusal comes back as a failed `Result` with the reason, before anything is locked, and the entity is not touched.
- **Held to the plan.** The engine asks before every step it takes, cascaded ones included, and a step the gate did not judge (an action changed a child before its cascade ran, say) refuses and rolls back the whole transition, without another read of the permissions.
- **Who may store edits.** A step's gate says who may take it, not who may change the fields a dialog's edits carry with it: edits go with a step other than the state's save only for a user who also passes the gate of the step that stores edits in that state (its save; the edit-authority rule, the workflow skill's "Who may store edits"). So a registrar who holds `JobsCreate` alone hands a job over as it is stored, and a coordinator who holds `PerformCoordination` alone starts and confirms it as it is, whatever an edit past the dialog says.
- **Held tenants.** After the gate, a record of a tenant that is Suspended, Pending deletion or Archived takes no step, whoever asks (`WorkflowService.HeldTenant`), and the footer says so; the tenant's own steps stay open ([Tenant states and access](#tenant-states-and-access)).

### History is for owners

A state entity's history (who took which step, and when) is read only by an active system owner of the record's organization (`SystemOwners.IsActiveOwnerAsync`) or by a platform owner on the platform console (platform reach) ([ADR 0036](adr/0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md)).

- A platform owner who impersonates reads exactly what the impersonated user reads. A customer portal's tenant has no system owner, so only the platform owner reads a portal record's history.
- Everyone else (an administrator or coordinator who is no system owner, a staff member, a portal's users) sees the record's state and the steps its footer offers, never its history.
- The read goes through the entity's own service's `Get{Entity}HistoryAsync`, which checks the permission the entity's load checks, and then `StateHistoryService.GetHistoryAsync`, which enforces the owner rule centrally: anyone else is refused with `StateHistoryService.OwnersOnly` before anything of the record is read.
- A dialog asks the same question once as it first loads (`StateHistoryService.MayReadHistoryAsync`) and offers no last steps, no "Full history" and no history button to anyone else.
- `StateHistoryService` reads under the tenant filter (a platform owner on the console through the platform lease) and names only users of the entity's own tenant, describing anyone else.
- There is no workflow inspector.

Verified by: `StateHistoryServiceTests`, `StateEntityDialogTests`.

### Data access

Contexts come from `IApplicationDbContextFactory<ApplicationDbContext>`: `CreateDbContext(runtime)` for every service, the platform owner's included, and `CreateSystemDbContext()` for startup, Identity and session resolution. The rules, the exceptions and their tests are in [tenant-isolation.md](tenant-isolation.md) and [ADR 0013](adr/0013-tenant-isolation-in-the-data-context.md). What matters for authorization:

- **Finding a user before any tenant applies.** Users are tenant rows, so Identity's user store at sign-in and cookie validation (`ApplicationUserStore`), the session's first read of the signed-in user and the impersonation authorizer each open a lease of their own with its reason.
- **Identity's store and the tenant.** The store refuses to create, update or delete a user of another tenant than the circuit's effective one while the circuit has a user and no platform reach; with no user in the circuit (signing in) it writes any tenant's user on that tenant's behalf.
- **Sign-in bookkeeping** is the exception to that refusal: an update that changes only the failed-attempt count and the lockout end (`ApplicationUserStore.SignInBookkeepingColumns`) is saved from any circuit, always stamped with the user's tenant's system account, so wrong passwords tried from a session of another tenant still count and lock the account. Any other change from such a session, a password rehash included, stays refused.
- **Leases.** Leaving the tenant boundary inside a context takes a disposable, logged system-scope lease; the platform console's services open it through `BeginPlatformScope`, which opens only for a platform owner who is not impersonating. While impersonating, every service reads and writes the impersonated tenant only.

## Impersonation

A platform owner may act as an eligible user in **any** tenant, to see and do exactly what that user can ([ADR 0014](adr/0014-session-on-the-auth-cookie-audited-impersonation.md)).

### Eligibility

One server-side authorizer, `ImpersonationAuthorizer` (`Staffora.Services/Authorization/ImpersonationAuthorizer.cs`), decides from the database, never from claims or the circuit's flags, at start and again on every circuit whose cookie names an impersonation:

| Who | Rule |
|---|---|
| Acting user | Active, holds `AppRoles.Admin`, belongs to the platform tenant |
| Target | Active user of an Active tenant; in a tenant still in Setup, only its active system owners, so the platform owner may help finish its setup as one of them |
| Never a target | The acting user itself, another platform user (a user of the platform tenant, or a holder of the `Admin` role in any tenant, whose cookie would carry it), a tenant's system account |

Each refusal comes back as a failed `Result` with its reason and is logged. The dialog lists only eligible users (`ImpersonationAuthorizer.ListEligibleTargetsAsync`, which applies the same rules to the same facts), but the authorizer's decision is the guard: the start endpoint asks it before anything changes, and every circuit asks it again and ends a refused session.

### Start, switch and end

A circuit cannot set a cookie, so starting and ending are form posts to two endpoints that re-issue the auth cookie and redirect; the page that loads next opens a circuit that resolves the session from the new cookie (`Staffora.Blazor/Components/Account/ImpersonationEndpoints.cs`).

Both need a signed-in user and a valid antiforgery token (the forms carry `AntiforgeryToken`; a post without one gets 400 and changes nothing), and both hand the cookie to `ImpersonationService` (`Staffora.Services/Authorization/`), which decides and returns the principal to sign in with. The cookie keeps the platform owner's own id, name and security stamp; while impersonating it also carries the target's id and the id of the session's audit row (`ImpersonationClaims`), and the **target's** Identity roles in place of the owner's.

1. **Start.** `Staffora.Blazor/Components/Common/Dialogs/ImpersonateUserDialog.razor` lists eligible users with their tenant, searched by name, email or role and filtered by organization, through `ImpersonationService.ListEligibleTargetsAsync`, which is refused without platform reach.

   Each listed person is a button that posts the user's id to `POST /Account/Impersonation/Start`. The endpoint authorizes the target again, writes the audit row, re-issues the cookie with the impersonation and the target's roles, and redirects to `/`. A refusal there sends the browser to `/Account/AccessDenied` and leaves the cookie as it was.
   - A start for the target the cookie already impersonates, on its still open row (the button pressed again once the first response set the cookie), writes no second row and re-issues the cookie with the same one.
   - When the cookie cannot be issued after the row was written, the endpoint closes that new row again (`ImpersonationService.AbandonStartAsync`), so none stays open for a session that never began; when that start was a switch, the previous row was closed with it and the unchanged cookie's next circuit offers to end the impersonation.
   - Two posts sent together with the same cookie, before either response arrived, still write a row each, and the one whose cookie the browser drops stays open.
2. **While impersonating.** The effective user and tenant are the target's; permissions are the target's. Tenant pages name the effective user (`UserSessionService.EffectiveUserName` and `EffectiveEmail`), never the platform owner the cookie keeps (`UserName`, `Email`, which only the platform console and the account menu's "Signed in as" show). `AccountMenu` shows the impersonated user with an end button and no link to a profile.

   Because the cookie carries the target's roles and an impersonation, `/superadmin/*` is closed. Anything that re-issues the cookie from the user row keeps the impersonation: the security-stamp refresh carries it over (`ImpersonationClaims.CarryOver`), and so does `ApplicationSignInManager.RefreshSignInAsync`, so no path drops it while its audit row stays open.
3. **Switch.** Starting again while impersonating switches to the new target and closes the cookie's previous audit row in the same save.
4. **End.** `AccountMenu`'s end button posts to `POST /Account/Impersonation/End`, which closes the cookie's own audit row (a no-op when it is already closed or does not record this owner and target; when the close cannot be written, the end is refused and the cookie stays as it was), re-issues the cookie without the impersonation and with the platform owner's own roles, and redirects to `/superadmin/overview`. Ending only takes rights away, so a cookie that carries an impersonation may always end it; without one, only a platform owner may post it (a second press), and anyone else is forbidden.

**Every circuit checks again.** The session gate (`UserSessionService`) applies the cookie's impersonation only while the authorizer still allows the signed-in user to act as the target and the audit row the cookie names is open and records exactly that owner and target. A cookie that names anything else (an ended session, another session's row, a malformed claim) opens no session at all: the circuit reads and writes nothing, and the account menu offers only to end the impersonation. A target the authorizer refuses, and a platform owner whose own session is refused, also have that impersonation's own row closed. The claims name a session; they never grant one.

**Open circuits check again every five minutes.** A circuit already open keeps the session it resolved until its next revalidation ([Revalidation](authentication.md#revalidation)), which repeats the same check: an impersonation whose audit row was closed (ended in another tab of the same browser) or whose owner lost platform reach ends on that circuit too. So a row is stamped with an impersonator after its audit row ended for at most one revalidation interval, five minutes.

### Audit

- **Impersonation audit.** Each session writes a row (`ImpersonationAudit`, [database.md](database.md#impersonationaudit)): acting user, target user, target tenant, start time and end time (empty while running, or when the session lapsed through a sign-out or a closed browser).
  - `ImpersonationAuditWriter` writes it from the injected clock: a row at start, whose id the session keeps; a switch closes the session's own row and starts the next in one save; an end closes the session's own row, and writes nothing when that row is already closed.
  - Tabs of one browser share its cookie and so its session. A platform owner may still run several sessions at once in different browsers, so a row is always addressed by the id its cookie carries and one session never closes another's.
  - A refused start writes nothing, and a start whose row cannot be written does not start.
  - It is a platform table, not tenant data, and append-only: the data context shows and writes it only in system scope, where a row is only inserted or closed once (its empty end time given a value, nothing else changed), never deleted, and never touched by `ExecuteUpdate` or `ExecuteDelete`. No tenant reads it, the impersonated one included. The platform overview reads the newest rows under the platform lease, naming the platform owner, the target and the target's tenant.
- **Row stamping.** Rows created while impersonating record `CreatedById` = the effective (impersonated) user and `ImpersonatedById` = the real platform owner, written by `SaveChanges`. A user created while impersonating (through the user workflow's create step, `UserService.CreateUserAsync` or `CreateApplicationUserAsync`) records the same pair, since the step saves on the runtime's context.

  A row written into another tenant on that tenant's behalf is stamped with that tenant's system account and has no `ImpersonatedById`; a creator or editor from any other tenant, another tenant's system account included, is refused. The state history of a platform owner's transition on a tenant records the owner as `ActorId` and is stamped with that tenant's system account. Updates record the effective user in `ModifiedById`; the runtime keeps the platform owner's own tenant apart from the impersonated one, so ending impersonation returns to it. Stamping rules in full: [tenant-isolation.md](tenant-isolation.md).

### Edge cases

| Case | Behaviour |
|------|-----------|
| Target is inactive, a platform user, a system account or the acting user | Not listed; the start endpoint refuses it |
| Target's tenant is Suspended, Pending deletion or Archived | Refused at start; a live impersonation ends on its next circuit (its audit row closed) and on an open circuit at its next revalidation, within five minutes. The platform owner's own cookie stands, since every request judges the owner, whose tenant is the platform's, so the end button gives the console back ([Tenant states and access](#tenant-states-and-access)) |
| Target became ineligible after start | The claim is ignored on the next circuit, which opens no session; its audit row is closed and the account menu offers to end the impersonation, which restores the platform owner's cookie |
| Dialog cancelled | No request is sent; nothing changes |
| Start refused | Message shown inline in the dialog; dialog stays open. Refused at the endpoint (a race), the browser is sent to the access-denied page and the cookie stays as it was |
| Start or end posted without a valid antiforgery token | 400; nothing changes |
| Page reload or reconnect | The session resolves again from the cookie and re-authorizes the impersonation |
| Several tabs | They share the cookie and its audit row; each tab follows on its next load, an open tab at its next revalidation (within five minutes), and a copy of the cookie from before an end resumes nothing, since its row is closed |
| Start pressed twice | A second start for the target the cookie already impersonates, on its open row, writes no second row |
| Start's cookie cannot be issued | The new audit row is closed again and the cookie stays as it was |
| Platform owner lost the `Admin` role or the platform tenant while impersonating | The next circuit, and an open one at its next revalidation, ends the impersonation and closes its row |
| Sign-out while impersonating | The impersonation's own audit row is closed (only a row that is open and records that owner and target), then the cookie is cleared |
| Platform owner's own session refused while impersonating (deactivated, a new password or another security-stamp rotation, or a tenant state that no longer admits them) | The circuit is signed out, on its next load or at its next revalidation, and the impersonation's own audit row is closed (only a row that is open and records that owner and target). When the end cannot be recorded, a warning is logged, the row stays open and the session is refused all the same |
| Browser abandoned while impersonating | The audit row stays open until a circuit resolves that cookie again and ends it by one of the rules above; a cookie that is never presented again leaves its row open |
| Session that is not a platform owner's (an `Admin` outside the platform tenant included), or an impersonating one, requests `/superadmin/*` | Refused by `SessionPolicies.PlatformConsole`: the request is sent to `/Account/AccessDenied`; within a circuit `AuthorizeRouteView` renders `RedirectToLogin` |
| Platform owner opens a tenant page without impersonating | Refused by `SessionPolicies.TenantPages`, the same way |

## Tenant states and access

Only a platform owner moves a tenant ([PRODUCT.md](../PRODUCT.md), "Tenant lifecycle"), through the tenant workflow's steps (`TenantWorkflowAttribute`, every one `RequiresPlatformOwner`), from the console's tenant dialog; the platform tenant is never suspended or scheduled for deletion. What each state means for the tenant's people and data:

| | Setup | Active | Suspended | Pending deletion | Archived |
|---|---|---|---|---|---|
| Sign-in (password) | Only its active system owners; everyone else refused | Allowed | Refused | Refused | Refused |
| A cookie issued before, at its next request | Its active system owners' admitted; any other refused | Admitted | Refused, and deleted | Refused, and deleted | Refused, and deleted |
| A cookie issued before the move, once Active again | | | Refused (Suspend rotated the stamps) | Refused (Schedule deletion from Active rotated them) | |
| A new circuit (session gate) | A session for its active system owners; none for anyone else | Session | No session | No session | No session |
| An open circuit | Kept for its active system owners; anyone else signed out at its next revalidation | Kept | Signed out at its next revalidation (within five minutes) | Same | Same |
| Impersonation start | Only of its active system owners | Allowed | Refused | Refused | Refused |
| A live impersonation | Ends on its next circuit | Kept | Ends on its next circuit, an open one at its next revalidation; its audit row closed | Same | Same |
| Workflow steps on its records | Run | Run | Refused, whoever asks | Refused (read-only, the platform owner included) | Refused |
| Any other write of its data (a live circuit's role holding, setting, name, comment, custom field, notice read) | Written | Written | Refused, with the reason | Refused | Refused |
| In-app notices into it | Delivered | Delivered | Held (pending) | Held | Held |
| The platform console | Tenant dialog editable | Tenant dialog editable | Read-only but its steps (Reactivate, Schedule deletion) | Read-only but its steps (Cancel deletion, Archive) | Read-only, no step |

- **Where each is checked.**
  - Sign-in: `ApplicationSignInManager.CanSignInAsync` (every path asks it before a cookie), `CheckPasswordSignInAsync` (answers a refused user as any account until the password is right), and `SignInWithClaimsAsync`, where every path issues its cookie and which refuses the same users ([authentication.md](authentication.md#signing-in)).
  - Every request: `RequestStampCheck` with `ApplicationSignInManager.AdmitsCookieAsync`, which reads the cookie's user and their tenant's state in one query ([Every request](authentication.md#every-request)).
  - A circuit: the session gate (`UserSessionService`) when it starts, and the revalidation every five minutes.
  - Impersonation: `ImpersonationAuthorizer` at start and on every circuit whose cookie names one ([Eligibility](#eligibility)).
  - Steps: `WorkflowService` ([Transition access gate](#transition-access-gate), `WorkflowService.HeldTenant`), judged again inside the step's transaction.
  - Every other write of the tenant's data: the data context (`ApplicationDbContext.HeldTenant.cs`, in every save and, through `BulkUpdateGuard`, every bulk statement outside system scope).
  - Notices: `OutgoingMessageDispatcher`.
- **The platform owner's own access** never depends on a tenant's state. An impersonating owner's cookie carries the owner's own id and stamp, so the per-request check judges the owner (whose tenant is the platform's, always Active) and admits it: only the impersonation ends, on its next circuit, and the end button gives the console back.
- **Data held.** No workflow step changes a record of a Suspended, Pending deletion or Archived tenant (`TenantStates.HoldsData`), whoever asks: not the platform owner on the console (so Pending deletion is read-only for the owner, and Archived has no access), and not a live circuit of the tenant's own user, which keeps its session for up to one revalidation interval.
  - The footer answers every such step with the reason ("The tenant is suspended, scheduled for deletion or archived, so its records are held and no step changes them."), and a create is refused, its insert rolled back with the step. The tenant's own steps belong to no tenant and stay open, so the owner reactivates it or takes it off the deletion schedule.
  - No other write changes its data either: a live circuit of the tenant's own user is refused at once whatever it writes (a role holding given or taken away, a tenant setting, a comment, a custom field, notices marked read), with the reason "The organization's account is suspended, scheduled for deletion or archived, so its records are held and nothing in it can be changed." (`TenantStates.HeldDataReason`).
  - The check is one, shared and fail closed, in the data context: every save that writes the tenant's data and every bulk statement outside system scope reads the tenant's state inside its transaction under a shared lock on the tenant's row that the transaction keeps, so a suspension waits for every write in flight and a write that comes after it reads the new state; no write commits after a suspension ([tenant-isolation.md](tenant-isolation.md), held tenants and lock order). System scope (sign-in bookkeeping, the ensures, the dispatcher's marks, the platform lease) is not held there; a workflow step judges its record's tenant on the same locked read, so the console is held too.
  - A notice of a held tenant is sent on no channel and stays pending, and the integration outbox's hosted dispatcher delivers it once the tenant is Active again, an archived tenant's never. It holds an integration message the same way while its sending or its receiving tenant is not Active (`TenantStates.ExchangesMessages`).
- **Setup** is not held: the tenant is set up by working in it, by its system owners, the only people of it who sign in or are impersonated, so its records take their steps and its notices go out. It becomes Active by the platform owner's Activate, which needs an active system owner ([System owners](#system-owners)).
- **Everyone signs in again.** Suspend and Schedule deletion (from Active), the steps that stop an Active tenant's users from signing in, rotate the security stamp (and renew the concurrency stamp) of every user of the tenant, in one bulk statement inside the step's own transaction (`ITenantSignOut`, `TenantSignOut`, allow-listed in `DataContextScanTests`), stamped by the tenant's system account. So a cookie issued before the step stays refused for good, also once the tenant is reactivated, and a copy of a user read before it cannot write an old stamp back.
- **Reactivation** (Suspended to Active) restores sign-in, impersonation, steps, every other write and the delivery of held notices. Everyone signs in afresh, since no cookie from before the suspension stands (a user deactivated or given a new password meanwhile stays refused by their own stamp and state).
- **Purge.** A tenant in Pending deletion longer than the grace period (`Retention:PendingDeletionGraceDays`, 30 days, counted from its latest Schedule deletion in its history) has all its data removed by the retention pass and is moved to Archived by the system step `tenant-purged`, keeping its `Tenants` row (and a user the impersonation audit names). A cancellation of the deletion that comes while the purge runs waits for it and then finds the tenant Archived; nothing else is ever purged ([ADR 0042](adr/0042-retention-and-the-tenant-purge.md), [features/operations.md](features/operations.md#retention)).

Verified by: `TenantLifecycleAccessTests`, `TenantLifecycleSignInTests`, `TenantWorkflowTests`, `TenantLockOrderTests`, `TenantConsolePageTests`, `RetentionTests`, `TenantPurgeGuardTests`, and the browser journey `tests/e2e/tenant-lifecycle.spec.ts`.

## References

- `Staffora.Core/Entities/Tenancy/RolePermission.cs`, `PermissionArea.cs`, `TenantRole.cs`, `TenantRolePermission.cs`, `RolePresets.cs`, `RolePermissions.cs`, `ITenantOwnerGuard.cs`
- `Staffora.Core/Entities/Organization/OrganizationUnitUserRole.cs`, `OrganizationStructurePolicy.cs`
- `Staffora.Data/SystemOwners.cs`, `TenantOnboardingWriter.cs`, `ApplicationDbContextFactory.cs`, `ApplicationDbContext.TenantIsolation.cs`
- `Staffora.Services/Services/TenantOnboardingService.cs`, `TenantOwnerGuard.cs`, `UserAccountGuard.cs`
- `Staffora.Services/Authorization/AppRoles.cs`, `PermissionService.cs`, `ImpersonationAuthorizer.cs`, `ImpersonationAuditWriter.cs`, `ImpersonationService.cs`, `ImpersonationClaims.cs`, `SessionPolicies.cs`
- `Staffora.Core/Execution/RuntimeExecutionContext.cs`, `IApplicationDbContextFactory.cs`
- `Staffora.Core/Entities/Platform/ImpersonationAudit.cs`
- `Staffora.Blazor/Components/Common/PermissionView.razor`, `Staffora.Blazor/Components/Common/Dialogs/ImpersonateUserDialog.razor`, `TenantOnboardingDialog.razor`, `Staffora.Blazor/Components/Account/ImpersonationEndpoints.cs`
- Sign-in and session: [authentication.md](authentication.md)
- Deviations from these rules: [known-issues.md](known-issues.md)
