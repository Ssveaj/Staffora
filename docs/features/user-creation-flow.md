# Users, roles and the organization tree

Two concerns, two flows:

1. **The user**: creating an `ApplicationUser`, editing it, deactivating and reactivating it. A user is a state entity: its lifecycle is the user workflow (`ApplicationUserWorkflowAttribute`), and its only UI is the user's state-entity dialog, `UserDialog` ([ADR 0009](../adr/0009-state-entity-dialog-only-and-workflow-governed-attributes.md)).
2. **Role assignment**: giving an existing user a `TenantRole` on an organization unit (the `Company` root means the whole company). The role's permissions apply tenant-wide; the unit records where the person works ([ADR 0021](../adr/0021-one-organization-tree-per-tenant.md)). Done from the user dialog's Roles view, a unit's `RoleAssignmentView` on the organization page, or a role's dialog.

A user's `TenantId` only says which tenant the user belongs to. Permissions come from the roles an active user holds (`OrganizationUnitUserRole` rows); how they are resolved is in [authorization.md](../authorization.md), sign-in in [authentication.md](../authentication.md), and the tables in [database.md](../database.md).

## The user's lifecycle

```text
Create --Create--> Active --Deactivate--> Inactive --Reactivate--> Active
Active: Save, Unlock (while locked out), New password
```

| Step | Key | Permission | Also |
|---|---|---|---|
| Create | `user-create` | `UsersCreate` | The first step, Create to Active, in one transaction with the insert; first and last name, email and user name required. The initial password is made by the service and shown once in the footer. |
| Save | `user-save` | `UsersEdit` | The active user's designated save step (the leave rule saves through it). |
| Unlock | `user-unlock` | `UsersEdit` | Offered only while the user is locked out; clears the lockout and the failed count. The platform console may take it too, on the tenant's behalf. Within the grant ceiling, never for a platform owner or a system account. |
| New password | `user-new-password` | `UsersEdit` | An account recovery reset, confirmed with what it does: a new password shown once to whoever took it, a new security stamp (every cookie issued before is refused at its next request and opens no circuit), the lockout lifted, the user's Identity tokens removed and two-factor sign-in switched off, all in the step's transaction. Not for one's own account unless one is a system owner (greyed with the reason; [ADR 0034](../adr/0034-no-self-service-account-pages.md)), a system account, a platform owner, or a user who holds more than the session (greyed with the reason). |
| Deactivate | `user-deactivate` | `UsersEdit` | Confirmed; rotates the security stamp. Not for one's own account (greyed with the reason), a system account, a platform owner, or a user who holds more than the session (greyed with the reason). |
| Reactivate | `user-reactivate` | `UsersEdit` | Not for a platform owner, or a user who holds more than the session (greyed with the reason). |

