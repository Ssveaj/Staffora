# Page shell and shared UI

Every signed-in page sits in one shell: a left sidebar with the navigation, a top row with the breadcrumb, the page's header, search, the bell and the account trigger, and the page below it. This document describes that shell, the design tokens and icons every stylesheet reads, the shared components and helpers pages are built from, global search, the accessibility checks and the enum annotations the UI reads. Lists, URL state and dialogs are described in [lists-and-dialogs.md](lists-and-dialogs.md); permissions in [authorization.md](../authorization.md).

## Page chrome and navigation

The shell follows [ADR 0029](../adr/0029-left-sidebar-shell.md), which supersedes the top bar of [ADR 0025](../adr/0025-top-bar-shell-section-menus.md).

### The shell

Every signed-in page sits in `AppShell` (`Layout/`). Both layouts host it inside the session gate, in every area: the platform console, a tenant's pages, one's own pages, a customer portal and the system pages. It holds:

- a "Skip to content" link, the first tab stop, which moves the focus to the page;
- a full-height sidebar down the window's left edge, white with a 1 px line at its right (`--app-sidebar-width`, 248 px). It holds the brand (the mark on a rounded accent square and "Staffora" bold in the deep accent, a link to the start page) with a "<<" button named "Collapse menu" at its right, then the navigation (`NavMenu`);
- beside it the content: the 56 px top row (`--app-header-height`, the page's one `header` banner, white with a 1 px line under it), with the breadcrumb at its start (`AppBreadcrumb`) and the search button ([Global search](#global-search)), the bell and the account trigger (`AccountMenu`) at its end;
- then the page in a `main` landmark (`#main-content`) on the warm floor, full width and without a gutter. The body under the top row scrolls on its own.

`MainLayout` draws the tenant's chrome and, for a platform owner who is not impersonating, the platform console's. `SuperAdminLayout` always draws the console's (`app-shell--platform`), told apart by its own navigation, its breadcrumb ("Platform / ...") and the trigger's "Platform owner" line. A visitor who is not signed in gets the plain frame (`anonymous-layout`).

### Sidebar

Each section of `AppNavigation` is a group in the `nav` landmark "Main navigation". The group's title, in small muted capitals with letter spacing, names the list of its links (`aria-labelledby`).

- A link is a 40 px row with an 18 px line icon and its label, rounded 8 px, muted at rest and on the hover fill when pointed at.
- The current link sits on the soft accent with its text in the deep accent, semibold, and a small accent dot at its end.
- The current link is the page's own or, for a page under a link's page (a tenant's console under Tenants, a job under Jobs), the link with the longest path it lies under (`AppNavigation.Locate`, query left out, decided by the circuit from the address). Only the page's own link says `aria-current="page"`.
- A customer portal's sections are groups like the others.

### The fold

"Collapse menu" folds the sidebar to a 64 px rail (`--app-sidebar-rail`):

- the group titles give way to a line between the groups;
- the labels are read but not shown: each link keeps its label as its `aria-label` and its tooltip (`title`);
- the brand shows only its mark, and ">>", "Expand menu", unfolds it. The focus moves to the button that took the pressed one's place.

The choice is remembered per browser in a cookie of its own (`staffora.sidebar=collapsed`, one year, `SameSite=Lax`). It is a display preference with no identity or session state, so the session stays on the auth cookie. The server reads it for every page it serves and marks the root element (`data-sidebar`, `SidebarPreference`, written by `App.razor`), so the page paints at its width from the start and the fold survives every navigation and a reload.

### The drawer

Below 900 px the sidebar is an off-canvas drawer, always unfolded, with a close button ("Close menu") in place of the fold.

- The top row's menu button ("Open menu", `aria-controls` the sidebar, `aria-expanded`) opens it over a scrim (`--color-scrim`) and moves the focus to the current link. The skip link and the content behind it are `inert`.
- Its close button, Escape, a click on the scrim (which reaches nothing under it) or a followed link closes it, and the focus returns to the menu button. Crossing the 900 px width closes it.

`AppShell.razor.js`, loaded by `App.razor` on every page, gives the fold and the drawer their behaviour in the browser, so they also work on pages rendered on the server. It keeps their state where the circuit never renders it: the root's `data-sidebar`, the shell's `data-drawer-open`, the content's `inert` and the menu button's `aria-expanded`. Both fold buttons are rendered and the stylesheet shows the one that fits.

### Breadcrumb

The breadcrumb (`nav` "Breadcrumb" in the top row) reads the page's place by the same rule as the sidebar:

- "Section / Page" ("Platform / Overview"): the section muted and a link to its first page unless that is the page on screen, "/" between the steps, and the page semibold in the text colour (`aria-current="page"`).
- The last step is the page header's title, which is the page's `h1`, with its help, wherever the page declares a header (see [Header](#header)).
- A page under a link adds that link and ends in the title its `PageHeader` gives ("Platform / Tenants / Company A", through the section `AppBreadcrumb.PageSection`).
- A page outside the navigation (a system page) shows its header's title alone. An empty step is not drawn.
- On a phone it keeps only the page.

### Account trigger

The account trigger is a 36 px pill with the initials of the user name (`AccountMenu.Initials`), the name of the person the session acts as and, for a session that is not an ordinary one, a second line ("Platform owner", "Impersonating", "Impersonation ended"). Its name for assistive technology is "Account menu:" (read, not shown) followed by those words.

- It is a native disclosure (`details`/`summary`), so it opens without a script. Escape closes it and returns the focus to it, and a click outside closes it.
- Its panel says who is signed in ("Signed in as", the account's own name), offers one's own profile ("My profile", read only, for a tenant session that is not impersonating) and Sign out. Nobody changes their own account ([ADR 0034](../adr/0034-no-self-service-account-pages.md)).
- While impersonating, the trigger is drawn on the brand's ink with light text on every page (`data-impersonating`, no yellow), and the panel opens with a "Running as" box naming the impersonated user and the "End impersonation" button, a form post to the end endpoint. An impersonation that ended keeps the ink trigger and the button.
- A refused session keeps the chrome with only the sign-out: no navigation landmark and no link to its own account.

The bell (36 × 36, its count white on the danger dot) stands before the trigger for every session that has notices ([notifications-and-comments.md](notifications-and-comments.md#notifications)).

### Header

Every routable page declares `PageHeader`, in its own markup or in the layout it names. `PageChromeScanTests` fails on a page without one unless it is allow-listed with its reason: the start page, the sign-in flow, the system pages, the homes and the coordination workspace, each with an `h1` of its own.

`PageHeader` draws no band: the shell's top row is the page's header.

- The title is the breadcrumb's last step and the page's one `h1`, at the breadcrumb's size and semibold (`AppBreadcrumb.PageSection`).
- It is followed by the list's count when the page passes `Count`, and by a "?" button named "About this page" whose popover holds the `Description`. The popover opens and closes in the browser (`AppShell.razor.js`): the button says whether it is open, and a second click, Escape or a click outside closes it.
- The page's `Actions` stand inside the page, at the end of its first row on the floor beside the meta facts, in the page's order with their ids and attributes. No page action stands in the shell's top row.
- `Meta` facts (`MetaItem`s) and `Filters` stay in the page, in a row on the floor at its top.
- `PageHeader` renders title and actions through Blazor's sections, so the markup keeps its scope and its handlers. `PageChromeScanTests` holds the shape: no `header` band, one `h1`, both outlets in the shell.
- It sets the browser tab's title ("Title - Staffora").
- `Icon`, `RootText` and `RootPath` are kept for the call sites and drawn nowhere.

A page without a header (a home, the workspace, a system page) shows the navigation link's text as the breadcrumb's last step instead, hidden whenever a header's `h1` is present.

`PageScaffold` puts the page's body under the top row with a 1.5 rem gutter (1 rem on a phone). `PageSection` and `OverviewCard` are bordered blocks (radius 12) without a shadow, and the tenant console's tabs are underline tabs with icons. Nothing is wider than the viewport.

### Navigation

`AppNavigation` (`Layout/`) holds the sections.

**The tenant's sections:**

- **Overview.** Each user is offered one overview, the home of their strongest role: Organization's to whoever holds `TenantView`, Staffing's only to a coordinator without it, and "My overview" to whoever has neither ([coordination-and-homes.md](coordination-and-homes.md#homes)). One's own profile is in the account menu above Sign out, not in the navigation.
- **My work:** "My work" (`ViewOwnAssignments`), "My assignments" (`ViewOwnAssignments`) and "My schedule" (`ViewOwnSchedule`).
- **Staffing:** "Overview" (`/staffing/overview`, `ViewCoordination`, the coordinator's home; the breadcrumb reads "Staffing / Overview"; not offered to a holder of `TenantView`, whose overview is Organization's), then "Coordination" (`/coordination`, the workspace, a link of its own), then staff members (`ResourcesView`).
- **Organization:** overview (`TenantView`), units (`OrganizationUnitsView`), users (`UsersView`), roles (`RolesView`), settings (`SettingsView`). A tenant's users say organization; only the platform console says tenant.

**The platform console's sections:** overview, tenants, platform settings, Platform health and the error log. They are not permission-gated, since the console opens only with platform reach.

**Rules:**

- A link shows when the user holds any of its permissions, which are the ones its page gates on. A section with no link left is left out. `NavigationGateTests` reads each tenant and portal page's gate (the `PermissionView` that wraps it) and requires its link to name exactly those permissions.
- `NavMenu` reads one snapshot of the runtime and takes every fact from it: no user, nothing; platform reach, the console's sections; anyone else, an impersonating platform owner included, the tenant's sections that the effective user's permissions open, looked up for that same snapshot (`PermissionService.GetUserPermissionsAsync(RuntimeSnapshot)`, primed when the session resolved).
- A customer portal session gets the portal's own sections instead (`AppNavigation.Portal`), and every page that is not part of the portal shows "Not part of your portal" in its place ([customers-and-portal.md](customers-and-portal.md#customer-portal)).
- `NavMenu` renders the sections as labelled groups of links in the `nav` landmark "Main navigation" and rebuilds on every session change. `AppBreadcrumb` reads the same sections for the same snapshot (`AppNavigation.ForSessionAsync`).

Verified by: `NavMenuTests`, `AppShellTests`, `NavigationModelTests`, `NavigationGateTests`, `PermissionSnapshotTests`, `NavigationPermissionTests`, `PageChromeScanTests`; in a browser `tests/e2e/navigation.spec.ts` and `tests/e2e/page-chrome.spec.ts`.

### System pages

Not found (`/not-found` inside a circuit, `/status/404` for a request), the other status pages, access denied, the error page, lockout and invalid-user are `SystemPage` cards inside `MainLayout`: two panels on the muted floor, the page's tone tinting the panel with its icon, an eyebrow pill such as "Error 404", a 2.5 rem title, the message and one button on. A signed-in user sees them with the chrome and navigation, an anonymous one in the plain frame.

- The status and error pages are re-executed passes, which Blazor renders without waiting for the session gate. `ReExecutedPageSession` (`Helper/`, in `Program.cs` after authorization) therefore resolves the request's session before such a page renders. A session that cannot be resolved renders as signed out.
- A request the rate limiter refused (429) is the exception: its page renders in the plain frame as signed out, without that read, so refused traffic costs no database work.
- A cookie whose user the sign-in manager refuses (deactivated, of a tenant that is not Active, or whose security stamp changed) is rejected at the request itself (`RequestStampCheck`), so its page renders in the plain frame as for a visitor.

Verified by: `SystemPageLayoutTests` (both frames, the refused cookie and the 429 over HTTP).

### Sign-in page

`/Account/Login` (`Components/Account/Pages/Login.razor` and its `.razor.css`) is a split screen in the brand's look of the homes, full-bleed in the plain frame (`.anonymous-layout:has(> .sign-in)` in `app.css` drops the frame's padding for it).

**From 1024 px:**

- On the left, 55 % wide and as tall as the window, the deep ink panel (`--color-ink` to `--color-ink-strong`) over the art. It holds the brand (the mark on the accent square with `--shadow-ink-glow`, "Staffora"), the headline "Every job, the right people." (a paragraph, not a heading), one line in `--color-on-ink-muted` and three proof points, each a line icon on a faint ink chip ("Plan jobs and their staff", "Staff answer in one tap", "Customers order through their portal"). A scrim deepens the ink on the words' side, so the art never lowers their contrast.
- On the right, the warm floor with a white card at most 420 px wide (`--radius-2xl`, 16 px, and `--shadow-lift`): "Welcome back" as the page's one `h1`, "Sign in to Staffora", the email and password fields with their labels above them, "Remember me", the full-width teal "Sign in", and a foot line saying that a forgotten password comes from one's organization's administrator. There is no e-mail flow.

**Below 1024 px** the panel is a band about 180 px tall with the brand and the headline only, and the card is lifted over its lower edge.

**The form** is Identity's static SSR form, unchanged: its handler `login`, the antiforgery token, the names `Input.Email`, `Input.Password`, `Input.RememberMe` and the messages.

- A password is the one way in: there is no passkey, two-factor or external sign-in ([ADR 0034](../adr/0034-no-self-service-account-pages.md)).
- A refused attempt (invalid credentials, a refused account or organization) is the status line (`StatusMessage`, `role="alert"`) in the danger tokens with an error icon that assistive technology does not read. Field messages and the validation summary take `--color-danger`.
- A lockout goes to the Lockout page and a 429 to its status page.
- There is no show-password toggle: it would need a script and a second control named after the password, which every journey's `getByLabel('Password')` would also find.

**The art** is `wwwroot/images/sign-in-art.svg`, drawn for the app and served from its own origin (the content security policy allows images from `self` only): an abstract map on the ink with a hub and concentric orbits (two dotted), companies as ringed nodes, smaller satellites joined by hairlines, three soft glows, a fine dot grid fading from the hub and two tilted orbits for depth, with no text.

- It is an `<img alt="">`, so assistive technology skips it, cropped to the panel with `object-fit: cover`.
- An image cannot read custom properties, so the SVG writes the ink tokens' values itself and names them.
- The art drifts slowly (48 s), a glow over its hub breathes (9 s) and the card rises 12 px once, by moving only. All three stand still under `prefers-reduced-motion`.
- Fonts are the system stack.

Verified by: `tests/e2e/sign-in.spec.ts` (screenshots under `output/playwright/sign-in/`), the showcase's `login-1280` and `login-390` baselines, the accessibility sweep's `/Account/Login`, `TokenUsageScanTests` and `TokenContrastTests`.

### Names

Page titles, breadcrumbs and cards use the navigation's words in sentence case: the tenant's pages sit under "Organization", the console's under "Platform" ("Platform overview", "Tenants", "Users", "Organization units", "Custom fields", "Platform health"). An Identity role shows by what it means (`AppRoles.DisplayName`: "Platform owner", "Standard user"), never by its identifier.

## Design tokens and icons

Colours, spacing, radii, elevation, density and the type scale are CSS custom properties in `Staffora.Blazor/wwwroot/app.tokens.css`. Page and component styles read them (`var(--space-4)`, `var(--color-text-muted)`) instead of writing literal values. Their values are the design tokens of the [UI plan](../plans/ui-plan.md) and [ADR 0024](../adr/0024-visual-language-tokens-and-state-colour.md): a warm off-white floor (`#faf9f6`) under white cards, a teal accent (`#0f766e`), 1 px lines and a muted band instead of shadows, which only floating things carry.

### Stylesheets

- `app.typography.css` applies the type tokens to the page and declares the icon font.
- `App.razor` loads the stylesheets in this order: the Radzen theme, `app.tokens.css`, `app.typography.css`, `app.css`, then the scoped component styles.
- The tokens file ends with a Radzen bridge, which is why it must follow the theme. The bridge points Radzen's own properties at the tokens: `--rz-primary`, the status colours, `--rz-text-font-family`, the text colours, `--rz-border-color` and the inputs' line, `--rz-input-placeholder-color`, `--rz-input-disabled-color` (the subtle text colour, so a disabled field and its label keep 4.5:1) and the header. It also keeps Radzen's buttons from uppercasing their labels, rests a field's label in the subtle text colour and gives only a placeholder (`::placeholder`) the placeholder grey.
- A dark block is declared under `prefers-color-scheme: dark` but applies only under `data-color-scheme="auto"` on the root, which nothing sets. Dark mode is therefore not active (an open point of the UI plan).

### Token families

| Token family | Names |
|---|---|
| Colour | `--color-accent` (`-strong`, `-soft`, `-tint`), `--color-on-accent`, `--color-text` (`-muted`, `-subtle`, `-faint`, `-placeholder`, `-inverse`), `--color-canvas`, `--color-surface` (`-muted`, `-sunken`, `-hover`), `--color-border` (`-strong`), `--color-divider-faint`, `--color-{success,warning,danger,info}` (`-soft`, `-border`, `-dot`), `--color-neutral` (`-soft`, `-dot`), `--color-chrome`, `--color-chrome-platform`, `--focus-ring` |
| Spacing | `--space-0` to `--space-12`, on a 4px rhythm |
| Radius | `--radius-sm` (4), `-md` (6), `-lg` (8), `-xl` (12), `-2xl` (16, the sign-in card), `-full` |
| Elevation | `--shadow-1` to `--shadow-3`, `--shadow-pop` (menus, popovers), `--shadow-drawer`, `--shadow-lift` (a card lifted over the ink), `--shadow-ink-glow` (the brand's mark on the ink) |
| Density | `--control-height-sm`, `-md`, `-lg` (30, 34, 40 px), `--app-header-height` (60 px) |
| Type | `--font-family-sans`, `--font-family-mono`, `--font-size-2xs`, `-xs`, `-dense`, `-sm` to `-4xl`, `--line-height-{tight,snug,normal,relaxed}`, `--font-weight-{regular,medium,semibold,bold}` |
| Icons | `--icon-size-sm`, `-md`, `-lg` |

### Colour rules

- A component stylesheet (`*.razor.css`), `app.css` and `app.typography.css` write no hex colour. `TokenUsageScanTests` refuses one, and its allow-list is empty.
- Every text pair of the token table reaches 4.5:1, and the faint and placeholder greys never carry information. `TokenContrastTests` reads the values from `app.tokens.css` and checks them.
- A status family's foreground is for text and filled badges; its `-dot` shade is for dots, bars and lines only.
- A surface filled with a state's colour from `[StateStyling]` takes its text colour from `ColorContrast.OnFill` (`Helper/`): white where it reaches 4.5:1, else the text colour, else the fill darkened until white does. `TokenContrastTests` runs it over every state colour.

### Icons

- Icons outside Radzen components use `MaterialSymbol` (`Components/Common/`): `<MaterialSymbol Name="logout" />` renders the Material Symbols (Outlined) ligature.
  - Without a `Label` the icon is decorative and carries `aria-hidden="true"`; with one (`Label="Slots attention"`) it is an image with that accessible name.
  - `Class` adds classes (`material-symbol--sm` and `material-symbol--lg` pick a size), and any other attribute passes through.
- The font is the one the Radzen package ships, served from the app's own origin (`_content/Radzen.Blazor/fonts/`), so no font host is contacted.
- A button with an icon before its text is an `IconTextButton` (`Components/Common/`): Radzen's button with its classes and markup, the icon's ligature hidden from assistive technology, so the button is named by its text alone ("Save", never "save Save"). While busy it keeps its text.
- A `RadzenButton` takes an `Icon` only when it shows nothing else, and then carries an `aria-label` ([Accessibility](#accessibility)).
- Avatars are a generic `account_circle` symbol, not a picture from a gravatar host, which would send each address's hash to a third party.

The showcase (`tests/e2e/showcase.spec.ts`) compares the key pages with reviewed baselines, so a change to tokens or icons shows as a failed comparison until the baselines are regenerated and reviewed ([Staffora.Test/E2E/README.md](../../Staffora.Test/E2E/README.md)).

## Shared UI components

| Component | Location | Purpose |
|---|---|---|
| `PageScaffold` | `Components/Common/Layout/` | Page frame with `Header` and `Main` slots: the header full width, the body on the white floor with a 1.5 rem gutter (1 rem on a phone) |
| `PageHeader` | `Components/Common/Layout/` | The header of every page; see [Header](#header) |
| `PageSection` | `Components/Common/Layout/` | A bordered block (radius 12, no shadow) with an optional `h2` title and description, then its content |
| `OverviewCard` | `Components/Common/Layout/` | A bordered block that leads somewhere: a link (`Path`) or a button (`Click`), with an icon chip of its `Tone`, a title and a description; rows of them sit in `ui-card-grid` |
| `SystemPage` | `Components/Common/Layout/` | The two-panel card of a system page: a panel tinted in its `Tone` with the icon large, then an eyebrow pill (`Eyebrow`), the `h1`, the message, details (`ChildContent`) and a link on (`LinkPath`, `LinkText`; the start page by default) |
| `IconChip` | `Components/Common/Ui/` | A Material Symbols icon on a rounded square of its `UiTone`; decorative; `Large` for empty states and system pages |
| `EmptyState` | `Components/Common/Ui/` | What a section shows when it has nothing to show or may not show it: centred, a 64 px tile of its tone (the accent by default, a status tone for a refusal or failure) with a 24 px icon, a title (1.25 rem, 500), one subtle sentence and at most one step (`ChildContent`); `Compact` inside a panel, one muted line in a dashed box; `Role="alert"` for a refusal |
| `LoadingState` | `Components/Common/Ui/` | What a page, a dialog or a part of one shows while it loads; see [Loading](#loading) |
| `StatTile` | `Components/Common/Ui/` | One figure with its label and icon, for a summary row |
| `MetaItem` | `Components/Common/Ui/` | A small icon spaced from a short fact, for the header's `Meta` slot |
| `TabBar` | `Components/Common/Ui/` | The one tab list outside a dialog; see [Tab bar](#tab-bar) |
| `FormDialogFrame` | `Components/Common/Dialogs/` | A form dialog's body and buttons: the fields, then Cancel (closes with `false`) and the primary step (`OnSave`, `SaveDisabled`, `IsBusy`, `SaveText`, `SaveIcon`, `CancelText`), or other buttons through `Footer`; see [Dialogs](lists-and-dialogs.md#dialogs) |
| `FormDialogHeader` | `Components/Common/Dialogs/` | The common header of every dialog without a lifecycle: icon, title and a close button |
| `StateEntityDialogHost`, `StateEntityDialogLayout`, `StateEntityDialogHeader`, `StateEntityDialogFooter`, `TransitionButton`, `OpenStateEntityDialogs` | `Components/Common/Dialogs/StateEntity/` | The dialog of a state entity, opened only through `OpenStateEntityDialogAsync` (or a session of several, `OpenStateEntityDialogSessionAsync`); see [Dialogs](lists-and-dialogs.md#dialogs) |
| `StateEntityDialogTabStrip`, `StateEntityDialogViewPanel`, `StateEntityDialogChildList`, `StateEntityDialogSessionNavigator` | `Components/Common/Dialogs/StateEntity/` | A state-entity dialog's view tabs, each view's panel, a child list in a view, and a session's Previous and Next; see [Dialogs](lists-and-dialogs.md#dialogs) |
| `PermissionView` | `Components/Common/` | Permission gating in markup; see [authorization.md](../authorization.md) |
| `MaterialSymbol` | `Components/Common/` | One Material Symbols icon, decorative unless labelled; see [Icons](#icons) |
| `UnitTree` | `Components/OrganizationComponents/` | The one tree of a tenant's organization units; see [Organization tree](user-creation-flow.md#organization-tree) |
| `AttributeValueField` | `Components/EntityAttributes/` | The one input of a custom field value, for every value type; see [Entity attributes](custom-fields-and-settings.md#entity-attributes-dynamic-custom-fields) |
| `StateFieldTextBox`, `StateFieldTextArea`, `StateFieldDropDown`, `StateFieldNumeric`, `StateFieldDatePicker`, `StateFieldTime`, `StateFieldDayAndTime`, `StateFieldSwitch`, `StateAreaFieldset`, `StateCustomFields`, `StatePermissionMatrix`, `StateWorkPatternSegments`, `StateJobTimetable`, `StateSlotQualifications` | `Components/Common/WorkflowFields/` | The state-aware fields of a state-entity dialog, including the permission matrix, a work pattern's working-hours editor, a job's weekly timetable and a slot's required qualifications. Field access comes from the workflow for the persisted state; see [Dialogs](lists-and-dialogs.md#dialogs) |
| `CustomFieldsPanel` | `Components/EntityAttributes/` | The custom fields on the list surface, with the editor's dialog; see [Entity attributes](custom-fields-and-settings.md#entity-attributes-dynamic-custom-fields) |
| `GeneralSettingsPanel` | `Components/Settings/` | The settings shell's General panel: the tenant's time zone; see [Tenant settings](custom-fields-and-settings.md#tenant-settings) |
| `ListView`, `ListColumn` | `Components/Common/List/` | The one list surface; see [List view](lists-and-dialogs.md#list-view) |

### Loading

`LoadingState` shows the app's spinner (`.ui-spinner` in `app.css`, shared with the boot loader, the session gate and the reconnect banner; it keeps turning under reduced motion) centred over one muted line, in at least 16 rem. `Inline` puts a small spinner beside the line. It is a polite status.

- The state-entity dialog's loader is `LoadingState`, and a list's first load is `LoadingState` `Inline`.
- There are no skeleton screens. A list keeps its rows and dims them to 60 % on a later load instead.

### Tab bar

`TabBar` is the one tab list outside a dialog.

- Parameters: `Items` (`TabBarItem`: `Key`, `Label`, an optional `Count` and `Icon`), `Selected` and `SelectedChanged` (the key), `Label` (the list's name), `IdPrefix` (each tab's id is `{IdPrefix}-{Key}`, `TabBar.TabId`), `PanelId` (`aria-controls`), `ItemAttribute` (a test hook carrying each tab's key) and `Class`.
- It follows the ARIA tab pattern with one tab stop: the arrow keys, Home and End move and select (`scripts/tab-bar.js`). The selected tab is underlined in the accent and its count inverted.
- The Priorities dock's kinds use it. A dialog's views and side column keep `StateEntityDialogTabStrip` and `DialogSideTabs`, and the tenant console's tabs stay links, one address each.

### Helpers

Helpers in `Staffora.Blazor/Helper/`:

- `DialogServiceExtensions`: the only way to open a dialog, with or without a lifecycle (`OpenStateEntityDialogAsync`); see [Dialogs](lists-and-dialogs.md#dialogs).
- `EnumDisplayHelper` (static methods): `GetDisplayName<TEnum>(value)` reads `[Display(Name)]` with the enum name as fallback, and `GetDisplayName(Enum)` reads the same for a boxed value, such as a transition's target state. `GetDescription<TEnum>(value)` reads `[Description]`. `GetPermissionArea(RolePermission)` and `GetPermissionsByArea()` group permissions for role editors.

### Feedback

- **No toasts.** A step's outcome is announced in its dialog (to assistive technology), and an error opens `ShowErrorAsync`. Radzen's notifications are not used (`DialogUsageScanTests`).
- **The reconnect banner** (`Layout/ReconnectModal.razor`) is Blazor's reconnect display as a small card in the bottom left corner: a spinner, one line, Retry once reconnecting has failed, and Resume for a paused circuit. It is never an overlay and never inert. The element is a polite status region that is empty only when no state is set. `tests/e2e/feedback.spec.ts` drops the circuit and checks that the banner covers nothing.

## Global search

One dialog finds staff members, jobs, customers and, for the platform owner on the console, tenants, by name ([ADR 0040](../adr/0040-global-search-reads-each-kind-through-its-view-permission.md)). Who finds what is defined in [authorization.md](../authorization.md#global-search-who-finds-what).

### The button and the shortcut

- `GlobalSearchButton` (`Layout/`, in `AppShell`'s top row before the bell and the account trigger) is a 36 px icon button named "Search" (`aria-keyshortcuts="/"`, `data-global-search`). It shows from the circuit's first render to a session that may search at least one kind (`GlobalSearchService.KindsAsync`).
- "/" opens the same dialog from anywhere on the page (`wwwroot/scripts/global-search.js`, one document listener per circuit, removed with the button). It does not fire while the focus is in an input, a text area, a drop-down or editable text, while any dialog is open, or with Ctrl, Alt or Meta held.
- The shortcut moves the focus to the button first, so the focus returns there when the dialog closes.

### The dialog

`GlobalSearchDialog` (`Components/Common/Dialogs/`, title "Search", 640 px) opens through `OpenFormDialogAsync`. A click beside it closes it.

- **The field.** One field follows the ARIA combobox pattern (`role="combobox"`, `aria-expanded`, `aria-controls`, `aria-activedescendant`). It is named and filled in by the kinds the reader searches ("Search staff members, jobs and customers", "Search tenants") and takes the focus as the dialog opens (`data-dialog-autofocus`, which the form dialog host honours before its first focusable element).
- **Typing.** A search waits 200 ms on the injected clock before it asks, and a newer keystroke cancels the older search. It needs two characters and keeps the earlier hits dimmed while the next answer comes.
- **The hits** are a listbox of groups, one per kind in a fixed order: Staff members, Jobs (or "My assignments" for a staff member's own), Customers, Tenants. Each group is named by its label. Each hit is an option with the kind's icon, the name, a short subtitle (an address, the customer and the period, the slot and the customer, a description, "Customer portal of …") and its state.
- **Keys.** The first hit is active as the answer arrives. ArrowDown and ArrowUp move round the hits (the script keeps the caret where it is and scrolls the active hit into view), Enter answers the active one and a click answers the one clicked. Escape closes the dialog without an answer; the dialog handles it itself (`CloseOnEsc` off). The keys are named in the foot, hidden on a phone.
- **States.** A polite status says "3 matches" or "No matches". Before two characters the dialog shows the Common `EmptyState` with what can be found, a search with no hit "Nothing matches “…”.", and a failed one its message as an alert.

### Opening a hit

The button maps the hit (`SearchOpens`) to its dialog token (`user`, `job`, `assignment`, `customer`) and opens it through `DialogRouteRegistry` with the dialog's id-only opener on the current page, so the address carries `?dlg=` and F5 reopens it ([URL state](lists-and-dialogs.md#url-state)). A tenant hit goes to the tenant's console page.

Verified by: `GlobalSearchTests` (tenant isolation, per-kind permissions, a staff member's own assignments only, the platform owner's tenants under the lease, an impersonating owner searching as the user, prefix matches first and the limit applied in SQL), `GlobalSearchDialogTests`, the accessibility sweep (`sweep-tenant.spec.ts`) and `tests/e2e/search.spec.ts`.

## Accessibility

Accessibility is checked in the browser journeys, by an accessibility sweep over every page and dialog, and by scan tests over the markup. A serious or critical axe violation fails the run. No axe rule is switched off; a rule that must be switched off needs its reason in `tests/e2e/lib/accessibility.ts` and in [Staffora.Test/E2E/README.md](../../Staffora.Test/E2E/README.md).

### Journeys

Every page a browser journey opens through `gotoReady` is checked with axe (`@axe-core/playwright`, pinned exactly in `package.json`) once it has settled: no loading indicator showing and the document quiet (`waitForSettled`), so the result does not depend on timing. A serious or critical violation fails the journey (`tests/e2e/lib/accessibility.ts`, proven by `tests/e2e/accessibility.spec.ts`).

### The sweep

The accessibility sweep (`tests/e2e/sweep.spec.ts`, `sweep-tenant.spec.ts` and `sweep-staffing.spec.ts`, ten tests sharing `tests/e2e/lib/sweep.ts`, so the workers share them) opens every routable page as each actor who can open it, and the main dialogs over them.

- It fails on any serious or critical violation not in its allow-list. Every allow-list entry names the known issue that tracks it; the list is empty.
- It checks every page and dialog family at 1500, 1280 and 390 px.
- It fails a page that scrolls sideways, a dialog wider than the window, a control out of reach, a dialog that lets Tab take the focus out, and a closed dialog that leaves the focus anywhere but its opener or the opener's row (`layoutProblems`, `focusStaysInside`, `focusReturn` in `tests/e2e/lib/sweep.ts`; [Staffora.Test/E2E/README.md](../../Staffora.Test/E2E/README.md)).
- `AccessibilitySweepCoverageTests` holds its page lists to the app's routes, and every dialog the app opens to the sweep: each is opened by a sweep `dialog(...)` call naming its component, or listed in `unsweptDialogs` with its reason. Two are listed: the message dialog, which nothing calls yet, and the prompt dialog, whose one caller is a state-entity dialog step's comment prompt that no dialog offers yet. A "no caller yet" exception fails once something calls the dialog.
- Every unswept dialog's structure (named by its title, named buttons, labelled fields) is checked by `ShortDialogAccessibilityTests` until the sweep can open it.
- The error dialog is swept on a role's refused delete (a role somebody holds, refused with the reason).

### Names of controls

- `IconButtonScanTests` requires an `aria-label` with a value on every button or link that shows only an icon: a `RadzenButton` with an `Icon` and no `Text`, or a `<button>`, `<a>` or `NavLink` whose content is only icons. An empty `Text` or `aria-label` counts as none, and `@ChildContent` or an icon fragment is no text. A wrapper whose button shows its caller's content is listed, and every use of it must pass text.
- Icons beside text stay decorative (`MaterialSymbol` without `Label`). The same scan refuses a `RadzenButton` with an `Icon` beside a `Text` or text of its own, whose ligature would join the button's name: such a button is an `IconTextButton` ([Icons](#icons)).
- A Radzen form field names its input when the field's `Component` matches the input's `Name`. A drop-down's hidden input is named by `InputAttributes="@FieldLabel.Of(...)"`, and a check box carries an `aria-label` with `InputAttributes="@FieldLabel.CheckBoxInput"`, which makes its inner input inert (axe's nested-interactive).

### Contrast

- Radzen's status colours are bridged to the status tokens (`app.tokens.css`), so a filled or soft badge keeps 4.5:1. The filled amber of Warning buttons (step buttons such as Deactivate, Suspend, Archive and Cancel, and "End Impersonation") takes the warning token (`--rz-warning`), like green, blue and red; the theme's own orange gives white text only 2.16:1.
- Axe leaves the contrast of every Radzen button unjudged ("needs review", never a violation): the Material theme draws a button's ripple as a gradient on its `::before`, which covers the button, so axe cannot tell the background.
- The check therefore adds its own button contrast (`buttonContrastViolations` in `tests/e2e/lib/accessibility.ts`, reported as `button-contrast`, serious). For every visible, enabled, fully opaque `.rz-button` it compares the text colour against the first opaque background behind it, with translucent layers blended and each colour read in whatever notation the browser computes (including `color(srgb …)` for a `color-mix()` token). It requires at least 4.5:1 for a button with text and 3:1 for an icon-only button.
- An add button inside a form is the primary colour outlined (`ButtonStyle.Primary`, `Variant.Outlined`, as "Add option", "Add time" and "Add hours"). A remove button is the base colour as text (`ButtonStyle.Base`, `Variant.Text`, as a choice option's, a time's, a pattern's hours' and a required qualification's). Never use the light style as text on a light background.

## Enum annotations

| Attribute | Purpose | Used on |
|---|---|---|
| `[Display(Name, GroupName)]` | Display label and grouping | `RolePermission`, state enums |
| `[Description]` | Help text | `RolePermission`, state enums |
| `[PermissionArea]` | Groups a permission into an area | `RolePermission` |

State enums also carry workflow annotations from `Staffora.Core/Annotations/` (`[Icon]`, `[StateStyling]`, `[EntityStateCategory]`, `[ButtonStyle]`, `[EntryState]`, `[DeleteState]`), read through `CoreExtensions`. The [workflow-state-entities skill](../../.claude/skills/workflow-state-entities/SKILL.md) describes them. `EnumDisplayHelper` reads the display annotations ([Helpers](#helpers)).
