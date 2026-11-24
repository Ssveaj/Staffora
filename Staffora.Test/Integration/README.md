# Integration tests

Tests that need a real SQL Server database: isolation, saving, orchestration, sweeps (ADR 0018). Every class carries `[Trait("Category", "Integration")]` and gets its own database through the fixture in `Infrastructure/`.

## Writing a test class

```csharp
[Trait("Category", "Integration")]
public class SomethingTests(RealDatabase database) : IClassFixture<RealDatabase>
{
    [Fact]
    public async Task Something_holds()
    {
        var scenario = await TwoTenantScenario.CreateAsync(database, cancellationToken: TestContext.Current.CancellationToken);
        await using var context = database.CreateContext(scenario.A.Runtime());
        // ...
    }
}
```

- `RealDatabase` (class fixture): the class's own database, restored from the migrated template before its first test. `CreateContext(runtime, clock)` acts as a tenant's user and stamps times from `clock` (a `FakeClock` where a test checks them); `CreateSystemContext()` holds a system-scope lease for its lifetime to arrange and inspect rows across tenants, so rows it inserts name their tenant and creator themselves. Tests in a class share the database, so each test creates its own tenants.
  `CreateIsolatedAsync(purpose, cancellationToken)` gives one test a database of its own (see **Isolated** below); dispose it last, after every context and host over it. Its contexts, like `ServiceHost`'s, throw on a query that cuts rows without an order ([../README.md](../README.md#a-query-that-cuts-rows-without-an-order-fails)).
- `TwoTenantScenario`: two active tenants, Company A and Company B, each with one user and its system account; `Runtime()` gives that user's runtime. Users are tenant rows, so each is created on its tenant's behalf (stamped with the account). `AddUserAsync` adds another user to a tenant and `AddTenantAsync` a further tenant.
  Its tenants hold no time zone row (`General.TimeZone`), as a tenant the startup ensure has not reached yet: a test that needs the pinned state, every real tenant's, pins it through `TenantSettingsEnsureService.EnsureTimeZoneAsync`, and the fallback for a missing row is tested as such.
  `AddPlatformTenantAsync` adds a platform tenant (`IsRootTenant`) whose owner holds the `Admin` role, a platform owner, signed in by `PlatformOwnerRuntime()`; `GiveAdminRoleAsync` gives any user the role, which alone grants no platform reach, and `GiveIdentityRoleAsync` any other Identity role.
- `SessionCircuit`: resolves a runtime as a new circuit does, through `UserSessionService` from a cookie and the rows in the database, so a test of platform reach or impersonation does not set the flags it proves by hand. `Cookie` builds a sign-in cookie; a cookie that impersonates comes from `ImpersonationService.StartAsync`, as the start endpoint gets it. `Open` gives the services of such a circuit before its session resolves.
- `ServiceHost`: the application's services over the class database, acting as one runtime and wired as `Program.cs` wires them (`AddApplicationDbContexts`, Identity's stores with the app's user store through `AddApplicationStores`, the impersonation authorizer, audit writer and service, `AddWorkflowServices`, the domain services, the platform bootstrap, the lookup cache and the tenant settings with the platform's time zone `ServiceHost.PlatformTimeZone` and the ensure that pins a tenant's zone), with every context a `TestDataContext` so `WorkflowService` reaches the test-only state entities; `ContextLog` holds what the data context logged, such as its system-scope leases and transition save scopes, and `CommandLog` the SQL every context sent.
  An optional `configure` callback adds or replaces registrations last. Each host has a lookup cache of its own; `ServiceHost.SharingLookupCacheOf(owner)` as `configure` gives it another host's instead, so several hosts act as circuits of one app, which shares one cache per process, and the owner's `CommandLog` holds the cache's loads. Use it to test a rule through a service.
  A test whose rows name fixed dates that the rule reads against "now" gives the host a clock anchored before them (`AnchoredClock`, which starts at a fixed instant and runs on): `AssignmentScenario.HostOf` does so with `AssignmentScenario.Clock` (Monday 2026-11-09), because staffing judges only the times not yet over, so a period in 2027 reads the same on whatever day the suite runs.
- `AppHost`: the real app (`Staffora.Blazor/Program.cs`, its pipeline, endpoints, cookies, antiforgery and policies) served in memory over the class database, for what only HTTP shows: what a cookie carries after a POST, which pages a cookie opens. Every context is built over the class database whatever connection string the app read, startup runs in the `Test` environment (production data only), and `CreateBrowser()` gives a client with a cookie jar of its own that follows no redirects.
  Test-only routes stand in for pages: `SignInAsync` signs a user in through the app's sign-in manager, `PostFormAsync` posts with the antiforgery token a page would carry, and `ClaimsAsync` reads the cookie as the app validates it. `AppHost.Start(connectionString, environment, settings)` starts it over another database, in another environment and with extra configuration, as the end-to-end tests do (`Staffora.Test/E2E/README.md`).