- **Where steps run.** Every step needs the step's session in the user's own tenant (a tenant's user, or a platform owner impersonating one), except those the platform console may take to help with signing in: Unlock, New password and Save of the name, email address and user name ([ADR 0030](../adr/0030-platform-owner-corrects-a-users-sign-in-details.md)).
- **Grant ceiling.** A Save that changes the user name or email address of a user who holds more than the session is refused, and not offered by the dialog ([authorization.md](../authorization.md#grant-ceiling)).
- **Concurrency.** Every step renews the row's concurrency stamp, so a copy Identity read before (a sign-in) is refused instead of writing back over it.
- **The console** reads a tenant's users read-only otherwise and creates none ([ADR 0012](../adr/0012-platform-owner-and-tenant-onboarding.md)).
- **No delete.** A user is never deleted: they are deactivated.
- **System owners.** A tenant keeps an active system owner: the last one is not deactivated, and only a system owner changes another owner's password, lockout or access ([authorization.md](../authorization.md#system-owners)).
- **The first user.** A new tenant's first user, its first system owner, is created by tenant onboarding on the platform console (`TenantOnboardingService`), judged by the same rules as the create step (`UserService.NewUserRowAsync`), with an initial password shown once to the platform owner ([Onboarding](../authorization.md#onboarding)); every later user of the tenant is created inside it.

**Fields per state.** In Create and Active the person's fields (names, email, user name, phone) and custom fields are edited, the four identifying ones required; an Inactive user is kept as they were and reactivated to be changed. Identity's own columns (password hash, stamps, normalized name and address, lockout and counters) stay editable in every state for Identity and are never edited through the dialog.

## Where the flows appear

| View | Opened from | What it does |
|---|---|---|
| `Pages/TenantAdmin/Users.razor` | The tenant's administration | The tenant's users on the list surface, each row with its state; a row opens `UserDialog`, "New User" opens it in create mode ([List view](lists-and-dialogs.md#list-view)). |
| `OrganizationTreeView` | Tenant admin page `Pages/TenantAdmin/OrganizationUnits.razor` | The selected unit's header (name, a facts line, "Open unit" (the unit's state-entity dialog) and "Add unit below"), and, for a reader with `RolesView`, a **People** tab with its `RoleAssignmentView` beside an **Information** tab with the description ([Organization tree](#organization-tree)). |
| `TenantConsoleUsers` (a tenant's console, `Pages/SuperAdmin/TenantConsole.razor`) | The platform console, a tenant's Users tab | The tenant's users on the list surface, read through `PlatformConsoleService` under the platform lease; a row opens `UserDialog` read-only but for the sign-in help the console gives. The console creates no users and changes no units, roles or holdings, for a tenant in any state. |

## Components

| Component | Location | Purpose |
|---|---|---|
| `UserDialog` / `UserDialogContext` | `Components/Common/Dialogs/` | The user's state-entity dialog. Info: first and last name, email, user name, phone and the tenant's custom user fields, each shown, locked and marked by the workflow for the persisted state, and read-only for a reader who may not change the user. Roles: the user's role holdings (a child list, once the user exists), with "Add role" and a holding's "Remove role" for whoever may manage holdings. Opened with `OpenAsync(dialogService, userService, userId)` (also the link's opener, token `user`) or `OpenCreateAsync(dialogService, userService)`. |
| `AddRoleHoldingDialog` | `Components/Common/Dialogs/` | A role holding to give, from `RoleHoldingChoices` (only the roles within the reader's grant ceiling, the units in use, the active people): opened for a user (a role and a unit chosen), a role (a person and a unit) or a unit (a person and a role); gives it through `RoleHoldingService.AssignUserToRoleAsync`. |
| `RoleHoldingDialog` | `Components/Common/Dialogs/` | One holding: its holder (in a role's or a unit's list), role and unit, and "Remove role" for whoever may manage holdings, when its role is within their grant ceiling. |
| `RoleAssignmentView` | `Components/OrganizationComponents/` | The people holding a role on the selected unit, one row each with their roles as chips, filtered by role when more than one role is held there. Whoever may manage holdings gets one **Add person** (`AddRoleHoldingDialog` for the unit) and, on each role within their grant ceiling, a remove button confirmed first; on a grouping it says why it holds none. |
| `TenantRoleDialog` / `TenantRoleDialogContext` | `Components/Common/Dialogs/` | The role's state-entity dialog. Info: name and description. Permissions: one check box per permission (`StatePermissionMatrix`), a permission the reader does not hold disabled. Holders: the people holding it (a child list), with "Add holder" (a person and a unit, the Company root by default; groupings are not offered) for whoever may manage holdings, when the role is within their grant ceiling. Its Delete is refused, with the reason, while anybody holds the role. |
| `UnitTree`, `OrganizationTreeView` | `Components/OrganizationComponents/` | The tenant's organization tree and the organization page's tree view ([Organization tree](#organization-tree)). |

## Service methods

| Method | Service | Purpose |
|---|---|---|
| `CreateApplicationUserAsync(user, transitionKey, ct)` | `UserService` | The dialog's create: the row built from the dialog's values with Identity's columns (stamp, normalized names, lockout, hash of an initial password the service makes), judged by Identity's user and password rules and the custom fields' rules, then created with its first step through `WorkflowService.CreateAndTransitionAsync`. The answer's message carries the password, shown once. |
| `CreateUserAsync(user, tenantId, password)` | `UserService` | The same create with a chosen password, in the session's own tenant (fixtures, tests). Identity's own `UserManager.CreateAsync` is no way around it: it is refused from any session with a signed-in user, and only the setup paths call it (`UserCreationScanTests`). |
| `LoadApplicationUserUpdaterAsync(id, ct)` | `UserService` | The dialog's one load: `UsersView` (or one's own account), the user through its workflow graph, its updater excluding Identity's own columns. |
| `UpdateApplicationUserAndTransitionAsync(updater, key, comment, ct)` | `UserService` | The dialog's edits with a step, through `WorkflowService.SaveAndTransitionAsync`; `UsersEdit`; the name and address trimmed and normalized, a changed one judged by Identity's rules. |
| `GetApplicationUserEditorOptionsAsync(userId, ct)` | `UserService` | Whether the reader may change the user's fields and manage their holdings, and the choices for a new holding. |
| `QueryApplicationUserRoleHoldingsAsync(userId, query, ct)` | `RoleHoldingService` (through `UserService.Holdings`) | The Roles view's page of holdings; `RolesView`. |
| `QueryApplicationUserStaffUnitsAsync(userId, query, ct)` | `UserService` | The Staff view's page of the units the user belongs to as a staff member; `ResourcesView`. The editor options name the user's staff record (`UserEditorOptions.Staff`) for a reader with `ResourcesView`, read again with every load, so the view is offered only for a staff member. |
| `QueryAssignmentsOfUserAsync(userId, query, ct)` | `PatternAssignmentService` (through `UserService.PatternAssignments`) | The Schedule view's page of the work patterns given to the staff member; `ResourcesView` or `AssignWorkPatterns`, the view offered with `ResourcesView`. |
| `QueryAbsencesOfUserAsync(userId, query, ct)` | `ResourceAbsenceService` (through `UserService.Absences`) | The Absence view's page of the staff member's absences; `ViewResourceAbsence`, `ManageResourceAbsence` or `ApproveResourceAbsence`, the view offered with `ViewResourceAbsence`. |
| `GetRoleHoldingChoicesAsync(tenantId)` | `RoleHoldingService` | The roles within the reader's grant ceiling (and, apart, the ids of those beyond it), the units that take a holding and the people who take one, for whoever may manage holdings. |
| `CanManageRoleHoldingsAsync(tenantId)` | `RoleHoldingService` | The check every role-holding screen gates its add and remove on: `RolesEdit`, in the session's own tenant, not the platform tenant. |
| `AssignUserToRoleAsync(userId, organizationUnitId, tenantRoleId, tenantId)` | `RoleHoldingService` | Creates an `OrganizationUnitUserRole` after that check: the holder an active person of the tenant, the unit and the role the tenant's own, the unit active and no grouping, no second holding of the role on the unit, and the role within the session's grant ceiling, each refused with its reason. |
| `RemoveUserFromRoleAsync(assignmentId)` | `RoleHoldingService` | Deletes the holding after that check, when its role is within the session's grant ceiling. |

## Permissions

| Action | UI gate | Service check |
|---|---|---|
| Create user | `UsersCreate` ("New User"), the Create step | `UsersCreate`, in the session's own tenant |
| Edit, deactivate, reactivate, unlock, new password | The footer's steps, offered by the access gate and the workflow's conditions (the grant ceiling among them) | `UsersEdit`, the step's gate and conditions |
| Add or remove a role holding | `RoleHoldingService.CanManageRoleHoldingsAsync` (the user dialog's editor options, `RoleAssignmentView`, the role dialog), and the role within the reader's grant ceiling (`RoleHoldingChoices`) | The same check (`RolesEdit` in the session's own tenant), and the grant ceiling |
| See role holdings | `RolesView` (the Roles view) | `RolesView` |
| See a staff member's record and units | `ResourcesView` (the Staff view, offered only for a staff member) | `ResourcesView` |
| Make someone a staff member | "New staff member" on the Staff page (`RolesEdit`), or any role holding of a role whose holders are staff members | The role-holding service's checks; the staff record follows in its transaction |

The pick lists for a new holding come with the permission to assign (`GetRoleHoldingChoicesAsync`), not with `UsersView` or `OrganizationUnitsView`, so a role editor who may not list users or units still has people and units to choose from.

## Flows

### Create a user and give them a role

1. On **Users**, click **New User**; `UserDialog` opens in create mode for the current tenant.
2. Fill in the first and last name, email and user name; **Create** appears once they are there. It creates the user Active and shows the initial password once in the footer: give it to the user.
3. Open the **Roles** tab and click **Add role**; choose the role and the unit (the Company root, the whole company, by default) and add it. Its permissions apply across the whole tenant.

### Give an existing user a role from the organization page

1. Select a unit in the tree (the Company root for the whole company); its **People** tab is open.
2. Click **Add person** and choose the person and the role: the drop-downs list the tenant's active people and the roles within the reader's grant ceiling. A unit out of use (inactive or archived) has no **Add person**: it shows why instead, since it takes no new role holdings ([authorization.md](../authorization.md#role-holdings-and-inactive-units)).

### Make someone a staff member

Giving a user a role whose holders are staff members (the Staff preset, "Holders are staff members" in the role dialog) on a unit in use makes them a staff member of that unit ([ADR 0008](../adr/0008-users-become-resources-through-a-role.md)). `RoleHoldingService.AssignUserToRoleAsync` saves the holding and calls the `ResourceLinker` in the same transaction, which creates the user's staff record (`Resource`, one per user) or moves it to Active, and makes it a member of the unit.

On **Staff** (`Pages/Staffing/StaffMembers.razor`, `ResourcesView`), **New staff member** (for whoever may give roles) picks the person, the unit and, when the tenant has more than one, the role (`AddRoleHoldingDialog.OpenForNewStaffAsync`) and gives it through the same service. A row of the Staff list opens the staff member's user dialog, whose **Staff** view shows the record's status and units.

Taking the last such role away, deactivating the user, turning the role's flag off or taking the unit out of use takes them out of service (Inactive, history kept), each in its own transaction.

### Remove a holding

Open the holding's row in the user dialog's Roles view and click **Remove role**, or click the remove (×) button next to the user under the role on the organization page.

### Deactivate and reactivate a user

Open the user and click **Deactivate** (confirmed): they are signed out at their next request, cannot sign in or be impersonated, take no new roles, and their roles grant nothing. A staff member's record goes out of service in the same step and back into service with **Reactivate** while they still hold a staff role. **Reactivate** brings them back; a cookie issued before the deactivation stays refused.

### Give a user a new password

Open the user and click **New password** (confirmed): an account recovery reset. The password shown once in the footer is the user's only way in (a password is the one way to sign in, [ADR 0034](../adr/0034-no-self-service-account-pages.md)); every cookie issued before is refused at its next request. A user who holds more than you, a platform owner and your own account (unless you are a system owner) are not offered it (greyed with the reason).

## Organization tree

Each tenant has one tree of units under its `Company` root ([ADR 0021](../adr/0021-one-organization-tree-per-tenant.md); the model and its structure policy are in [database.md](../database.md#organizationunit)). One component shows it wherever a tree is shown.

### The tree component

`UnitTree` (`Components/OrganizationComponents/`) takes the tenant's units as read-only `OrganizationUnitNode`s, arranges them by their parents (siblings by name, the root first) and raises `SelectedChanged` when a unit is selected; the page keeps the selection (`SelectedId`).

- **Arrangement.** `OrganizationUnitOutline` (`Staffora.Core/Models/`) places each unit once by a walk down from the top, so no walk loops on any data. A unit whose parents never reach the top (a cycle, or below one, which the data context does not let anyone store) is listed at the top level after the tree, on its own, marked "(not linked to the tree)", and a warning is logged, so it is shown rather than lost and can be moved back. An inactive unit is marked "(inactive)" in its row.
- **Markup.** It is its own markup, not Radzen's tree, whose expanders wrap child nodes in a region that breaks the tree roles. It follows the WAI-ARIA tree pattern: a `ul` with the role `tree` and a name (`Label`); per unit a row with the role `treeitem` and `aria-level`, `aria-setsize`, `aria-posinset`, `aria-selected` and, for a unit with children, `aria-expanded`; the children in a `ul` with the role `group` beside the row, which the open row owns (`aria-owns`), both in an `li` with the role `none`, as the WAI-ARIA navigation tree example does.

  So a row holds only its own text and is named by its content (the unit's name and type), and axe finds nothing in it: a row that held its children would be named by their text too, and one named by a label instead would hide its visible text (`label-content-name-mismatch`).
- **Keyboard.** One row is in the tab order at a time (a roving `tabindex`: the focused row, else the selected, else the root). Down and Up move between the visible rows, Right opens a closed row or moves into an open one, Left closes an open row or moves to the parent, Home and End go to the first and last row, Enter and Space select; a click selects, a click on the chevron opens or closes. The focus moves on the server (`ElementReference.FocusAsync` after the render), and `wwwroot/scripts/unit-tree.js` only keeps those keys from also scrolling the page while a row has the focus.

Verified by: `UnitTreeTests` (bUnit: roles, states and keys) and the accessibility sweep, which works the tree from the keyboard in a browser (`tests/e2e/lib/organization-tree.ts`).

### The organization page

`Pages/TenantAdmin/OrganizationUnits.razor` ("Organization units", gated on `OrganizationUnitsView`) hosts, in its tree view (the default; its list view is on the [list surface](lists-and-dialogs.md#list-view)), `OrganizationTreeView`.

- **Layout.** The tree beside the selected unit's name and a facts line (its type, "Below Company A" or "The whole company", its state when out of use, its number of people and of units right below it), a **People** tab with its `RoleAssignmentView` for a reader with `RolesView`, and an **Information** tab with the description. The Company root is selected when the page opens. The tree view is one bordered surface (radius 12, no shadow): the tree in a muted pane on the left (each unit's type a small chip, the selected unit marked by an accent bar), the selected unit's header and its tabs on the right, stacked below 900 px with the tree first.
- **Actions.** "Open unit" opens the selected unit's state-entity dialog, and "Add unit below" (`OrganizationUnitsCreate`, offered on a unit in use) the same dialog in create mode, below the selected unit ([Dialogs](lists-and-dialogs.md#dialogs), "The unit and role dialogs"); the tree reloads when either changed something.
- **Pickers.** The unit dialog's parent drop-down offers the units it may move below (every unit in use the tree reaches except itself and the units below it, each named by its path from the root, "Company A › North › Sales", in tree order), and its type drop-down the types below the root (the root keeps its own).

  Role-holding pickers name the unit a role is held on by its path the same way, offering only units in use that may hold a role (no grouping), and only active users. On a unit out of use, the "Add person" of `RoleAssignmentView` is replaced by the reason ([authorization.md](../authorization.md#role-holdings-and-inactive-units)). A row of the tree marks a unit out of use "(inactive)" or "(archived)".
- **On the platform console.** A tenant's console Overview (`TenantConsoleOverview`) shows the same tree beside the selected unit's type, state and description, read-only for a tenant in any state, as every tab of the console is: it offers no step on a unit, no "New Role", no "Add user" and no removal of a holding (a role's row on its Roles tab opens its dialog read-only), since the platform owner changes a tenant's people and roles only by impersonating one of its users ([ADR 0012](../adr/0012-platform-owner-and-tenant-onboarding.md)).

### One parent, no cycle

- **One parent.** A second parent cannot be stated: a unit has exactly one `ParentId` column and no link table exists, the structure the policy judges holds one link per unit, and every path that changes a parent replaces it (a move).
- **No cycle.** A cycle is refused with its reason by the policy (`OrganizationStructurePolicy`), by the services under the tenant's structure lock when two moves cross, and by the data context for a write past the services.

Verified by: `OrganizationUnitModelTests`, `OrganizationStructurePolicyTests`, `OrganizationTreeTests`, `OrganizationStructureTests`.
