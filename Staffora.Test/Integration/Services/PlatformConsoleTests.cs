using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Integration;
using Staffora.Services.Platform;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class PlatformConsoleTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public PlatformConsoleTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_platform_owner_creates_suspends_and_inspects_a_tenant_through_the_console_services_without_impersonating()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var runtime = platform.PlatformOwnerRuntime();
        await using var host = ServiceHost.Create(this.database, runtime);
        var console = host.Get<PlatformConsoleService>();
        var name = TwoTenantScenario.UniqueName("Northfield Staff");
        var ownerEmail = $"owner.{Guid.NewGuid():N}@northfield.example";

        var onboarded = await host.Get<TenantOnboardingService>().OnboardTenantAsync(new TenantOnboarding(name, "Places staff members in the north", "Robin", "Hale", ownerEmail), cancellationToken);
        Assert.True(onboarded.IsSuccess, onboarded.Error);
        var tenantId = onboarded.Value!.TenantId;

        var listed = await console.QueryTenantsAsync(new ListQuery { Search = name });
        var row = Assert.Single(listed.Value!.Rows);
        Assert.Equal((tenantId, name, TenantState.Setup, 1), (row.Id, row.Name, row.State, row.UserCount));
        Assert.Empty(row.ManagedCustomers);

        var summary = (await console.GetTenantSummaryAsync(tenantId, cancellationToken)).Value!;
        Assert.Equal((name, "Places staff members in the north", TenantState.Setup, 1, 0), (summary.Name, summary.Description, summary.State, summary.ActiveUsers, summary.InactiveUsers));
        var owner = Assert.Single(summary.SystemOwners);
        Assert.Equal((onboarded.Value.OwnerId, "Robin Hale", ownerEmail), (owner.Id, owner.Name, owner.Email));
        var root = Assert.Single(summary.Units);
        Assert.Equal((name, true), (root.Name, root.IsRoot));
        Assert.Equal(RolePresets.Editable.Count + 1, summary.RoleCount);

        var users = (await console.QueryTenantUsersAsync(tenantId, new ListQuery())).Value!;
        var ownerRow = Assert.Single(users.Rows);
        Assert.Equal(onboarded.Value.OwnerId, ownerRow.Id);
        Assert.Equal([$"{RolePresets.SystemOwner.Name} on {name}"], ownerRow.Roles);

        var roles = (await console.QueryTenantRolesAsync(tenantId, new ListQuery())).Value!.Rows.ToList();
        Assert.Equal(
            RolePresets.Editable.Select(preset => preset.Name).Append(RolePresets.SystemOwner.Name).Order(StringComparer.Ordinal),
            roles.Select(role => role.Name).Order(StringComparer.Ordinal));
        var ownerRole = Assert.Single(roles, role => role.IsSystemOwner);
        Assert.Equal((RolePresets.SystemOwner.Permissions.Count, 1), (ownerRole.PermissionCount, ownerRole.HolderCount));

        Assert.True((await host.Get<CustomFieldService>().QueryTenantDefinitionsAsync(tenantId, new ListQuery())).IsSuccess);

        var tenants = host.Get<TenantService>();
        foreach (var step in new[] { "tenant-activate", "tenant-suspend" })
        {
            var updater = (await tenants.LoadTenantUpdaterAsync(tenantId, cancellationToken)).Value!;
            var moved = await tenants.UpdateTenantAndTransitionAsync(updater, step, null, cancellationToken);
            Assert.True(moved.IsSuccess, moved.Error);
        }

        Assert.Equal(TenantState.Suspended, (await console.GetTenantSummaryAsync(tenantId, cancellationToken)).Value!.State);
        Assert.Equal(TenantState.Suspended, Assert.Single((await console.QueryTenantsAsync(new ListQuery { Search = name })).Value!.Rows).State);
        Assert.Equal([onboarded.Value.OwnerId], (await console.QueryTenantUsersAsync(tenantId, new ListQuery())).Value!.Rows.Select(user => user.Id));
        Assert.False(runtime.IsImpersonating);
        Assert.Contains(host.ContextLog, message => message.Contains("System scope opened for Platform console: read a tenant's console overview", StringComparison.Ordinal));
        Assert.Contains(host.ContextLog, message => message.Contains("System scope opened for Platform console: list a tenant's roles on its console", StringComparison.Ordinal));

        var history = (await tenants.GetTenantHistoryAsync(tenantId, StateHistoryQuery.Newest(5), cancellationToken)).Value!;
        var ownerName = await NameOfAsync(platform.OwnerId, cancellationToken);
        Assert.Equal(["tenant-suspend", "tenant-activate", "tenant-save-setup"], history.Steps.Select(step => step.TransitionKey));
        Assert.All(history.Steps, step => Assert.Equal(ownerName + StateHistoryService.PlatformOwnerSuffix, step.Actor));
    }

    [Fact]
    public async Task Every_console_read_refuses_a_session_without_platform_reach_before_it_reads_anything()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, [.. RolePermissions.Grantable]);
        await TwoTenantScenario.GiveAdminRoleAsync(this.database, scenario.B.OwnerId, cancellationToken);
        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);
        var adminOfB = new RuntimeExecutionContext();
        adminOfB.SetUser(scenario.B.OwnerId, isSuperAdmin: true);
        adminOfB.SetBaseTenant(scenario.B.TenantId);
        adminOfB.MarkInitialized();

        foreach (var (who, runtime) in new (string, IRuntimeExecutionContext)[] { ("a tenant's user with every permission", scenario.A.Runtime()), ("an impersonating platform owner", impersonating), ("an Admin outside the platform tenant", adminOfB) })
        {
            await using var host = ServiceHost.Create(this.database, runtime);
            var console = host.Get<PlatformConsoleService>();

            foreach (var tenantId in new[] { scenario.A.TenantId, scenario.B.TenantId })
            {
                Assert.Equal(PlatformConsoleService.ConsoleOnly, (await console.GetTenantSummaryAsync(tenantId, cancellationToken)).Error);
                Assert.Equal(PlatformConsoleService.ConsoleOnly, (await console.QueryTenantUsersAsync(tenantId, new ListQuery())).Error);
                Assert.Equal(PlatformConsoleService.ConsoleOnly, (await console.QueryTenantRolesAsync(tenantId, new ListQuery())).Error);
                Assert.Equal(PlatformConsoleService.ConsoleOnly, (await console.QueryDeadLettersAsync(tenantId, new ListQuery())).Error);
                Assert.Equal(CustomFieldService.ConsoleOnly, (await host.Get<CustomFieldService>().QueryTenantDefinitionsAsync(tenantId, new ListQuery())).Error);
            }

            Assert.Equal(PlatformConsoleService.ConsoleOnly, (await console.GetOverviewAsync(cancellationToken)).Error);
            Assert.Equal(PlatformConsoleService.ConsoleOnly, (await console.QueryTenantsAsync(new ListQuery())).Error);

            Assert.True(host.CommandLog.Count == 0, $"{who}: {string.Join(" | ", host.CommandLog)}");
            Assert.DoesNotContain(host.ContextLog, message => message.Contains("System scope opened", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_tenant_the_platform_does_not_hold_reads_as_not_found_on_every_tab()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var console = host.Get<PlatformConsoleService>();
        var missing = Guid.NewGuid();

        Assert.Equal(PlatformConsoleService.TenantNotFound, (await console.GetTenantSummaryAsync(missing, cancellationToken)).Error);
        Assert.Equal(PlatformConsoleService.TenantNotFound, (await console.QueryTenantUsersAsync(missing, new ListQuery())).Error);
        Assert.Equal(PlatformConsoleService.TenantNotFound, (await console.QueryTenantRolesAsync(missing, new ListQuery())).Error);
        Assert.Equal(PlatformConsoleService.TenantNotFound, (await console.QueryDeadLettersAsync(missing, new ListQuery())).Error);
        Assert.Equal(CustomFieldService.UnknownTenant, (await host.Get<CustomFieldService>().QueryTenantDefinitionsAsync(missing, new ListQuery())).Error);
    }

    [Fact]
    public async Task The_overview_counts_the_tenants_by_state_and_shows_the_newest_errors_and_impersonations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var console = host.Get<PlatformConsoleService>();
        var before = (await console.GetOverviewAsync(cancellationToken)).Value!;

        var added = new Dictionary<TenantState, ScenarioTenant>();
        foreach (var state in Enum.GetValues<TenantState>())
        {
            added[state] = await TwoTenantScenario.AddTenantAsync(this.database, $"Overview {state}", cancellationToken, state);
        }

        await TwoTenantScenario.AddTenantAsync(this.database, "Overview Suspended again", cancellationToken, TenantState.Suspended);

        var now = DateTime.UtcNow;
        var recent = await AddErrorAsync($"Overview error {Guid.NewGuid():N}", now, added[TenantState.Active].TenantId, cancellationToken);
        var old = await AddErrorAsync($"Old overview error {Guid.NewGuid():N}", now.AddDays(-(PlatformConsoleService.RecentErrorDays + 1)), null, cancellationToken);
        var audits = host.Get<ImpersonationAuditWriter>();
        var target = added[TenantState.Active];
        var session = (await audits.RecordStartAsync(platform.OwnerId, new ImpersonationTarget(target.OwnerId, null, null, target.TenantId), endingSessionId: null, cancellationToken)).Value;
        Assert.True((await audits.RecordEndAsync(platform.OwnerId, session, cancellationToken)).IsSuccess);

        var after = (await console.GetOverviewAsync(cancellationToken)).Value!;
        Assert.Equal(Enum.GetValues<TenantState>(), after.TenantsByState.Select(count => count.State));
        Assert.All(after.TenantsByState, count => Assert.Equal(
            before.TenantsByState.Single(previous => previous.State == count.State).Count + (count.State == TenantState.Suspended ? 2 : 1),
            count.Count));
        Assert.Equal(before.TenantCount + 6, after.TenantCount);

        Assert.True(after.RecentErrors.Count <= PlatformConsoleService.RecentCount);
        Assert.Equal((recent, added[TenantState.Active].Name), (after.RecentErrors[0].Id, after.RecentErrors[0].TenantName));
        Assert.DoesNotContain(after.RecentErrors, error => error.Id == old);

        var newest = after.RecentImpersonations[0];
        Assert.Equal((session, await NameOfAsync(platform.OwnerId, cancellationToken), await NameOfAsync(target.OwnerId, cancellationToken), target.Name), (newest.Id, newest.Impersonator, newest.Target, newest.TenantName));
        Assert.NotNull(newest.Ended);
    }

    [Fact]
    public async Task The_overview_reads_each_tenants_work_and_health_and_the_platforms_figures_in_a_fixed_number_of_queries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime(), AssignmentScenario.UseClock);
        var console = host.Get<PlatformConsoleService>();
        var before = (await console.GetOverviewAsync(cancellationToken)).Value!;
        var commandsOfOneRead = host.CommandLog.Count;

        var company = await AssignmentScenario.CreateAsync(this.database, TwoTenantScenario.UniqueName("Overview Staffing"), cancellationToken);
        var (_, thisWeek) = await company.StartedJobAsync(this.database, "Runs this week", new DateOnly(2026, 11, 9), new DateOnly(2026, 12, 18), cancellationToken, ["Main", "Second"], AssignmentScenario.Occurrence(Weekday.Friday, new TimeOnly(8, 0), new TimeOnly(10, 0)));
        await company.StartedJobAsync(this.database, "Begins in January", new DateOnly(2027, 1, 4), new DateOnly(2027, 2, 1), cancellationToken);
        await company.StartedJobAsync(this.database, "Begins on Thursday", new DateOnly(2026, 11, 12), new DateOnly(2026, 12, 18), cancellationToken, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0)));
        var ada = await company.AddStaffAsync(this.database, "Ada", cancellationToken);
        var assigned = await company.AssignAsync(this.database, company.Coordinator, thisWeek[0], ada, cancellationToken);
        Assert.True(assigned.IsSuccess, assigned.Error);

        var portal = await TwoTenantScenario.AddTenantAsync(this.database, TwoTenantScenario.UniqueName("Overview Portal"), cancellationToken);
        await LinkAsync(portal.TenantId, company.TenantId, cancellationToken);
        var now = AssignmentScenario.Clock.GetUtcNow().UtcDateTime;
        await OutboxMessageAsync(company, portal.TenantId, IntegrationMessageState.DeadLettered, now, cancellationToken);
        await OutboxMessageAsync(company, portal.TenantId, IntegrationMessageState.Delivered, now, cancellationToken);
        await AddErrorAsync($"Overview health {Guid.NewGuid():N}", now, company.TenantId, cancellationToken);
        await AddErrorAsync($"Overview platform health {Guid.NewGuid():N}", now, null, cancellationToken);

        var session = (await host.Get<ImpersonationAuditWriter>().RecordStartAsync(platform.OwnerId, new ImpersonationTarget(company.Coordinator, null, null, company.TenantId), endingSessionId: null, cancellationToken)).Value;

        var read = host.CommandLog.Count;
        var after = (await console.GetOverviewAsync(cancellationToken)).Value!;

        Assert.Equal(commandsOfOneRead, host.CommandLog.Count - read);

        var companyRow = Assert.Single(after.Tenants, tenant => tenant.Id == company.TenantId);
        Assert.Equal((1, 3), (companyRow.JobsThisWeek, companyRow.OpenSlots));
        Assert.Equal(await PeopleOfAsync(company.TenantId, cancellationToken), companyRow.Users);
        Assert.DoesNotContain(after.Tenants, tenant => tenant.Id == portal.TenantId);
        Assert.DoesNotContain(after.Tenants, tenant => tenant.Id == platform.TenantId);
        Assert.Equal(after.Tenants.Count, after.TenantCount);

        Assert.Equal(before.ErrorsLastDay + 2, after.ErrorsLastDay);
        Assert.Equal(before.ErrorsLastWeek + 2, after.ErrorsLastWeek);
        Assert.NotNull(after.LastError);
        Assert.Equal(before.DeadLetters + 1, after.DeadLetters);
        Assert.Equal(before.DeliveredToday + 1, after.DeliveredToday);
        Assert.Equal(before.ImpersonationsToday + 1, after.ImpersonationsToday);
        Assert.Equal((session, await NameOfAsync(company.Coordinator, cancellationToken), company.TenantId), (after.OpenImpersonation!.Id, after.OpenImpersonation.Target, Assert.Single(after.RecentTargets, target => target.UserId == company.Coordinator).TenantId));
        Assert.Equal(before.ActiveUsers + await ActivePeopleAsync([company.TenantId], cancellationToken), after.ActiveUsers);
        Assert.Equal(before.TenantCount + 1, after.TenantCount);

        Assert.Contains(host.ContextLog, message => message.Contains("System scope opened for Platform console: read the platform overview", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_console_names_the_platform_owner_who_acted_while_the_tenant_reads_someone_outside_its_organization()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);

        await TwoTenantScenario.MakeSystemOwnerAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken);
        await LockAsync(colleague, cancellationToken);
        var ownerName = await NameOfAsync(platform.OwnerId, cancellationToken);

        await using (var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var users = console.Get<UserService>();
            Assert.True((await users.StepUserAsync(colleague, ApplicationUserWorkflowAttribute.UnlockStep)).IsSuccess);
            var seen = (await users.GetApplicationUserHistoryAsync(colleague, StateHistoryQuery.Newest(1), cancellationToken)).Value!;
            Assert.Equal(ownerName + StateHistoryService.PlatformOwnerSuffix, Assert.Single(seen.Steps).Actor);
        }

        await using var inside = ServiceHost.Create(this.database, scenario.A.Runtime());
        var read = (await inside.Get<UserService>().GetApplicationUserHistoryAsync(colleague, StateHistoryQuery.Newest(1), cancellationToken)).Value!;
        Assert.Equal(StateHistoryService.OutsideActor, Assert.Single(read.Steps).Actor);
        Assert.DoesNotContain(ownerName, read.Steps.Select(step => step.Actor));
    }

    [Fact]
    public async Task The_tenant_list_shows_staffing_companies_naming_the_customer_tenants_they_manage_and_both_consoles_link_each_other()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var family = $"Family {Guid.NewGuid().ToString("N")[..6]}";

        var company = await TwoTenantScenario.AddTenantAsync(this.database, $"{family} Beta Staffing", cancellationToken);
        var customer = await TwoTenantScenario.AddTenantAsync(this.database, $"{family} Alpha Portal", cancellationToken);
        var held = await TwoTenantScenario.AddTenantAsync(this.database, $"{family} Delta Portal", cancellationToken, TenantState.Suspended);
        var other = await TwoTenantScenario.AddTenantAsync(this.database, $"{family} Gamma", cancellationToken);
        await LinkAsync(customer.TenantId, company.TenantId, cancellationToken);
        await LinkAsync(held.TenantId, company.TenantId, cancellationToken);

        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var console = host.Get<PlatformConsoleService>();

        var byName = (await console.QueryTenantsAsync(new ListQuery { Search = family })).Value!.Rows.ToList();
        Assert.Equal([company.TenantId, other.TenantId], byName.Select(row => row.Id));
        Assert.Equal([customer.Name, held.Name], byName[0].ManagedCustomers);
        Assert.Empty(byName[1].ManagedCustomers);

        var descending = (await console.QueryTenantsAsync(new ListQuery { Search = family, Sort = PlatformTenantList.SortName, Descending = true })).Value!.Rows;
        Assert.Equal([other.TenantId, company.TenantId], descending.Select(row => row.Id));
        Assert.Empty((await console.QueryTenantsAsync(new ListQuery { Search = customer.Name })).Value!.Rows);

        var grouped = (await console.QueryTenantsAsync(new ListQuery { Search = family, Group = PlatformTenantList.ByState })).Value!;
        Assert.DoesNotContain(grouped.Sections, section => section.Key == PlatformTenantList.StateValue(TenantState.Suspended));
        var active = Assert.Single(grouped.Sections, section => section.Key == PlatformTenantList.StateValue(TenantState.Active));
        Assert.Equal([company.TenantId, other.TenantId], active.Rows.Select(row => row.Id));

        var companySummary = (await console.GetTenantSummaryAsync(company.TenantId, cancellationToken)).Value!;
        Assert.Equal([new TenantConsoleLink(customer.TenantId, customer.Name, TenantState.Active), new TenantConsoleLink(held.TenantId, held.Name, TenantState.Suspended)], companySummary.ManagedCustomers);
        Assert.Null(companySummary.ManagedBy);
        var customerSummary = (await console.GetTenantSummaryAsync(customer.TenantId, cancellationToken)).Value!;
        Assert.Equal(new TenantConsoleLink(company.TenantId, company.Name, TenantState.Active), customerSummary.ManagedBy);
        Assert.Empty(customerSummary.ManagedCustomers);
        var otherSummary = (await console.GetTenantSummaryAsync(other.TenantId, cancellationToken)).Value!;
        Assert.Equal((0, (TenantConsoleLink?)null), (otherSummary.ManagedCustomers.Count, otherSummary.ManagedBy));
    }

    [Fact]
    public async Task A_dead_letter_for_a_tenant_appears_in_its_console_and_in_no_other_tenants()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var (company, customer) = (scenario.A, scenario.B);
        await LinkAsync(customer.TenantId, company.TenantId, cancellationToken);
        var stranger = await TwoTenantScenario.AddTenantAsync(this.database, TwoTenantScenario.UniqueName("Dead Letter Stranger"), cancellationToken);
        var bystander = await TwoTenantScenario.AddTenantAsync(this.database, TwoTenantScenario.UniqueName("Dead Letter Bystander"), cancellationToken);
        var clock = new ManualClock(new DateTimeOffset(2027, 2, 1, 8, 0, 0, TimeSpan.Zero));
        var probe = new IntegrationOutboxTests.HandlerProbe();

        var failing = await DeadLetterSendAsync(company, customer.TenantId, clock, cancellationToken);
        var unlinked = await DeadLetterSendAsync(company, stranger.TenantId, clock, cancellationToken);
        probe.FailTimes(failing, int.MaxValue);
        await using (var dispatcherHost = ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
            services.AddSingleton(probe);
            services.AddIntegrationHandler<IntegrationOutboxTests.FolderHandler>(DeadLetterKind);
        }))
        {
            var dispatcher = dispatcherHost.Get<IntegrationOutboxDispatcher>();
            for (var attempt = 1; attempt <= IntegrationRetryRules.MaxAttempts; attempt++)
            {
                await dispatcher.RunOnceAsync(cancellationToken);
                clock.Advance(IntegrationRetryRules.RetryAfter(attempt));
            }
        }

        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var console = host.Get<PlatformConsoleService>();

        var sent = (await console.QueryDeadLettersAsync(company.TenantId, new ListQuery())).Value!.Rows.ToList();

        Assert.DoesNotContain(sent, letter => letter.ToString().Contains(DeadLetterPayload, StringComparison.Ordinal));
        Assert.Equal([failing, unlinked], sent.Select(letter => letter.Id));
        var givenUp = sent[0];
        Assert.Equal(
            (DeadLetterKind, true, customer.TenantId, customer.Name, IntegrationRetryRules.MaxAttempts, nameof(InvalidOperationException)),
            (givenUp.Kind, givenUp.Sent, givenUp.OtherTenantId, givenUp.OtherTenant, givenUp.Attempts, givenUp.LastFailure));
        Assert.NotNull(givenUp.GivenUp);
        Assert.Equal((stranger.Name, 0, IntegrationOutboxDispatcher.NotLinkedReason), (sent[1].OtherTenant, sent[1].Attempts, sent[1].LastFailure));
        Assert.Equal(2, (await console.GetTenantSummaryAsync(company.TenantId, cancellationToken)).Value!.DeadLetters);

        var received = Assert.Single((await console.QueryDeadLettersAsync(customer.TenantId, new ListQuery())).Value!.Rows);
        Assert.DoesNotContain(DeadLetterPayload, received.ToString(), StringComparison.Ordinal);
        Assert.Equal((failing, false, company.TenantId, company.Name), (received.Id, received.Sent, received.OtherTenantId, received.OtherTenant));
        Assert.Equal([unlinked], (await console.QueryDeadLettersAsync(stranger.TenantId, new ListQuery())).Value!.Rows.Select(letter => letter.Id));
        Assert.Empty((await console.QueryDeadLettersAsync(customer.TenantId, new ListQuery { Filters = new Dictionary<string, string> { [PlatformDeadLetterList.Direction] = PlatformDeadLetterList.Sent } })).Value!.Rows);
        Assert.Single((await console.QueryDeadLettersAsync(customer.TenantId, new ListQuery { Filters = new Dictionary<string, string> { [PlatformDeadLetterList.Direction] = PlatformDeadLetterList.Received } })).Value!.Rows);
        Assert.Equal(1, (await console.GetTenantSummaryAsync(customer.TenantId, cancellationToken)).Value!.DeadLetters);

        Assert.Empty((await console.QueryDeadLettersAsync(bystander.TenantId, new ListQuery())).Value!.Rows);
        Assert.Equal(0, (await console.GetTenantSummaryAsync(bystander.TenantId, cancellationToken)).Value!.DeadLetters);

        Assert.Contains(host.ContextLog, message => message.Contains("System scope opened for Platform console: list a tenant's dead letters on its console", StringComparison.Ordinal));
        await using var inside = ServiceHost.Create(this.database, customer.Runtime());
        Assert.Equal(PlatformConsoleService.ConsoleOnly, (await inside.Get<PlatformConsoleService>().QueryDeadLettersAsync(customer.TenantId, new ListQuery())).Error);
    }

    private const string DeadLetterKind = "test.dead-letter";

    private const string DeadLetterPayload = "Please bring the compasses to the first occurrence";

    private async Task<Guid> DeadLetterSendAsync(ScenarioTenant from, Guid to, TimeProvider clock, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateContext(from.Runtime(), clock);
        var message = IntegrationOutbox.Enqueue(context, DeadLetterKind, to, Guid.NewGuid(), DeadLetterPayload);
        await context.SaveChangesAsync(cancellationToken);
        return message.Id;
    }

    private async Task LinkAsync(Guid customerTenantId, Guid companyTenantId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET ParentTenantId = {companyTenantId} WHERE Id = {customerTenantId}", cancellationToken);
    }

    private async Task OutboxMessageAsync(AssignmentScenario from, Guid to, IntegrationMessageState state, DateTime finished, CancellationToken cancellationToken)
    {
        Guid id;
        await using (var context = this.database.CreateContext(from.StaffMembers.Onboarded.RuntimeOf(from.Coordinator), AssignmentScenario.Clock))
        {
            id = IntegrationOutbox.Enqueue(context, DeadLetterKind, to, Guid.NewGuid(), DeadLetterPayload).Id;
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var system = this.database.CreateSystemContext();
        await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE IntegrationMessages SET State = {(int)state}, Finished = {finished} WHERE Id = {id}", cancellationToken);
    }

    private async Task<int> PeopleOfAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: count a tenant's people");
        return await system.Users.CountAsync(user => user.TenantId == tenantId && !user.IsSystemAccount, cancellationToken);
    }

    private async Task<int> ActivePeopleAsync(Guid[] tenantIds, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: count active people");
        return await system.Users.CountAsync(user => tenantIds.Contains(user.TenantId) && !user.IsSystemAccount && user.State == UserState.Active, cancellationToken);
    }

    private async Task<Guid> AddErrorAsync(string message, DateTime occurred, Guid? tenantId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        using var lease = context.BeginSystemScope("test: an error-log row");
        var row = new ErrorLog { Occurred = occurred, Level = ErrorLogLevel.Error, Category = nameof(PlatformConsoleTests), Message = message, TenantId = tenantId };
        context.ErrorLogs.Add(row);
        await context.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    private async Task<string> NameOfAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var user = await context.Users.AsNoTracking().SingleAsync(row => row.Id == userId, cancellationToken);
        return $"{user.FirstName} {user.LastName}";
    }

    private async Task LockAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var user = await context.Users.SingleAsync(row => row.Id == userId, cancellationToken);
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(15);
        context.NameEditor(user, SystemAccount.IdFor(user.TenantId));
        await context.SaveChangesAsync(cancellationToken);
    }
}
