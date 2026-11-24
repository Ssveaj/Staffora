# Customers and the customer portal

A tenant keeps its customers and the contacts it deals with there. A staffing company may give a customer a portal of its own: a managed tenant where the customer's people order staff, and their orders reach the staffing company as jobs ([PRODUCT.md](../../PRODUCT.md), Actors and Core flows 3 and 5; [ADR 0006](../adr/0006-course-staffing-domain.md), [ADR 0007](../adr/0007-cross-tenant-ordering-customer-portal.md)). The cross-tenant transport is the [integration outbox](operations.md#integration-outbox); tenant isolation and system-scope leases are defined in [tenant-isolation.md](../tenant-isolation.md); permissions in [authorization.md](../authorization.md#rolepermission).

## Customers and contacts

A customer is a [Customer](../database.md#customer), the organization jobs are staffed for. The people there the coordinator deals with are its contacts, each a [CustomerContact](../database.md#customercontact) with its own dialog.

- **Setting a customer up.** "New customer" on the Customers page opens the customer's dialog in create mode. Its Save creates the customer as a draft (Create), which offers its Contacts view. "Add contact" opens a contact's own dialog in create mode on top, whose Save adds the contact as new (Create). Activate puts the customer in use, then each contact; a contact's Activate needs its customer active. The services also accept Activate as the create of either (one step), and refuse any other first step.
- **Out of use.** Deactivating a customer takes every active contact out of use with it, each through its own Deactivate with its own history (a cascade, [ADR 0016](../adr/0016-deletion-cascades-through-child-workflows.md)); a new contact stays as it is. An inactive customer and its contacts are kept as they are: no field changes, and no contact is added (`CustomerNotInactiveReason`) or reactivated (`CustomerActiveReason`) until the customer is reactivated; its contacts are then reactivated one by one. An inactive customer therefore never has an active contact.
- **Deleting.** A draft customer, and an inactive customer nothing uses, is deleted with every contact it has, each through its own Delete with its own history and access gate (a cascade into the contacts' delete state, which leaves none behind). A contact is deleted alone from the same two states.
  - What uses a customer or a contact refuses the delete through a registered delete blocker (`IDeleteBlocker`): jobs register theirs (`JobsBlockCustomerDelete`, `JobsBlockContactDelete`; [Jobs, timetable and slots](jobs-and-staffing.md#jobs-timetable-and-slots)). A blocker of contacts refuses the customer's delete too, since the cascade asks about each contact.
  - A unit a customer's portal jobs go to is not deleted (`CustomersBlockUnitDelete`).
- **Names.** A customer's name is unique in its tenant (the service's check and `IX_Customers_TenantId_Name`). Contacts' names are not unique; two people may share one.
- **One writer at a time per tenant.** Every customer and contact step first takes the tenant's customer lock (`AppLock.Customers`, with the tenant's row held before it), so a contact judged on its customer's state and a customer step that moves its contacts never cross. A lock not granted in time refuses the step with the lock's busy reason ([Application locks](../database.md#application-locks)).
- **The portal link.** An active customer without a portal takes "Enable portal" (`customer-enable-portal`, `CustomerPortalEnable`; [Customer portal](#customer-portal)). The step links it to a new managed tenant (`Customer.LinkedTenantId`) and to the unit of this tenant its portal's jobs go to (`Customer.DefaultUnitId`); both are written by the step alone, and no state of the workflow opens them to a dialog. A customer with a portal keeps it whatever its state, and is not deleted (`KeepsItsPortalReason`).
- **Who.** `CustomersView` or `CustomersManage` reads; `CustomersManage` keeps (every step, "New customer", "Add contact"). A reader sees both dialogs read-only, with no step and no "Add contact". The platform console keeps no tenant's customers.
- **Pages.**
  - The Customers page (`/staffing/customers`, Staffing in the navigation): `CustomerList` on the one list surface, searched by name or description, filtered by state, sorted by name, each row with its contacts ("2 (1 active)"), the query kept in the address.
  - The customer dialog (`CustomerDialog`, token `customer`): Info (name, description) and Contacts (`CustomerContactList`, rows opening each contact's dialog on top).
  - The contact dialog (`CustomerContactDialog`, token `contact`): whose contact it is, the names, title, email address and phone.
- **No custom fields.** Customers and contacts take no custom fields. Adding a target is a change of its own (the definition and value tables, and the custom field panel).

Verified by: `CustomerWorkflowTests`, `CustomerLifecycleTests`, `CustomerDialogsTests`, `DeleteCoverageTests`, `ReadSweepTests`, `tests/e2e/customers.spec.ts` and the accessibility sweep.

## Customer portal

A staffing company gives one of its customers a portal: a managed tenant where the customer's people (customer administrators and orderers) order staff, and their orders reach the staffing company as jobs ([PRODUCT.md](../../PRODUCT.md), Actors; Core flows "Ordering" and "Following up"; [ADR 0007](../adr/0007-cross-tenant-ordering-customer-portal.md)).

### Enabling it

The customer dialog of an active customer without a portal offers "Enable portal" to a holder of `CustomerPortalEnable` (the System owner role holds it; the Coordinator preset does not).

- **The form.** The body asks first, in a form dialog (`EnableCustomerPortalDialog`, through `OnBeforeTransition`; Cancel abandons the step): the unit in use of this tenant the portal's jobs go to (the Company root listed first and chosen), and the portal's first customer administrator (first and last name, and email address, which is their user name).
- **The service.** The step goes to `CustomerService.EnableCustomerPortalAsync`: `CustomerPortalEnable` in the session's own tenant (never the platform console). The administrator's names and address are held to their columns and judged by Identity's rules exactly as the user workflow's create judges a user, with an initial password made there (`UserService.NewUserRowAsync`, `UserPasswords.Generate`); the unit must be one in use of this tenant. Then the customer workflow's step runs through `WorkflowService.SaveAndTransitionAsync`. It takes no edits: it is refused while the dialog holds any, or when the customer changed since the dialog opened.
- **Provisioning.** The service prepares the portal on the scoped `CustomerPortalProvisioner` (`ICustomerPortalProvisioner`, resolved by the step as the user workflow's New password resolves its password issuer). The step's action links the customer to a new tenant id and the unit.
- **The follow-up.** Once the customer is saved, inside the same transaction and customer lock, the step's follow-up (`CustomerPortalFollowUp`, an `IStepFollowUp`) has tenant onboarding create that tenant (`TenantOnboardingService.OnboardManagedTenantAsync`):
  - Active from the start, naming this tenant as its staffing company (`Tenant.ParentTenantId`).
  - What every tenant gets: system account, Company root and pinned time zone (`TenantService.GiveNewTenantItsOwnRowsAsync`).
  - Its portal roles (`RolePresets.Portal`: Customer administrator and Orderer, no System owner role), and the administrator holding the customer administrator role on the portal's own Company root (`TenantOnboardingWriter.WriteManagedAsync`).
  - Its supplier party (`Supplier`, the reciprocal of the customer: the staffing company's name and tenant, Active).
- **On whose behalf.** Every row of the portal is written on its behalf, stamped with its system account and no impersonator, and so is every history entry, whose actor is the portal's system account (`StateHistoryWriter.AddOnBehalfOf`). No row of the portal names a user of the staffing company, not even by id; the staffing company's user is recorded in the customer's own history, by its step.
- **The tenant's name.** The tenant takes the customer's name or, when another tenant holds it, the name with " (2)", " (3)" and so on. Nothing tells the staffing company, and the portal's Company root, which its people read as their organization, keeps the customer's own name, so they never see the number either.
- **Refusals.** A staffing company still in setup enables no portal, which would be in use at once (`CustomerService.PortalRequiresActiveCompany`; a held company is refused by the step under its tenant row's lock). A failure anywhere rolls back everything, the customer's link included.

### The password, once

The administrator's initial password comes back once, in the answer (`EnabledCustomerPortal`, whose text leaves it out) and in its message, which the dialog shows once in its footer (`ShowOnce`). It is stored only as Identity's hash and never logged. Nobody can show it again; a customer administrator gives another of the portal's people a new password, and a second administrator gives the first one theirs ([The portal's users](#the-portals-users)).

### Twice, at once, and afterwards

- A customer whose portal is enabled no longer takes the step (`PortalNotEnabledReason`, judged under the customer lock), so a second click, a second editor or a retry finds the portal there and writes nothing.
- An address held by another user of any tenant, including one taken at the same moment, is answered with one message (`UserService.SignInNameUnavailable`).
- A second enabling of the same customer in the same session while the first one's step still runs (the circuit runs a second click meanwhile) is refused (`CustomerService.PortalBeingEnabled`) and leaves the first one's preparation alone, so the first creates the administrator whose password it answers.
- A customer deactivated afterwards keeps its link, and its portal stays Active (its people still sign in; sending an order judges the customer's state). Reactivated, it has the same portal. It is never deleted while it has one (`KeepsItsPortalReason`).
- The dialog says where the portal stands in terms of this tenant's own unit only ("Customer portal: enabled. Jobs requested through it go to Head office."), never the portal tenant's name or id.

### The edition

A tenant's edition is derived from its own row, never stored (`TenantEditions.Of`): a managed tenant (one that names a staffing company) is a customer portal, every other tenant a staffing company. The rule reads the portal's own row only, so it holds whatever the other tenant does and fails closed. Each edition grants only its own permissions (`TenantEditions.Caps`) whatever a role names: a portal the `Portal` area (`OrdersView`, `OrdersCreate`, `OrdersSend`, `OrdersCancel`, `CustomerUsersManage`), a staffing company everything but it ([authorization.md](../authorization.md#the-edition-ceiling)).

### The portal's pages

A page is part of the portal only when it carries `[CustomerPortalPage]` (`Layout/CustomerPortalPageAttribute.cs`): the portal's own pages (`Pages/Portal/`, all of them through its `_Imports.razor`), the start page, one's own profile (`/me/profile`, read-only), sign-in and its system pages (`Components/Account/Pages/_Imports.razor`) and the system pages. `CustomerPortalPageScanTests` holds the allow-list.

Every other page belongs to a staffing company. A portal session reaching one (a link, a typed address, an impersonating platform owner) sees "Not part of your portal" in its place: a `SystemPage` card with the way to the portal's home. The page never renders or reads anything (`PortalPageGuard`, around the page in `MainLayout`, judged on the page the router cascades and the session's edition, again on every session change). While the edition is being read nothing of the page renders, also when a session moves on from a page it was shown. The services refuse such a session anyway; the guard keeps a staffing company's page from showing at all.

### The portal's home

`/portal` (`Pages/Portal/PortalHome.razor`) is where every session of a portal lands (`AppHome.PortalHome`; [Homes](coordination-and-homes.md#homes)). It is open with any of the portal's permissions, as its navigation link is. It shows (`TenantHomeService.ReadPortalAsync`: the reader's own tenant only, refused to a staffing company's user and to the console):

- The staffing company the portal orders staff from (its active supplier party).
- The reader's own latest orders: at most five, newest first, each a link to its page with its period and state; then "My orders", with the count of all of them when there are more, and "New order" for a writer; "No orders yet" before the first.
- What the reader may do, each item by the permission that opens it.

### Orders

An [Order](../database.md#order) belongs to the portal's own tenant: the job asked for (its name and description, as the job it becomes), its period, the staffing company it goes to (the portal's supplier party in Active) and who wrote it (the orderer).

- **The dialog.** An order is shown only in its state-entity dialog (`OrderDialog`, token `order`; [ADR 0009](../adr/0009-state-entity-dialog-only-and-workflow-governed-attributes.md)): whom it goes to and who wrote it, then the job, the description and the period's two days, and its steps in the footer: Save (the create, which keeps it a draft, and the draft's save; `OrdersCreate`) and Send (`OrdersSend`, confirmed: "It can no longer be changed once it is sent."). A sent order is read-only.
- **The service.** `OrderService` (`Staffora.Services/Portal/`) answers every call in the portal's own tenant, behind the portal's permissions; through the edition ceiling a staffing company's user and the platform console are refused.
- **Pages** (all under `Pages/Portal/`, all part of the portal):
  - **"New order"** (`/portal/orders/new`, `OrdersCreate`) opens the dialog in create mode as it first shows (and again from "Write an order"), with the period from today on the portal's calendar to three months later. Once the dialog has written the order, the reader goes on to its page. The page explains how an order goes: write it, send it, follow it.
  - **"My orders"** (`/portal/orders`, `OrdersView`) is the one list surface (`OrderList`): the reader's own orders by default and everyone's in the portal with "Everyone's orders" on (it says how many of others' it hides); searched by the job or description, filtered by state and sorted by when written (newest first), the job or the period, in SQL (`QueryRequestsAsync`). "Written by" names the writer ("You" for one's own). Each row is a page of its own: its name links to the order's page (`RowLink`). "New order" in the header opens the dialog in create mode.
  - **The order's page** (`/portal/orders/{id}`, `OrdersView`) shows the job, its period and description, its state, the staffing company, who wrote it and when, and when it was sent (its Send step's history entry), with what happens next; only the portal's own rows, nothing of the staffing company's job. "Open order" opens its dialog, and the page reads it again once it closes. Another portal's order reads as "Order not found". The state shows as a filled badge in the header over a meta line, with a notice in the tone of where it stands and the facts as field cards.
  - Every page of the portal stands in a centred column of 54 rem (`PageScaffold` with Class `page-scaffold--column`; the home's bands likewise).

### Sending

Send is the one direct write of one tenant into another ([ADR 0007](../adr/0007-cross-tenant-ordering-customer-portal.md)).

- **In the order's step.** Its action links the order (`IOrderSender.Link`: its `CrossTenantId` and the job's, new Guids written once and kept on a later send). Its follow-up (`OrderSendFollowUp`), once the order is saved Sent, writes into the staffing company's tenant under one logged system-scope lease with its reason, on that tenant's behalf: every row created by its system account, no impersonator, every history entry with that account as its actor.
- **What it writes in the staffing company:**
  - The orderer's contact at the customer, paired through `CustomerContact.LinkedUserId` and created Active on their first send (their first and last name and address; nothing more of them crosses).
  - The job, in AwaitingCoordination on the customer's unit for portal jobs (`Customer.DefaultUnitId`), with the paired contact, the order's name, description, place and period, and the link ids both ways; its history starts with `job-receive`.
  - The notice to every holder of `PerformCoordination` there (`job-requested`: "{customer} ordered {job}", linking to the job's dialog), delivered after the commit as the staffing company's system account (`OrderSender.DeliverAsync`). A delivery that cannot start stays pending for the integration outbox's dispatcher.
- **Locks.** Under the locks, in one fixed order across the two tenants (the portal's tenant row and the order's row, which the step holds; then the staffing company's tenant row, its structure lock, its customer lock and its job lock; then its rows), the send judges:
  - both tenants Active and linked (`TenantStates.ExchangesMessages`, `TenantStates.AreLinked`: the portal names the staffing company its supplier party names);
  - the supplier party Active (also the step's own condition);
  - the staffing company's customer whose portal this is Active, with its unit.
- **One answer for every refusal.** Anything else, and a lock not granted in time, refuses the send with one answer that says nothing of why: "The staffing company cannot take your order right now, so nothing was sent. Try again later." (`SupplierUnavailable`). The log says why.
- **All or nothing.** The order and everything of the staffing company commit together or not at all: a failure anywhere, a save or the commit, rolls both tenants back (the transaction is the compensation).
- **Idempotence.** An order whose job the staffing company already holds writes nothing more (no second job, contact or notice). A sent order is not sent again, and two sends at once make one job.
- Nothing of the staffing company comes back to the portal but the order's own state and links; the job's states return as integration messages ([Following the job](#following-the-job)).

### Lines

Once its draft is saved, an order asks for people. Its dialog's "People" fieldset (`StateOrderLines`) adds a service the staffing company offers in the portal ("Add a service", `order-line-service`, the portal's copies from the dialog's options, `OrderEditorOptions.Services`) with its default number of people, changed per line (1 to 20, `order-line-count-n`) or removed. Once sent, the lines read "2 × Office cleaning"; a service withdrawn since is marked "(no longer offered)".

- A draft whose line names a withdrawn service is not sent ("Office cleaning is no longer offered. Remove it and send again.").
- A send that reaches the staffing company before the withdrawal reached the portal is refused there ("A service in this order is no longer offered, so nothing was sent. Remove it and send again.").
- Sending makes the job's slots, one slot per person, from the staffing company's own services ([OrderLine](../database.md#orderline)). Nothing of the service but its link, name, description and default number of people crosses, and nothing of the job's slots crosses back.

### Following the job

Once sent, an order follows its job in the staffing company.

- Every job step into a customer-visible state from another queues one integration message (`job-state`) to the portal in its own transaction (`IJobStateSync`, `JobStateSync`): being staffed, confirmed, in progress, completed, canceled by either side, deleted. Registration and awaiting coordination belong to the staffing company and queue nothing, so the portal never sees them.
- In the portal the handler (`JobStateSyncHandler`) maps the job's state through one table (`OrderSync.Table`: canceled by the staffing company reads "Canceled by the staffing company" once the job was confirmed and "Declined" before; canceled by the customer reads "Canceled by us"). It moves the order through the system-driven step from where it stands to there, inside the message's delivery, as the portal's system account.
- Movement is forward only, so a late, repeated or retried message changes nothing, and a message that overtook a lost one still lands where the job is.
- The orderer is told on Confirmed, Completed, Canceled by the staffing company and Declined (a notice of the portal linking to the order).

### The staff members' names

From the confirmation on, the job's messages carry its assigned staff members' first and last names, and an assignment step that gives a confirmed or running job a staff member or takes one away sends them anew. Nothing else of them crosses. The portal keeps them beside the order (`OrderStaff`), and the order's page shows them ("Staff") while it is confirmed, in progress or completed, never before.

### Canceling

The order's dialog offers "Cancel order" (`OrdersCancel`, confirmed) until the job is confirmed: for a draft (which is how a draft goes away), a sent order and one being staffed.

- A sent order's cancellation queues `order-cancel` to the staffing company in the step's transaction. Its handler there (`OrderCancelHandler`) cancels the job as canceled by the customer ("Canceled in the portal", a system-driven step, as the staffing company's system account) from any state short of completed, its slots and live assignments with it. Each staff member is freed and told.
- The job's coordinator is told (`job-canceled-in-portal`); every coordinator is told while the job has none, or when its coordinator's account is no longer active or no longer holds `ViewCoordination`, judged as the notice is delivered (the notice's fallback permission; [Notifications](notifications-and-comments.md#notifications)).
- A cancellation that races the confirmation wins on both sides.
- **Two phases.** A sent order reads "Cancellation sent" (`OrderState.CancellationSent`) until the staffing company answers; a message of the job's progress there (being staffed, confirmed, in progress) changes nothing meanwhile. It then ends where the job did: "Canceled by us" once the job was canceled, or "Completed" when the cancellation found the job already completed (its messages held meanwhile, for example a tenant suspended for its period). The handler leaves such a job as it is and answers with its state (`IJobStateSync.QueueAnswerAsync`). Both sides always end alike, and no final state is left.

### The portal's users

A customer administrator (`CustomerUsersManage`) manages the portal's own people on "Users" (`/portal/users`, `Pages/Portal/PortalUsers.razor`, linked from the portal's home): the one list surface over `UserService.QueryUsersAsync` (the user list's definition, each row with the role it holds), a row opening the user's state-entity dialog (`UserDialog`) and "New user" its create mode.

- **Permissions.** A portal grants no `UsersView`, `UsersCreate` or `UsersEdit` (the edition ceiling), so `UserService` takes `CustomerUsersManage` in their place (`UserService.Viewers`, `Creators`, `Editors`).
- **Steps.** The user workflow has the portal's own steps, each needing `CustomerUsersManage` (`ApplicationUserWorkflowAttribute.PortalSteps`):
  - "Create orderer" and "Create administrator" (Create to Active). The step's follow-up (`PortalUserRoleFollowUp`) gives the new user the portal's Orderer or Customer administrator role on its Company root in the create's own transaction, refused whole with `RoleBeyondCeiling` when the role grants a permission the creator does not hold.
  - New password, Deactivate and Reactivate, with the staffing company's own rules (not one's own account, within the grant ceiling).
  - A staffing company never holds `CustomerUsersManage`, so each edition reaches only its own steps. The portal's steps take no edits: its administrators change nobody's details, and the fields are read-only without `UsersEdit`.
- **Passwords.** The initial password and a new one come back once, in the step's answer, which the dialog shows once in its footer (`ShowOnce`): never after a reload or a restored dialog, stored only as Identity's hash and never logged.
- **Deactivation.** A deactivated user signs in nowhere until reactivated.
- **Recovery.** An administrator gives any other of the portal's people a new password, and a second administrator gives the first one theirs. A portal whose only administrator never received theirs is helped by the platform owner, who impersonates that administrator (audited) to create a second one.
- **The last administrator.** A portal has no system owner, so its last active customer administrator (an active holder of a role granting `CustomerUsersManage`) is never deactivated, judged under the tenant's structure lock like the system owner's rule (`ApplicationUserWorkflowAttribute.LastCustomerAdministratorReason`): nobody outside the portal could give it one again.

### The customer thread

The coordinators and the customer talk about a job a portal sent ([ADR 0010](../adr/0010-comment-threads-per-audience.md); [Comment threads](notifications-and-comments.md#comment-threads)).

- **Where.** In the staffing company, the job's Customer thread (`JobService.CustomerThread`: read with `ViewCoordination`, written with `PerformCoordination`, offered only on a job a portal sent, so a staff member never reads it). In the portal, the sent order's "Messages with supplier" (`OrderService.RequestThread`: read with `OrdersView`, written with `OrdersCreate`, none for a draft).
- **Crossing.** A comment there is written with a cross-tenant id of its own (`Comment.CrossTenantId`) and its integration message in the post's own transaction (`JobThreadSync`: `job-comment` from the staffing company, correlated by the job's `CrossTenantId`; `order-comment` from the portal, by the order's). The message carries the comment's id and words, and from the portal the writer's names and address.
- **Copying.** Each side's handler copies it once inside the message's delivery (`JobCommentHandler`, `OrderCommentHandler`), idempotent on the source comment (`Comment.LinkedCrossTenantId`, unique per tenant), stamped with its own tenant's system account.
- **Authors.** A customer's copy is authored by the contact the writer is paired with (`CustomerContactPairing`, as the send pairs the orderer; created on their first message), shown as "name, customer". The staffing company's copy in the portal is authored by the portal's supplier party, never a staffing company's user.
- **Whose turn.** Whose turn it is follows the side that wrote last (`CommentThread.JudgedBySide`). A copy from the other side makes the tab say "Awaits your reply" and marks the other side's comments since one's own side last wrote, while a colleague's last message (another coordinator's, another of the portal's people's) awaits nobody on its own side.
- **Notices.** A customer's message tells the job's coordinator (`job-customer-wrote`) while their account is active and holds `ViewCoordination` as the notice is written, every holder of `PerformCoordination` otherwise. The staffing company's message tells the order's orderer (`order-supplier-wrote`) while they are active and hold `OrdersView`, the portal's customer administrators (`CustomerUsersManage`) otherwise. Neither notice contains the words.
- **Closing.** The job's thread closes once the job is completed, canceled or deleted, the order's once it is final; a message already on its way is still copied.
- **Size.** A text whose message would not fit the outbox's column is refused (`JobThreadSync.TooLongToSend`).

### On the platform console

- A customer tenant stands under its staffing company in the tenant list (`PlatformConsoleService.QueryTenantsAsync`). The sort orders families, each by its staffing company's name, state or creation: the staffing company first and its customer tenants after it by name, each indented under it by a tree line drawn in the stylesheet (`Tenants.razor.css`, so the row's link keeps the tenant's name alone).
- The columns "Managed customers" and "Managed by" name the other side. A search for a staffing company's name finds its customer tenants too. In a grouping by state a customer tenant stands in the group of its own state, naming who manages it.
- A staffing company's console lists the customer tenants it manages, and a customer tenant's console names the staffing company managing it, each with its state and a link to its console (`TenantConsoleSummary.ManagedCustomers`, `ManagedBy`). The Dead letters tab lists the messages between them that were given up ([Integration outbox](operations.md#integration-outbox)).
- Tenant names show on the console only.

### Navigation

A portal's session gets the portal's own sections (`AppNavigation.Portal`: Portal: Home, My orders (`OrdersView`), New order (`OrdersCreate`); the profile in the account menu), never a staffing company's, whatever its roles name. The portal's users page is reached from its home ("Users", for a holder of `CustomerUsersManage`); it has no link in the navigation.

Verified by: `CustomerPortalTests`, `PortalEditionTests`, `PortalPagesTests`, `CustomerPortalPageScanTests`, `TenantEditionTests`, `RolePresetTests`, `CustomerWorkflowTests`, `AppHomeTests`, `NavigationGateTests`, `FoundationPrepTests`, `OrderWorkflowTests`, `OrderTests`, `OrderSendTests`, `OrderLinesTests`, `OrderSyncTests`, `PortalUserTests`, `PortalUserWorkflowTests`, `JobThreadSyncTests`, `tests/e2e/portal.spec.ts`, `tests/e2e/portal-round-trip.spec.ts`, `tests/e2e/home.spec.ts` and the accessibility sweep.
