# End-to-end platform tests

This folder tests the browser-test platform itself: the database guard, the reset, the personas, the foundation state, the startup-seed gate and `/health`. The journeys live in `tests/e2e/` ([ADR 0018](../../docs/adr/0018-test-strategy.md)). Every class here carries `[Trait("Category", "E2E")]`, so the fast filter leaves them out (`TestCategoryTests`).

## Running

```powershell
$env:STAFFORA_E2E_CONNECTION = 'Server=(localdb)\mssqllocaldb;Database=Staffora.E2E;Trusted_Connection=True;MultipleActiveResultSets=true'
dotnet test --project Staffora.Test --filter-trait "Category=E2E"   # the platform tests
npm ci; npx playwright install chromium                           # once
npm run test:e2e:smoke                                             # every change: the @smoke set
npm run test:e2e:affected -- <changed paths>                       # every change: the areas those paths reach (or, without paths, every file changed since the last passing full run)
npm run test:e2e:area -- staffing                                  # one area (or several: staffing,portal)
npm run test:e2e                                                   # at merge: everything, the accessibility sweeps and the visual baselines included (also needs the integration pool, below)
npm run test:e2e -- --update-snapshots                             # regenerate the showcase baselines, then review them
npm run test:e2e:slowest                                           # the 15 slowest tests of the last run
```

### Tiers

Every spec carries at least one area tag (`E2ESpecTagScanTests` holds it):

| Tag | Covers |
|---|---|
| `@shell` | The shell, menus, chrome, bell, search, banner, words |
| `@dialogs` | The state-entity dialog and its host |
| `@staffing` | Jobs, slots, staff, customers, schedules, absence |
| `@coordination` | The coordination workspace |
| `@portal` | The customer portal |
| `@platform` | Console, tenants, onboarding |
| `@homes` | The homes |
| `@admin` | Users, roles, units, settings, custom fields |
| `@lists` | The list surface and URL state |
| `@a11y` | The accessibility checks and sweeps |
| `@visual` | The reviewed baselines and the busy-week captures |

About ten journeys also carry `@smoke`: health and the platform owner's sign-in, the platform overview, an impersonation started and ended, a job created and staffed, a staff member answering a request, an order sent from the portal, the coordination workspace, a user dialog's steps, the shell's page frame and a staff member's home. Specs that need the dialog host also carry `@dialog-host`.

The tiers (`scripts/e2e-run.mjs`):

| Tier | Command | When |
|---|---|---|
| Smoke | `npm run test:e2e:smoke` | Every change |
| Affected | `npm run test:e2e:affected -- <paths>` | Every change: `@smoke` plus the areas the changed paths reach |
| Area | `npm run test:e2e:area -- <tag>[,<tag>]` | Working in one area |
| Full | `npm run test:e2e` | At merge, and for a change's Definition of done |

`scripts/affected-specs.mjs` maps each changed path to tags through a short table by folder (its `pathMap`; a new folder of the app adds a line there):

- A page, dialog or service maps to its area.
- The shell's layouts and stylesheets map to `@shell`, `@homes` and `@visual`.
- A spec maps to itself, a helper under `tests/e2e/lib/` to the specs that import it, and a journey contract to the specs that name it.
- The domain and the data model reach every functional area.
- The test platform itself (the config, the server scripts, `Staffora.E2ESetup`, the setup project, the helpers every spec uses) reaches the full run.
- A path the table does not know is listed and leaves `@smoke` alone.

Without paths it takes every file modified since the last passing full run, whose start `npm run test:e2e` records in `.e2e/last-full-run.json`. The selector compares each file's modification time with that start time, so it works without version control and does not use Playwright's `--only-changed`. The script prints the `--grep` it chose; `--run` (the npm script) runs it. Extra Playwright arguments go after a second `--`.

The tiers below the full run:

- leave out the review screenshots under `output/playwright/` (`STAFFORA_E2E_SHOTS=0`: every check before a shot still runs, only the image is not written); `--shots` writes them, as a visual change's Definition of done requires;
- start without the browser host of the state-entity dialogs unless the selection can reach a spec tagged `@dialog-host` (`STAFFORA_E2E_DIALOG_HOST=off`).

The accessibility sweeps (`@a11y`) and the showcase baselines (`@visual`) run in the full run, or by their tag.

### Speed

- Every run lists its slowest files (`reportSlowTests`, over 15 s) and writes its results to `.e2e/playwright-report/results.json`. `npm run test:e2e:slowest` prints the slowest tests, the wall time and how far the workers ran side by side. A single test over a minute is a defect to fix: split it, or give it its data from the preparation instead of the browser.
- The long journeys name their set-up (`test.step('set-up: …')` around the job they make), so a trace and the report show what the set-up costs beside the journey itself.
- A journey that only needs a started job makes it with the one helper `startedJob` (`tests/e2e/lib/jobs.ts`: a weekly time, its slots, started) for the customer the preparation made, and cleans up with `cancelJob`. Registering a job through its dialog step by step is the subject of `jobs.spec.ts` and `job-staffed.spec.ts` alone.
- A journey runs at one width (1280 px) unless the layout is its subject. Where a phone's picture of a state is wanted, the journey narrows the window at that state and shoots it, rather than replaying the journey at 390 px.
- Recording costs every test. A trace is kept only of a failed test's retry (`trace: on-first-retry`), no video is recorded (a spec that wants one says `test.use({ video: 'on' })`), and a screenshot is kept of every failure.
- The full run retries a failure once (`STAFFORA_E2E_RETRIES=1`), so a failure comes with a trace and a test that passes the second time is reported as flaky. The tiers run on every change retry nothing and stop after three failures (`STAFFORA_E2E_MAX_FAILURES`, 0 for no limit).
- The apps run an optimized build (Release) in the Development environment. `gotoReady` waits for the document's parsing and then the session gate's ready landmark, not for every resource's load.
- The specs wait on conditions, never on the clock: Radzen's dialog timers are counted on the page (`tests/e2e/lib/frames.ts`), a rendered change waits two frames, and the tab keys' scroll check runs with smooth scrolling off. One timed wait remains, in `dialog-restore.spec.ts`: proving that Back reopens nothing needs a signal from the restore host that it has finished, which the app does not give.

