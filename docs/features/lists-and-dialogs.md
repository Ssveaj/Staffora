# Lists, URL state and dialogs

Every list page finds its rows on one list surface, keeps its query in the address and opens a row in a dialog. This document describes that surface (`ListView` over `ListQuery`, `ListDefinition` and `ListPage`), the URL state service that keeps page filters and the dialog stack in the query string, and the dialog family: dialogs without a lifecycle, opened through `DialogServiceExtensions`, and state-entity dialogs, opened through one door and saved through a `DetachedEntityUpdater`.

The recipe for building a state-entity dialog is in the [workflow-state-entities skill](../../.claude/skills/workflow-state-entities/SKILL.md) and its [DIALOGS.md](../../.claude/skills/workflow-state-entities/DIALOGS.md); this document links to it rather than restating it.

## List view

Lists find; rows open dialogs and carry no actions. Every list page of a tenant runs on one list surface, `ListView<TItem>` (`Staffora.Blazor/Components/Common/List/`), over a service that answers one page at a time.

### The query and the definition

`ListQuery` (`Staffora.Core/Lists/`) is what a list asks for: the search text, the chosen value of each filter (one value or "All"), the toggles that are on, the grouping, the sort and its direction, and the page. Two queries are equal when every part is; filters and toggles are compared by content.

A `ListDefinition` per list says what the list offers:

