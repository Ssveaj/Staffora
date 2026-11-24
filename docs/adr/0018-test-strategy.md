# ADR 0018: Test strategy

## Status

Accepted

## Context

The codebase relies on rules that span many files (tenant isolation, workflow orchestration, dialog architecture, code style) and on user flows that only a browser can exercise. Tests must be fast enough to run on every change, reach a real database where behaviour depends on it, and enforce the conventions that review cannot reliably catch.

## Decision

### .NET tests

.NET tests live in one project, `Staffora.Test`, organised as a pyramid by folder:

| Folder | Level |
|---|---|
| `Unit/` | No I/O: the workflow engine, rules, EF model sweeps, architecture and source scans |
| `Integration/` | A real SQL Server database: isolation, saving, orchestration, sweeps |
| `Web/` | bUnit: dialog host, footer, transition buttons, URL state |
| `E2E/` | Tests of the browser-test platform itself |
| `Docs/` | Documentation integrity |

Each test sits at the lowest level that can prove its behaviour. Conventions that review cannot reliably catch are enforced by scan tests.

### Browser journeys

- Browser journeys are written in TypeScript with Playwright Test under `tests/e2e/`. Its runner provides the managed web server, a setup project, named steps, traces and the HTML report without custom code.
- A console project, `Staffora.E2ESetup`, prepares each run. It refuses any database whose name does not mark it as an end-to-end database, migrates, empties every table except the migration history, seeds through the application's own services, creates one persona per actor with a fresh password and verifies its permissions, and writes `.e2e/foundation-state.json` only after everything succeeded.
- Playwright runs the setup before starting the application with startup seeding switched off. Specs read logins and names from the foundation state file, never from code.
- Each journey is described in business terms in `journeys/<id>.journey.json` (actors, preconditions, steps, completion; no screen detail). Specs wrap each business step in `journeyStep(id)`, so failures and screenshots are named by journey and step.
- Nothing in the end-to-end platform runs unless `STAFFORA_E2E_CONNECTION` is set.

## Rationale

One project with clear levels is simpler than several projects for a codebase of this size, and traits keep the fast loop fast. Playwright Test provides the browser-test infrastructure that would otherwise have to be written. Describing journeys in business terms keeps them stable when screens change.

## Consequences

- Tests that need a database carry `[Trait("Category", "Integration")]`; end-to-end platform tests carry `[Trait("Category", "E2E")]`. Tests run on Microsoft.Testing.Platform (`global.json`); the fast loop is `dotnet test --project Staffora.Test --filter-not-trait "Category=Integration" --filter-not-trait "Category=E2E"`.
- `npm run test:e2e` prepares the databases, starts the applications and runs the journeys. Screenshots go to the ignored `output/playwright/<journey>/`, traces and the report to the ignored `.e2e/`.
- Journeys run in parallel, one worker per slot, each slot with an application and an end-to-end database of its own (`<name>.w<n>`, prepared by its own run of `Staffora.E2ESetup`), so no journey depends on another worker's rows, sign-ins or rate limit. A persona signs in through the login page once per worker and reuses that cookie.
- Journeys run in tiers. Every spec carries an area tag and about ten carry an `@smoke` tag. Each change runs the smoke set and the areas its paths reach (`scripts/affected-specs.mjs`, which maps changed paths to areas); the full run, with the accessibility sweeps and the reviewed visual baselines, runs at merge (`Staffora.Test/E2E/README.md`, "Tiers"). Only the full run retries a failure, once, and a trace is recorded only for that retry, so recording costs no passing test its time. The per-change tiers stop after a few failures.
- Tests use xUnit v3 with plain `Assert` and hand-written fakes; no assertion or mocking libraries.
- Test names are sentences (for example `An_insert_with_no_user_to_stamp_as_creator_is_refused`), and subfolders mirror production areas. Specs use role-based locators, wait for a ready landmark after every navigation, and wait on Playwright's retrying expectations; one journey (`dialog-restore.spec.ts`) waits a fixed second to prove that nothing reopens.
- Time comes from an injected `TimeProvider`, and tests that depend on time use a fake clock. Tests synchronize on deterministic signals where they exist (`TaskCompletionSource`, the ready landmark) and otherwise poll a condition with a bounded timeout (bUnit's `WaitForAssertion`); a few concurrency and hosted-service tests pace their loops with short delays.
- A test whose code can loop (a walk over a tree that may hold a cycle) is an async test with an xUnit `Timeout`, so a regression fails the test instead of hanging the run. Long runs can pass `--hangdump` (Microsoft.Testing.Platform's hang-dump extension) as a backstop that ends a hung run (`Staffora.Test/README.md`).
- Database tests restore a migrated template per test class and create their own tenants. The same kind of name guard protects the integration and end-to-end databases.
- Every level runs locally with the commands in the README; the repository defines no hosted pipeline.

## Related Decisions

- [ADR 0020](0020-dates-and-times.md): time from an injected `TimeProvider`.
- [ADR 0013](0013-tenant-isolation-in-the-data-context.md): the isolation tests run against a real database.