### Workers and slots

The journeys run on four workers by default (`STAFFORA_E2E_WORKERS`, 1 to 8; `--workers N` through the npm scripts sets both). Each worker costs a browser (about 1 to 1.5 GB and a core) and an app with its database, so four is the default and more is a choice for a quiet machine.

Each worker runs against a slot of its own (`scripts/e2e-slots.mjs`): an app, a database and a state file, so no worker sees another's rows, sign-ins or sign-in rate limit. `fullyParallel` spreads the tests, a file's too, over the workers by their parallel index; a restarted worker keeps its slot.

| Slot | App | Database | State file |
|---|---|---|---|
| 0 | `STAFFORA_E2E_BASE_URL` (default `http://localhost:5280`) | the one `STAFFORA_E2E_CONNECTION` names | `.e2e/foundation-state.json` |
| n | the base port + 2n (5282, 5284, …) | `<name>.w<n>` (`Staffora.E2E.w1`, …) | `.e2e/foundation-state.w<n>.json` |

- **Dialog host.** The port after the base (5281 by default, `STAFFORA_E2E_DIALOG_HOST_URL`) is the browser host of the state-entity dialogs, which every slot shares. Its specs hold the lock `dialog-host`, so they run one at a time.
- **Names.** Every slot's database keeps the base name and so its "e2e", and the preparation checks each name again before it connects.
- **The server script.** `scripts/e2e-server.mjs` builds once (`STAFFORA_E2E_CONFIGURATION`, Release by default: an optimized build, clear of the Debug build an open IDE holds). The apps still run in the Development environment, which the startup-seed gate and the plain-HTTP cookie need, and without hot reload, which only `dotnet watch` starts. It prepares the slots side by side (`STAFFORA_E2E_STATE_FILE` gives each preparation its state file), starts each slot's app over its database once its preparation succeeded and the dialog host beside them, then writes `.e2e/slots.json` and prints the ready line Playwright waits for.
- **Specs and slots.** The specs take their slot's address as `baseURL` from `tests/e2e/lib/slot.ts` (through `page-health`'s `test`) and their personas from their slot's state file. The setup project checks every slot.
- **Sign-in reuse.** A persona signs in through the login page once per worker. The auth cookie that sign-in set is kept in the worker's memory and given to the next browser context that signs the persona in (`loginAs`); a cookie the app no longer admits sends the next sign-in through the login page again. Specs about signing in pass `{ throughForm: true }` or start on the login page.

### Databases

`npm run test:e2e` also needs the integration pool's SQL Server (LocalDB by default, or the server `STAFFORA_INTEGRATION_CONNECTION` names; [Integration README](../Integration/README.md)): the dialog host leases a database of its own from it.

- Each slot's database is created by its first preparation when it does not exist.
- Its name must contain "e2e" and none of "prod", "stage" or "staging". The connection string may not attach a database file (`AttachDbFilename`) or use a user instance, since the attached file could be any database (`E2EDatabaseGuard`).
- Every run empties the database, so never point it at a database you keep.
- The tests that use the database or start the app skip when `STAFFORA_E2E_CONNECTION` is not set on a developer's machine, and fail when the `CI` variable is set (`E2EDatabase.MustFailWhenUnset`), so a CI job without the database cannot pass by skipping. The guard and state-file tests run without it, since they reach no database.
- Classes that use the database share it, so they join `E2EDatabaseCollection` and run one at a time.

## How a run is prepared

`Staffora.E2ESetup` (`dotnet run --project Staffora.E2ESetup`) prepares the database `STAFFORA_E2E_CONNECTION` names, in this order (`FoundationPrep`):

1. Remove `.e2e/foundation-state.json` and any temporary file a killed write left.
2. Refuse a database whose name does not mark it as an end-to-end database, before any connection.
3. Migrate, then empty every table except `__EFMigrationsHistory` with Respawn (`DatabaseReset`, which checks the name again itself).
4. Seed through the application's services (`PrepServices` wires them as `Program.cs` does), as described under "What the preparation seeds" below.
5. Verify each persona (`PersonaVerifier`): the password signs in through the app's sign-in manager, the cookie principal resolves through `UserSessionService` as a new circuit resolves it, and `PermissionService` grants the contract's permissions. A persona that falls short fails the run with its name and what it lacks; no role is adjusted to make it pass.
6. Write `.e2e/foundation-state.json` (camelCase) atomically: to a temporary file beside it, then moved over the name. Any failure returns a failed result and leaves no state file.

