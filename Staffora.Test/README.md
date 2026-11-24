# Staffora.Test

The .NET tests of every level, by folder: `Unit/`, `Integration/`, `Web/` (bUnit), `E2E/` and `Docs/` ([ADR 0018](../docs/adr/0018-test-strategy.md)). Database tests are described in [Integration/README.md](Integration/README.md), the end-to-end platform in [E2E/README.md](E2E/README.md).

## A query that cuts rows without an order fails

A query with `Skip` or `Take` and no `OrderBy` returns whichever rows the database finds first, so its answer can change from one run to the next. EF only warns about one (`CoreEventId.RowLimitingOperationWithoutOrderByWarning`); every run that checks the code turns the warning into an exception, so such a query fails loudly where it is written:

- The database tests: `RealDatabase`'s contexts, `ServiceHost`, `AppHost` and the seeding host build their options with `throwOnUnorderedRowLimit: true` (`ApplicationDbContextOptions.Create`, `AddApplicationDbContexts`).
- The end-to-end preparation (`Staffora.E2ESetup/PrepServices.cs`) does the same in code, and the app the browser journeys start runs in Development, where `appsettings.Development.json` sets `Database:ThrowOnUnorderedRowLimit` (`ApplicationDbContextOptions.ThrowOnUnorderedRowLimitSetting`); `Program.cs` reads it in Development only, so production only logs the warning.

`ContextOptionsTests` holds the switch and where it is on; `Integration/Data/UnorderedRowLimitTests` shows a test context refusing such a query and the user store's address lookup, which reads two rows to tell one match from several, ordering them. Order the query; never switch the warning off.

## A test that can loop fails, it does not hang

Code that walks an organization tree (the structure policy, the unit outline, the tree component, a service judging a move) ends on a stored or a proposed cycle only because it remembers what it passed. A change that drops that memory makes the walk loop, and a test of it must then fail in seconds instead of hanging the run.

- Every test that builds, judges or renders a cycle, and every other test of a class that walks the tree, returns a `Task` and carries `[Fact(Timeout = WalkTimeout.InMemory)]` (10 seconds: policy, outline, bUnit) or `[Fact(Timeout = WalkTimeout.Database)]` (30 seconds: through the services or the data context).
  A test without an `await` returns `Task.CompletedTask`. "A class that walks the tree" means the test class, not the method: `ListQueryServiceTests` and `ListPagesTests` hold the user list's tests to the limit too, since their unit tests walk it.
- The analyzer rule xUnit1069 asks a test with a `Timeout` to reference `TestContext.Current.CancellationToken`. The synchronous walks of `OrganizationStructurePolicyTests`, `OrganizationUnitOutlineTests` and `UnitTreeTests` take no token, and a token could not stop a walk that loops; the timeout is what fails it. Those three files switch the rule off at their top (`#pragma warning disable xUnit1069`) with that reason; a test that does await something passes the token instead. xunit runs a timed test apart from its own thread, fails it with "Test execution timed out" when the limit passes and goes on with the run; the looping thread spins until the process ends, and the process still exits.
- Do not run the work on a thread of the test's own and join it with a limit: a bUnit test that loops while rendering holds the renderer, and the test context's teardown then waits for it forever.
- When a database test times out, its transaction stays open until the run ends, so other tests of its class that wait for every transaction (such as `ALTER DATABASE … ALLOW_SNAPSHOT_ISOLATION`) time out after it. The first failure in the class is the one to read.

`WalkTimeout` (`Staffora.Test/WalkTimeout.cs`) holds the two limits.

## Whole-run backstop

A loop or a wait that no test limit covers must still not hold a run for hours. The project references Microsoft.Testing.Platform's hang-dump extension (`Microsoft.Testing.Extensions.HangDump`); pass it to a long run:

```
dotnet test --project Staffora.Test --filter-not-trait "Category=Integration" --filter-not-trait "Category=E2E" --hangdump --hangdump-timeout 20m --hangdump-type Mini
dotnet test --project Staffora.Test --filter-trait "Category=Integration" --hangdump --hangdump-timeout 20m --hangdump-type Mini
dotnet test --project Staffora.Test --filter-trait "Category=E2E" --hangdump --hangdump-timeout 20m --hangdump-type Mini
```

When the limit passes, it writes a dump and a `*_hang.log` naming the tests still running into the results directory (`--results-directory`) and ends the test process, so the run fails (exit code 7) instead of hanging. The platform's own `--timeout` is not enough on its own: it cancels the run, but a test thread that loops never sees the cancellation, and the process stays up. Both are optional.
