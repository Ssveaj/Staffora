using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Execution;
using Staffora.Core.Settings;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public partial class StateHistoryServiceTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public StateHistoryServiceTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_missing_and_another_tenants_id_read_alike_and_no_history_is_read_for_either()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await OwnedScenarioAsync(cancellationToken);
        var theirs = await AddParentAsync(scenario.B, TestParentState.Draft, cancellationToken);
        await MoveAsync(scenario.B.Runtime(), theirs, "test-parent-save", cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var answers = new List<string?>();
        foreach (var id in new[] { Guid.NewGuid(), theirs })
        {
            host.ClearCommandLog();
            var page = await host.Get<StateHistoryService>().GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(10), cancellationToken);
            Assert.False(page.IsSuccess);
            Assert.Null(page.Value);
            Assert.DoesNotContain(host.CommandLog, command => command.Contains("[StateHistoryEntries]", StringComparison.Ordinal));
            answers.Add(page.Error);
        }

        Assert.Equal("The TestParent was not found.", answers[0]);
        Assert.Equal(answers[0], answers[1]);
    }

    [Fact]
    public async Task Only_a_system_owner_and_a_platform_owner_on_the_console_read_a_history_and_anyone_else_is_refused_before_anything_is_read()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        await MoveAsync(scenario.A.Runtime(), id, "test-parent-save", cancellationToken);
        var ordinary = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Robin", cancellationToken);

        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(ordinary, scenario.A.TenantId);
        foreach (var reader in new IRuntimeExecutionContext[] { scenario.A.Runtime(), scenario.A.RuntimeOf(ordinary), impersonating })
        {
            await using var host = ServiceHost.Create(this.database, reader);
            Assert.False(await host.Get<StateHistoryService>().MayReadHistoryAsync(cancellationToken));
            host.ClearCommandLog();
            var refused = await host.Get<StateHistoryService>().GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(10), cancellationToken);
            Assert.Equal((false, StateHistoryService.OwnersOnly), (refused.IsSuccess, refused.Error));
            Assert.DoesNotContain(";", refused.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(host.CommandLog, command => command.Contains("[StateHistoryEntries]", StringComparison.Ordinal) || command.Contains("[TestParents]", StringComparison.Ordinal));
        }

        await TwoTenantScenario.MakeSystemOwnerAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken);
        foreach (var reader in new IRuntimeExecutionContext[] { scenario.A.Runtime(), platform.PlatformOwnerRuntime() })
        {
            await using var host = ServiceHost.Create(this.database, reader);
            Assert.True(await host.Get<StateHistoryService>().MayReadHistoryAsync(cancellationToken));
            var page = await host.Get<StateHistoryService>().GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(10), cancellationToken);
            Assert.True(page.IsSuccess, page.Error);
            Assert.Equal("test-parent-save", Assert.Single(page.Value!.Steps).TransitionKey);
        }

        await TwoTenantScenario.MakeSystemOwnerAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken);
        await using (var other = ServiceHost.Create(this.database, scenario.B.Runtime()))
        {
            Assert.Equal("The TestParent was not found.", (await other.Get<StateHistoryService>().GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(10), cancellationToken)).Error);
        }

        var asOwner = platform.PlatformOwnerRuntime();
        asOwner.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);
        await using (var host = ServiceHost.Create(this.database, asOwner))
        {
            Assert.True((await host.Get<StateHistoryService>().GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(10), cancellationToken)).IsSuccess);
        }
    }

    [Fact]
    public async Task The_entitys_service_reads_the_history_only_for_a_user_who_may_open_the_entity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await OwnedScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        await MoveAsync(scenario.A.Runtime(), id, "test-parent-save", cancellationToken);

        await using (var without = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddScoped<TestParentDialogService>()))
        {
            var refused = await without.Get<TestParentDialogService>().GetTestParentHistoryAsync(id, StateHistoryQuery.Newest(3), cancellationToken);
            Assert.Equal((false, TestParentDialogService.NoAccess), (refused.IsSuccess, refused.Error));
            Assert.DoesNotContain(without.CommandLog, command => command.Contains("[StateHistoryEntries]", StringComparison.Ordinal));
        }

        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.ViewPermission);
        await using var with = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddScoped<TestParentDialogService>());
        var page = await with.Get<TestParentDialogService>().GetTestParentHistoryAsync(id, StateHistoryQuery.Newest(3), cancellationToken);
        Assert.True(page.IsSuccess, page.Error);
        Assert.Equal("test-parent-save", Assert.Single(page.Value!.Steps).TransitionKey);
    }

    [Fact]
    public async Task Steps_come_newest_first_in_one_order_even_when_stamped_in_the_same_instant_and_a_page_at_a_time()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await OwnedScenarioAsync(cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.SettingsEdit);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        var clock = new FakeClock(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
        await using (var mover = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddSingleton<TimeProvider>(clock)))
        {
            var workflows = mover.Get<WorkflowService>();
            Assert.True((await workflows.TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-save", cancellationToken: cancellationToken)).IsSuccess);
            Assert.True((await workflows.TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-save", cancellationToken: cancellationToken)).IsSuccess);
            Assert.True((await workflows.TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken)).IsSuccess);
            Assert.True((await workflows.TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-cancel", comment: "Done", cancellationToken: cancellationToken)).IsSuccess);
        }

        Assert.Single((await HistoryOfAsync(id, cancellationToken)).Select(entry => entry.Created).Distinct());

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var history = host.Get<StateHistoryService>();
        host.ClearCommandLog();
        var first = (await history.GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(10), cancellationToken)).Value!;

        Assert.Contains(host.CommandLog, command => OrderedByStampThenKey().IsMatch(command));
        var again = (await history.GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(10), cancellationToken)).Value!;
        string[] newestFirst = ["test-parent-cancel", "test-parent-activate", "test-parent-save", "test-parent-save"];
        Assert.Equal(newestFirst, first.Steps.Select(step => step.TransitionKey));
        Assert.Equal(first.Steps.Select(step => step.Id), again.Steps.Select(step => step.Id));
        Assert.Equal((4, 0, false, false), (first.TotalCount, first.NewerCount, first.HasNewer, first.HasOlder));
        Assert.Equal(["Canceled", "Active", "Save", "Save"], first.Steps.Select(step => step.StepName));
        Assert.Equal((TestParentState.Active, TestParentState.Canceled), ((TestParentState)first.Steps[0].From, (TestParentState)first.Steps[0].To));

        var page = (await history.GetHistoryAsync<TestParent>(id, new StateHistoryQuery(2, OlderThan: first.Steps[0].Cursor), cancellationToken)).Value!;
        Assert.Equal(first.Steps.Skip(1).Take(2).Select(step => step.Id), page.Steps.Select(step => step.Id));
        Assert.Equal((4, 1, true, true), (page.TotalCount, page.NewerCount, page.HasNewer, page.HasOlder));
        host.ClearCommandLog();
        var newer = (await history.GetHistoryAsync<TestParent>(id, page.Newer(10)!, cancellationToken)).Value!;
        Assert.Contains(host.CommandLog, command => OldestFirstByStampThenKey().IsMatch(command));
        Assert.Equal([first.Steps[0].Id], newer.Steps.Select(step => step.Id));
        Assert.Equal((0, false, true), (newer.NewerCount, newer.HasNewer, newer.HasOlder));

        Assert.Equal(ServiceHost.PlatformTimeZone, first.TimeZoneId);
        Assert.All(first.Steps, step => Assert.Equal(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(2)), step.At));
        Assert.All(first.Steps, step => Assert.Equal(TimeSpan.FromHours(2), step.At.Offset));
    }

    [Fact]
    public async Task A_step_taken_between_two_pages_never_repeats_or_skips_a_step_of_the_next_page()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await OwnedScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        var clock = new FakeClock(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
        await using (var mover = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddSingleton<TimeProvider>(clock)))
        {
            for (var step = 0; step < 25; step++)
            {
                Assert.True((await mover.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-save", cancellationToken: cancellationToken)).IsSuccess);
            }
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var history = host.Get<StateHistoryService>();
        var all = (await history.GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(StateHistoryService.MaxPageSize), cancellationToken)).Value!.Steps.Select(step => step.Id).ToList();
        var newest = (await history.GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(20), cancellationToken)).Value!;
        Assert.Equal(all.Take(20), newest.Steps.Select(step => step.Id));

        await MoveAsync(scenario.A.Runtime(), id, "test-parent-save", cancellationToken, clock);
        var older = (await history.GetHistoryAsync<TestParent>(id, newest.Older(20)!, cancellationToken)).Value!;
        Assert.Equal(all.Skip(20), older.Steps.Select(step => step.Id));
        Assert.Equal((26, 21, false), (older.TotalCount, older.NewerCount, older.HasOlder));

        var back = (await history.GetHistoryAsync<TestParent>(id, older.Newer(20)!, cancellationToken)).Value!;
        Assert.Equal(all.Take(20), back.Steps.Select(step => step.Id));
        Assert.Equal((1, true), (back.NewerCount, back.HasNewer));
    }

    [Fact]
    public async Task A_call_reads_at_most_a_page_of_fifty_and_at_least_one_step_and_never_older_and_newer_at_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await OwnedScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        await AddEntriesAsync(scenario.A, id, StateHistoryService.MaxPageSize + 5, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var history = host.Get<StateHistoryService>();
        var big = (await history.GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(500), cancellationToken)).Value!;
        Assert.Equal((StateHistoryService.MaxPageSize, StateHistoryService.MaxPageSize + 5), (big.Steps.Count, big.TotalCount));
        var none = (await history.GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(0), cancellationToken)).Value!;
        Assert.Single(none.Steps);
        var negative = (await history.GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(-5), cancellationToken)).Value!;
        Assert.Single(negative.Steps);

        var cursor = big.Steps[1].Cursor;
        var both = await history.GetHistoryAsync<TestParent>(id, new StateHistoryQuery(5, OlderThan: cursor, NewerThan: cursor), cancellationToken);
        Assert.False(both.IsSuccess);
    }

    [Fact]
    public async Task Only_the_entitys_own_tenants_entries_are_read_also_by_a_platform_owner_who_reads_every_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await OwnedScenarioAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        await MoveAsync(scenario.A.Runtime(), id, "test-parent-save", cancellationToken);

        await using (var writer = this.database.CreateContext(scenario.B.Runtime()))
        using (writer.BeginSystemScope("test: a foreign entry naming another tenant's entity"))
        {
            writer.StateHistoryEntries.Add(new StateHistoryEntry { EntityType = nameof(TestParent), EntityId = id, TransitionKey = "test-parent-activate", FromState = 0, ToState = 1, ActorId = scenario.B.OwnerId });
            await writer.SaveChangesAsync(cancellationToken);
        }

        foreach (var reader in new IRuntimeExecutionContext[] { scenario.A.Runtime(), platform.PlatformOwnerRuntime() })
        {
            await using var host = ServiceHost.Create(this.database, reader);
            var page = (await host.Get<StateHistoryService>().GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(10), cancellationToken)).Value!;
            Assert.Equal(["test-parent-save"], page.Steps.Select(step => step.TransitionKey));
            Assert.Equal(1, page.TotalCount);
        }
    }

    [Fact]
    public async Task A_step_names_its_actor_only_inside_the_entitys_tenant_and_says_what_the_stamped_columns_record()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await OwnedScenarioAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Canceled, cancellationToken);

        await MoveAsync(platform.PlatformOwnerRuntime(), id, "test-parent-reopen", cancellationToken);
        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);
        await MoveAsync(impersonating, id, "test-parent-save", cancellationToken);
        await MoveAsync(scenario.A.Runtime(), id, "test-parent-save", cancellationToken);
        await MoveAsync(scenario.A.RuntimeOf(scenario.A.SystemAccountId), id, "test-parent-save", cancellationToken);

        var owner = $"Owner {scenario.A.Name}";

        var platformOwner = $"Owner {platform.Name}{StateHistoryService.PlatformOwnerSuffix}";
        foreach (var (reader, outside, foreignNames) in new (IRuntimeExecutionContext, string, string[])[]
        {
            (scenario.A.Runtime(), StateHistoryService.OutsideActor, [platform.Name, scenario.B.Name]),
            (platform.PlatformOwnerRuntime(), platformOwner, [scenario.B.Name]),
        })
        {
            await using var host = ServiceHost.Create(this.database, reader);
            var page = await host.Get<StateHistoryService>().GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(10), cancellationToken);
            Assert.True(page.IsSuccess, page.Error);
            var steps = page.Value!.Steps;

            Assert.Equal(
                [
                    ("test-parent-save", StateHistoryService.SystemAccountName, (string[])[]),
                    ("test-parent-save", owner, []),
                    ("test-parent-save", owner, [StateHistoryService.ImpersonatedNote]),
                    ("test-parent-reopen", outside, [StateHistoryService.OnBehalfNote]),
                ],
                steps.Select(step => (step.TransitionKey, step.Actor, step.ActorNotes.ToArray())));

            var shown = string.Join(" ", steps.SelectMany(step => step.ActorNotes.Append(step.Actor)));
            Assert.All(foreignNames, name => Assert.DoesNotContain(name, shown, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_history_shows_its_times_and_its_today_in_the_entitys_own_tenants_zone_on_the_platform_console_too_and_says_whose_zone_it_is()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await OwnedScenarioAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        await MoveAsync(scenario.A.Runtime(), id, "test-parent-save", cancellationToken, new FakeClock(new DateTimeOffset(2027, 1, 15, 12, 0, 0, TimeSpan.Zero)));

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            var row = await writer.TenantSettings.FirstOrDefaultAsync(setting => setting.Key == TenantSettingDefinitions.TimeZone.Key, cancellationToken);
            if (row is null)
            {
                writer.TenantSettings.Add(new TenantSetting { TenantId = scenario.A.TenantId, Key = TenantSettingDefinitions.TimeZone.Key, Value = "America/New_York" });
            }
            else
            {
                row.Value = "America/New_York";
            }

            await writer.SaveChangesAsync(cancellationToken);
        }

        foreach (var (reader, readersOwn) in new (IRuntimeExecutionContext, bool)[] { (scenario.A.Runtime(), true), (platform.PlatformOwnerRuntime(), false) })
        {
            var lateEvening = new FakeClock(new DateTimeOffset(2027, 1, 16, 3, 0, 0, TimeSpan.Zero));
            await using var host = ServiceHost.Create(this.database, reader, services => services.AddSingleton<TimeProvider>(lateEvening));
            var page = await host.Get<StateHistoryService>().GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(10), cancellationToken);
            Assert.True(page.IsSuccess, page.Error);
            Assert.Equal(("America/New_York", readersOwn), (page.Value!.TimeZoneId, page.Value.InReadersZone));
            Assert.Equal(new DateOnly(2027, 1, 15), page.Value.Today);
            Assert.Equal(new DateTimeOffset(2027, 1, 15, 7, 0, 0, TimeSpan.FromHours(-5)), Assert.Single(page.Value.Steps).At);
        }

        await using (var other = ServiceHost.Create(this.database, scenario.B.Runtime()))
        {
            var refused = await other.Get<TenantClock>().GetTimeZoneOfAsync(scenario.A.TenantId, cancellationToken);
            Assert.Equal(TenantSettingsService.NotYourTenant, refused.Error);
            Assert.Equal(ServiceHost.PlatformTimeZone, (await other.Get<TenantClock>().GetTimeZoneOfAsync(scenario.B.TenantId, cancellationToken)).Value!.Id);
        }

        await using (var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            Assert.Equal("America/New_York", (await console.Get<TenantClock>().GetTimeZoneOfAsync(scenario.A.TenantId, cancellationToken)).Value!.Id);
            Assert.Equal(ServiceHost.PlatformTimeZone, (await console.Get<TenantClock>().GetTimeZoneOfAsync(platform.TenantId, cancellationToken)).Value!.Id);
        }
    }

    [Fact]
    public async Task The_console_names_only_a_platform_owner_not_a_platform_tenant_member_without_Admin_nor_another_tenants_Admin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await OwnedScenarioAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var member = await TwoTenantScenario.AddUserAsync(this.database, platform, "Member", cancellationToken);
        await TwoTenantScenario.GiveAdminRoleAsync(this.database, scenario.B.OwnerId, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        var clock = new FakeClock(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
        foreach (var actor in new[] { platform.OwnerId, member, scenario.B.OwnerId })
        {
            await using var writer = this.database.CreateContext(scenario.A.Runtime(), clock);
            using var lease = writer.BeginSystemScope("test: an entry taken by an outsider");
            writer.StateHistoryEntries.Add(new StateHistoryEntry { EntityType = nameof(TestParent), EntityId = id, TransitionKey = "test-parent-save", ActorId = actor });
            await writer.SaveChangesAsync(cancellationToken);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var page = await host.Get<StateHistoryService>().GetHistoryAsync<TestParent>(id, StateHistoryQuery.Newest(10), cancellationToken);
        Assert.True(page.IsSuccess, page.Error);
        Assert.Equal(
            [StateHistoryService.OutsideActor, StateHistoryService.OutsideActor, $"Owner {platform.Name}{StateHistoryService.PlatformOwnerSuffix}"],
            page.Value!.Steps.Select(step => step.Actor));
    }

    [GeneratedRegex(@"ORDER BY \[\w+\]\.\[Created\] DESC, \[\w+\]\.\[Id\] DESC")]
    private static partial Regex OrderedByStampThenKey();

    [GeneratedRegex(@"ORDER BY \[\w+\]\.\[Created\], \[\w+\]\.\[Id\]")]
    private static partial Regex OldestFirstByStampThenKey();

    private async Task<TwoTenantScenario> OwnedScenarioAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.MakeSystemOwnerAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken);
        await TwoTenantScenario.MakeSystemOwnerAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken);
        return scenario;
    }

    private async Task<Guid> AddParentAsync(ScenarioTenant tenant, TestParentState state, CancellationToken cancellationToken)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        using var lease = writer.BeginSystemScope("test: insert a parent as a transition left it");
        var parent = new TestParent { Name = "Parent", State = state };
        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task AddEntriesAsync(ScenarioTenant tenant, Guid id, int count, CancellationToken cancellationToken)
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
        for (var index = 0; index < count; index++)
        {
            await using var writer = this.database.CreateContext(tenant.Runtime(), clock);
            using var lease = writer.BeginSystemScope("test: history entries as steps left them");
            writer.StateHistoryEntries.Add(new StateHistoryEntry { EntityType = nameof(TestParent), EntityId = id, TransitionKey = "test-parent-save", ActorId = tenant.OwnerId });
            await writer.SaveChangesAsync(cancellationToken);
            clock.Advance(TimeSpan.FromMinutes(1));
        }
    }

    private async Task MoveAsync(IRuntimeExecutionContext runtime, Guid id, string transitionKey, CancellationToken cancellationToken, TimeProvider? clock = null)
    {
        await using var host = ServiceHost.Create(this.database, runtime, clock is null ? null : services => services.AddSingleton(clock));
        var moved = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, transitionKey, cancellationToken: cancellationToken);
        Assert.True(moved.IsSuccess, moved.Error);
    }

    private async Task<List<StateHistoryEntry>> HistoryOfAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == id).ToListAsync(cancellationToken);
    }
}