The state file holds:

- the personas;
- the impersonation targets (`impersonationTarget`, `tenantAdministrator` and `tenantViewer`: tenant, name, email). Sam Taylor holds no role, Alex Morgan the System owner role (every permission outside own work), and Robin Lee a role that may view users, units and settings and change nothing, so the list journey sees a row's dialog read-only and the settings journey the shell read-only;
- the error-log rows (`errorLog`: the search word and each row's key, level, message and age in days), which the smoke spec reads its messages from;
- what the staffing journey starts from (`staffing`: the customer's and the qualification's names).

### What the preparation seeds

- **Platform.** The seeder's production data, the platform tenant and its owner through the platform bootstrap (a fresh random password every run), and every tenant's system account. The platform owner is the one person, and its persona the one persona, the preparation creates itself.
- **The journeys' tenant** (`JourneyTenantFixture`, built by `CompanyASetup` in `Staffora.Services/Demo/`). The platform owner, its cookie principal resolved by `UserSessionService` as a circuit resolves it, onboards Company A through `TenantOnboardingService` as the platform console does: in Setup, with its role presets and its first system owner, Alex Morgan. It adds the global user field Employee number through `CustomFieldService`.
- **Administration data.** Alex, signed in as the system owner a tenant in Setup admits, adds Sam Taylor (no role) and Robin Lee through the user workflow's create step; the Viewer role (`JourneyTenantFixture.ViewerPermissions`: users, units and settings to look at, nothing to change) through the role workflow's create step, given to Robin on the Company root; the head office below the root through the unit workflow; and the tenant's own fields of users (Team, a choice with the options Working, Planning and the retired Support; Badge number, a text; Has keys, a yes or no) and of units (Opened, a date; Rooms, a whole number; Floor area, a number).
- **Discarded passwords.** No custom field value is seeded. The passwords onboarding and the create step issue are discarded, since nobody signs in as these users (the journeys impersonate them). The platform owner then activates the tenant.
- **Staffing personas.** The system owner creates Casey Lind with the Coordinator preset on the Company root, and Kim Berg and Jamie Fors with the Staff preset on the head office, which makes them staff members of it. The system owner adds the qualification Mathematics and gives it to both staff members; the coordinator, once the tenant is Active, adds the active customer Brightpath Learning. The staffing journey so starts from two qualified staff members and a customer.
- **Portal personas.** The system owner enables Brightpath Learning's portal through the customer dialog's step (`CustomerService.EnableCustomerPortalAsync`), its jobs going to the whole company. Its first customer administrator, Morgan Reed, is the customer-admin persona, a person of the portal's own tenant, with the initial password the step issued this run. That administrator, signed in to the portal, creates the orderer Riley Quinn through the user workflow's "Create orderer" (`UserService.CreateUserAsync` with `ApplicationUserWorkflowAttribute.PortalCreateOrdererStep`), holding the Orderer preset.
- **Passwords.** Each persona gets a fresh random password of the run (`UserService.CreateUserAsync`, `UserPasswords.Generate`), or the one the portal's step issued. Nothing with credentials written in the source is seeded.
- **The busy week** (`CoordinationShowcaseFixture`, built by `CoordinationShowcase`). Through the services, as the workspace's dialogs make it: the system owner adds six staff members on the Company root (the Staff preset; the tree is left as it is). The coordinator publishes two work patterns and gives one to each, adds the active customers Lakeside College and Hillview School, registers and starts eight jobs with weekly times (five whose period began this week's Monday, so the workspace lists them as behind, three from tomorrow), assigns most slots, asks one staff member, leaves three slots uncovered and registers two absences. The personas, the head office and their staff members are left as they were.
- **Error-log rows** (`ErrorLogFixture`, platform rows with a date in the past): a Critical and two Errors of today and an Error 20 days old, three sharing the search word "timetable", so the level, the period (7 days by default, 30 chosen) and the search each have a row to show and a row to hide. They are inserted as the error-log writer inserts its rows, on a system context under a logged system-scope lease, since the logging path cannot date a row back.
- **Time zones.** Last, every tenant's time zone is pinned to the platform's (`TenantSettingsEnsureService`, as app startup pins it; [ADR 0020](../../docs/adr/0020-dates-and-times.md)), stamped with each tenant's system account; the platform tenant is left without one. The zone comes from the app's own configuration (`PrepServices.AppConfiguration`: `Staffora.Blazor/appsettings.json`, `appsettings.Development.json`, then the environment), so Company A's zone is `Europe/Stockholm` and the app's startup finds nothing to add.

So the sweep reaches every administration page and dialog with data on it as Alex, the tree has a level to open and close, and the user and unit dialogs show a field of every type. Every row a person would write carries the history of the step that wrote it (`FoundationPrepTests`). Every other tenant and person the journeys meet is created through the services people use, and the tenant-onboarding journey creates a tenant's first system owner itself, in the browser.