- its filters (`ListFilterDefinition`, each with its `ListOption`s; `ListOption.ForEnum<T>()` offers an enum's members by their lowercase names and display names);
- its toggles (`ListToggleDefinition`, off by default, with the words for the rows it hides);
- its groupings and sorts, the default grouping and sort;
- the page size (25 by default, at most 100).

The definition serves both sides. The list draws its toolbar from it and keeps the query in the address through it. The service normalizes every query with it (`Normalize`) before it builds SQL, so a key or value the list does not offer (a filter by another tenant, a page of a thousand rows, a sort on a column it does not name) never reaches a query. A definition whose keys could not live in the address, or would mean two things, is refused when it is built. The definitions (`UserList`, `OrganizationUnitList`, `CustomFieldList` and the others) live beside the query in `Staffora.Core/Lists/`.

### The service

A list service method takes a `ListQuery` and returns `Result<ListPage<TRow>>` (for example `UserService.QueryUsersAsync`, `OrganizationService.QueryUnitsAsync`). It checks the view permission, normalizes the query, then filters, groups, sorts and cuts the page in SQL under the tenant filter (naming the runtime tenant as well). It counts the rows across every page and answers a page past the end with the last page.

Two lists are exceptions: each is read in a fixed number of queries and cut in memory, because every row is judged by availability first. They are the staffing dialog's candidates, ranked over every staff member's availability ([Staffing dialog](jobs-and-staffing.md#staffing-dialog)), and the coordination workspace's lists, answered from one snapshot of the tenant's slots ([Coordination workspace](coordination-and-homes.md#coordination-workspace)).

- **Order.** `ListOrder<T>` builds the order key by key: the group first, then the chosen sort, then the row's id, so a page never repeats or skips a row.
- **Sections.** A `ListPage` holds the page's rows in `ListSection`s: one without a key when ungrouped. Grouped, the rows arrive ordered by their group, so a group's rows are contiguous across pages, and each section carries its heading and its row count across every page (`ListPage.Grouped`).
- **Hidden counts.** `HiddenCounts` says how many rows each toggle that is off keeps out, under the other filters.
- **Fixed command count.** Each list sends a fixed number of commands whatever the tenant's size: the user list its count, its page and the page's role holdings; the unit list its hidden count, its total, the tree, the group counts and its page.
- **Search collation.** The search is a `LIKE` under the database's default collation, so it ignores case but not accents ("asa" does not find "Åsa"). A different collation would be a schema change and is not made.

The user list searches the full name, the address and the user name, filters by status, groups by status and sorts by name, address or user name. It names the roles each user holds ("role on unit") only for a caller with `RolesView`.

The unit list searches the name and the description, filters by type, shows inactive units only with its toggle, groups by the unit each sits below (the default) or by type, and sorts by name or type. It names each unit's parent by its path, from the tenant's tree read as ids, parents and names alone through `OrganizationUnitOutline`, so a stored cycle never makes the list loop. It counts the units directly below each unit, and the role holdings on it only for a caller with `RolesView` (`RoleHoldings` is null for anyone else).

Grouped by parent, the groups follow their parents' paths in tree order (the root's group "Top of the tree" first), which SQL cannot compute. The service reads the counts per parent, works out which groups the page covers, and reads the rows of just those groups ordered by a rank over at most a page's worth of parents (a `CASE` whose ids are parameters), cut from the first group's offset, so paging still happens in SQL.

### The component

A page declares:

- the list's definition and its service method (`Load`);
- the row's id and name (`RowKey`, `RowLabel`);
- its columns (`ListColumn`: a heading and the cell). A heading names its column and never sorts: a list sorts through its Sort tool alone. The `Primary` column holds the row's name. `HiddenWhenGroupedBy` leaves a column out while the list is grouped by what it names, as each group's heading says it. `ListColumn.Subtitle` adds a second, quieter line and `Avatar` an initials avatar (the users list's name over its email address), both outside the row's open button so the button keeps the row's name;
- the row's state (`RowState`, a `ListRowState`: the state's name and its `UiTone`);
- `OnOpen`, or `RowLink` for rows that are pages of their own.

A row has no panel that unfolds under it: what the columns do not show, the row's dialog or page shows. A list of rows with no lifecycle opens a form dialog to look at (the console's dead letters, `DeadLetterDialog`).

**Toolbar.** The list draws, from the definition, a toolbar in a `search` landmark, one row inside the list's bordered frame:

- the quick filter when the page names one (`QuickFilter`: that filter's options as segmented buttons, `aria-pressed`, "All" first, the chosen one with the count of rows);
- the search box (a 32 px field with an icon, its label "Search" hidden from view; committed on Enter or on leaving the field);
- a chip per other filter ("Status: All", dashed; once set "Status: Active" on the accent's tint, with a "Clear Status" button beside it);
- a chip per toggle (`aria-pressed`, an eye);
- the Group and Sort tools ("Group: None", "Sort: Name"). The sort's options are each sort ascending and descending.

A chip or a tool is a button (`aria-haspopup="listbox"`, `aria-expanded`, `aria-controls` while open, the chosen value as its `value`) that opens a popover listbox of its choices (`role="option"`, the chosen one `aria-selected`). Enter or Space opens it with the focus on the chosen option; the arrow keys, Home and End move; Enter or a click chooses and gives the focus back to the button; Escape closes it the same way and never closes a dialog the list sits in; a click or the focus outside closes it (`scripts/list-view.js`).

On a phone (640 px or less) the quick filter and the search stay in view, and the chips, toggles and tools fold behind a "Filters" button (`aria-expanded`, `aria-controls` naming the region) that counts the choices differing from the list's defaults ("Filters (2)"), so the first rows show on the first screen. On a wide window the button is hidden and the choices sit beside the search. Above the table a line counts the rows a toggle that is off keeps out ("1 inactive unit is hidden.") with the toggle beside it.

**Table.** The table is named by `Label` and names its roles itself (`table`, `rowgroup`, `row`, `columnheader`, `cell`), so the card layout, which makes its parts blocks, keeps the table's semantics in every browser.

- Every row has a state strip in its tone at its starting edge and, in its status cell, a dot in the tone's dot shade beside the state's name, so colour is never the only sign.
- The head is a muted 34 px band; cells are 7 by 10 px in the dense size.
- A group is a `tbody` headed by a row whose button opens and closes it (`aria-expanded`) and names the group and its count.
- The footer counts the rows ("26–50 of 60 users", a polite status) and pages them: previous and next as named icon buttons and the page numbers (the first, the last, the current one and its neighbours, the current one `aria-current="page"`).
- After each read the list tells the page the count of matching rows (`TotalChanged`), which list pages show in their header (`PageHeader.Count`).
- A page section holding only a list leaves the frame to the list (`app.css`), so a list page is one bordered container on the white floor.

On a window of 640 px or less each row is a card, its cells labelled by their headings. The label is an element of its own in the cell, hidden from assistive technology (`aria-hidden`), which names the cell by its column heading instead, so the label is not read twice. The headings are clipped from view there but still name the columns, so Tab goes from the toolbar straight to the first row.

**Cards.** A page whose rows are a person's own work shows them as cards instead (`Cards`, a `ListCard` per row: a date block, the title as the row's open button with an optional badge, facts with their icons, a quiet hint, the status, and the state's colour as the card's edge), at every width, sorted through the Sort tool. "My assignments" does.

**Loading and empty states.** Loading shows a spinner once; later loads keep the rows on screen under a thin bar (`aria-busy`), and a newer load supersedes an older one still running. An empty list says so ("No users yet"); a narrowed one says nothing matches and offers to clear the search and filters; a refused one shows the service's reason as an alert. A load its owner cancels (an `OperationCanceledException` from `Load`, when a dialog's view is left or the dialog closes) leaves the list as it was; the owner asks again when it needs the rows.

The list's CSS classes start with `list-` but avoid Bootstrap's own (`list-group`, `list-inline`, `list-unstyled`), which the app loads.

### Rows open dialogs

A click anywhere on a row opens it through `OnOpen`, which calls the dialog's static opener: a state-entity dialog's id-only `OpenAsync(dialogService, service, id)`, such as `UserDialog.OpenAsync(dialogService, userService, userId)` or `OrganizationUnitDialog.OpenAsync(dialogService, organizationService, unitId)` (whose editor options carry the parents the unit may move below). The row's name is a button, so the keyboard reaches every row with Tab and Enter or Space opens it.

- One dialog opens at a time. The list reads its page again once the dialog has closed, whatever it answered.
- The focus returns to that row's name (`wwwroot/scripts/list-view.js`), or to the table when the row is no longer on the page, or to the summary when no row is left, unless the focus has moved elsewhere on the page meanwhile. Without this the browser would leave the focus on the page's body when the dialog's element goes.
- Rows carry no action buttons; a page's own actions (New user) sit in its header.
- Someone who may view the rows but not edit them (`UsersView` without `UsersEdit`, `OrganizationUnitsView` without `OrganizationUnitsEdit`) gets the row's dialog read-only: every field is a focusable `readonly` input, the record is locked for the reader (`IsLockedRecord`), and no step the reader may not take is offered. The services still refuse a save, and the workflow's steps refuse a step from outside the unit's tenant. The organization tree's "Open unit" opens the same dialog for every reader.

**A row that is a page of its own.** A list passes `RowLink` instead of `OnOpen`: the row's name is a link to the page and a click on the row follows it, so the list is left and reads nothing again. The platform console's tenant list is such a list, each row opening the tenant's console.

### In the address

The list keeps its query in the address through the URL state service ([URL state](#url-state)) under `q`, each filter's and toggle's key (`status`, `type`, `inactive`), `group` (`none` when the default groups), `sort` (the key, with a leading minus for descending: `-email`) and `page`. Defaults are left out.

- It binds each key as raw text and reads the whole query through its definition, so a value the list does not offer (`?type=planet`, `?page=0`) reads as its default and the address is set back to what the list shows.
- F5 or a copied link opens the same view; a link to the page with other values, or Back, shows what the address names and reads again.
- Every change of the query goes back to the first page except a change of page; a page past the end is shown as the last page and the address follows.
- A list's keys are its own: it binds them exclusively (`UrlStateService.Bind(key, exclusive: true)`), so a second list on the same page that would bind the same names throws as it binds, naming the key, instead of silently reading and writing the first one's query.
- Such a second list takes an `AddressPrefix` (1 to 16 lowercase letters or digits, never `dlg`; `ListDefinition.AddressName`): its keys become `prefix.q`, `prefix.status`, `prefix.page`, `prefix.row` and so on, beside the page's own list's bare names, and its element ids start `prefix-list` unless it is given an `Id`. A list given neither gets an id of its own (`list-3`), so two lists never share element ids.
- A list inside a dialog keeps its query in memory. The dialog frames (`FormDialogFrame`, `FormDialogHost`, `StateEntityDialogHost`) cascade `DialogScope`, and `BindAddress` is on by default only outside one; setting it true or false decides it either way. A state-entity dialog's child list (`StateEntityDialogChildList`) is a `ListView` in a view of its parent's dialog, so it binds nothing unless it is given `BindAddress` and, beside a page's own list, an `AddressPrefix`.
- The coordination workspace holds no `ListView`; its timeline (`board.*`) and Priorities dock (`prio.*`) keep their own keys under their prefixes ([Coordination workspace](coordination-and-homes.md#coordination-workspace)).

### The pages

- **Users** (`Pages/TenantAdmin/Users.razor`): a `ListView` over `UserService.QueryUsersAsync`. Each row has its state strip (Active or Inactive); the status is the quick filter, and the filter and grouping use the keys `status`, `active` and `inactive`. The name cell shows the person's initials and their email address under the name. The columns show when each person last signed in ("Never" until they do) and since when they are a member, on the company's clock (`TenantClock`). The phone and the roles held are shown in the user's dialog.
- **Organization units**: the one tree ([ADR 0021](../adr/0021-one-organization-tree-per-tenant.md)) in two views of the same units, chosen by a switch in the header (two buttons, `aria-pressed`) and kept in the address (`?view=list`). The tree, the default, shows the structure and holds the steps that change it ([Organization tree](user-creation-flow.md#organization-tree)); adding a unit stays a step of the tree, where the new unit's parent is the selected unit. The list is a second way to find the same units, not a second structure. A row opens the unit's dialog, which shows the unit's place, what sits below it and, for a reader of role holdings, the holdings on it.
- **Custom fields**: listed on the same surface in the tenant's settings shell (its Custom fields panel), on the platform settings page and on a tenant's console Custom fields tab ([Entity attributes](custom-fields-and-settings.md#entity-attributes-dynamic-custom-fields)).
- **Roles & permissions** (`Pages/TenantAdmin/Roles.razor`): a `ListView` over `TenantRoleService.QueryRolesAsync` (`TenantRoleList`: searched by name or description, sorted by name or by permissions). Each row has its state strip, the role's name over the first three of its permissions by name, the count of its permissions, whether its holders are staff members, and how many people hold it. The count is in the header and "New Role" is the header's primary button; a row click or Enter on its name opens the role's dialog.
- **Staffing lists** name their status filter as the quick filter where its options are few (staff members, qualifications, work patterns, absence, customers; the jobs' eight states stay a chip). Their name cells carry a second line: a staff member's email address with their initials (the address sorts through the Sort tool), a qualification's, a work pattern's, a customer's and a unit's description, and a job's customer.
- **Platform console tenant list** (`Pages/SuperAdmin/Tenants.razor`, `/superadmin/tenants`): a `ListView` over `PlatformConsoleService.QueryTenantsAsync` (`PlatformTenantList`: search by name or description, no filter, grouping by state, sorts by name, state and creation). It lists the staffing companies only, never the platform's own tenant nor a customer tenant a company runs a portal for (its company's console lists and links it). Each row has its state strip, the company's icon before its name (drawn by the stylesheet in an element hidden from assistive technology, so the link keeps the tenant's name alone), its people and the customer tenants it manages, and links to its console (`RowLink`).
- **A tenant's console** (`Pages/SuperAdmin/TenantConsole.razor`, `/superadmin/tenants/{id}` and `/{tab}`): read-only tabs, each a link with its own address (`aria-current` on the open one; an unknown tab reads as the Overview). "Tenant details" in its header opens the tenant's dialog; the header carries no state line.
  - *Overview*: an ink band holding the tenant's initials, its name as an `h2`, its description, its state as a pill, when it was created and its kind, and under a rule five figures (active and inactive users, units in use, roles and dead letters; users, roles and dead letters each link their tab). White cards lifted over the band's edge hold the system owners and the managed customers or the managing company, each with a state pill, and the organization tree, alone until a unit is selected, then beside the unit's type, state, description and units below.
  - *Users*: the tenant's user list (`UserList`, read through `PlatformConsoleService.QueryTenantUsersAsync`): each person with their email under the name, the roles they hold, when they last signed in ("Never" until they do) and since when they are a member, times in UTC.
    A user opened here offers Unlock, Save and New password (its context's `TransitionFilter`; [ADR 0030](../adr/0030-platform-owner-corrects-a-users-sign-in-details.md)): the platform owner corrects the name, email address and user name and gives a new password, while the phone and custom fields are read-only (`IsLockedMember`) and a save that changes them is refused by the user service.
    No greyed steps are shown. A role holding in the user's Roles view opens the role's own dialog (its permissions and holders), as it does for every reader who may not remove the holding.
  - *Roles* (`PlatformTenantRoleList`) and *Settings*.
- **Error log**: platform health on the same surface (`ErrorLogList`), grouped by day, a row opening the entry's dialog ([Error log and log scopes](operations.md#error-log-and-log-scopes)).

Verified by: `ListQueryTests`, `ListViewTests` (bUnit), `ListQueryServiceTests` and `ListPagesTests` (database), `tests/e2e/lists.spec.ts` (browser).

## URL state

The query string describes what a page shows, so F5, or a link pasted into another browser, opens the same view. `UrlStateService` (`Staffora.Blazor/Helper/UrlState/`, scoped: one per circuit) owns it; `AddUrlState()` registers it with its browser writer. Pages never use `NavigationManager.NavigateTo` or `[SupplyParameterFromQuery]` for page state.

### Keys

A page declares each key once as a static `UrlStateKey<T>`: its name from `UrlStateKeys`, its default, and how it reads and writes. `UrlStateKeys` is one vocabulary, so a name with two meanings shows in review: `q`, `status`, `level`, `days`, the list keys `group`, `sort`, `page`, `type`, `inactive`, `entity` and `scope`, `view`, `month`, `day`, `dock`, and the reserved `dlg`.

The factories cover the common kinds; each takes an optional check (`isValid`) that narrows it to what the page offers, such as the periods a drop-down lists:

| Factory | Reads and writes |
|---|---|
| `Text` | trimmed and capped, 100 characters by default |
| `Enum`, `OptionalEnum` | the member's name in lowercase, read in any case; never a number or a combination |
| `Int` | invariant digits |
| `Guid` | the 36-character form, never the empty id |
| `Date` | an optional `DateOnly`, only as `yyyy-MM-dd` |
| `Bool` | `true` / `false` |

- A name is 1 to 16 lowercase ASCII letters or digits, or two such names joined by a dot (`slots.q`, the keys of a list bound under an address prefix).
- A value is read only up to 512 characters. Reading never throws: a missing, malformed, too long or refused value reads as the default, and a parser or check that throws reads as the default too.
- The default is never written, so a page in its default state has a bare address.
- An enum used in a key keeps its member names, since a name is in every saved link.
- The URL carries no secrets and no other tenant's identifiers: only filter values and the ids of the page's own rows.

### Binding

A page binds its keys in `OnInitialized`, before its first load, and disposes them in `Dispose`:

```razor
@inject UrlStateService UrlState
@implements IDisposable
<RadzenDropDown @bind-Value="level.Value" ... />
@code {
    private static readonly UrlStateKey<ErrorLogLevel?> levelKey = UrlStateKey.OptionalEnum<ErrorLogLevel>(UrlStateKeys.Level);
    private UrlStateValue<ErrorLogLevel?> level = null!;
    protected override void OnInitialized() => level = UrlState.Bind(levelKey);
    public void Dispose() => level.Dispose();
}
```

- `Bind` reads the value the address names and returns a `UrlStateValue<T>`; setting its `Value` writes the address, and the default removes the key.
- A value the address held that the key refuses, or holds in another spelling (`CRITICAL`), is rewritten to what the page shows as it binds.
- A key has one owner on a page. Binding a bound name again supersedes the older binding and logs a warning, except that an exclusive binding (`Bind(key, exclusive: true)`, as a list's are) refuses to share its name either way and throws, so two owners fail at once instead of overwriting each other.
- Disposing a binding removes its key while its page is still on screen (a panel closing).
- A text key written as null (a text box bound two ways, cleared) leaves the address.

### Pages, not paths

A binding belongs to the page instance that was on screen when it was bound, not to an address. `UrlStatePageTracker`, in the router's `Found` template in `Routes.razor` before the route view, tells the service which page type the router rendered each time it renders one (`EnterPage`), before the page renders and binds.

The router keeps a page instance exactly when it renders the same page type again: when only a route parameter changes (`/tenants/a` to `/tenants/b`), when only the case of the path does (`/list` to `/List`), or when only the query does. A navigation suspends every binding until the router has rendered. The same page type makes the kept page's bindings current again; another type ends the old page's ownership for good. The bindings of a page left behind go quiet: their writes are dropped and their disposal removes nothing, so they never touch the next page's keys, even before it has bound them. A page reads the new address even when the router renders it before the navigation event reaches the service.

### A link to the page on screen

When the router keeps the page through a navigation, the address wins. Each binding takes the value the new address names (its default for a key the address leaves out) and raises `UrlStateValue<T>.Changed`, so the page reloads what it shows (the error log reads the log again for a new period and re-filters for a new level or search). A link from `?level=critical` to `?level=error`, Back to an earlier entry of the page, and the menu's link to the page's bare path (which resets its filters) all show what the address names.

A navigation whose address names what the page already shows changes only the spelling: the address is set to the page's own (`?status=INACTIVE` becomes `?status=inactive`). `Changed` is not raised for the page's own writes; a handler that renders goes through `InvokeAsync`.

### Writing

Only changed keys are written, merged into the live address by `wwwroot/scripts/url-state.js` (a static module, as the content security policy allows only scripts of this origin) with `history.replaceState`. Every other key, including ones the app never wrote, stays; Blazor's history entry state is kept; nothing navigates.

`NavigationManager.NavigateTo(..., replace: true)` is not used: a navigation runs the router, and `FocusOnNavigate` would move the focus to the heading on every filter change, while a write happens as someone types or picks. Because the address changes without a navigation, `NavigationManager.Uri` keeps the address the page opened with; the service keeps its own copy of the query and answers from it.

- Writes run one at a time and in order; a burst made during a write goes as one.
- A write carries the page's path as .NET reads it (`Uri.AbsolutePath`, escaped letters decoded), and the module drops a write that arrives after the user has left that path. It compares the two paths with their percent-escapes decoded, so `/superadmin/error%6Cog` and `/superadmin/errorlog` are one page.
- A failed write (the module failing to load or to apply) is logged as a warning and never reaches the circuit. It is not retried at once, as a broken writer would spin, but its keys go again with the next write, a value queued since winning.

**Interactive.** Writes wait until the circuit can run scripts. The session gate, which both layouts host, calls `MarkInteractive()` after its first render, the first moment interop works. A page rendered on the server alone (the sign-in pages) never writes the address.

### Dialog stack

The service reserves `dlg` for the open dialogs, outermost first, as `token-id` segments joined by dots (`?dlg=tenant-6f1c….role-7`, at most eight).

- **Segments.** A dialog over a saved row has a segment: a token from `DialogTokens` (lowercase ASCII, unique, never renamed once released) and the row's id. A dialog in create mode, a confirmation, an error or a prompt has none and stays out of the address: a new entity has no row a link could reopen, so a create dialog is not restorable; it goes on the address once its first step gives the row an id. A session's segment is the record it shows, and a move replaces it.
- **Push and pop.** The dialog host pushes the segment when the dialog mounts and disposes the entry when it unmounts, which Radzen does however the dialog closes (`UrlStateService.PushDialog`). Entries may be disposed in any order and more than once; the key always names the dialogs still open. The state-entity dialog host pushes the segment of every dialog over a saved row (its context's `DialogToken` and id), and a create dialog's once its first save gave the row an id. Dialogs without a lifecycle can push theirs through `FormDialogOptions.UrlSegment` (`FormDialogHost` pushes it); none sets it.
- **No page binds `dlg`** (`Bind` refuses it). The key belongs to the circuit, not to a page, and names whatever dialogs are mounted. No dialog stays open across a navigation: Radzen's dialog service closes every dialog when the location changes, each host pops its entry as its dialog unmounts, and the new page's address ends without the chain. The history entry the user left keeps the `dlg` it had, since `replaceState` touches only the current entry.

**Restore.** The chain the circuit's address arrived with (F5, a pasted link, a link opened in a new tab) is kept and removed from the address once the circuit is interactive, since none of its dialogs is open yet. `TakeArrivedDialogChain()` hands it out once per circuit; every later call answers an empty chain. Until it is taken, a link to the same page that names no chain keeps it, a move to another page drops it, and an address naming a chain replaces it.

- The restore host (`StateEntityDialogRestoreHost`), hosted by both layouts inside the session gate and their `Authorized` branch, takes the chain after its first render, so only once the session is resolved.
- It reopens the chain only for a signed-in user on a page whose own authorization passes (its `[Authorize]` policies over the `RouteData` that `Routes.razor` cascades). An anonymous visitor, a page that sends its visitor to sign in, and a page whose policy refuses the session reopen nothing and ask no service anything.
- It reopens the chain in order, each dialog over the one before: it finds each token in the `DialogRouteRegistry` and calls that dialog's own id-only opener, the one a list row calls, whose door loads the row before anything shows.
- A page that opens a kind of dialog on one of its views names it (`[DialogViewOnPage(token, view)]`: the coordination workspace's slot dialogs on Staffing), and the restore passes that view in the opener's options (`StateEntityDialogOptions.View`), so F5 shows the dialog as the page's own opener did. The view is shown only when the reader is offered it.
- A row the reader cannot see (another tenant's, a deleted one, one the load's permission refuses; after an impersonation ended, a tenant's row on the console) opens nothing, not even "could not open", and the chain stops there. A missing and a foreign id read alike.
- Each level is judged by its own load alone. The chain is not checked as a parent-child path, so it reaches nothing its reader could not open one dialog at a time.
- The chain is read strictly and never throws. A segment is read in any case and kept in the form the app writes (the token in lowercase, a `Guid` id in its lowercase "D" form), so one row in two spellings is named twice. An unknown token, an id that is no row's key and a segment named twice stop the chain with a warning; a form dialog's token (one no link reopens) stops it without one; text that does not parse, a value over 512 characters and segments past the eighth are dropped as the key is read.
- Each restored dialog waits for the one below to name itself and take the focus, so the focus returns to it when the one above closes. One that closes having changed something reloads the one below, as a child list would. The reopened dialogs push their segments again, so the address names the chain once more.

**Back and Forward.** Only the arrival reopens. Once the arrival's chain is taken, no navigation records a chain, so Back or Forward inside the app to an entry that kept its `?dlg=`, or a link naming one, shows the page without the dialogs, and the address is set to what is open. Back is page history: dialogs a user left, perhaps discarding edits on the way, do not spring open again.

- A history entry the browser loads as a new document (Back after leaving the app, or a reload) is an arrival and reopens its chain as F5 does.
- A restored chain is always the first entry of its document, so Back from it leaves the document: the browser's own prompt asks while any of its records has edits or a step runs, once for the whole chain.
- A navigation inside the app with a restored chain open meets the navigation guard of the dialog on top, which asks once for every open dialog, as for a chain opened by hand.
- A link inside the app that should open a dialog calls its opener, not the address.

Verified by: `UrlStateServiceTests`, `UrlStateKeyTests` (the dialog tokens), `DialogStackHostTests`, `DialogRestoreTests`; in a browser `tests/e2e/url-state.spec.ts` (merge, history and path rules) and `tests/e2e/dialog-restore.spec.ts` (restore, Back and Forward across both layouts).

## Dialogs

Two kinds of dialog exist. A state entity is shown only in its state-entity dialog ([ADR 0009](../adr/0009-state-entity-dialog-only-and-workflow-governed-attributes.md)); everything else (a confirmation, an error, a message, a prompt, a form) is a dialog without a lifecycle, opened through `DialogServiceExtensions`.

### Dialogs without a lifecycle

Every dialog without a lifecycle opens through `DialogServiceExtensions` (`Staffora.Blazor/Helper/`), never through Radzen's `OpenAsync`, `Confirm` or `Alert`. `DialogUsageScanTests` fails on any other opener, called on any receiver or named as a method group on a dialog service, with nested type arguments, and reads strings before comments so a `//` inside a string hides nothing. Its allow-list holds the extensions' own two hosts alone, and it refuses a state-entity body opened through `OpenFormDialogAsync`.

| Method | Opens | Answers |
|---|---|---|
| `ConfirmAsync(message, title, okText, cancelText, destructive, icon)` | `ConfirmDialog`: a question; `destructive` gives a step that cannot be undone the danger tone | `true` only on the confirming button |
| `ShowErrorAsync(message, title)` | `ErrorDialog`: a message meant for the user, such as a service's failed `Result`, never an exception's text | when closed |
| `ShowMessageAsync(message, title, okText, icon, tone)` | `MessageDialog`: an outcome to read and acknowledge | when closed |
| `PromptAsync(title, message, placeholder, okText, cancelText, required, requiredMessage, initialText)` | `PromptDialog`: a short text; a required prompt refuses an empty answer | the text, or `null` |
| `OpenFormDialogAsync<TComponent>(title, parameters, options)` and `<TComponent, TResult>` | `TComponent` under the header (`FormDialogHost`), its parameters set by expression (`p => p.Set(c => c.Users, users)`) | `true` when it closes with `true`; or what it closes with as a `TResult` |

Each opens under the common header (`FormDialogHeader`) with the title the opener gives, without Radzen's title bar. Esc closes it without an answer, and the header's close button answers nothing too. The title is also the dialog element's accessible name (Radzen's `aria-label` when no title bar is drawn).

`FormDialogOptions` sets:

- the width (the dialog never grows past the viewport less a 16 px gutter) and height;
- a header icon;
- whether Esc or a click beside the dialog closes it (for forms Esc does, a click does not);
- resizing;
- for a dialog over a saved row, the segment it puts on the dialog stack while open (`UrlSegment`, [Dialog stack](#dialog-stack));
- a question asked before the user closes it (`CanClose`: by the header's close button, which closes through Radzen's `TryCloseAsync`, by Esc or by a click beside it; false keeps it open). Tenant onboarding asks it while the one-time password is not marked copied;
- for a dialog about one record, that record's state colour (`StateColor`, below).

A form component renders its fields inside `FormDialogFrame` and closes the dialog with its answer from `OnSave`; it draws no title of its own. The short dialogs share `DialogNotice` (icon, message, centred buttons, tinted by `DialogTone`).

**Focus** (WCAG 2.4.3). The host (`FormDialogHost`, `wwwroot/scripts/dialog-focus.js`) remembers the element that had the focus as the dialog opened and puts the focus on the element the dialog marks `data-dialog-autofocus` (a search field) or else on the dialog's first focusable element. As the dialog closes it gives the focus back to that element or, when the element is gone or inert, to the heading of the dialog now on top. A focus the user moved elsewhere meanwhile stays, and a dialog opened with the focus on nothing leaves it to the component that opened it. So Cancel on a step's confirmation leaves the focus on the step's button, and Keep editing on a leave question on the button that asked.

Radzen's own first focus is off: it runs 500 ms late, on whichever dialog is last on the page by then, so a question answered sooner sent the focus to the dialog below. Radzen also writes the options its Esc handler reads only at that late moment, so the host arms the dialog's own Esc option as it renders and an Esc in the first half second closes it.

### The look

Following [ADR 0024](../adr/0024-visual-language-tokens-and-state-colour.md):

- **Dialogs without a lifecycle** (forms, confirmations, errors, messages, prompts, the impersonation picker) share one frame with a state-entity dialog's proportions: a low header band (2.5 rem) in the frame's colour (`--dialog-frame-color`, the accent) with a white title, icon and a 32 px close, and a 5 px edge down the left side and along the first 30 % of the foot in that same colour (4 px on a phone). The body is padded 1.25 rem and the buttons sit at the right.
- **A dialog about one record** (its full history, [ADR 0036](../adr/0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md)) takes that record's frame instead: the header and the edge in the record's state colour as its own dialog draws them (`FormDialogOptions.StateColor`).
- **A state-entity dialog** is framed in its state's colour:
  - The header (2.5 rem) is filled with it, darkened just enough for its always-white icon, title, note and buttons to reach 4.5:1 (`DialogStateColors`, `ColorContrast.ForWhite`). A 5 px edge in the header's fill runs down the left side and along the first 30 % of the foot (4 px on a phone), so the two read as one frame in every state. The history button (for a reader who may read the history) and the close button sit on the right, both 32 px and named.
  - The green of the active and confirmed states is `#37DE57`, its header a deeper green.
  - The state pill hangs on the header's bottom edge in exactly the header's fill, with white text and the state's name in capitals. Its card opens with a round badge in the state's colour, the state's name and description.
  - The view tabs are words; the selected one is underlined in the state's colour and labelled in it, darkened until it reaches 4.5:1 on white.
  - The side column stands on the state's colour at 18 % with folder side tabs (3 by 2 rem); the footer on the state at 7 %, without a line.
  - A step is a white button with a 1 px border, a 3 px leading edge and its icon in the target state's colour, its words in the text colour.
  - The frame is 4 px round; a dialog with the side column is 1180 px wide; the column moves under the body in a dialog narrower than 56 rem (a container query); on a phone the dialog fills the screen.

`TokenContrastTests` holds every state colour's header, pill and tab text to 4.5:1.

### State-entity dialogs

A state entity is never shown in a dialog without a lifecycle: its dialog is a StateEntityDialog, built by the recipe in [DIALOGS.md](../../.claude/skills/workflow-state-entities/DIALOGS.md). It loads through one service call that returns a `DetachedEntityUpdater<T>` ([ADR 0017](../adr/0017-dialogs-load-through-one-service-call.md)) and saves edits through it, never by sending a whole entity ([ADR 0015](../adr/0015-dialogs-save-through-a-detached-entity-updater.md)). No page or component outside the dialog's context runs a step or deletes a state entity (`LifecycleCallScanTests`).

**The door.** `OpenStateEntityDialogAsync<TBody, TContext>(context, options)` is the one door. An entity's body component calls it from its static `OpenAsync` (or `OpenCreateAsync`) and answers the `StateEntityDialogResult` (`Changed`, `Deleted`, `EntityId`) however the dialog closed. `StateEntityDialogOptions` sets the width (720 px by default, never past the viewport less a 16 px gutter), the height and a CSS class.

**The host.** The door opens `StateEntityDialogHost` (`Components/Common/Dialogs/StateEntity/`), which:

- gives the context the circuit's `WorkflowService`, clock and services, and cascades `DialogScope`;
- pushes the URL segment;
- holds navigation through `StateEntityDialogNavigationGuard` (a `NavigationLock`): held back while a step runs or a session moves, "Discard changes?" with edits, the browser's prompt on leaving the app. One guard per circuit answers, the top dialog's, for every open dialog and every entity a session keeps, through the scoped `OpenStateEntityDialogs`, so stacked dialogs and a session's entities with edits are asked once ("Discard changes in 2 records?");
- closes the dialog by reference on a delete, waiting while another dialog is above it, since Radzen closes only the top one;
- ends the context when Radzen disposes it;
- renders `StateEntityDialogLayout`: the header, the body and the footer.

**Layout.**

- The header (`StateEntityDialogHeader`) shows the title as the one `h2`, which names the dialog element once rendered (`wwwroot/scripts/state-entity-dialog.js`) and takes the focus; the focus returns to the opener when the dialog goes. It shows "Unsaved changes" while there are edits, and a close button.
- The body shows the loader as a status while the one load runs, "could not open" with the service's reason as an alert, or the body component with the entity in hand, given the context and an `EditContext` as cascading values, and inert while a step runs.
- The footer (`StateEntityDialogFooter`) shows the steps in a named group, handing back at the left and going forward at the right, a refused step's message as an alert above them, a status region that says a step ran, and a create's one-time message.
- The current state's `[StateStyling]` colour frames the dialog and each step's leading edge is its target state's colour, never a background behind text.

**Closing.** Radzen's title bar is off, nothing takes the focus on its own, and a click beside the dialog does not close it. Esc and the close button close it through Radzen's `CanClose`: never while a step runs, and with unsaved changes only after "Discard changes?". Esc closes the top state-entity dialog from the moment its heading renders, stacked or not: Radzen writes the option its Esc handler reads 500 ms late and on whichever dialog is last then, so the module gives each dialog its own as its heading is named (`armEscape`; `tests/e2e/dialog-escape.spec.ts`).

**The context.** `StateEntityDialogContextBase<TState, TEntity>`:

- loads once through its service, and cancels the load and any step's server call when the dialog closes (`Lifetime`);
- judges a step's conditions on the edited entity in memory first, for instant feedback; a condition on a navigation it does not hold as the server would judge it is left to the server;
- sends the updater's edits and the step as one call and one transaction (`Update{Entity}AndTransitionAsync`, `WorkflowService.SaveAndTransitionAsync`): a refused step stores neither and the edits stay in the form. In create mode it creates the row and its first step (`WorkflowService.CreateAndTransitionAsync`);
- reloads after a step, or closes on a delete state, and counts entity versions. The footer asks the server for the steps once per version (in create mode the access gate alone) and judges them again in memory on every edit, which the layout's `EditContext` reports through `Context.NotifyEdited()`.

**Steps.** Each step is a `TransitionButton`, greyed with its blocking reasons (`aria-disabled` and a card it is described by) only when its transition is `ShowWhenBlocked`, otherwise drawn only when it can run. A greyed step's card says what it needs; there is no separate next-step hint. A dialog that offers fewer steps on a screen narrows them with its context's `TransitionFilter` (the user dialog on the platform console offers Unlock, Save and New password). A step asks its confirmation or comment prompt first.

**Locked until Edit** ([ADR 0031](../adr/0031-dialogs-open-locked-until-edit.md)). A saved record opens with every field read-only and the footer's own Edit (`data-dialog-action="edit"`) in the place of the state's save, for a reader the server lets save; then Cancel and Save alone. Cancel drops the edits and locks again, and every load locks again. A new record is open from the start; a state without a save follows the workflow alone.

Verified by: `StateEntityDialogTests` (on the test-only `TestParentDialog` and `TestChildDialog`), `DialogArchitectureScanTests` (the rules of the recipe).

### Routes

Rules in [DIALOGS.md](../../.claude/skills/workflow-state-entities/DIALOGS.md), "Routes and restore".

- Every dialog context declares its token with `[DialogRoute(DialogTokens.…)]`. `DialogToken` is read from the attribute alone and is not virtual, so the address a dialog writes and the route that reopens it never differ. The attribute is not inherited, and an open generic component is no body.
- `DialogRouteRegistry` (a singleton, `AddDialogRoutes(typeof(Program).Assembly)` in `Program.cs`, built as the app starts, so a broken declaration stops it) maps each token to its context, its one body and the body's one id-only opener, exactly `public static Task<StateEntityDialogResult> OpenAsync(DialogService dialogService, TService service, Guid id, StateEntityDialogOptions? options = null)`, with `TService` a concrete class, the entity's own service. The opener hands its options on to the door.
- The restore ([Dialog stack](#dialog-stack)) calls the opener with options only it can make: the door then loads the row before it shows the dialog and opens nothing for one the reader cannot see.

Verified by: `DialogRouteRegistryTests` (every declared token registered, every registered opener declared, tokens unique and valid, each rule first on a sample that breaks it), `AppWiringTests` (the started app registers the registry over its own assembly).

### Views and sessions

Rules in [DIALOGS.md](../../.claude/skills/workflow-state-entities/DIALOGS.md), "Views and sessions".

**Views.** A context declares its entity's views (`DeclareViews()`): a key, a label, an order, an icon, the permission its data's service checks, the workflow area it shows, and for a list view its definition and its load (one call to the entity's service or to a child service it hands on through a property, such as `JobSlotService.Staffing`). A view is offered while its permission is held, its area is visible in the persisted state and, for a list, the entity has a row. A view can also be offered conditionally (`StateEntityDialogView.When`).

- With more than one view offered, `StateEntityDialogTabStrip` draws them under the header as WAI-ARIA tabs with manual activation: the arrow keys, Home and End move the focus without scrolling (`state-entity-dialog.js`); Enter, Space or a click selects; the selected tab alone is in the tab order.
- The body wraps each view's content in a `StateEntityDialogViewPanel`, mounted when its view is first shown and kept, hidden, after.
- A child list (`StateEntityDialogChildList`, on `ListView`) loads its rows only when its view is shown, cancels a load when the view is left or the dialog closes, reads them again after a step while shown (stale otherwise), and opens a row in the child's own StateEntityDialog on top. When the child changed something, the parent reloads through the context's public `ReloadAsync()`.

**The leave rule** runs before the dialog shows stored rows (a hop to a list view, a session's move, that reload) while the entity has edits:

- The state's designated save step (the self-transition its workflow marks `IsSave`, checked at registration; never another step, never a create) takes the edits when the access gate offers it and its conditions pass on the edited entity.
- Otherwise "Discard changes?" asks: Keep editing stays, Discard changes reloads the stored row. A save the server refuses asks the same with the server's reason.
- A hop between views of the entity's own fields keeps the edits in the form.
- A dialog that has ended (the page navigated away while a child dialog was open over it) neither saves nor asks.

**Sessions.** A session (`OpenStateEntityDialogSessionAsync` over `StateEntityDialogSession` targets, saved rows only, when there are several) shows several entities in one modal with Previous and Next above the dialog. A move settles the edits, holds the shown entity busy and inert while it loads the next one, then swaps it in, so no loader shows and nothing done meanwhile is swapped away. Closing and navigating ask once about every entity the session keeps with edits; a close during the load stops it; a delete shows the neighbour. The focus returns to the opener when a dialog or a session closes, and moves to the new heading on a swap, which the session announces to the module.

Verified by: `StateEntityDialogTests` (the `Views` and `Leaving` parts), `tests/e2e/state-entity-views.spec.ts` (tab keys, axe, screenshots and focus).

### State-aware fields

Rules in [DIALOGS.md](../../.claude/skills/workflow-state-entities/DIALOGS.md), "Reuse catalogue".

A state-entity dialog's body places its fields through shared components bound to the dialog's own entity (`Components/Common/WorkflowFields/`):

- the `StateField*` components: text box, text area, drop-down, number, date picker, time, switch, and a day with an optional time in one frame (`StateFieldDayAndTime`, which binds two properties and reads the access of each);
- `StateAreaFieldset` for an area's group;
- `StateCustomFields` for the custom fields;
- `StatePermissionMatrix` for a role's permissions;
- `StateWorkPatternSegments` for a work pattern's hours.

Each reads its access from the workflow for the persisted state through the cascaded context (`FieldAccessOf`, `AreaAccessOf`):

- hidden renders nothing;
- read-only is the input's own `readonly` and stays focusable (a switch or yes-or-no reads as "Yes" or "No" in a read-only box);
- required shows a mark after the label and `aria-required`;
- every input is named by its label.

A drop-down over a foreign key sets the reference beside it to the chosen row from the context's lookups (`LoadEditorOptionsAsync`), so the footer judges a condition on it in memory.

`StateFieldScanTests` reads the compiled bodies and contexts and refuses any hand-written visibility, read-only or required check: a state read, the field config, create mode by any signal, the context beyond its edit channel, an access attribute or parameter, a raw input however it is opened, a name or markup built at run time, `dynamic`. It follows every Staffora helper a body reaches, static or instance, and the code of a Staffora component it opens (the shared foundation is a list of exact types); each rule is proven on evasion samples.

**Look** (`wwwroot/app.forms.css`):

- Radzen's outlined field: a 1 px line in the strong border colour, radius 4, the label floating on the line at 0.75 rem and resting in the subtle text colour.
- Focus is a 1 px line and a 1 px inner ring in the accent. Inside a state-entity dialog the focus, the floated label on focus and an open drop-down use the state's colour as text (`--dialog-state-text`, 4.5:1 on white).
- A field's `Helper` puts a sentence under it with an info icon, named by the input's `aria-describedby` (`StateFieldFrame.HelperIdOf`).
- `.field-row` sets two fields side by side once the dialog's body is 40 rem (640 px) wide and one below the other under that (a container query on the body, so the side column and a phone both count).
- An area's or the custom fields' legend is a section eyebrow (`.form-eyebrow`: 0.75 rem, capitals, subtle) over a group without a box.
- A drop-down's disabled option keeps the disabled text colour.
- Every drop-down of the app is a Radzen drop-down, never a native `select`; `NativeSelectScanTests` refuses one outside its (empty) allow-list.

Verified by: `StateFieldScanTests`, `StateEntityDialogTests` (the `Fields` part: fields in every state, a required custom field greying the activation, the server refusing a changed custom field of a locked area on every save path), `tests/e2e/state-entity-fields.spec.ts` (axe and screenshots at desktop width and 390 px).

### Header and history

Rules in [DIALOGS.md](../../.claude/skills/workflow-state-entities/DIALOGS.md), "Header and history"; [ADR 0036](../adr/0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md).

**The state pill.** The header shows the entity icon the opener sets (`EntityIcon`) and, once loaded, the state pill (`StateEntityDialogStatePill`): a disclosure button whose card shows the state's description to everyone and, to a reader who may read the history, the last three steps and the button "Full history".

- The card opens when a mouse moves over the pill (never because a pill appeared under a resting pointer) and toggles with a click, a tap, Enter or Space.
- It closes on Esc with the focus in it, without closing the dialog, and on a press or the focus outside it, a dialog opened on top or a step starting.
- It is held to the viewport and scrolls on a short screen.
- "Full history" opens `StateHistoryDialog` (20 steps a page, newest first, paged by a step's place so a new step never repeats or skips one) through `StateHistoryDialog.OpenAsync` (`OpenFormDialogAsync`); the focus returns to the pill when it closes. The header's history button opens the same dialog, for the same readers only.

**Who reads the history.** An active system owner of the record's organization, or a platform owner on the platform console (platform reach, not impersonating). An impersonating platform owner reads what the impersonated user reads. A customer portal's tenant has no system owner, so a portal record's history is left to the platform owner. Everyone else sees the state and the footer's steps, never who took a step or when: the dialog asks once as it first loads (`StateHistoryService.MayReadHistoryAsync`, the context's `MayReadHistory`) and reads nothing for them, and `StateHistoryService.GetHistoryAsync` refuses them (`StateHistoryService.OwnersOnly`) before anything of the record is read, whatever the entity's service let through.

**Reading.** History is read only when the card first opens, then once for each new entity version, through the context's `LoadHistoryAsync`, the entity service's `Get{Entity}HistoryAsync` (the load's permission) and `StateHistoryService`, which:

- applies the owner rule and reads the entity under the tenant filter, and only its own tenant's entries, newest first in a stable order;
- shows times on the wall clock of the entity's own tenant (`TenantClock.GetTimeZoneOfAsync`), naming whose zone it is: the reader's organization's inside the tenant ("times in Europe/Stockholm"), and on the platform console the record's organization's, not the platform's ("times in Europe/Stockholm, the zone of this record's organization", `StateHistoryPage.InReadersZone`);
- names actors only inside the entity's tenant, and notes impersonation and writes on the tenant's behalf.

**The timeline.** Each step is a marker in the colour and icon of the state it led to (a small dot when it kept the state) on a thin line, with its name, who and when on one muted line, and its notes and notices as small lines with an icon. The full history groups steps under day headings (Today, Yesterday, "22 Nov 2026", by `StateHistoryPage.Today`). When a step changed the state, its From and To are small chips with the state's own `[Icon]` and `[StateStyling]` colour, the text white or the text colour by `ColorContrast.OnFill` (`StateHistoryStepView`). The full history dialog is framed in the record's current state colour, its header and left edge in `DialogStateColors`' header fill (`FormDialogOptions.StateColor`), never the accent of a dialog without a lifecycle.

There is no workflow inspector: the footer is where a reader learns what a record can do next.

Verified by: `ShortDialogAccessibilityTests`, the accessibility sweep (the full history opened from the tenant dialog's state pill), `tests/e2e/state-entity-dialog.spec.ts` (the pill's module and the dialog's stylesheets).

### The tenant dialog and tenant onboarding

**The tenant dialog.** `TenantDialog` (`Components/Common/Dialogs/TenantDialog.razor`, its context `TenantDialogContext.cs`) over `TenantService`, token `tenant`, so `?dlg=tenant-{id}` on the console reopens it.

- A tenant's console opens it from "Tenant details" in its header and reads the tenant again once it closes having changed it. The dialog has no create mode.
- The body is the tenant's name (required) and description, editable in Setup and Active and read-only in Suspended, Pending deletion and Archived, as the tenant workflow says.
- The footer offers the workflow's steps (Save, Activate, Suspend, Reactivate, Schedule deletion, Cancel deletion, Archive), each the platform owner's (`RequiresPlatformOwner`), the destructive ones confirmed. Save is offered as the name is typed: the text fields report every keystroke and the footer judges in memory. Activate needs the tenant's active system owner.
- The tenant workflow designates no save step, so closing the dialog with edits asks whether to lose them, and a save happens only through a footer step.
- A rename renames the tenant only: its Company root keeps the name it was created with, as any unit does once created ([OrganizationUnit](../database.md#organizationunit)); its administrators rename it on the organization page.
- The platform tenant's name and description are fixed: its dialog shows them read-only (`IsLockedRecord`) with no step, and the service refuses any edit of it.
- Every method of the dialog checks platform reach, so a tenant's user and an impersonating platform owner get "could not open" with the reason.
- What each state means for access (sign-in, cookies, impersonation, the tenant's records held) is in [authorization.md](../authorization.md#tenant-states-and-access).

**Tenant onboarding.** The tenant list's "New Tenant" opens `TenantOnboardingDialog` (`Components/Common/Dialogs/`), a form dialog through `OpenFormDialogAsync`, since onboarding writes the tenant, its roles and its first user together and is no step of one entity. The form asks for the tenant's name and description and the first system owner's first and last name and email address; Create tenant is offered once the required ones are typed.

- `TenantOnboardingService.OnboardTenantAsync` creates the tenant in Setup through `TenantService.CreateTenantAsync` (internal, the one create of a tenant), which trims the name and description and refuses a name another tenant holds ("A tenant with that name already exists."). Names are unique as the database compares them, case aside, and two creates of one name at once get the same answer from the unique index.
- It hands the row to the internal `WorkflowService.CreateAndTransitionAsync` overload with an after-insert hook that gives the new tenant, in the same transaction, its system account, its Company root and its time zone pinned to the platform's as configured then ([ADR 0020](../adr/0020-dates-and-times.md)), the last two stamped with the account, then its role presets and its first system owner (`TenantOnboardingWriter`). Any failure among them rolls the whole create back.
- A refusal stays in the form with its reason. On success the form shows the system owner's initial password once (`.tenant-onboarding__password`, painted over in the journeys' screenshots), and Done closes it and opens the new tenant's dialog in Setup ([System owners](../authorization.md#system-owners), "Onboarding").

Verified by: `TenantDialogTests`, `TenantCreateTests`, `TenantOnboardingTests`, `tests/e2e/tenant-dialog.spec.ts`.

### The unit and role dialogs

An organization unit and a tenant role are state entities, each edited only in its own state-entity dialog, by the recipe in [DIALOGS.md](../../.claude/skills/workflow-state-entities/DIALOGS.md).

**The unit dialog.** `OrganizationUnitDialog` (`Components/Common/Dialogs/OrganizationUnitDialog.razor`, its context `OrganizationUnitDialogContext.cs`) over `OrganizationService`, token `unit`, so `?dlg=unit-{id}` reopens it.

- *Info*: name, type, the unit it sits below, description and custom fields, each shown, locked and marked by the unit workflow: editable while the unit is being created and while active, the name required; read-only while inactive or archived, and for a reader without `OrganizationUnitsEdit` or a tenant's unit on the platform console (`IsLockedRecord`).
- *Units below*: a child list of the units directly below (`OrganizationUnitChildList`), each row opening its own unit dialog on top.
- *Role holdings* (behind `RolesView`): `UnitRoleHoldingList`, read through `OrganizationService.Holdings`, the role-holding service. A row opens the holding's form dialog; "Add role holding" opens `AddRoleHoldingDialog` for the unit, offered while the unit is active and to whoever the role-holding service lets manage holdings.
- The footer offers the unit workflow's steps: Create (below a unit in use), Save (the active state's save step), Deactivate (with every active unit below), Reactivate (below a unit in use), Archive (once every unit below is archived), Restore and Delete. The steps out of use are confirmed, and each is greyed with its reason when blocked: the root's Deactivate, Archive and Delete; a parent's Delete ("It has units below it"); a unit somebody holds a role on (its holdings' count); the grant ceiling.
- The create comes from the organization page's "Add unit below" (`OpenCreateAsync(dialogService, service, parentId)`), with the parent set. `OrganizationService.CreateOrganizationUnitAsync` judges the structure policy (a parent, never a second Company, a name free among the siblings) and the custom fields first, and `WorkflowService.CreateAndTransitionAsync` creates the row with its first step under the tenant's structure lock.
- The dialog's Delete is the one way a unit is deleted: the delete step from the state it is in, through `UpdateOrganizationUnitAndTransitionAsync`, which checks `OrganizationUnitsDelete` first.

**The role dialog.** `TenantRoleDialog` (`Components/Common/Dialogs/TenantRoleDialog.razor`, its context `TenantRoleDialogContext.cs`) over `TenantRoleService`, token `role`.

- *Info*: name (required), description, and "Holders are staff members", a switch over `ApplyResourceMembershipToUser` whose change the save refuses without `ResourcesManage`.
- *Permissions*: `StatePermissionMatrix`, one check box per permission grouped by area (one group per `PermissionArea`, named by its display name), over the role's own collection of permission rows, editable as the workflow's Permissions area says. A permission the reader does not hold is disabled and marked "you do not hold it".
- *Holders* (behind `RolesView`): `RoleHolderList`, read through `TenantRoleService.Holdings`, the role-holding service. A row opens the holding's form dialog; "Add holder" opens `AddRoleHoldingDialog` for the role.
- A role that grants a permission the reader does not hold shows read-only with the reason (`LockedReason`, `TenantRoleService.RoleBeyondCeilingReadOnly`).
- The footer offers Create, Save (the active state's save step, through the updater: name, description and the permission rows diffed by key) and Delete. Delete is greyed with the reason while anybody holds the role ("Someone holds this role: remove it from them first."), judged again at once when a holder is added or removed in the Holders view (the role reloads), and runs through the role service, which needs `RolesDelete`, not `RolesEdit`.
- The Roles page's rows open it (`OpenAsync`) and "New Role" opens it in create mode; the footer is the one way to delete a role (`LifecycleCallScanTests`).

**Holdings in three places.** A role holding is the same row seen from its user (the user dialog's Roles view), its role (Holders) and its unit (Role holdings). `UserRoleHoldingRow` carries the unit's state and, outside the user's own list, the holder's name. `RoleHoldingDialog` names the holder where it is shown. `AddRoleHoldingDialog` is opened fixed to a user (`OpenAsync`), a role (`OpenForRoleAsync`) or a unit (`OpenForUnitAsync`), choosing the other two, or for "New staff member" (`OpenForNewStaffAsync`: the person and the unit chosen, and a role whose holders are staff members, preset when the tenant has one, from `RoleHoldingChoices.ForNewStaff`; its button says "Make staff member").

Verified by: `OrganizationUnitLifecycleTests`, `TenantRoleLifecycleTests`, `OrganizationDialogTests`, `tests/e2e/lifecycles.spec.ts`, the accessibility sweep.

### Staffing dialogs

**Staff** ([ADR 0008](../adr/0008-users-become-resources-through-a-role.md)). The Staff page (`Pages/Staffing/StaffMembers.razor`, `/staffing/staff`, `ResourcesView`) lists the tenant's staff records on the list surface (`StaffList`: searched by name or address, filtered by status and by work pattern, sorted by name, address or work pattern; `ResourceService.QueryStaffMembersAsync`). Each row has its status in the strip, the units it belongs to and its **Work pattern**: the pattern in force today ("Weekdays 07–16 · since 2026-11-02", or "… · ends 2026-12-31" when it has an end, in the warning ink when that end is within 30 days), else the next one ("From 2026-12-01: Weekdays 07–16"), else "No work pattern", muted.

- One rule decides the pattern shown (`StaffPatternCover`, `Staffora.Core/Models/StaffModels.cs`; in SQL `StaffPatternCoverage`, `Staffora.Services/Scheduling/`), on the tenant's today: only an **active** assignment covers (a prepared or withdrawn one gives no hours), the one in force today first, else the earliest that starts later.
- The "Work pattern" filter (a chip, `?pattern=`): "Has a work pattern" (`current`, one in force today); "No work pattern" (`none`: a record that is not inactive with no active assignment in force today or later; one out of service needs none); "Ending soon" (`ending`: the one in force ends within `StaffPatternCover.EndingSoonDays` = 30 days and no active one starts after it).
- The sort by work pattern orders by the name of the pattern in force or next, none first. Filter and sort run in the database; the column's patterns take one more query for the page.
- A staff member has no dialog of their own: a row opens their user dialog, whose **Staff** view (behind `ResourcesView`, offered only when the dialog's options name a staff record, `StateEntityDialogView.When`) shows the record's status and the units as a read-only child list (`StaffUnitList`, `UserService.QueryApplicationUserStaffUnitsAsync`). The user dialog reads its options with every load (`ReadsEditorOptionsWithEveryLoad`), so a Deactivate or Reactivate in the dialog shows the record's new status at once.
- "New staff member" in the page's header, for whoever may give roles (`RolesEdit`; a coordinator, who reads staff members but gives no roles, is not offered it), gives a role whose holders are staff members through the role-holding service, which makes the person a staff member of the unit in the same transaction. With no such role the reader may give, the page says so instead.
- A staff member read that fails in the database is logged and answered as a failed result the page shows; one chosen as a deadlock victim is read once more first (`ResourceService`).

Verified by: `StaffDialogsTests`, `StaffReadTests`, `tests/e2e/staff.spec.ts`, the accessibility sweep at 1500, 1280 and 390 px.

**Qualifications.** The Qualifications page (`Pages/Staffing/Qualifications.razor`, `/staffing/qualifications`, `ResourcesView` or `QualificationsManage`) lists the catalogue on the list surface (`QualificationList`: searched by name or description, filtered by state, with how many staff members hold each; `QualificationService.QueryQualificationsAsync`).

- A row opens `QualificationDialog` (its context `QualificationDialogContext.cs`, token `qualification`) with two views. *Info*: its name (required, unique in the tenant) and description, read-only for a reader without `QualificationsManage` (`IsLockedRecord`). *Staff* (behind `ResourcesView`, for a stored qualification): a child list of the staff members who hold it (`QualificationHolderList`: searched by name or address, filtered by the staff record's state, sorted by name, each row with the record's state in the strip; `QualificationService.QueryQualificationHoldersAsync`), a row opening the staff member's user dialog on top and the list read again once it closes.
- The footer offers Create, Save (the active state's save step), Archive (confirmed) and Restore. "New qualification" in the header, for `QualificationsManage`, opens it in create mode.
- The staff member's user dialog has a **Qualifications** view (behind `ResourcesView`, for a staff member only; `StaffQualificationList`, `UserService.QueryApplicationUserQualificationsAsync`): a child list of the qualifications the staff member holds, an archived one marked. Its "Add qualification" (`AddQualificationDialog`, a form dialog over the cached catalogue's active entries the staff member does not hold) and a row's own form dialog (`StaffQualificationDialog`, with "Remove qualification") are offered exactly when the qualification service would take them (`CanManageQualifications`: `QualificationsManage` and a staff record that is not inactive). The view reads its page again after either.

Verified by: `QualificationTests`, `QualificationDialogsTests`, `tests/e2e/qualifications.spec.ts` (1280 and 390 px), the accessibility sweep.

**Work patterns.** The Work patterns page (`Pages/Staffing/WorkPatterns.razor`, `/staffing/work-patterns`, `ManageWorkPatterns` or `AssignWorkPatterns`) lists the patterns on the list surface (`WorkPatternList`: searched, filtered by state, sorted by name or cycle; `WorkPatternService.QueryWorkPatternsAsync`). Each row shows its cycle, its working hours per cycle and its **Staff**: how many staff members work by it today and from a later day ("4 today · 2 upcoming"), counted in one query for the page over the active assignments that have not ended.

- A row opens `WorkPatternDialog` (its context `WorkPatternDialogContext.cs`, token `workpattern`); "New pattern" (`ManageWorkPatterns`) opens it over a new one-week draft, which its Save creates.
- *Info*: the name, description and cycle length (weeks) and the working hours in `StateWorkPatternSegments`: one row per day of the cycle (grouped by week in a longer one), each with "Add hours" and its segments' start and end as the browser's own time inputs and a remove button, each named for its day and place. What the rules refuse is listed under the days as it is typed, in a polite live region; a time input left empty keeps the time it had, which the problem names. The hours read in words once read-only. A segment on a day the cycle no longer has is listed apart to be removed.
- The footer offers Save and Publish (confirmed) on a draft, Delete (confirmed: the draft goes with its segments) and Archive (confirmed) on a published pattern. For a maker of patterns on a saved pattern it adds "Copy to new draft" (the context's `FooterActions`, no step, decided with every load and announced with `NotifyChanged`, so it shows as soon as a create's Save made the row): it settles the dialog's edits, then opens a new draft in create mode on top, built by `WorkPatternService.NewWorkPatternAsync` from the stored pattern.
- The Delete goes through `WorkPatternService.UpdateWorkPatternAndTransitionAsync`, whose one delete branch takes the draft as it is stored, as the service's own callers do.
- *Staff* (for a stored pattern and a reader of the assignments: `ResourcesView` or `AssignWorkPatterns`, `PatternAssignmentService.Readers`, read with the editor options as `WorkPatternEditorOptions.ReadsStaff`): every assignment of the pattern but a deleted one (`WorkPatternStaffList`: searched by the staff member's name or address, filtered by its dates on the tenant's today (current, upcoming or ended) and by state, sorted by start, the latest first, or by name; read through `WorkPatternService.Assignments` from `PatternAssignmentService.QueryAssignmentsOfPatternAsync`). Each row shows the staff member, the dates, "Current", "Upcoming" or "Ended" and its state in the strip, and opens the assignment's own dialog on top (`PatternAssignmentDialog`).
- **"Assign to staff"** (the Staff view's add action, for `AssignWorkPatterns` on a published pattern, `WorkPatternEditorOptions.MayAssignStaff`) opens a form dialog (`AssignPatternToStaffDialog`): the staff members whose record is not inactive in a searchable multi-select, each with the pattern they have today or next, a start date (today on the tenant's clock to begin with) and an optional end. "Assign" calls `PatternAssignmentService.AssignPatternToStaffAsync`:
  - it requires `AssignWorkPatterns` in the session's own tenant (the platform console is refused), 1 to `MaxStaffPerAssign` (50) staff members, the dates in order, and a published pattern of the tenant;
  - every staff member is judged by the workflow's own rules before anything is written (a staff record of the tenant, another tenant's read as not found; not inactive; no active assignment sharing a date), so a refusal names each staff member refused and why (`PatternStaffAssignment.Refused`, listed in the form) and nothing is written for anyone;
  - when none is refused, each staff member gets one assignment, created by its Save and put into effect by its Activate (its cycle from the Monday of the start's week), all in one workflow batch (`WorkflowService.RunBatchAsync`, `WorkflowBatch.CreateAsync` then `WorkflowBatch.TransitionAsync`): one transaction under the tenant's schedule lock, each step judged again with its own history, the history rows sharing one correlation id, all of them or none.

Verified by: `WorkPatternLifecycleTests`, `WorkPatternDialogsTests`, `WorkPatternRulesTests`, `WorkPatternCoverageTests`, `WorkPatternStaffDialogsTests`, `tests/e2e/work-patterns.spec.ts` (1280 and 390 px), the accessibility sweep.

**Schedules.** The user dialog's Schedule view, for a staff member (`ResourcesView`, `StateEntityDialogView.When`), shows the staff member's hours across a period and the work patterns given to them as a child list whose rows open `PatternAssignmentDialog` (its context `PatternAssignmentDialogContext.cs`, token `patternassignment`) on top; its "Assign work pattern" opens one in create mode. The user dialog's context opens both through the assignment service its user service works with (`UserService.PatternAssignments`), so its body injects nothing new ([Schedules and availability](schedules-and-absence.md#schedules-and-availability)).
