using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class DataContextScanTests
{
    private static readonly string[] productionProjects = ["Staffora.Core", "Staffora.Data", "Staffora.Services", "Staffora.Blazor", "Staffora.E2ESetup"];

    private static readonly Dictionary<(string Path, string Call), string> bypassAllowList = new()
    {
        [("Staffora.Core/Execution/IApplicationDbContextFactory.cs", "CreateSystemDbContext")] = "declares the factory method",
        [("Staffora.Data/ApplicationDbContextFactory.cs", "CreateSystemDbContext")] = "implements the factory method",
        [("Staffora.Data/ApplicationDbContextRegistration.cs", "CreateSystemDbContext")] = "the directly injected context, a system context for ASP.NET Identity's stores only",
        [("Staffora.Data/ApplicationUserStore.cs", "BeginSystemScope")] = "Identity finds a user before any tenant is known (sign-in, cookie validation) and saves a user on its tenant's behalf, one lease per call",
        [("Staffora.Data/ApplicationDbContext.TenantIsolation.cs", "BeginSystemScope")] = "defines the system-scope lease",
        [("Staffora.Data/ApplicationDbContext.TenantReferences.cs", "IgnoreQueryFilters")] = "the write check reads the stored tenant of every referenced row, whatever tenant holds it",
        [("Staffora.Data/ApplicationDbContext.WorkflowFieldEnforcement.cs", "IgnoreQueryFilters")] = "field enforcement reads the stored state of a written child row's parent, after the tenant checks confined the row to its tenant",
        [("Staffora.Data/Seed/DatabaseSeeder.cs", "CreateSystemDbContext")] = "startup seeding writes the rows of several tenants",
        [("Staffora.Services/Demo/DemoErrorLog.cs", "CreateSystemDbContext")] = "the development demo dates its error-log rows back, which the real logging path cannot, into the platform table written only in system scope",
        [("Staffora.Services/Demo/DemoErrorLog.cs", "BeginSystemScope")] = "the development demo writes its error-log rows under a logged lease, as the end-to-end preparation writes its own",
        [("Staffora.Data/Seed/DatabaseSeeder.cs", "BeginSystemScope")] = "startup seeding writes the rows of several tenants, under the \"seed\" lease",
        [("Staffora.Data/Seed/PlatformBootstrap.cs", "CreateSystemDbContext")] = "the one-time bootstrap creates the platform tenant and its first owner from the command line, outside any user's session",
        [("Staffora.Data/Seed/PlatformBootstrap.cs", "BeginSystemScope")] = "the bootstrap looks for a platform owner in every tenant and writes the platform tenant's rows",
        [("Staffora.Data/AppLock.cs", "ExecuteSql")] = "every application lock (sp_getapplock) is taken here: a tenant's structure, schedule, customer and job locks and a record's comment thread lock, owned by the caller's transaction after the tenant's row is held, in one fixed order (TakeInOrderAsync), the tenant-create lock of the platform console, and the bootstrap's session lock, taken and released so runs started together create one platform tenant and one owner",
        [("Staffora.Data/ApplicationDatabase.cs", "ExecuteSql")] = "the migration turns on READ_COMMITTED_SNAPSHOT once, so a plain read never waits on another tenant's uncommitted rows",
        [("Staffora.Data/SystemAccountService.cs", "CreateSystemDbContext")] = "ensures the system accounts at startup and when a tenant is onboarded, outside any user's session",
        [("Staffora.Data/SystemAccountService.cs", "BeginSystemScope")] = "lists every tenant and writes one tenant's system account, whichever context ensures it",
        [("Staffora.Data/ApplicationDbContext.OrganizationStructure.cs", "SqlQuery")] = "the structure check reads, as committed (READCOMMITTEDLOCK), the units that no longer reach a root through their parents (a cycle), the holdings on groupings and the stored type of a deleted unit before the save commits",
        [("Staffora.Data/TenantOnboardingWriter.cs", "BeginSystemScope")] = "writes a tenant's role presets and its first system owner on the tenant's behalf, stamped with its system account (onboarding, the development seeding), and a customer portal's roles and first customer administrator (ADR 0007)",
        [("Staffora.Services/Services/TenantOnboardingService.cs", "BeginSystemScope")] = "creates a customer portal's managed tenant and its supplier party inside the staffing company's Enable portal step, on the portal's behalf, stamped with its system account (ADR 0007)",
        [("Staffora.Services/Portal/OrderSender.cs", "BeginSystemScope")] = "a portal's Send step judges its staffing company's side and writes the job, the orderer's paired contact and the coordinators' notice into the staffing company's tenant on its behalf, inside the step's transaction and under its locks, stamped with its system account: the one direct cross-tenant write (ADR 0007)",
        [("Staffora.Data/CompanyRoot.cs", "BeginSystemScope")] = "writes one tenant's Company root on the tenant's behalf, whichever context ensures it (seeding, the bootstrap, onboarding)",
        [("Staffora.Services/Authorization/PlatformScope.cs", "BeginSystemScope")] = "the platform lease, opened only for a runtime with platform reach",
        [("Staffora.Services/Authorization/PlatformScope.cs", "BeginPlatformScope")] = "defines the platform lease",
        [("Staffora.Services/Authorization/UserSessionService.cs", "CreateSystemDbContext")] = "resolves the session from users and Identity's tables before any tenant is known",
        [("Staffora.Services/Authorization/UserSessionService.cs", "BeginSystemScope")] = "reads the signed-in user before a tenant is known, and the tenant of the user a request's cookie names for its log scope",
        [("Staffora.Services/Authorization/ImpersonationAuthorizer.cs", "CreateSystemDbContext")] = "reads the acting platform owner and the target user, of any tenant, before any tenant applies",
        [("Staffora.Services/Authorization/ImpersonationAuthorizer.cs", "BeginSystemScope")] = "reads the acting platform owner and the target users of any tenant, with their tenants and Identity roles, to decide and to list them",
        [("Staffora.Services/Authorization/ImpersonationAuditWriter.cs", "CreateSystemDbContext")] = "writes the impersonation audit, a platform table outside every tenant",
        [("Staffora.Services/Authorization/ImpersonationAuditWriter.cs", "BeginSystemScope")] = "the impersonation audit is shown and written only in system scope",
        [("Staffora.Services/Diagnostics/ErrorLogWriter.cs", "CreateSystemDbContext")] = "stores the errors the application logged in the error log, a platform table outside every tenant, from the background",
        [("Staffora.Services/Diagnostics/ErrorLogWriter.cs", "BeginSystemScope")] = "the error log is written only in system scope",
        [("Staffora.Services/Caching/TenantLookupCache.cs", "CreateSystemDbContext")] = "the lookup cache reads one named tenant's catalogue on a context of no circuit, so an entry depends on its key alone and never on the circuit that filled it; the owning service has checked the caller's permission and tenant first",
        [("Staffora.Services/Caching/TenantLookupCache.cs", "BeginSystemScope")] = "reads exactly the tenant its key names (the catalogue's query names the tenant), and refuses to cache a row of any other tenant",
        [("Staffora.Services/Settings/TenantSettingsEnsureService.cs", "CreateSystemDbContext")] = "pins every tenant's time zone at startup, in the end-to-end preparation and at onboarding, outside any user's session",
        [("Staffora.Services/Settings/TenantSettingsEnsureService.cs", "BeginSystemScope")] = "lists every tenant and writes one tenant's time zone row on its behalf, stamped with its system account",
        [("Staffora.Services/Services/SystemOwnerPermissionsEnsure.cs", "CreateSystemDbContext")] = "brings every tenant's locked System owner role up to date at startup, outside any user's session",
        [("Staffora.Services/Jobs/JobClock.cs", "CreateSystemDbContext")] = "the job clock is a hosted service with no session: it lists the active tenants' ids before it acts in each of them as that tenant's system account",
        [("Staffora.Services/Jobs/JobClock.cs", "BeginSystemScope")] = "lists the ids of the active tenants and nothing else; each tenant's jobs are then read and moved in a context filtered to that tenant",
        [("Staffora.Services/Platform/Retention.cs", "CreateSystemDbContext")] = "the retention pass (ADR 0042) is a hosted service with no session: it lists every tenant's id and removes, one tenant and one batch at a time, the error log's, notices' and integration messages' rows past their age",
        [("Staffora.Services/Platform/Retention.cs", "BeginSystemScope")] = "removes error-log rows past their age, a platform table written only in system scope, and each tenant's read notices and delivered integration messages, whose rows are deleted only in system scope, every query naming its tenant",
        [("Staffora.Services/Platform/Retention.TenantPurge.cs", "CreateSystemDbContext")] = "the retention pass lists the ids of the tenants scheduled for deletion past their grace period, and the platform tenant's, before it purges each in a transaction of its own",
        [("Staffora.Services/Platform/Retention.TenantPurge.cs", "BeginSystemScope")] = "purges one tenant scheduled for deletion past its grace period (TenantPurge, which holds the tenant's row and refuses any other) and archives it through its system step, as the platform tenant's system account",
        [("Staffora.Services/Integration/IntegrationOutboxDispatcher.cs", "CreateSystemDbContext")] = "the integration outbox's dispatcher (ADR 0007) is a hosted service with no session: it lists what is due across tenants (ids and kinds only), counts what is pending and dead-lettered for telemetry, and claims and records one message or notice of its sending tenant at a time, before it acts in the target tenant as that tenant's system account",
        [("Staffora.Services/Integration/IntegrationOutboxDispatcher.cs", "BeginSystemScope")] = "lists the ids and kinds of what is due, counts the pending and dead-lettered messages, claims and records one message of its sending tenant (or one notice of its tenant) with both tenants' rows held first, and, inside a delivery whose context is the target tenant's, reads and marks the sender's message; the handler's writes are made outside the lease, as the target tenant",
        [("Staffora.Services/Services/SystemOwnerPermissionsEnsure.cs", "BeginSystemScope")] = "lists the tenants with a System owner role and adds the permissions a release introduced to one tenant's role on its behalf, stamped with its system account",
        [("Staffora.Services/CustomFields/CustomFieldService.cs", "BeginPlatformScope")] = "the platform console manages global custom field definitions and those of every tenant",
        [("Staffora.Services/CustomFields/CustomFieldService.Dialogs.cs", "BeginPlatformScope")] = "a custom field's dialog on the platform console (ADR 0033): it opens, checks and steps the global fields and those of every tenant",
        [("Staffora.Services/Services/OrganizationService.cs", "BeginPlatformScope")] = "the platform console reads a tenant's organization tree and opens its units",
        [("Staffora.Services/Tenancy/TenantService.cs", "BeginPlatformScope")] = "the platform console opens a tenant's dialog and checks a tenant's name against every other tenant's",
        [("Staffora.Services/Tenancy/TenantRoleService.cs", "BeginPlatformScope")] = "the platform console reads a tenant's roles and opens a role read-only",
        [("Staffora.Services/Tenancy/RoleHoldingService.cs", "BeginPlatformScope")] = "the platform console lists the role holdings of a tenant's role, unit and user, and of a unit on the organization page",
        [("Staffora.Services/Services/UserService.cs", "BeginPlatformScope")] = "the platform console lists one tenant's users on its console page and opens a tenant's user read-only",
        [("Staffora.Services/Platform/PlatformConsoleService.cs", "BeginPlatformScope")] = "the platform console's reads: the overview, the tenant list, one tenant's overview and roles, and its dead letters, each after its platform reach check",
        [("Staffora.Services/Platform/PlatformConsoleService.ErrorLog.cs", "BeginPlatformScope")] = "the platform console reads the error log, a platform table shown only in system scope, after its platform reach check",
        [("Staffora.Services/Search/GlobalSearchService.cs", "BeginPlatformScope")] = "the platform owner's search on the console (ADR 0040) finds tenants by name across the platform, the one kind it searches with platform reach",
        [("Staffora.Services/Workflow/WorkflowService.cs", "BeginPlatformScope")] = "a platform owner on the console moves tenants and the entities of every tenant, stamped on each tenant's behalf",
        [("Staffora.Services/Workflow/WorkflowService.Batch.cs", "BeginPlatformScope")] = "a batch of creates runs its steps under the lease each step of WorkflowService takes alone; each create still refuses the platform console",
        [("Staffora.Services/Workflow/WorkflowService.Judgement.cs", "BeginPlatformScope")] = "a platform owner on the console reads the steps of the entities of every tenant (the footer's question), loaded and judged as running them is",
        [("Staffora.Services/Workflow/StateHistoryService.cs", "BeginPlatformScope")] = "a platform owner on the console reads the state history of the entities of every tenant, as WorkflowService answers their steps",
        [("Staffora.Services/Messaging/InAppChannelSender.cs", "BeginPlatformScope")] = "a platform owner's step on the console wrote its notice into the moved entity's tenant, so it is delivered there, stamped on that tenant's behalf",
        [("Staffora.Services/Messaging/OutgoingMessageDispatcher.cs", "BeginPlatformScope")] = "a notice a platform owner's step wrote into another tenant is marked not delivered there, stamped on that tenant's behalf",
        [("Staffora.Blazor/Program.cs", "CreateSystemDbContext")] = "applies the migrations at startup",
        [("Staffora.E2ESetup/FoundationPrep.cs", "CreateSystemDbContext")] = "applies the migrations to the end-to-end database before it is emptied and seeded",
        [("Staffora.E2ESetup/ErrorLogFixture.cs", "CreateSystemDbContext")] = "seeds the end-to-end database's error log with the rows the smoke journey filters",
        [("Staffora.E2ESetup/ErrorLogFixture.cs", "BeginSystemScope")] = "the error log is written only in system scope, as the error-log writer writes it",
    };

    private static readonly Dictionary<string, string> undisposedAllowList = new(StringComparer.Ordinal)
    {
        ["Staffora.Data/ApplicationDbContextFactory.cs"] = "the factory returns each context to its caller, who disposes it",
        ["Staffora.Data/ApplicationDbContextRegistration.cs"] = "the dependency-injection scope disposes the context Identity uses",
    };

    private static readonly Dictionary<string, string> leaseAllowList = new(StringComparer.Ordinal)
    {
        ["Staffora.Services/Authorization/PlatformScope.cs"] = "returns the platform lease to the calling service, which takes it with using",
    };

    private static readonly Dictionary<(string Path, string Call), string> bulkStatementAllowList = new()
    {
        [("Staffora.Data/BulkUpdateGuard.cs", "ExecuteUpdate")] = "names the statement to recognize and judge it; the guard runs none",
        [("Staffora.Data/BulkUpdateGuard.cs", "ExecuteDelete")] = "names the statement to recognize and refuse it; the guard runs none",
        [("Staffora.Data/TenantPurge.cs", "ExecuteDelete")] = "the tenant purge (ADR 0042) deletes a tenant scheduled for deletion past its grace period one table and one batch at a time, in the order the model's foreign keys give, each statement tagged and naming the tenant, which the guard admits only inside the purge's transaction that holds the tenant's row in Pending deletion",
        [("Staffora.Services/Platform/Retention.cs", "ExecuteDelete")] = "the retention pass removes error-log rows past their age in batches, a platform table written only in system scope",
        [("Staffora.Services/Workflow/WorkflowService.Locks.cs", "ExecuteUpdate")] = "the lock step of a transition sets IsInTransition on its candidate rows, one statement per entity type (TRANSITION-ORCHESTRATION.md, step 7)",
        [("Staffora.Services/Jobs/StaffConversationService.cs", "ExecuteUpdate")] = "the staff conversation's whose-turn timestamps (ADR 0010): a post's stamp is the first statement of the comment service's transaction, so it holds the assignment's row and puts two posts at once in one order, and the staff member's read stamps what they have seen, one statement over their own assignment in the tenant; neither touches a column a workflow state opens, and the updater leaves them alone",
        [("Staffora.Services/Messaging/NoticeService.cs", "ExecuteUpdate")] = "marking one's notices read is one statement over the reader's own unread copies in the tenant, setting only the read flag, the read time and the editor stamp (BulkUpdateGuard holds the columns; docs/features/notifications-and-comments.md, \"Notifications\")",
        [("Staffora.Services/Messaging/HandledNotices.cs", "ExecuteUpdate")] = "a coordinator's step that handles a staff member's question or message, or a reply in the conversation, marks the notices that question or message raised read for every recipient: one statement over the unread copies of those kinds about that record in its tenant, inside the step's or the reply's own transaction, setting only the read flag, the read time and the editor stamp (BulkUpdateGuard holds the columns; docs/features/notifications-and-comments.md, \"Notifications\")",
        [("Staffora.Data/ApplicationUserStore.cs", "ExecuteUpdate")] = "a failed sign-in is counted in one statement on the stored row (the count, the lockout at the threshold, the editor stamp), so concurrent wrong passwords are each counted (docs/authentication.md, \"Sign-in hardening and security headers\")",
        [("Staffora.Services/Services/TenantSignOut.cs", "ExecuteUpdate")] = "Suspend and Schedule deletion rotate the security and concurrency stamps of every user of the tenant in one statement inside the step's transaction, naming the tenant, stamped by its system account (docs/authorization.md, \"Tenant states and access\")",
    };

    private static readonly string[] saveScopeProjects = [.. productionProjects, "Staffora.Test"];

    private static readonly Dictionary<string, string> saveScopeAllowList = new(StringComparer.Ordinal)
    {
        ["Staffora.Data/ApplicationDbContext.WorkflowFieldEnforcement.cs"] = "defines the transition save scope",
        ["Staffora.Services/Workflow/WorkflowService.cs"] = "saves a transition it has locked and executed (TRANSITION-ORCHESTRATION.md, step 13); the only production caller",
        ["Staffora.Test/Integration/Data/ActingInAnotherTenantTests.cs"] = "changes a managed tenant's staffing company in a tenant step's save scope, which the workflow's field rules do not judge, to prove the data context still refuses it",
        ["Staffora.Test/Integration/Data/BulkUpdateGuardTests.cs"] = "activates a parent through the engine and saves it as WorkflowService does, to set up a row with history",
        ["Staffora.Test/Integration/Data/StateHistoryWriterTests.cs"] = "saves the history the writer adds as a transition's save does, without the service",
        ["Staffora.Test/Integration/Data/TenantIsolationTests.cs"] = "deletes a parent through its delete transition in a save scope, to prove the tenant checks still hold inside one",
        ["Staffora.Test/Integration/Data/WorkflowFieldEnforcementTests.cs"] = "proves what a save scope lets through, what it does not, and that scopes nest and end; it opens some outside a using on purpose",
        ["Staffora.Test/Integration/Services/OrganizationUnitLifecycleTests.cs"] = "writes a unit's state and a delete as a step's save would, past the step's conditions, to prove the data context's structure check refuses them",
        ["Staffora.Test/Integration/Services/UserAccountProtectionTests.cs"] = "takes a unit holding a role out of use as its deactivation's save writes it, since a unit out of use takes no new holding",
    };

    private static readonly Dictionary<(string Path, string Construct), string> runtimeAllowList = new()
    {
        [("Staffora.Data/ApplicationDbContextFactory.cs", "RuntimeContext =")] = "the factory gives each context the runtime it is created for",
        [("Staffora.Services/Authorization/UserSessionService.cs", "new RuntimeExecutionContext(")] = "builds the runtime of a session it resolved but has not applied yet, only to prime the permission cache for it",
        [("Staffora.Services/Authorization/PlatformOwnerResetCommand.cs", "new RuntimeExecutionContext(")] = "the reset-platform-owner command runs outside any session and, once Identity says the address is an Admin's, acts as that user's tenant's system account (which signs in nowhere) to check that it is the platform tenant and record the reset in the owner's history on its behalf (docs/authentication.md, \"A platform owner who lost their password\")",
    };

    private static readonly Dictionary<(string Path, string Setter), string> runtimeSetterAllowList = new()
    {
        [("Staffora.Services/Authorization/UserSessionService.cs", "ApplySession")] = "the session applies, in one step, the signed-in user, their tenant and whether they are a platform owner as it read them from the database, and the cookie's impersonation only after the authorizer allowed it and its audit row was found open for that owner and target",
        [("Staffora.Services/Authorization/SystemAccountSession.cs", "ApplySession")] = "the job clock, the integration outbox's dispatcher and the retention pass's archive of a purged tenant (as the platform tenant's system account) have no circuit: each gives the fresh runtime of a service scope of its own, and only one that holds no session yet, the session of one tenant's system account in that tenant (which signs in nowhere), so everything it reads and writes there is that tenant's and stamped by its own account",
    };

    private static readonly Dictionary<string, string> contextConstructorAllowList = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> historyConstructionAllowList = new(StringComparer.Ordinal)
    {
        ["Staffora.Data/StateHistoryWriter.cs"] = "the one writer of state history: one entry per step of a transition, with the runtime's effective user as the actor",
    };

    private static readonly Dictionary<(string Path, string Type), string> outboxRowConstructionAllowList = new()
    {
        [("Staffora.Data/IntegrationOutbox.cs", "IntegrationMessage")] = "the one way a tenant queues a message: pending and due, a row of the runtime's tenant, in the sender's own step",
        [("Staffora.Services/Integration/IntegrationHandlerRegistry.cs", "IntegrationReceipt")] = "the receipt is added beside the handler's run, on the delivery's context of the target tenant, and saved with its writes",
    };

    private static readonly Dictionary<string, string> injectingComponentAllowList = new(StringComparer.Ordinal);

    [Fact]
    public void Boundary_calls_appear_only_in_allow_listed_files()
    {
        var offenders = ProductionSources()
            .SelectMany(source => FindBoundaryCalls(source.Text).Distinct().Select(call => (source.Path, Call: call)))
            .Where(hit => !bypassAllowList.ContainsKey(hit))
            .Select(hit => $"{hit.Path}: {hit.Call}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_boundary_allow_list_entry_still_matches_a_call_and_names_a_reason()
    {
        var sources = ProductionSources().ToDictionary(source => source.Path, source => source.Text, StringComparer.Ordinal);

        var stale = bypassAllowList
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value)
                || !sources.TryGetValue(entry.Key.Path, out var text)
                || !FindBoundaryCalls(text).Contains(entry.Key.Call))
            .Select(entry => $"{entry.Key.Path}: {entry.Key.Call}");

        Assert.Empty(stale);
    }

    [Fact]
    public void The_boundary_scan_catches_every_bypass_call_outside_comments()
    {
        const string sample = """
            // context.IgnoreQueryFilters() in a comment is no call.
            /* factory.CreateSystemDbContext() neither */
            var rows = context.Rows.IgnoreQueryFilters().ToList();
            using var system = factory.CreateSystemDbContext();
            using (context.BeginSystemScope("reason")) { }
            using var lease = context.BeginPlatformScopeFor(runtime, tenantId, "reason");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM Rows");
            var raw = context.Rows.FromSqlInterpolated($"SELECT * FROM Rows");
            var ids = context.Database.SqlQuery<Guid>($"SELECT Id FROM Rows");
            await context.Rows.ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "x"));
            """;

        Assert.Equal(
            ["IgnoreQueryFilters", "CreateSystemDbContext", "BeginSystemScope", "BeginPlatformScope", "ExecuteSql", "FromSql", "SqlQuery"],
            FindBoundaryCalls(sample));
    }

    [Fact]
    public void Every_context_created_from_a_factory_is_disposed()
    {
        var offenders = ProductionSources()
            .Where(source => !undisposedAllowList.ContainsKey(source.Path))
            .SelectMany(source => FindUndisposedContexts(source.Text).Select(hit => $"{source.Path}: {hit}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_undisposed_allow_list_entry_still_creates_an_undisposed_context()
    {
        var sources = ProductionSources().ToDictionary(source => source.Path, source => source.Text, StringComparer.Ordinal);

        var stale = undisposedAllowList
            .Where(entry => !sources.TryGetValue(entry.Key, out var text) || FindUndisposedContexts(text).Count == 0)
            .Select(entry => entry.Key);

        Assert.Empty(stale);
    }

    [Fact]
    public void The_disposal_scan_catches_a_context_created_outside_a_using()
    {
        const string sample = """
            using var a = dbFactory.CreateDbContext(runtime);
            await using var b = await dbFactory.CreateDbContextAsync(runtime);
            using (var c = dbFactory.CreateSystemDbContext())
            {
                await using (var d = scope.ServiceProvider.GetRequiredService<IApplicationDbContextFactory<ApplicationDbContext>>().CreateSystemDbContext())
                {
                }
            }
            var leaked = dbFactory.CreateDbContext(runtime);
            var alsoLeaked = await dbFactory.CreateDbContextAsync(runtime);
            return factory.CreateSystemDbContext();
            """;

        Assert.Equal(
            ["var leaked = dbFactory.CreateDbContext", "var alsoLeaked = await dbFactory.CreateDbContextAsync", "return factory.CreateSystemDbContext"],
            FindUndisposedContexts(sample));
    }

    [Fact]
    public void No_component_injects_a_context_or_a_context_factory_outside_the_allow_list()
    {
        var offenders = ProductionSources()
            .Where(source => source.Path.EndsWith(".razor", StringComparison.Ordinal))
            .Where(source => !injectingComponentAllowList.ContainsKey(source.Path))
            .SelectMany(source => FindContextInjections(source.Text).Select(hit => $"{source.Path}: {hit}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_injecting_component_allow_list_entry_still_injects()
    {
        var sources = ProductionSources().ToDictionary(source => source.Path, source => source.Text, StringComparer.Ordinal);

        var stale = injectingComponentAllowList
            .Where(entry => !sources.TryGetValue(entry.Key, out var text) || FindContextInjections(text).Count == 0)
            .Select(entry => entry.Key);

        Assert.Empty(stale);
    }

    [Fact]
    public void The_injection_scan_catches_a_context_or_a_factory_injected_into_a_component()
    {
        const string sample = """
            @inject ApplicationDbContext Db
            @inject IApplicationDbContextFactory<ApplicationDbContext> DbFactory
            @inject IDbContextFactory<ApplicationDbContext> EfFactory
            @inject UserService UserService
            @code {
                [Inject] private ApplicationDbContext Context { get; set; } = default!;
                [Inject] public required TenantService Tenants { get; set; }
            }
            """;

        Assert.Equal(
            ["ApplicationDbContext", "IApplicationDbContextFactory<ApplicationDbContext>", "IDbContextFactory<ApplicationDbContext>", "ApplicationDbContext"],
            FindContextInjections(sample));
    }

    [Fact]
    public void Every_lease_is_taken_by_a_using()
    {
        var offenders = ProductionSources()
            .Where(source => !leaseAllowList.ContainsKey(source.Path))
            .SelectMany(source => FindUnscopedLeases(source.Text).Select(hit => $"{source.Path}: {hit}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_lease_allow_list_entry_still_opens_a_lease_outside_a_using()
    {
        var sources = ProductionSources().ToDictionary(source => source.Path, source => source.Text, StringComparer.Ordinal);

        var stale = leaseAllowList
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value)
                || !sources.TryGetValue(entry.Key, out var text)
                || FindUnscopedLeases(text).Count == 0)
            .Select(entry => entry.Key);

        Assert.Empty(stale);
    }

    [Fact]
    public void The_lease_scan_catches_a_lease_outside_a_using()
    {
        const string sample = """
            public IDisposable BeginSystemScope(string reason) => Open(reason);
            public static IDisposable? BeginPlatformScope(this ApplicationDbContext context, string reason) => null;
            using var a = context.BeginSystemScope("reason");
            using (context.BeginPlatformScope(runtime, "reason")) { }
            await using (var c = factory.CreateSystemDbContext())
            using (c.BeginSystemScope("reason")) { }
            using var d = tenantId is null ? null : context.BeginPlatformScopeFor(runtime, tenantId, "reason");
            // context.BeginSystemScope("in a comment") is no call.
            var leaked = context.BeginSystemScope("reason");
            context.BeginPlatformScope(runtime, "reason");
            return context.BeginPlatformScopeFor(runtime, tenantId, "reason");
            """;

        Assert.Equal(
            ["var leaked = context.BeginSystemScope", "context.BeginPlatformScope", "return context.BeginPlatformScopeFor"],
            FindUnscopedLeases(sample));
    }

    [Fact]
    public void Transition_save_scopes_are_named_only_in_allow_listed_files()
    {
        var offenders = SourcesOf(saveScopeProjects)
            .Where(source => !saveScopeAllowList.ContainsKey(source.Path) && FindSaveScopeNames(source.Text) > 0)
            .Select(source => source.Path);

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_save_scope_allow_list_entry_still_names_a_scope_and_a_reason()
    {
        var sources = SourcesOf(saveScopeProjects).ToDictionary(source => source.Path, source => source.Text, StringComparer.Ordinal);

        var stale = saveScopeAllowList
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value)
                || !sources.TryGetValue(entry.Key, out var text)
                || FindSaveScopeNames(text) == 0)
            .Select(entry => entry.Key);

        Assert.Empty(stale);
    }

    [Fact]
    public void The_save_scope_name_scan_counts_every_name_outside_comments_and_literals()
    {
        const string sample = """"
            // context.BeginTransitionSaveScope([parent], "in a comment") is none.
            var message = "BeginTransitionSaveScope in a string is none either";
            var raw = """context.BeginTransitionSaveScope([parent], "in a raw string")""";
            public IDisposable BeginTransitionSaveScope(IEnumerable<IStateEntity> entities, string reason) => Open(entities, reason);
            using (context.BeginTransitionSaveScope([parent], "reason")) { }
            Func<IEnumerable<IStateEntity>, string, IDisposable> open = context.BeginTransitionSaveScope;
            """";

        Assert.Equal(3, FindSaveScopeNames(sample));
    }

    [Fact]
    public void Every_transition_save_scope_in_production_is_the_resource_of_a_using()
    {
        var offenders = ProductionSources()
            .SelectMany(source => FindUnscopedSaveScopes(source.Text).Select(hit => $"{source.Path}: {hit}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_save_scope_scan_catches_a_scope_that_is_not_the_resource_of_a_using()
    {
        const string sample = """
            public IDisposable BeginTransitionSaveScope(IEnumerable<IStateEntity> entities, string reason) => Open(entities, reason);
            using (context.BeginTransitionSaveScope(candidates.All.Where(entity => entity.Id != Guid.Empty), transition.Key))
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            using var scope = context.BeginTransitionSaveScope([parent], "reason (with parentheses)");
            using (var named = context.BeginTransitionSaveScope([parent], "reason")) { }
            var open = context.BeginTransitionSaveScope(candidates.All, transition.Key);
            context.BeginTransitionSaveScope([parent], "reason");
            using (lease) context.BeginTransitionSaveScope([parent], "reason");
            using var wrapped = Wrap(context.BeginTransitionSaveScope([parent], "reason"));
            using (context.BeginTransitionSaveScope([parent], "reason").Also()) { }
            Func<IDisposable> later = () => context . BeginTransitionSaveScope ([parent], "reason");
            Func<IEnumerable<IStateEntity>, string, IDisposable> group = context.BeginTransitionSaveScope;
            """;

        Assert.Equal(
            [
                "var open = context.BeginTransitionSaveScope",
                "context.BeginTransitionSaveScope",
                "using (lease) context.BeginTransitionSaveScope",
                "using var wrapped = Wrap(context.BeginTransitionSaveScope",
                "using (context.BeginTransitionSaveScope",
                "Func<IDisposable> later = () => context.BeginTransitionSaveScope",
                "Func<IEnumerable<IStateEntity>, string, IDisposable> group = context.BeginTransitionSaveScope",
            ],
            FindUnscopedSaveScopes(sample));
    }

    [Fact]
    public void Bulk_statements_appear_only_in_allow_listed_files()
    {
        var offenders = ProductionSources()
            .SelectMany(source => FindBulkStatements(source.Text).Distinct().Select(call => (source.Path, Call: call)))
            .Where(hit => !bulkStatementAllowList.ContainsKey(hit))
            .Select(hit => $"{hit.Path}: {hit.Call}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_bulk_statement_allow_list_entry_still_matches_and_names_a_reason()
    {
        var sources = ProductionSources().ToDictionary(source => source.Path, source => source.Text, StringComparer.Ordinal);

        var stale = bulkStatementAllowList
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value)
                || !sources.TryGetValue(entry.Key.Path, out var text)
                || !FindBulkStatements(text).Contains(entry.Key.Call))
            .Select(entry => $"{entry.Key.Path}: {entry.Key.Call}");

        Assert.Empty(stale);
    }

    [Fact]
    public void The_bulk_statement_scan_catches_every_name_of_ExecuteUpdate_and_ExecuteDelete_outside_comments_and_literals()
    {
        const string sample = """
            // rows.ExecuteDeleteAsync() in a comment is no statement.
            var message = "ExecuteUpdate in a string is none either";
            var isUpdate = node.Method.Name == nameof(EntityFrameworkQueryableExtensions.ExecuteUpdate);
            await context.TestParents.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Notes, "x"), cancellationToken);
            context.Rows . ExecuteUpdate (set => set.SetProperty(row => row.Name, "x"));
            await rows
                .ExecuteDeleteAsync(cancellationToken);
            Func<CancellationToken, Task<int>> later = rows.ExecuteDeleteAsync;
            rows.ExecuteDelete();
            var executeUpdates = 0;
            """;

        Assert.Equal(["ExecuteUpdate", "ExecuteUpdate", "ExecuteUpdate", "ExecuteDelete", "ExecuteDelete", "ExecuteDelete"], FindBulkStatements(sample));
    }

    [Fact]
    public void A_runtime_is_assigned_or_built_only_in_allow_listed_files()
    {
        var offenders = ProductionSources()
            .SelectMany(source => FindRuntimeConstructs(source.Text).Distinct().Select(construct => (source.Path, Construct: construct)))
            .Where(hit => !runtimeAllowList.ContainsKey(hit))
            .Select(hit => $"{hit.Path}: {hit.Construct}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_runtime_allow_list_entry_still_matches_and_names_a_reason()
    {
        var sources = ProductionSources().ToDictionary(source => source.Path, source => source.Text, StringComparer.Ordinal);

        var stale = runtimeAllowList
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value)
                || !sources.TryGetValue(entry.Key.Path, out var text)
                || !FindRuntimeConstructs(text).Contains(entry.Key.Construct))
            .Select(entry => $"{entry.Key.Path}: {entry.Key.Construct}");

        Assert.Empty(stale);
    }

    [Fact]
    public void The_runtime_scan_catches_an_assignment_and_a_construction()
    {
        const string sample = """
            if (context.RuntimeContext == null) { }
            var tenant = context.RuntimeContext?.TenantId;
            // context.RuntimeContext = other; in a comment is no assignment.
            context.RuntimeContext = runtime;
            var copy = new ApplicationDbContext(options) { RuntimeContext=runtime };
            IRuntimeExecutionContext forged = new RuntimeExecutionContext(tenantId, userId);
            var empty = new RuntimeExecutionContext ();
            """;

        Assert.Equal(
            ["RuntimeContext =", "RuntimeContext =", "new RuntimeExecutionContext(", "new RuntimeExecutionContext("],
            FindRuntimeConstructs(sample));
    }

    [Fact]
    public void Runtime_setters_that_change_the_acting_user_or_tenant_are_called_only_in_allow_listed_files()
    {
        var offenders = ProductionSources()
            .SelectMany(source => FindRuntimeSetters(source.Text).Distinct().Select(setter => (source.Path, Setter: setter)))
            .Where(hit => !runtimeSetterAllowList.ContainsKey(hit))
            .Select(hit => $"{hit.Path}: {hit.Setter}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_runtime_setter_allow_list_entry_still_matches_and_names_a_reason()
    {
        var sources = ProductionSources().ToDictionary(source => source.Path, source => source.Text, StringComparer.Ordinal);

        var stale = runtimeSetterAllowList
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value)
                || !sources.TryGetValue(entry.Key.Path, out var text)
                || !FindRuntimeSetters(text).Contains(entry.Key.Setter))
            .Select(entry => $"{entry.Key.Path}: {entry.Key.Setter}");

        Assert.Empty(stale);
    }

    [Fact]
    public void The_runtime_setter_scan_catches_every_call_and_method_group_outside_comments_and_literals()
    {
        const string sample = """
            // runtime.SetUser(forged) in a comment is no call.
            var message = "runtime.StartImpersonation(target, tenant) in a string is none either";
            public void SetUser(Guid? userId, bool isSuperAdmin = false) { }
            await session.StartImpersonationAsync(targetId);
            await session.EndImpersonationAsync();
            _runtime.SetUser(appUser.Id, appUser.IsAdmin);
            runtime . SetBaseTenant (tenantId, isPlatformTenant: true);
            ((RuntimeExecutionContext)runtime).StartImpersonation(target.UserId, target.TenantId);
            Action end = runtime.EndImpersonation;
            runtime.ApplySession(new RuntimeSession(userId, true, tenantId, true, null, null));
            """;

        Assert.Equal(["SetUser", "SetBaseTenant", "StartImpersonation", "EndImpersonation", "ApplySession"], FindRuntimeSetters(sample));
    }

    [Fact]
    public void No_constructor_in_services_or_components_takes_a_context_outside_the_allow_list()
    {
        var offenders = ContextConstructorSources()
            .Where(source => !contextConstructorAllowList.ContainsKey(source.Path))
            .SelectMany(source => FindContextConstructors(source.Text).Select(hit => $"{source.Path}: {hit}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_context_constructor_allow_list_entry_still_takes_a_context_and_names_a_reason()
    {
        var sources = ContextConstructorSources().ToDictionary(source => source.Path, source => source.Text, StringComparer.Ordinal);

        var stale = contextConstructorAllowList
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value)
                || !sources.TryGetValue(entry.Key, out var text)
                || FindContextConstructors(text).Count == 0)
            .Select(entry => entry.Key);

        Assert.Empty(stale);
    }

    [Fact]
    public void The_constructor_scan_catches_a_context_taken_by_a_primary_or_an_ordinary_constructor()
    {
        const string sample = """
            public class Allowed(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, Dictionary<string, ApplicationDbContext> byName);
            public static class Extensions
            {
                public static IDisposable? BeginPlatformScope(this ApplicationDbContext context, string reason) => null;
                private IQueryable<ApplicationUser> UsersInScope(ApplicationDbContext context) => context.Users;
            }
            // public class Commented(ApplicationDbContext context);
            public class Primary(ILogger<Primary> logger, ApplicationDbContext context);
            internal sealed record Holder(Staffora.Data.ApplicationDbContext? Context);
            public class Ordinary
            {
                public Ordinary(
                    TimeProvider clock,
                    ApplicationDbContext context)
                {
                }
            }
            """;

        Assert.Equal(["Primary", "Holder", "Ordinary"], FindContextConstructors(sample));
    }

    [Fact]
    public void State_history_entries_are_constructed_only_in_allow_listed_files()
    {
        var offenders = ProductionSources()
            .Where(source => !historyConstructionAllowList.ContainsKey(source.Path))
            .SelectMany(source => FindHistoryConstructions(source.Text).Select(hit => $"{source.Path}: {hit}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_history_allow_list_entry_still_constructs_an_entry_and_names_a_reason()
    {
        var sources = ProductionSources().ToDictionary(source => source.Path, source => source.Text, StringComparer.Ordinal);

        var stale = historyConstructionAllowList
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value)
                || !sources.TryGetValue(entry.Key, out var text)
                || FindHistoryConstructions(text).Count == 0)
            .Select(entry => entry.Key);

        Assert.Empty(stale);
    }

    [Fact]
    public void The_history_scan_catches_every_construction_of_an_entry_outside_comments()
    {
        const string sample = """
            // var commented = new StateHistoryEntry { ActorId = forged };
            /* new StateHistoryEntry() neither */
            public class StateHistoryEntry : TenantEntityBase { }
            List<StateHistoryEntry> entries = [];
            context.StateHistoryEntries.Add(new StateHistoryEntry { ActorId = forged });
            var qualified = new Staffora.Core.Entities.Workflow.StateHistoryEntry();
            StateHistoryEntry targetTyped = new() { ActorId = forged };
            StateHistoryEntry? nullable = new ();
            """;

        Assert.Equal(
            ["new StateHistoryEntry", "new Staffora.Core.Entities.Workflow.StateHistoryEntry", "StateHistoryEntry targetTyped = new", "StateHistoryEntry? nullable = new"],
            FindHistoryConstructions(sample));
    }

    [Fact]
    public void Integration_messages_and_receipts_are_constructed_only_in_allow_listed_files()
    {
        var offenders = ProductionSources()
            .SelectMany(source => FindOutboxRowConstructions(source.Text).Select(hit => (source.Path, Hit: hit)))
            .Where(hit => !outboxRowConstructionAllowList.ContainsKey((hit.Path, TypeOf(hit.Hit))))
            .Select(hit => $"{hit.Path}: {hit.Hit}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_outbox_row_allow_list_entry_still_constructs_its_row_and_names_a_reason()
    {
        var sources = ProductionSources().ToDictionary(source => source.Path, source => source.Text, StringComparer.Ordinal);

        var stale = outboxRowConstructionAllowList
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value)
                || !sources.TryGetValue(entry.Key.Path, out var text)
                || !FindOutboxRowConstructions(text).Any(hit => TypeOf(hit) == entry.Key.Type))
            .Select(entry => $"{entry.Key.Path}: {entry.Key.Type}");

        Assert.Empty(stale);
    }

    [Fact]
    public void The_outbox_row_scan_catches_every_construction_of_a_message_or_a_receipt_outside_comments()
    {
        const string sample = """
            // var commented = new IntegrationReceipt { MessageId = forged };
            /* new IntegrationMessage() neither */
            public class IntegrationReceipt : TenantEntityBase { }
            List<IntegrationMessage> messages = [];
            context.IntegrationReceipts.Add(new IntegrationReceipt { MessageId = forged });
            var qualified = new Staffora.Core.Entities.Integration.IntegrationMessage();
            IntegrationMessage targetTyped = new() { State = IntegrationMessageState.Delivered };
            IntegrationReceipt? nullable = new ();
            """;

        Assert.Equal(
            ["new IntegrationReceipt", "new Staffora.Core.Entities.Integration.IntegrationMessage", "IntegrationMessage targetTyped = new", "IntegrationReceipt? nullable = new"],
            FindOutboxRowConstructions(sample));
    }

    private static List<string> FindOutboxRowConstructions(string source) =>
        OutboxRowConstruction().Matches(WithoutComments(source)).Select(match => Regex.Replace(match.Groups["construction"].Value, @"\s+", " ")).ToList();

    private static string TypeOf(string construction) => construction.Contains("IntegrationReceipt", StringComparison.Ordinal) ? "IntegrationReceipt" : "IntegrationMessage";

    private static List<string> FindHistoryConstructions(string source) =>
        HistoryConstruction().Matches(WithoutComments(source)).Select(match => Regex.Replace(match.Groups["construction"].Value, @"\s+", " ")).ToList();

    private static List<string> FindBoundaryCalls(string source) =>
        BoundaryCall().Matches(WithoutComments(source)).Select(match => match.Groups["call"].Value).ToList();

    private static List<string> FindBulkStatements(string source) =>
        BulkStatement().Matches(CodeOnly(source)).Select(match => match.Groups["call"].Value).ToList();

    private static int FindSaveScopeNames(string source) => SaveScopeName().Count(CodeOnly(source));

    private static List<string> FindUnscopedSaveScopes(string source)
    {
        var text = CodeOnly(source);
        var hits = new List<string>();
        foreach (Match match in SaveScopeAccess().Matches(text))
        {
            var start = text.LastIndexOfAny([';', '{', '}'], match.Index) + 1;
            var prefix = Regex.Replace(text[start..match.Index], @"\s+", " ").Trim();
            var open = NextNonSpace(text, match.Index + match.Length);
            var close = open < text.Length && text[open] == '(' ? ClosingParenthesis(text, open) : -1;
            var after = close < 0 ? -1 : NextNonSpace(text, close + 1);
            var follows = after >= 0 && after < text.Length ? text[after] : '\0';
            var isResource = close >= 0
                && ((SaveScopeDeclaration().IsMatch(prefix) && follows == ';') || (SaveScopeUsingStatement().IsMatch(prefix) && follows == ')'));
            if (!isResource)
            {
                hits.Add($"{prefix}.BeginTransitionSaveScope");
            }
        }

        return hits;
    }

    private static int NextNonSpace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static int ClosingParenthesis(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            depth += text[index] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };
            if (depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static List<string> FindUndisposedContexts(string source)
    {
        var text = WithoutComments(source);
        var hits = new List<string>();
        foreach (Match match in ContextCreation().Matches(text))
        {
            var start = text.LastIndexOfAny([';', '{', '}'], match.Index) + 1;
            var statement = Regex.Replace(text[start..(match.Index + match.Length - 1)], @"\s+", " ").Trim();
            if (!UsingStatement().IsMatch(statement))
            {
                hits.Add(statement);
            }
        }

        return hits;
    }

    private static List<string> FindUnscopedLeases(string source)
    {
        var text = WithoutComments(source);
        var hits = new List<string>();
        foreach (Match match in LeaseCall().Matches(text))
        {
            var start = text.LastIndexOfAny([';', '{', '}'], match.Index) + 1;
            var statement = Regex.Replace(text[start..(match.Index + match.Length - 1)], @"\s+", " ").Trim();
            if (!UsingStatement().IsMatch(statement))
            {
                hits.Add(statement);
            }
        }

        return hits;
    }

    private static List<string> FindRuntimeConstructs(string source) =>
        RuntimeConstruct().Matches(WithoutComments(source))
            .Select(match => match.Value.StartsWith("new", StringComparison.Ordinal) ? "new RuntimeExecutionContext(" : "RuntimeContext =")
            .ToList();

    private static List<string> FindRuntimeSetters(string source) =>
        RuntimeSetter().Matches(CodeOnly(source)).Select(match => match.Groups["setter"].Value).ToList();

    private static List<string> FindContextConstructors(string source)
    {
        var text = WithoutComments(source);
        return PrimaryConstructor().Matches(text).Concat(OrdinaryConstructor().Matches(text))
            .Where(match => ContextParameter().IsMatch(match.Groups["parameters"].Value))
            .OrderBy(match => match.Index)
            .Select(match => match.Groups["name"].Value)
            .ToList();
    }

    private static IEnumerable<(string Path, string Text)> ContextConstructorSources() =>
        ProductionSources().Where(source => source.Path.EndsWith(".cs", StringComparison.Ordinal)
            && (source.Path.StartsWith("Staffora.Services/", StringComparison.Ordinal) || source.Path.StartsWith("Staffora.Blazor/", StringComparison.Ordinal)));

    private static List<string> FindContextInjections(string source) =>
        Injection().Matches(WithoutComments(source))
            .Select(match => match.Groups["type"].Value)
            .Where(type => ContextType().IsMatch(type))
            .ToList();

    private static string WithoutComments(string source) => Comment().Replace(source, string.Empty);

    private static string CodeOnly(string source) =>
        CommentOrLiteral().Replace(source, match => match.Groups["literal"].Success ? "\"\"" : string.Empty);

    private static IEnumerable<(string Path, string Text)> ProductionSources() => SourcesOf(productionProjects);

    private static IEnumerable<(string Path, string Text)> SourcesOf(IReadOnlyCollection<string> projects) =>
        RepoFiles.CommittedTextFiles(".cs", ".razor")
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(source => projects.Any(project => source.Path.StartsWith(project + "/", StringComparison.Ordinal)))
            .Select(source => (source.Path, File.ReadAllText(source.File)));

    [GeneratedRegex(@"\b(?<call>IgnoreQueryFilters|CreateSystemDbContext|BeginSystemScope|BeginPlatformScope|ExecuteSql|FromSql|SqlQuery)\w*")]
    private static partial Regex BoundaryCall();

    [GeneratedRegex(@"\.(?:CreateDbContextAsync|CreateDbContext|CreateSystemDbContext)\s*\(")]
    private static partial Regex ContextCreation();

    [GeneratedRegex(@"^(?:await\s+)?using\b")]
    private static partial Regex UsingStatement();

    [GeneratedRegex(@"\.(?:BeginSystemScope|BeginPlatformScopeFor|BeginPlatformScope)\s*\(")]
    private static partial Regex LeaseCall();

    [GeneratedRegex(@"\b(?<call>ExecuteUpdate|ExecuteDelete)(?:Async)?\b")]
    private static partial Regex BulkStatement();

    [GeneratedRegex(@"\bBeginTransitionSaveScope\b")]
    private static partial Regex SaveScopeName();

    [GeneratedRegex(@"\.\s*BeginTransitionSaveScope\b")]
    private static partial Regex SaveScopeAccess();

    [GeneratedRegex(@"^(?:await\s+)?using\s+[\w.<>?]+\s+\w+\s*=\s*[\w.]+$")]
    private static partial Regex SaveScopeDeclaration();

    [GeneratedRegex(@"^(?:await\s+)?using\s*\(\s*(?:[\w.<>?]+\s+\w+\s*=\s*)?[\w.]+$")]
    private static partial Regex SaveScopeUsingStatement();

    [GeneratedRegex(@"\bRuntimeContext\s*=(?!=)|\bnew\s+RuntimeExecutionContext\s*\(")]
    private static partial Regex RuntimeConstruct();

    [GeneratedRegex(@"\.\s*(?<setter>ApplySession|SetUser|SetBaseTenant|StartImpersonation|EndImpersonation)\b")]
    private static partial Regex RuntimeSetter();

    [GeneratedRegex(@"\b(?:class|record|struct)\s+(?<name>\w+)\s*(?:<[^>]*>)?\s*\((?<parameters>[^)]*)\)")]
    private static partial Regex PrimaryConstructor();

    [GeneratedRegex(@"\b(?:public|private|protected|internal)\s+(?<name>\w+)\s*\((?<parameters>[^)]*)\)\s*(?:\{|:\s*(?:base|this)\b)")]
    private static partial Regex OrdinaryConstructor();

    [GeneratedRegex(@"(?:^|[(,])\s*(?:\[[^\]]*\]\s*)?(?:Staffora\.Data\.)?ApplicationDbContext\??\s+\w+")]
    private static partial Regex ContextParameter();

    [GeneratedRegex(@"@inject\s+(?<type>[\w.<>,]+)|\[Inject\]\s*(?:(?:public|private|protected|internal|required)\s+)*(?<type>[\w.<>,]+)")]
    private static partial Regex Injection();

    [GeneratedRegex(@"DbContext(?:Factory)?\b")]
    private static partial Regex ContextType();

    [GeneratedRegex(@"(?<construction>\bnew\s+(?:[\w.]+\.)?StateHistoryEntry\b|\bStateHistoryEntry\??\s+\w+\s*=\s*new)(?=\s*[({])")]
    private static partial Regex HistoryConstruction();

    [GeneratedRegex(@"(?<construction>\bnew\s+(?:[\w.]+\.)?Integration(?:Message|Receipt)\b|\bIntegration(?:Message|Receipt)\??\s+\w+\s*=\s*new)(?=\s*[({])")]
    private static partial Regex OutboxRowConstruction();

    [GeneratedRegex(@"(?<!:)//[^\n]*|/\*.*?\*/|@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"(?<comment>(?<!:)//[^\n]*|/\*.*?\*/|@\*.*?\*@)|(?<literal>\$*(?<quotes>""{3,}).*?\k<quotes>|\$?@\$?""(?:[^""]|"""")*""|\$?""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])')", RegexOptions.Singleline)]
    private static partial Regex CommentOrLiteral();
}