The development demo builds its own staffing companies (`DemoDataSeeder`; [Demo data](../../docs/features/demo-data.md#demo-data) lists their sign-ins), so no persona of the journeys is a demo person. `JourneyTenantFixture` and `CoordinationShowcaseFixture` run the shared setup with the bootstrapped platform owner and no chosen password, and turn what they built into the state file's personas and targets. People sign in to the shared setup through `DemoSessions`, which sets the cookie principal as the scope's authentication state (`PersonaCookie` takes it from its host, as the app's circuit state does) and resolves the session through `UserSessionService`.

The preparation writes passwords only to the state file, which git ignores; `FoundationPersona.ToString()` leaves them out of every message it writes. How the browser run keeps them out of its report is under "Passwords in the evidence" below.

### Adding a persona

A change that introduces an actor:

1. adds a `PersonaContract` to `Personas.All` (key, actor, email, platform reach, required permissions);
2. seeds it through the services that create such a user (`FoundationPrep.SeedAsync` for a platform persona, `CompanyASetup` through `JourneyTenantFixture` for a tenant's, which hands its password back in `JourneyTenantUsers.Personas`);
3. adds its key to `tests/e2e/lib/foundation-state.ts` and to the setup project's check.

The personas:

| Key | Person | Holds |
|---|---|---|
| `platformOwner` | The platform owner | Every permission and platform reach |
| `coordinator` | Casey Lind | Exactly the Coordinator preset's permissions |
| `staff` | Kim Berg | Exactly the Staff preset's own-work permissions, a staff member of the head office |
| `secondStaff` | Jamie Fors | The same as Kim |
| customer administrator | Morgan Reed | Exactly the customer administrator preset, in the portal's tenant |
| orderer | Riley Quinn | Exactly the Orderer preset, in the portal's tenant |

After sign-in each lands on its own home (`tests/e2e/home.spec.ts`): the platform owner on the console, the coordinator on the coordination workspace, each staff member on "My work", the customer administrator on the portal's home. `impersonate` lands on the impersonated user's home (the system owner's setup checklist on the administration overview).

## How the journeys run

`npm run test:e2e` runs `playwright.e2e.config.ts`: a worker per slot (above), a screenshot of every failed test, a trace of a failed test's retry, and reports under `.e2e/` (the JSON results beside the HTML report, which `npm run test:e2e:slowest` reads).

Its web server, `scripts/e2e-server.mjs`, runs each slot's preparation and, only when it succeeded, starts that slot's app with `dotnet run --no-launch-profile --no-build` in Development on the slot's address over the same database, with:

- `STAFFORA_E2E_SKIP_STARTUP_SEED=1`, so startup adds nothing to what the preparation recorded (`StartupSeeding`; the setting counts only in Development, and any other environment logs it and seeds as usual);
- `JobClock__Enabled=false`, so no job a journey made moves to in progress or completed by itself as the real date passes its period's first or last day (the dialog host is an `AppHost`, which leaves the clock off too);
- `IntegrationOutbox__Interval=00:00:02`, so the integration outbox delivers a portal's messages within seconds;
- `RateLimits__SignIn__PermitLimit=1000`. The specs sign in one after another from one address, many times a minute; at the app's own limit (10 sign-ins a minute per address) whether a sign-in is refused would depend on how fast the run goes. This weakens nothing the journeys check: the limiter is the same code path in every run, and `SignInHardeningTests` proves it refuses past its limit and answers 429 with its status page, against the real app.

The run starts once every slot's `/health` answers healthy: anonymous, `Healthy` (200) while the database accepts connections and `Unhealthy` (503) while it does not, with no other detail (`DatabaseHealthCheck`).

The `setup` project (`tests/e2e/foundation.setup.ts`) checks that there is a slot for every worker and that each slot's state file comes from this run: prepared for the slot's database (derived from the one `STAFFORA_E2E_CONNECTION` names), within the last hour, with the personas complete. Every `chromium` spec depends on it.

### Helpers

`tests/e2e/lib/`:

- `foundation-state` reads the state file fail closed.
- `session` signs in through the login page (`loginAs`, which signs in on the login page it is already on rather than loading it again) and starts an impersonation from the platform console's dialog the way a platform owner does (`impersonate`). After every navigation it waits for the session gate's ready landmark `[data-session="ready"]` and checks that Blazor's error banner `#blazor-error-ui` is hidden. After every `gotoReady` it waits for the page to settle (`waitForSettled`: no loading indicator showing, the web fonts loaded, the document unchanged for 300 ms, at most ten seconds) and checks that axe finds no serious or critical violation (`accessibility`, below).
- `page-health` gives specs a `test` whose page fails the test on an uncaught script error, a console error, a same-origin response of 400 or more, or the error banner left showing. A content security policy violation is a console error, so every spec checks the policy ([Security headers](../../docs/authentication.md#sign-in-hardening-and-security-headers)) on the pages it opens. Expected failures go in its allow-list, each with a reason; the one entry is the unknown address `navigation.spec.ts` opens on purpose, and the browser's console error for an allowed failure is allowed with it. A spec that expects console errors names them (`expectedConsoleErrors`).
- `journey-contract` loads `journeys/<id>.journey.json` and names each business step (`journeyStep`).
- `shots` saves screenshots to `output/playwright/<area>/`, by journey id.
- `report-secret-guard` checks the report (below).
- `jobs` (`startedJob`, `cancelJob`), `workspace` (shared locators of the coordination timeline), `frames`, `circuit` (puts the circuit's connection under a spec's control), `dialog-host`, `platform-console`, `organization-tree`, `sweep` and `slot` serve the specs named below.

### The dialog host

`dialog-restore.spec.ts`, `dialog-escape.spec.ts` and `comments.spec.ts` (tag `@dialog-host`) run against a browser host of real nested state-entity dialogs over test-only entities, which the web server starts beside the slots once it has built Staffora.Test (`scripts/e2e-dialog-host.mjs`, which runs `dotnet run --project Staffora.Test --no-build -- dialog-host`, and builds first when run alone; `Staffora.Test/E2E/DialogHost/DialogHostServer.cs`).

- It is Staffora.Blazor's own `Program` over Kestrel on `http://localhost:5281` (`STAFFORA_E2E_DIALOG_HOST_URL`), in Development with startup seeding off, every data context a `TestDataContext`, with the test-only `TestParentDialog` and `TestChildDialog`, their services and routes.
- It runs on a database of its own leased from the integration pool (restored from the template with the test tables, a name with "test" in it).
- It listens on the loopback address only (`AppHost.StartOnKestrel` refuses any other), and its test routes answer this machine only.
- Playwright ends its web servers by killing them, so the run's global teardown (`tests/e2e/global-teardown.ts`) first posts `/test/dialog-host/stop`: the host stops the app, drops its database and then deletes its state file, which the teardown waits for. A host killed anyway leaves its database to the next start of any pool, whose template preparation drops what an earlier run left.
- It writes three tenants (Company A with a parent and two children, Company B with a parent of its own, and a platform tenant whose owner has a parent too) and `.e2e/dialog-host-state.json` with their ids. The specs sign a user in through the host's test route `/test/sign-in/{userId}` and share their helpers in `tests/e2e/lib/dialog-host.ts`.
- Nothing of the host is in Staffora.Blazor, which registers only its own dialog routes (`DialogRouteRegistryTests`).

### The specs

What each feature does is described in the feature documents ([docs/features.md](../../docs/features.md)); the specs prove it in a browser. Screenshots go under `output/playwright/<folder>/`.

| Spec | Tags | Covers | Screenshots |
|---|---|---|---|
| `smoke.spec.ts` | `@platform @smoke` | `/health` and the platform owner's sign-in (journey `platform-owner-sign-in`); on the same sign-in, the error log's URL state (filters written to and read from the address, kept after F5, links that change the filters) and the tenant dialog's `?dlg=` segment | `url-state/` |
| `navigation.spec.ts` | `@shell` | The left sidebar shell ([ADR 0029](../../docs/adr/0029-left-sidebar-shell.md)): named groups, current link, breadcrumb, the folded rail kept across links and reloads, the skip link, the account panel, the phone drawer, status pages inside each layout, the impersonated menu; a coordinator's sidebar and the workspace without a page scroll | `navigation/`, `shell/` |
| `page-chrome.spec.ts` | `@shell @smoke` | The page frame: the top row as the page's header, the breadcrumb ending in the page's one h1, the "?" description popover by mouse and keyboard, the not-found and access-denied cards | `chrome/` |
| `rename.spec.ts` | `@portal @shell` | The product's words on the coordinator's and the customer administrator's pages, none of teacher, course, lesson and term | `rename/` |
| `sweep.spec.ts`, `sweep-tenant.spec.ts`, `sweep-staffing.spec.ts` | `@a11y` | The accessibility sweep (below) | `dialogs/`, `roles/`, `organization/`, `platform-console/` |
| `accessibility.spec.ts` | `@a11y` | The accessibility check and the sweep's own checks, on pages the spec writes itself (no request reaches the app) | none |
| `sign-in.spec.ts` | `@a11y @shell` | The sign-in page | |
| `custom-fields.spec.ts` | `@admin @platform` | Journey `platform-owner-sets-up-custom-fields`: the platform owner adds a choice field for Company A's users; the tenant administrator sees it read-only beside the global field, and a value is saved and kept after F5 | `custom-fields/` |
| `settings.spec.ts` | `@admin` | Journey `tenant-administrator-changes-time-zone`: the settings shell's General panel, the time zone picker by keyboard and mouse, saving and restoring the platform's zone, the read-only viewer. Leaves the company on the platform's zone, as prepared | `settings/` |
| `lists.spec.ts` | `@admin @lists` | Journey `tenant-administrator-finds-in-lists`: search, filters and the open row kept in the address and after F5, a row's dialog and the focus back on the row, the phone's folded toolbar, the organization page's list view, a read-only dialog | `lists/` |
| `url-state.spec.ts` | `@lists` | The URL state module (`url-state.js`) driven on `/health`: writes set and remove only their keys, add no history entry, and are dropped once the address has left their path; percent-escaped paths | none |
| `state-entity-dialog.spec.ts` | `@dialogs` | The dialog header on rendered markup (`tests/e2e/fixtures/state-entity-dialog/`, which `StateEntityDialogTests` proves current and rewrites with `STAFFORA_UPDATE_E2E_FIXTURES=1`): the state pill's card, Esc, focus return, the frame at 1280 and 390 px | `state-entity/` |
| `state-entity-views.spec.ts` | `@dialogs` | The dialog's views on the same markup (`views-*.html`): the tablist's keys, the Children view, stacked dialogs, "Discard changes?", a session's navigator and its focus hand-off | `state-entity/` |
| `state-entity-fields.spec.ts` | `@dialogs` | State-aware fields of the state-entity dialog | |
| `dialog-restore.spec.ts` | `@dialogs @dialog-host` | Dialog restore through the address: a pasted link and F5 reopen the chain, Back reopens nothing, edits ask before leaving, another tenant's dialog opens nothing | `state-entity/restored-chain-*.png` |
| `dialog-escape.spec.ts` | `@dialogs @dialog-host` | Esc on stacked dialogs, opened by hand and on a restored chain. Radzen writes the option its Esc handler reads 500 ms after each open, on whichever dialog is last then; `state-entity-dialog.js` gives each state-entity dialog its own as its heading is named | |
| `comments.spec.ts` | `@dialogs @dialog-host` | Comment threads in a state-entity dialog | |
| `tenant-dialog.spec.ts` | `@dialogs @platform` | Tenant onboarding from the keyboard, the initial password shown once (painted over), every tenant state from the footer, the history card, focus return, the leave question | `tenant/` |
| `tenant-onboarding.spec.ts` | `@platform` | Journey `tenant-onboarding`: a new tenant with its first system owner, who signs in and adds a coordinator; the platform owner activates the tenant. Runs again on the same database (`--repeat-each`) | `tenant-onboarding/` |
| `tenant-lifecycle.spec.ts` | `@platform` | A tenant's lifecycle steps | |
| `platform-console.spec.ts` | `@platform @smoke` | The console overview, the tenant list and a tenant's console tabs (read-only), refusal while impersonating, a managed customer tenant and its Dead letters tab | `platform-console/` |
| `platform-overview.spec.ts` | `@platform @smoke` | The platform overview and an impersonation started and ended | |
| `users.spec.ts` | `@admin @dialogs @smoke` | A user dialog's steps | |
| `lifecycles.spec.ts` | `@admin` | Unit and role lifecycles: greyed steps with their reasons, deactivate, archive, restore, delete; removes what it made | `organization/`, `roles/` |
| `staff.spec.ts` | `@admin @staffing` | The Staff page, the user dialog's Staff view, the role editor's resource switch, a new staff member | `staff/`, `roles/` |
| `notifications.spec.ts` | `@shell` | The bell with two browsers of one app: a live badge, the panel as a drawer by keyboard, no Mark all read while impersonated | `notifications/` |
| `search.spec.ts` | `@shell` | Global search by "/" and the search button, keyboard choice, a hit's dialog kept by F5, tenants on the console | `search/` |
| `feedback.spec.ts` | `@shell` | The reconnect banner, with the circuit's connection cut and let through (`lib/circuit.ts`) | `feedback/` |
| `showcase.spec.ts` | `@visual` | The key pages at 1280 and 390 px, compared with the reviewed baselines (below) | `showcase/` |
| `home.spec.ts` | `@homes @smoke` | Each persona's home after sign-in and after impersonation, with axe, button contrast and no sideways scroll | `home/` |
| `staff-pages.spec.ts` | `@homes @smoke @staffing` | A staff member's own pages and answering a request | |
| `work-pattern-staff.spec.ts` | `@homes @staffing` | Staff without a work pattern | |
| `job-staffed.spec.ts` | `@staffing` | Journey `job-staffed`: a job with job fields and two slots, requests, an accept and a decline with a reason, an assignment, confirmation, what each staff member reads. Records a video | `job-staffed/` |
| `jobs.spec.ts` | `@staffing` | Registering a job through its dialog | |
| `single-job.spec.ts` | `@coordination @smoke @staffing` | A job created and staffed | |
| `job-place.spec.ts` | `@staffing` | A job's place | |
| `assignments.spec.ts`, `staffing.spec.ts`, `request-round.spec.ts`, `respond-by.spec.ts`, `conversations.spec.ts` | `@staffing` | Assignments, the staffing dialog, request rounds, respond-by deadlines and staff conversations | |
| `customers.spec.ts`, `qualifications.spec.ts`, `work-patterns.spec.ts`, `schedules.spec.ts`, `absence.spec.ts` | `@staffing` | Customers, qualifications, work patterns, schedules and absence | |
| `closed-days.spec.ts`, `public-holidays.spec.ts` | `@admin @staffing` | Closed days and public holidays blocking staffing | |
| `absence-impact.spec.ts` | `@coordination` | Absence impact on the coordination workspace | |
| `portal.spec.ts` | `@portal @smoke` | Enabling a customer's portal, the customer administrator's home and refusals, an order drafted and sent, the coordinator's notice, creating an orderer, the order's messages | `portal/` |
| `portal-round-trip.spec.ts` | `@portal` | Journey `portal-round-trip`: an order sent, staffed and confirmed through the outbox, then a second order canceled from the portal. Cancels every job it made, also after a failure (passes with `--repeat-each=2`) | `portal-round-trip/` |
| `coordination.spec.ts`, `coordination-timeline.spec.ts` | `@coordination` (`coordination.spec.ts` also `@smoke`) | The workspace frame, the calendar rail, the Priorities dock, the hour timeline's staffing, absence and Find staff, live updates from a second coordinator; shared locators in `lib/workspace.ts` | `coordination/` |
| `coordination-showcase.spec.ts` | `@coordination @visual` | The busy week at 1440, 1280 and 390 px (`showcase-<state>-<width>.png`) | `coordination/` |

Journeys that make jobs cancel them on their way out, also after a failure, so the staff members are free for the next run. A job's time is the first weekday at least two days from now and the job clock is off, so no journey depends on the day or hour it runs at. Each run adds rows and notices with names of its own, so specs never pick another's. The header's lack of a view choice and the dock's lack of a focus card, the List mode address showing the timeline, the rail day and the Open slots link writing the day into the address, and a staff member's name opening their schedule are held by `CoordinationPageTests` (bUnit).

### The accessibility sweep

The sweep is one sweep in three files of three or four tests each that share its lists and checks (`tests/e2e/lib/sweep.ts`), so the workers share it and none runs near its time limit. Each test is a part of its own (`SweepPart`), signs in itself and fails on its own allow-list entries that matched nothing.

- **Coverage.** Every routable page as each actor who can open it (a visitor, the platform owner, the tenant administrator impersonated, the customer administrator for `portalPages`, the staff member for `staffPages`) and the main dialogs over them, each checked once settled. `AccessibilitySweepCoverageTests` holds the page lists to the app's routes and the dialog list to the dialogs the app opens, and fails a "no caller yet" exception once something calls the dialog.
- **Allow-list.** It fails on a serious or critical violation its allow-list does not name, and on an allow-list entry that matched nothing. The allow-list is empty; an entry would name its known issue.
- **Skipped.** Routes it cannot open are listed with their reason (`skippedRoutes`: the tenant console's pages, which need a tenant's id; the console is swept through `tests/e2e/lib/platform-console.ts` instead). Dialogs it does not open are listed with their reason (`unsweptDialogs`); their structure is checked by `ShortDialogAccessibilityTests`.
- **Dialogs.** Each `dialog(...)` call names the dialog's component, and a dialog opened through the extensions is checked to be that component. Besides the main dialogs it opens a confirmation, a refused delete's error dialog and the unit's "Add user" dialog.
- **Widths.** Every page and dialog family is held to three widths (`sweepViewports`: 1500 × 900, 1280 × 800, 390 × 844): a page visited at 1280 px is checked again at 1500 and 390 px, and so is the first dialog of each component, with the dialog open. The tenant console's tabs are swept at all three.
- **Layout.** Every check, at any width, adds the layout and the button contrast to axe (`layoutProblems`). It fails a page whose document or signed-in body scrolls sideways, an open dialog reaching past the window, and a control a person cannot reach: a link, button, field or other tab stop that shows but lies wholly outside the window's width, or wholly outside a box that clips it without scrolling, following the boxes as CSS clips. With a dialog open only the topmost dialog's controls count.
- **Focus.** The first dialog of each component has its focus order checked (Tab, pressed once more than it has tab stops, keeps the focus inside it), and every dialog its focus return: once it has closed, the focus is on what a person last pressed to open it, inside it, or on another control of its row, never on the page's body or on something that does not show.
- **Organization tree.** The keyboard moves the one tab stop (Down, Up, Home, End), opens and closes rows, moves between parent and child (Right, Left) and selects (Enter, Space) without scrolling the page (`tests/e2e/lib/organization-tree.ts`).

### Comparison harness

`scripts/compare-screens.mjs` puts two screenshots side by side in one PNG, ours on the left and the target design on the right, each under a dark caption bar with its label and its width, so the remaining differences of a UI change can be read off one image. It draws the image with the Chromium Playwright already installs (no new dependency), and it takes every path from the command line:

```powershell
node scripts/compare-screens.mjs output/playwright/lists/01-users-searched-detail-open-1280.png <target.png> output/playwright/lists/compare/users-1280.png
node scripts/compare-screens.mjs <ours.png> <target.png> <out.png> --left-label "Staffora.Blazor" --right-label "Target design"
```

- The output is `16 + left + 16 + right + 16` pixels wide and `16 + 36 + the taller image + 16` high (a 16 px gutter and a 36 px caption bar).
- A file that is not a PNG is refused with exit code 1 and nothing is written.
- Compare images go under `output/playwright/<area>/compare/`, named for our width and, where the target design has another, its width too (`users-1280-vs-1440.png`).
- `CompareScreensTests` runs the harness on two images it draws itself and checks the output's size and the refusal.

The list pattern and the dialog look are compared from the journeys' captures: `lists.spec.ts` saves the users and units lists at 1500, 1280 and 390 px, a chip's listbox open (`07-units-chip-open-*`) and the user dialog (`dialogs/user-dialog-*`); `job-staffed.spec.ts` saves the staff member's request and the decline prompt at 1500 px (`06b-request-for-the-staff-1500`, `07b-decline-prompt-1500`); `assignments.spec.ts` a request with a question (`13-question-request-for-the-staff-1500`); and `jobs.spec.ts` a blocked step's card (`08-customer-delete-refused-1500` and `-390`). A list's chips and tools are found by their names ("Type: All", "Sort: Name"), their listboxes by the chip's label.

### Showcase baselines

The showcase's reviewed screenshots are committed under `tests/e2e/baselines/`, named `<page>-<width>-<platform>.png` (`login-1280-win32.png`), since fonts render differently per operating system (`snapshotPathTemplate` in `playwright.e2e.config.ts`).

- A run compares each page with its baseline (`toHaveScreenshot`, the whole page, animations off, at most 200 pixels different, so a changed word fails it). The comparison is soft, so every page is compared and the report shows each difference beside its baseline.
- The signed-in layouts fill the viewport and scroll only their body (`.app-shell__body`), so the spec first lets the layout grow to its content (a style added for the capture, nothing else changed); each screenshot holds the whole page. Before each capture it fails a page that scrolls sideways.
- Content that changes between runs is masked (`capture`'s `mask`). On the platform console the tenant counts, the sentence that sums them, and the recent errors and impersonations are masked, and their list (up to five) or empty state is first given one fixed box (`steadyOverview`), so the page keeps one height whichever specs ran before it.
- A run never rewrites a baseline. When a change is meant to alter the look, `npm run test:e2e -- --update-snapshots` writes new baselines; look at every image before committing it, as a reviewed baseline is what later runs hold the app to.
- A missing baseline fails the run without being written (`updateSnapshots: 'none'`), so a run on another platform needs baselines of its own, generated and reviewed the same way.

### Accessibility check

`gotoReady` runs axe (`@axe-core/playwright`, pinned exactly in `package.json`) on every page it opens, once its session is ready and the page has settled; a check made while a spinner still stands in for the page would depend on timing (`accessibility.spec.ts` proves the wait).

- Axe runs with its default rules (WCAG 2.x A and AA and its best practices). A serious or critical violation fails the journey with the rule, its help and the start of each failing element's markup (`tests/e2e/lib/accessibility.ts`). Moderate and minor findings do not fail.
- Axe leaves the contrast of every Radzen button unjudged (the Material theme's ripple, a gradient on the button's `::before`, covers the button, so axe files it under "needs review"). The check adds its own: every visible, enabled, fully opaque `.rz-button`'s text colour against the first opaque background behind it, each colour read in whatever notation the browser computes (`rgb()`, `color(srgb …)` for a `color-mix()` token, any other colour space through a canvas), at least 4.5:1 with text and 3:1 icon-only, reported as `button-contrast` (serious) beside axe's violations (`buttonContrastViolations`).
- A spec that reaches a page another way (a click inside the circuit) calls `expectNoSeriousViolations` itself.
- Violations are fixed in the app, not switched off. No rule is switched off; one that must be goes in `disabledRules` in `accessibility.ts` with its reason, and in a list here with the same reason.

### Passwords in the evidence

- `loginAs` never passes the password as an action's argument, which a trace records. Instead of a `fill` (whose step the report titles `Fill "<value>"`) or an `evaluate` with the password as its argument, the page fetches it from a function exposed for that one sign-in (`exposeFunction`; the trace records its name, not what it returns), sets it in the field and raises the input and change events typing raises. Secrets a journey reads from the page (an initial password) are typed with `enterSecret`, never `fill`, and painted over in every screenshot.
- Traces record no DOM snapshots (`trace: { mode: 'on-first-retry', snapshots: false, screenshots: true, sources: true }` in `playwright.e2e.config.ts`). `STAFFORA_E2E_TRACE=retain-on-failure` asks for a trace of every failure with the same options. Playwright's own `--trace` would bring the snapshots back and is not used: with snapshots on, Playwright 1.64.1 records every input's value in the snapshots and the sign-in's request body in the trace's network log, and cannot mask either.
- A failed test's trace still holds its actions, screenshots (a password field shows dots), console and sources, beside the failure's screenshot and video.
- After the HTML report is written, `report-secret-guard` (a reporter in the config) searches every file of `.e2e/playwright-report` and of Playwright's output folder `.e2e/test-results` (traces, error contexts, screenshots, videos), inside their zip archives too, for the passwords in the state file. It fails the run when it finds one, a passing run and an already failed one alike, naming each file. It searches the output folder once more as the runner exits (`onExit`, failing the process), since a failed test's trace there can be finished only after the reporters' `onEnd`.
- Such evidence is never handed on as clean, and is deleted rather than shared. The password belongs to the end-to-end database alone and the next preparation replaces it.

## The tests here

| Class | Proves |
|---|---|
| `E2EDatabaseGuardTests` | Names with and without "e2e", environment markers, connection strings with `AttachDbFilename` or `User Instance` refused, the reset and the preparation refusing before they connect |
| `FoundationStateFileTests` | camelCase round trip, a write failing part-way leaving no file, incomplete files (a missing or partial impersonation target, tenant administrator or error-log row included) reading as none, a run that cannot reach its server removing the previous state |
| `FoundationPrepTests` | Every persona signing in with exactly its preset's permissions (the customer administrator and the orderer in the portal's tenant, the orderer's create step naming the administrator), the staff members, qualification and customer written through the services and named in the state, a wrong password refused, a second run issuing a new password, one platform tenant and owner after two runs, the impersonation targets and their permissions, the error-log rows, every tenant's time zone pinned by its system account and none for the platform tenant, the reset keeping only the migration history, runs failing after seeding leaving no state file |
| `StartupSeedGateTests` | The gate's values, the gate ignored outside Development, the tenant and user counts unchanged by a Development start after the preparation, and the demo tenants a start without the gate would add |
| `CompareScreensTests` | The comparison harness turning two PNGs into one side-by-side PNG sized by both, and refusing a file that is not a PNG without writing anything |
| `HealthEndpointTests` | `/health` healthy and anonymous with the database, unhealthy with the database taken offline, healthy again once it is back |