- `TestDeleteCleanups`: the test entities' delete cleanups (a parent's lines go with it), registered by `ServiceHost` and read by the delete-coverage model test.
- `CommitProbe` and `ProbingDispatcher`: register them through `configure` to prove that what a transition hands on after its commit really comes after it; each record reads the parent's committed state on a connection that does not wait for locks, so a read before the commit records no state.
- `TestDataContext`: the application's context plus the test-only state entities `TestParent`, `TestChild` and `TestGrandchild` (tenant-owned, with workflows, a delete state and a cascade) and the parent's `TestParentLine` rows (no lifecycle, a composite key of parent and code), for saving and orchestration tests independent of the domain entities.
  For `WorkflowService` the parent's activation queues a message and after-commit work and fails when its notes ask it to (`TestParent.FailInAnAction`, `TestParent.FailAtTheDatabase`), its cancel needs `SettingsEdit` and asks a comment (`CommentPrompt`), its activation is drawn greyed while blocked (`ShowWhenBlocked`), and its reopening is marked `RequiresPlatformOwner`.
  `TestParentDialog` (`Staffora.Test/Web/StateEntity/`, with its context, service and `TestParentWorkflowQueries`) is the parent's state-entity dialog, which `StateEntityDialogTests` opens over real rows; `TestChildDialog` is the child's, whose graph loads the parent and leaves the grandchildren out, for what a dialog leaves to the server.
  A Draft parent's lines are `Required`, and a child's activation and its cancel from Draft can be refused by the parent's notes (`TestParent.TakesNoActiveChildren`) and a grandchild's name (`TestGrandchild.HoldsItsChildOpen`). A new test table joins the read sweep, which then needs a row of it for each tenant (`ReadSweepTests`).

## How the fixture works

- **Pool.** `DatabasePool` is an xunit assembly fixture. It touches the server only on the first lease, so the fast test loop runs without SQL Server.
- **Template.** The template database gets the application's migrations, then the test-only tables: the difference between the application's model and `TestDataContext`'s model, generated and executed by EF's own migration services. That difference may only add tables and their indexes; anything else fails the preparation. The template is backed up to a file named after a fingerprint of the Staffora.Data assembly and the test model; while both are unchanged, later runs reuse the backup and migrate nothing.
- **Per class.** Each class leases a numbered database and the template backup is restored into it. Backup and restore work on every SQL Server edition, LocalDB included; a database snapshot can only revert its own source, so it cannot hand each class a copy. Classes run in parallel on different numbers.
- **Guard.** Every database the pool creates, restores or drops must have "test" as a word of its own in its name (`Staffora_Integration_test`, not `latest`) and none of "prod", "stage" or "staging" (`DatabaseNameGuard`). Template preparation and each leased number are held with SQL Server application locks, so two test runs against one server do not collide. A server that cannot be reached fails with its name and `STAFFORA_INTEGRATION_CONNECTION` in the message.
- **Isolated.** A test that changes its database (`ALTER DATABASE`, such as `ALLOW_SNAPSHOT_ISOLATION`) or leaves its pooled connections in a state another test must not inherit gets a database of its own: `database.CreateIsolatedAsync("snapshot", cancellationToken)` restores the template into `<base>_isolated_<purpose>_<12 hex digits>` and drops it when disposed.
  Its connection string names that database and an `Application Name` of its own, so its connections form a pool no other test shares, and the release clears exactly that pool, once the test's connections are closed, before the drop.
  Never alter the class database or run a transaction at another isolation level on its pool: SQL Server keeps a session's isolation level when the pool hands the connection on (`sp_reset_connection` does not reset it), so the class's later tests would run at that level, and after `ALLOW_SNAPSHOT_ISOLATION OFF` fail with "snapshot isolation is not allowed", depending on the order they happen to run in.
  `OrganizationStructureTests`' two snapshot tests are the pattern, and `TemplateDatabaseTests` proves the lease.
- **Leftovers.** The class databases stay on the server after a run for inspection; the next run restores over them. The template database is dropped once it is backed up; only the backup file is kept. An isolated database a run ended before dropping is dropped by the next run as it prepares the template, unless another process still holds its application lock.
- **Parallel classes.** `ParallelDatabasesTests` proves that classes run at the same time on different databases: it leases two class databases at once, as two classes starting together would, and creates two tenants on each concurrently; each database holds only its own.

## Where it runs

| Setting | Default | Purpose |
|---|---|---|
| `STAFFORA_INTEGRATION_CONNECTION` | LocalDB, database `Staffora_Integration_test` | Server and base name; the template is `<base>_template`, class databases `<base>_1`, `<base>_2`, … |
| `STAFFORA_INTEGRATION_FILES` | LocalDB: `%LOCALAPPDATA%\Staffora.Test\Databases`; other servers: the server's default data folder | Where the server writes database files and the template backup |
