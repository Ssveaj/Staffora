using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Execution;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Messaging;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public partial class WorkflowServiceTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public WorkflowServiceTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_transition_locks_its_entity_executes_saves_and_records_its_history()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var actor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Coordinator", cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(actor));
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((TestParentState.Active, false), (stored.State, stored.IsInTransition));
        var entry = Assert.Single(await HistoryOfAsync(id, cancellationToken));
        Assert.Equal(("test-parent-activate", (int)TestParentState.Draft, (int)TestParentState.Active, actor, entry.Id),
            (entry.TransitionKey, entry.FromState, entry.ToState, entry.ActorId, entry.CorrelationId));

        var commands = host.CommandLog;
        var lockStatement = commands.ToList().FindIndex(command => LockStatement().IsMatch(command) && command.Contains("[TestParents]", StringComparison.Ordinal));
        var save = commands.ToList().FindIndex(command => command.Contains("UPDATE [TestParents] SET", StringComparison.Ordinal));
        Assert.InRange(lockStatement, 0, save - 1);
        Assert.Contains(host.ContextLog, message => message.StartsWith("Transition save scope opened for test-parent-activate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_transition_by_target_state_takes_the_one_step_there_and_is_refused_without_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<WorkflowService>();

        var noStep = await service.TransitionEntityToStateAsync<TestParentState, TestParent>(id, TestParentState.Canceled, cancellationToken: cancellationToken);
        var activated = await service.TransitionEntityToStateAsync<TestParentState, TestParent>(id, TestParentState.Active, cancellationToken: cancellationToken);

        Assert.False(noStep.IsSuccess);
        Assert.Contains("has no step to Canceled", noStep.Error, StringComparison.Ordinal);
        Assert.True(activated.IsSuccess, activated.Error);
        Assert.Equal("test-parent-activate", Assert.Single(await HistoryOfAsync(id, cancellationToken)).TransitionKey);
    }

    [Fact]
    public async Task A_transition_is_refused_while_another_transition_holds_the_entity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        await HoldAsync<TestParent>(id, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("is being moved by another step", result.Error, StringComparison.Ordinal);
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((TestParentState.Draft, true), (stored.State, stored.IsInTransition));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
    }

    [Theory]
    [InlineData(nameof(TestParent))]
    [InlineData(nameof(TestChild))]
    public async Task A_cascading_transition_is_refused_while_another_transition_holds_a_candidate_and_releases_every_lock_it_took(string held)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.SettingsEdit);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken, childState: TestChildState.Active);
        var childId = await ChildOfAsync(id, cancellationToken);
        if (held == nameof(TestParent))
        {
            await HoldAsync<TestParent>(id, cancellationToken);
        }
        else
        {
            await HoldAsync<TestChild>(childId, cancellationToken);
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-cancel", WithChildren, comment: "Canceled for the period", cancellationToken: cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("is being moved by another step", result.Error, StringComparison.Ordinal);

        Assert.Equal(held == nameof(TestParent), host.CommandLog.Any(command => LockStatement().IsMatch(command) && command.Contains("[TestParents]", StringComparison.Ordinal)));
        Assert.Contains(host.CommandLog, command => LockStatement().IsMatch(command) && command.Contains("[TestChildren]", StringComparison.Ordinal));
        var parent = await LoadAsync(id, cancellationToken);
        var child = await LoadChildAsync(childId, cancellationToken);
        Assert.Equal((TestParentState.Active, held == nameof(TestParent)), (parent.State, parent.IsInTransition));
        Assert.Equal((TestChildState.Active, held == nameof(TestChild)), (child.State, child.IsInTransition));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task Two_users_running_the_same_transition_at_once_move_the_entity_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var other = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Other", cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using var first = ServiceHost.Create(this.database, scenario.A.Runtime());
        await using var second = ServiceHost.Create(this.database, scenario.A.RuntimeOf(other));
        var results = await Task.WhenAll(
            first.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken),
            second.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(await HistoryOfAsync(id, cancellationToken));
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((TestParentState.Active, false), (stored.State, stored.IsInTransition));
    }

    [Fact]
    public async Task The_batched_query_answers_every_step_from_the_state_in_one_call_with_one_read_of_the_entity_and_of_the_permissions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Holder", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, holder, cancellationToken, RolePermission.SettingsEdit);
        var draft = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var active = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);

        await using var owner = ServiceHost.Create(this.database, scenario.A.Runtime());
        var ofDraft = (await owner.Get<WorkflowService>().GetAvailableTransitionsAsync<TestParentState, TestParent>(draft, WithChildren, cancellationToken)).Value!;
        var unnamed = (await owner.Get<WorkflowService>().GetAvailableTransitionsAsync<TestParentState, TestParent>(draft, cancellationToken: cancellationToken)).Value!;
        var ofActiveForOwner = (await owner.Get<WorkflowService>().GetAvailableTransitionsAsync<TestParentState, TestParent>(active, WithChildren, cancellationToken)).Value!;

        await using var granted = ServiceHost.Create(this.database, scenario.A.RuntimeOf(holder));
        var ofActiveForHolder = (await granted.Get<WorkflowService>().GetAvailableTransitionsAsync<TestParentState, TestParent>(active, WithChildren, cancellationToken)).Value!;

        Assert.Equal(
            ["test-parent-delete", "test-parent-save", "test-parent-file", "test-parent-stamp", "test-parent-annotate", "test-parent-activate"],
            ofDraft.Select(answer => answer.Transition.Key));
        Assert.All(ofDraft.Where(answer => answer.Transition.Key is "test-parent-delete" or "test-parent-save" or "test-parent-activate"), answer => Assert.True(answer.CanRun, string.Join(" ", answer.BlockingReasons)));
        Assert.Equal(
            [("test-parent-file", false, true), ("test-parent-stamp", false, true), ("test-parent-annotate", true, false)],
            ofDraft.Where(answer => answer.Transition.Key is "test-parent-file" or "test-parent-stamp" or "test-parent-annotate").Select(answer => (answer.Transition.Key, answer.MayRun, answer.BlockingReasons.Count == 0)));
        Assert.Equal(ofDraft.Select(answer => (answer.Transition.Key, answer.CanRun)), unnamed.Select(answer => (answer.Transition.Key, answer.CanRun)));

        Assert.Equal((false, true), (ofActiveForOwner.Single(answer => answer.Transition.Key == "test-parent-cancel").MayRun, ofActiveForHolder.Single(answer => answer.Transition.Key == "test-parent-cancel").CanRun));

        Assert.Single(granted.CommandLog, command => command.Contains("FROM [TestParents]", StringComparison.Ordinal) && !command.Contains("[TestParentAttributes]", StringComparison.Ordinal));
        Assert.Single(granted.CommandLog, command => command.Contains("FROM [TenantRolePermissions]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_with_a_required_permission_runs_only_for_a_holder_of_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Holder", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, holder, cancellationToken, RolePermission.SettingsEdit);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);

        await using var owner = ServiceHost.Create(this.database, scenario.A.Runtime());
        var refused = await owner.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-cancel", WithChildren, comment: "Canceled for the period", cancellationToken: cancellationToken);
        Assert.False(refused.IsSuccess);
        Assert.Equal("The step 'Canceled' needs the permission 'Edit Settings', which you do not have.", refused.Error);
        Assert.Equal(TestParentState.Active, (await LoadAsync(id, cancellationToken)).State);

        await using var granted = ServiceHost.Create(this.database, scenario.A.RuntimeOf(holder));
        var canceled = await granted.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-cancel", WithChildren, comment: "Canceled for the period", cancellationToken: cancellationToken);
        Assert.True(canceled.IsSuccess, canceled.Error);
        Assert.Equal(TestParentState.Canceled, (await LoadAsync(id, cancellationToken)).State);
    }

    [Fact]
    public async Task Edits_sent_with_a_step_other_than_the_save_are_judged_by_the_saves_conditions_too()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var filer = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Filer", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, filer, cancellationToken, TestParent.FilePermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, notes: "Plain");
        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(filer));
        var workflows = host.Get<WorkflowService>();

        var refusedEdit = await UpdaterAsync(scenario.A, id, cancellationToken);
        refusedEdit.Entity.Notes = TestParent.RefusedBySave;
        var refused = await workflows.SaveAndTransitionAsync<TestParentState, TestParent>(refusedEdit, "test-parent-file", cancellationToken: cancellationToken);
        Assert.False(refused.IsSuccess);
        Assert.Equal(TestParent.NotesTheSaveTakes, refused.Error);
        Assert.Equal("Plain", (await LoadAsync(id, cancellationToken)).Notes);
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));

        var takenEdit = await UpdaterAsync(scenario.A, id, cancellationToken);
        takenEdit.Entity.Notes = "Filed";
        var filed = await workflows.SaveAndTransitionAsync<TestParentState, TestParent>(takenEdit, "test-parent-file", cancellationToken: cancellationToken);
        Assert.True(filed.IsSuccess, filed.Error);
        Assert.Equal("Filed", (await LoadAsync(id, cancellationToken)).Notes);

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            (await writer.TestParents.SingleAsync(row => row.Id == id, cancellationToken)).Notes = TestParent.RefusedBySave;
            await writer.SaveChangesAsync(cancellationToken);
        }

        var unedited = await UpdaterAsync(scenario.A, id, cancellationToken);
        var filedAgain = await workflows.SaveAndTransitionAsync<TestParentState, TestParent>(unedited, "test-parent-file", cancellationToken: cancellationToken);
        Assert.True(filedAgain.IsSuccess, filedAgain.Error);
        Assert.Equal(["test-parent-file", "test-parent-file"], (await HistoryOfAsync(id, cancellationToken)).Select(entry => entry.TransitionKey));
    }

    [Theory]
    [InlineData("a user without permissions")]
    [InlineData("a holder of every tenant permission")]
    [InlineData("an Admin outside the platform tenant")]
    [InlineData("an impersonating platform owner")]
    public async Task A_step_marked_RequiresPlatformOwner_is_refused_for_every_user_without_platform_reach(string user)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        if (user == "a holder of every tenant permission")
        {
            await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, [.. Enum.GetValues<RolePermission>().Where(permission => permission != RolePermission.None)]);
        }

        if (user == "an Admin outside the platform tenant")
        {
            await TwoTenantScenario.GiveAdminRoleAsync(this.database, scenario.A.OwnerId, cancellationToken);
        }

        IRuntimeExecutionContext runtime = user switch
        {
            "an Admin outside the platform tenant" => await SessionCircuit.ResolveAsync(this.database, scenario.A.OwnerId, AppRoles.Admin),
            "an impersonating platform owner" => Impersonating(platform.PlatformOwnerRuntime(), scenario.A),
            _ => scenario.A.Runtime(),
        };
        var id = await AddParentAsync(scenario.A, TestParentState.Canceled, cancellationToken);

        await using var host = ServiceHost.Create(this.database, runtime);
        var service = host.Get<WorkflowService>();
        var offered = Assert.Single((await service.GetAvailableTransitionsAsync<TestParentState, TestParent>(id, cancellationToken: cancellationToken)).Value!, answer => answer.Transition.Key == "test-parent-reopen");
        var result = await service.TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-reopen", cancellationToken: cancellationToken);

        Assert.Equal(("test-parent-reopen", false), (offered.Transition.Key, offered.MayRun));
        Assert.False(result.IsSuccess);
        Assert.Contains("Only a platform owner", result.Error, StringComparison.Ordinal);
        Assert.Equal(TestParentState.Canceled, (await LoadAsync(id, cancellationToken)).State);
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_platform_owner_runs_a_step_marked_RequiresPlatformOwner_on_Tenant_A_and_what_it_writes_there_is_stamped_with_Tenant_As_system_account()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Canceled, cancellationToken);

        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var service = host.Get<WorkflowService>();
        var offered = Assert.Single((await service.GetAvailableTransitionsAsync<TestParentState, TestParent>(id, cancellationToken: cancellationToken)).Value!, answer => answer.Transition.Key == "test-parent-reopen");
        Assert.True(offered.CanRun, offered.AccessRefusal ?? string.Join(" ", offered.BlockingReasons));

        string[] steps = ["test-parent-reopen", "test-parent-activate", "test-parent-cancel", "test-parent-reopen"];
        foreach (var step in steps)
        {
            var result = await service.TransitionEntityAsync<TestParentState, TestParent>(id, step, comment: step == "test-parent-cancel" ? "Closed by the platform" : null, cancellationToken: cancellationToken);
            Assert.True(result.IsSuccess, $"{step}: {result.Error}");
        }

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((TestParentState.Draft, scenario.A.SystemAccountId), (stored.State, stored.ModifiedById));
        var history = await HistoryOfAsync(id, cancellationToken);
        Assert.Equal(steps.Order(), history.Select(entry => entry.TransitionKey).Order());
        Assert.All(history, entry => Assert.Equal((scenario.A.TenantId, scenario.A.SystemAccountId, platform.OwnerId), (entry.TenantId, entry.CreatedById, entry.ActorId)));
        Assert.Contains(host.ContextLog, message => message.Contains("System scope opened for Platform console: workflow steps of a TestParent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Queued_messages_reach_the_dispatcher_and_after_commit_work_runs_only_after_the_commit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var probe = new CommitProbe(this.database);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => Probe(services, probe));
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        var batch = Assert.Single(((ProbingDispatcher)host.Get<IOutgoingMessageDispatcher>()).Batches);
        var message = Assert.Single(batch.Messages);
        Assert.Equal((TestParent.ActivatedMessage, id), (message.MessageType, message.EntityId));
        Assert.Equal(Assert.Single(await HistoryOfAsync(id, cancellationToken)).Id, batch.HistoryEntryId);

        Assert.Equal(
            [new CommitRecord($"dispatch {TestParent.ActivatedMessage}", id, TestParentState.Active), new CommitRecord("after commit", id, TestParentState.Active)],
            probe.Records);
    }

    [Theory]
    [InlineData(TestParent.FailInAnAction)]
    [InlineData(TestParent.FailAtTheDatabase)]
    public async Task A_rolled_back_transition_dispatches_nothing_and_runs_no_after_commit_work(string failure)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, notes: failure);
        var probe = new CommitProbe(this.database);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => Probe(services, probe));
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("could not be completed, so nothing was changed", result.Error, StringComparison.Ordinal);
        Assert.Empty(((ProbingDispatcher)host.Get<IOutgoingMessageDispatcher>()).Batches);
        Assert.Empty(probe.Records);
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((TestParentState.Draft, false, "Parent"), (stored.State, stored.IsInTransition, stored.Name));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task Another_tenants_entity_reads_as_missing_and_is_not_moved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var theirs = await AddParentAsync(scenario.B, TestParentState.Draft, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<WorkflowService>();
        var transition = await service.TransitionEntityAsync<TestParentState, TestParent>(theirs, "test-parent-activate", cancellationToken: cancellationToken);
        var available = await service.GetAvailableTransitionsAsync<TestParentState, TestParent>(theirs, cancellationToken: cancellationToken);

        Assert.Equal((false, "The TestParent was not found."), (transition.IsSuccess, transition.Error));
        Assert.Equal((false, "The TestParent was not found."), (available.IsSuccess, available.Error));
        Assert.Equal(TestParentState.Draft, (await LoadAsync(theirs, cancellationToken)).State);
    }

    [Fact]
    public async Task SaveEntityAsync_saves_an_updater_and_returns_a_refusal_as_a_failed_result()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<WorkflowService>();
        var notes = await UpdaterAsync(scenario.A, id, cancellationToken);
        notes.Entity.Notes = "Saved through the service";
        var name = await UpdaterAsync(scenario.A, id, cancellationToken);
        name.Entity.Name = "Renamed while Active";

        var saved = await service.SaveEntityAsync(notes, cancellationToken);
        var refused = await service.SaveEntityAsync(name, cancellationToken);

        Assert.True(saved.IsSuccess, saved.Error);
        Assert.False(refused.IsSuccess);
        Assert.Contains("Name cannot be changed while the TestParent is Active", refused.Error, StringComparison.Ordinal);
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(("Parent", "Saved through the service"), (stored.Name, stored.Notes));
    }

    [Fact]
    public async Task An_AsNoTracking_configurator_still_moves_saves_and_releases_the_entity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", query => query.AsNoTracking(), cancellationToken: cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((TestParentState.Active, false), (stored.State, stored.IsInTransition));
        Assert.Single(await HistoryOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_configurator_that_hands_back_an_untracked_entity_is_refused_before_anything_is_locked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        static IQueryable<TestParent> Projected(IQueryable<TestParent> query) =>
            query.Select(parent => new TestParent { Id = parent.Id, TenantId = parent.TenantId, Name = parent.Name, State = parent.State });

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<WorkflowService>();
        var result = await service.TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", Projected, cancellationToken: cancellationToken);
        var available = await service.GetAvailableTransitionsAsync<TestParentState, TestParent>(id, Projected, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("loaded without change tracking", result.Error, StringComparison.Ordinal);
        Assert.Equal((false, result.Error), (available.IsSuccess, available.Error));
        Assert.DoesNotContain(host.CommandLog, command => LockStatement().IsMatch(command));
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((TestParentState.Draft, false), (stored.State, stored.IsInTransition));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_lock_candidate_the_context_does_not_track_is_refused_before_any_lock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var (folder, _) = await AddFolderAsync(scenario.A, cancellationToken, TestDocumentState.Draft);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestFolderState, TestFolder>(folder, "test-folder-touch-a-stranger", cancellationToken: cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("names a TestDocument that was not loaded with change tracking", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(host.CommandLog, command => LockStatement().IsMatch(command));
        await using var system = this.database.CreateSystemContext();
        Assert.False((await system.TestFolders.AsNoTracking().SingleAsync(row => row.Id == folder, cancellationToken)).IsInTransition);
        Assert.Empty(await HistoryOfAsync(folder, cancellationToken));
    }

    [Theory]
    [InlineData("a holder of the folder's permission only", TestDocumentState.Draft)]
    [InlineData("a holder of every tenant permission", TestDocumentState.Sealed)]
    public async Task A_cascade_is_refused_when_the_user_may_not_take_a_step_it_runs_on_a_child(string user, TestDocumentState documentState)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Holder", cancellationToken);
        RolePermission[] permissions = user == "a holder of every tenant permission"
            ? [.. Enum.GetValues<RolePermission>().Where(permission => permission != RolePermission.None)]
            : [TestFolder.DeletePermission];
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, holder, cancellationToken, permissions);

        var runtime = scenario.A.RuntimeOf(holder);
        var (folder, documents) = await AddFolderAsync(scenario.A, cancellationToken, documentState);
        var refusedStep = documentState == TestDocumentState.Sealed ? "test-document-purge" : "test-document-delete";

        await using var host = ServiceHost.Create(this.database, runtime);
        var service = host.Get<WorkflowService>();
        var offered = Assert.Single(
            (await service.GetAvailableTransitionsAsync<TestFolderState, TestFolder>(folder, cancellationToken: cancellationToken)).Value!,
            answer => answer.Transition.Key == "test-folder-delete");
        var result = await service.TransitionEntityAsync<TestFolderState, TestFolder>(folder, "test-folder-delete", cancellationToken: cancellationToken);

        Assert.False(offered.MayRun);
        Assert.Contains($"also takes the step '{(documentState == TestDocumentState.Sealed ? "Purge" : "Delete")}' on a TestDocument", offered.AccessRefusal, StringComparison.Ordinal);
        Assert.Equal((false, offered.AccessRefusal), (result.IsSuccess, result.Error));
        Assert.DoesNotContain(host.CommandLog, command => LockStatement().IsMatch(command));
        await using var system = this.database.CreateSystemContext();
        var storedFolder = await system.TestFolders.AsNoTracking().SingleAsync(row => row.Id == folder, cancellationToken);
        var storedDocuments = await system.TestDocuments.AsNoTracking().Where(row => documents.Contains(row.Id)).ToListAsync(cancellationToken);
        Assert.Equal((TestFolderState.Draft, false), (storedFolder.State, storedFolder.IsInTransition));
        Assert.Equal([(documentState, false)], storedDocuments.Select(row => (row.State, row.IsInTransition)));
        Assert.Empty(await HistoryOfAsync(folder, cancellationToken));
    }

    [Fact]
    public async Task A_platform_owner_runs_a_cascade_through_a_childs_step_marked_RequiresPlatformOwner_in_Tenant_A()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var (folder, documents) = await AddFolderAsync(scenario.A, cancellationToken, TestDocumentState.Sealed);

        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestFolderState, TestFolder>(folder, "test-folder-delete", cancellationToken: cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        var locks = host.CommandLog.Where(command => LockStatement().IsMatch(command)).ToList();
        Assert.Equal(2, locks.Count);
        Assert.All(locks, command => Assert.Contains("[TenantId]", command, StringComparison.Ordinal));
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.TestFolders.AnyAsync(row => row.Id == folder, cancellationToken));
        Assert.False(await system.TestDocuments.AnyAsync(row => documents.Contains(row.Id), cancellationToken));
        var history = await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == folder || documents.Contains(entry.EntityId)).ToListAsync(cancellationToken);
        Assert.Equal(["test-document-purge", "test-folder-delete"], history.Select(entry => entry.TransitionKey).Order());
        Assert.All(history, entry => Assert.Equal((scenario.A.SystemAccountId, platform.OwnerId), (entry.CreatedById, entry.ActorId)));
    }

    [Fact]
    public async Task A_cascade_runs_for_a_holder_of_every_step_it_takes_with_one_read_of_the_permissions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Holder", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, holder, cancellationToken, TestFolder.DeletePermission, TestDocument.DeletePermission);
        var (folder, documents) = await AddFolderAsync(scenario.A, cancellationToken, TestDocumentState.Draft, TestDocumentState.Draft);

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(holder));
        var service = host.Get<WorkflowService>();
        var offered = Assert.Single(
            (await service.GetAvailableTransitionsAsync<TestFolderState, TestFolder>(folder, cancellationToken: cancellationToken)).Value!,
            answer => answer.Transition.Key == "test-folder-delete");

        Assert.True(offered.CanRun, offered.AccessRefusal ?? string.Join(" ", offered.BlockingReasons));
        Assert.Single(host.CommandLog, command => command.Contains("FROM [TenantRolePermissions]", StringComparison.Ordinal));

        var result = await service.TransitionEntityAsync<TestFolderState, TestFolder>(folder, "test-folder-delete", cancellationToken: cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Single(host.CommandLog, command => command.Contains("FROM [TenantRolePermissions]", StringComparison.Ordinal));
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.TestFolders.AnyAsync(row => row.Id == folder, cancellationToken));
        Assert.False(await system.TestDocuments.AnyAsync(row => documents.Contains(row.Id), cancellationToken));
        Assert.Equal(3, await system.StateHistoryEntries.CountAsync(entry => entry.EntityId == folder || documents.Contains(entry.EntityId), cancellationToken));
    }

    [Theory]
    [InlineData(TestFolder.SealsItsDocuments)]
    [InlineData(TestFolder.AddsADocument)]
    public async Task A_step_an_action_puts_into_the_cascade_after_the_gate_judged_it_is_refused_and_nothing_changes(string folderName)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Holder", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, holder, cancellationToken, TestDocument.DeletePermission);

        var (folder, documents) = folderName == TestFolder.SealsItsDocuments
            ? await AddFolderAsync(scenario.A, folderName, cancellationToken, TestDocumentState.Draft)
            : await AddFolderAsync(scenario.A, folderName, cancellationToken);
        var runtime = folderName == TestFolder.SealsItsDocuments ? scenario.A.RuntimeOf(holder) : scenario.A.Runtime();
        var refusedStep = folderName == TestFolder.SealsItsDocuments ? "test-document-purge" : "test-document-delete";

        await using var host = ServiceHost.Create(this.database, runtime);
        var service = host.Get<WorkflowService>();
        var offered = Assert.Single(
            (await service.GetAvailableTransitionsAsync<TestFolderState, TestFolder>(folder, cancellationToken: cancellationToken)).Value!,
            answer => answer.Transition.Key == "test-folder-delete-unchecked");
        var result = await service.TransitionEntityAsync<TestFolderState, TestFolder>(folder, "test-folder-delete-unchecked", cancellationToken: cancellationToken);

        Assert.True(offered.CanRun, offered.AccessRefusal ?? string.Join(" ", offered.BlockingReasons));
        Assert.Equal(
            (false, $"The step 'test-folder-delete-unchecked' reached the step '{refusedStep}' on a TestDocument, which was not checked before it ran, so nothing was changed."),
            (result.IsSuccess, result.Error));
        await using var system = this.database.CreateSystemContext();
        var storedFolder = await system.TestFolders.AsNoTracking().SingleAsync(row => row.Id == folder, cancellationToken);
        var storedDocuments = await system.TestDocuments.AsNoTracking().Where(row => row.FolderId == folder).ToListAsync(cancellationToken);
        Assert.Equal((TestFolderState.Draft, false), (storedFolder.State, storedFolder.IsInTransition));
        Assert.Equal(documents.Select(_ => (TestDocumentState.Draft, false)), storedDocuments.Select(row => (row.State, row.IsInTransition)));
        Assert.Empty(await system.StateHistoryEntries.Where(entry => entry.EntityId == folder || documents.Contains(entry.EntityId)).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task An_action_that_writes_the_State_of_a_record_it_changes_itself_is_refused_and_nothing_changes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var (folder, documents) = await AddFolderAsync(scenario.A, cancellationToken, TestDocumentState.Draft);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestFolderState, TestFolder>(
            folder,
            "test-folder-seal-the-documents",
            query => query.Include(row => row.Documents),
            cancellationToken: cancellationToken);

        Assert.Equal((false, "The step 'Seal the documents' could not be completed, so nothing was changed."), (result.IsSuccess, result.Error));
        await using var system = this.database.CreateSystemContext();
        Assert.False((await system.TestFolders.AsNoTracking().SingleAsync(row => row.Id == folder, cancellationToken)).IsInTransition);
        var storedDocuments = await system.TestDocuments.AsNoTracking().Where(row => documents.Contains(row.Id)).ToListAsync(cancellationToken);
        Assert.Equal([(TestDocumentState.Draft, false)], storedDocuments.Select(row => (row.State, row.IsInTransition)));
        Assert.Empty(await system.StateHistoryEntries.Where(entry => entry.EntityId == folder || documents.Contains(entry.EntityId)).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task A_lock_candidate_that_is_not_saved_yet_is_refused_with_its_reason_before_any_lock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var (folder, _) = await AddFolderAsync(scenario.A, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestFolderState, TestFolder>(folder, "test-folder-name-a-new-document", cancellationToken: cancellationToken);

        Assert.Equal(
            (false, "The step 'test-folder-name-a-new-document' names a new TestDocument as a record it changes, but a record that is not saved yet cannot be locked, so nothing was changed."),
            (result.IsSuccess, result.Error));
        Assert.DoesNotContain(host.CommandLog, command => LockStatement().IsMatch(command));
        await using var system = this.database.CreateSystemContext();
        Assert.False((await system.TestFolders.AsNoTracking().SingleAsync(row => row.Id == folder, cancellationToken)).IsInTransition);
        Assert.False(await system.TestDocuments.AnyAsync(row => row.FolderId == folder, cancellationToken));
        Assert.Empty(await HistoryOfAsync(folder, cancellationToken));
    }

    [Fact]
    public async Task SaveEntityAsync_refuses_a_new_child_inserted_already_held_as_a_failed_result()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        DetachedEntityUpdater<TestParent> updater;
        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = await context.TestParents.AsNoTracking().Include(row => row.Children).SingleAsync(row => row.Id == id, cancellationToken);
            updater = new DetachedEntityUpdater<TestParent>(parent, context, new DetachedEntityUpdaterOptions<TestParent>().WithCollection(row => row.Children));
        }

        updater.Entity.Children.Add(new TestChild { Name = "Held from the start", IsInTransition = true });
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var result = await host.Get<WorkflowService>().SaveEntityAsync(updater, cancellationToken);

        Assert.Equal((false, "The state of the TestChild changes only through a transition."), (result.IsSuccess, result.Error));
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.TestChildren.AnyAsync(row => row.ParentId == id, cancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveEntityAsync_answers_an_unexpected_failure_with_a_failed_result_and_rethrows_only_the_callers_cancellation(bool callerCancels)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);
        var updater = await UpdaterAsync(scenario.A, id, cancellationToken);
        updater.Entity.Notes = "Never saved";
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), interceptors: [new FailingSave(callerCancels ? caller : null)]);
        var service = host.Get<WorkflowService>();

        if (callerCancels)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveEntityAsync(updater, caller.Token));
        }
        else
        {
            var result = await service.SaveEntityAsync(updater, caller.Token);
            Assert.Equal((false, "The TestParent could not be saved, so nothing was changed. Reload it and try again."), (result.IsSuccess, result.Error));
        }

        Assert.Null((await LoadAsync(id, cancellationToken)).Notes);
    }

    [Fact]
    public async Task A_condition_that_throws_comes_back_as_a_failed_result_that_tells_nothing_of_it_and_blocks_only_its_own_step()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, notes: TestParent.FailInACondition);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<WorkflowService>();
        var result = await service.TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken);
        var available = await service.GetAvailableTransitionsAsync<TestParentState, TestParent>(id, cancellationToken: cancellationToken);

        Assert.Equal((false, "The step 'Active' could not be completed, so nothing was changed."), (result.IsSuccess, result.Error));

        Assert.True(available.IsSuccess, available.Error);
        var answers = available.Value!.ToDictionary(answer => answer.Transition.Key);
        Assert.Equal([WorkflowService.StepCannotBeChecked], answers["test-parent-activate"].BlockingReasons);
        Assert.Empty(answers["test-parent-save"].BlockingReasons);
        Assert.Empty(answers["test-parent-delete"].BlockingReasons);
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((TestParentState.Draft, false), (stored.State, stored.IsInTransition));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_timeout_the_caller_did_not_ask_for_comes_back_as_a_failed_result()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), interceptors: [new TimingOutLock()]);
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken);

        Assert.Equal((false, "The step 'Active' could not be completed, so nothing was changed."), (result.IsSuccess, result.Error));
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((TestParentState.Draft, false), (stored.State, stored.IsInTransition));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_caller_cancelling_after_the_commit_still_gets_its_messages_dispatched_its_after_commit_work_run_and_success()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var probe = new CommitProbe(this.database);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services =>
        {
            services.AddSingleton(probe);
            services.AddSingleton(caller);
            services.AddScoped<IOutgoingMessageDispatcher, CancellingDispatcher>();
        });
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: caller.Token);

        Assert.True(caller.IsCancellationRequested);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(
            [new CommitRecord($"dispatch {TestParent.ActivatedMessage}", id, TestParentState.Active), new CommitRecord("after commit", id, TestParentState.Active)],
            probe.Records);
    }

    [Fact]
    public async Task A_delete_cleanup_registered_for_a_base_type_runs_for_every_entity_of_a_type_derived_from_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestFolder.DeletePermission, TestDocument.DeletePermission);
        var (folder, documents) = await AddFolderAsync(scenario.A, cancellationToken, TestDocumentState.Draft, TestDocumentState.Draft);
        var cleanup = new RecordingCleanup(typeof(TenantStateEntityBase<TestDocumentState>));

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddSingleton<IDeleteCleanup>(cleanup));
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestFolderState, TestFolder>(folder, "test-folder-delete", cancellationToken: cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(documents.Order(), cleanup.Removed.Order());
    }

    [Fact]
    public async Task Entities_materialized_as_a_subclass_the_model_does_not_map_lock_as_their_entity_type_in_one_statement()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestFolder.DeletePermission, TestDocument.DeletePermission);
        var (folder, documents) = await AddFolderAsync(scenario.A, cancellationToken, TestDocumentState.Draft, TestDocumentState.Draft);
        var proxies = new DocumentProxies();

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), interceptors: [proxies]);
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestFolderState, TestFolder>(folder, "test-folder-delete", cancellationToken: cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(2, proxies.Created);
        Assert.Single(host.CommandLog, command => LockStatement().IsMatch(command) && command.Contains("[TestDocuments]", StringComparison.Ordinal));
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.TestDocuments.AnyAsync(row => documents.Contains(row.Id), cancellationToken));
        Assert.Equal(
            [nameof(TestDocument), nameof(TestDocument)],
            await system.StateHistoryEntries.Where(entry => documents.Contains(entry.EntityId)).Select(entry => entry.EntityType).ToListAsync(cancellationToken));
    }

    private static IQueryable<TestParent> WithChildren(IQueryable<TestParent> query) => query.Include(parent => parent.Children).ThenInclude(child => child.Grandchildren);

    private static void Probe(IServiceCollection services, CommitProbe probe)
    {
        services.AddSingleton(probe);
        services.AddScoped<IOutgoingMessageDispatcher, ProbingDispatcher>();
    }

    private static RuntimeExecutionContext Impersonating(RuntimeExecutionContext owner, ScenarioTenant tenant)
    {
        owner.StartImpersonation(tenant.OwnerId, tenant.TenantId);
        return owner;
    }

    private async Task<DetachedEntityUpdater<TestParent>> UpdaterAsync(ScenarioTenant tenant, Guid id, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateContext(tenant.Runtime());
        return new DetachedEntityUpdater<TestParent>(await context.TestParents.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken), context);
    }

    private async Task<Guid> AddParentAsync(ScenarioTenant tenant, TestParentState state, CancellationToken cancellationToken, string? notes = null, TestChildState? childState = null)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());

        using var lease = writer.BeginSystemScope("test: insert a parent as a transition left it");
        var parent = new TestParent { Name = "Parent", Notes = notes, State = state };
        if (childState is { } child)
        {
            parent.Children.Add(new TestChild { Name = "Child", State = child });
        }

        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private Task<(Guid Folder, Guid[] Documents)> AddFolderAsync(ScenarioTenant tenant, CancellationToken cancellationToken, params TestDocumentState[] documentStates) =>
        AddFolderAsync(tenant, "Folder", cancellationToken, documentStates);

    private async Task<(Guid Folder, Guid[] Documents)> AddFolderAsync(ScenarioTenant tenant, string name, CancellationToken cancellationToken, params TestDocumentState[] documentStates)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());

        using var lease = writer.BeginSystemScope("test: insert a folder's documents as their steps left them");
        var folder = new TestFolder { Name = name, Documents = [.. documentStates.Select(state => new TestDocument { Name = "Document", State = state })] };
        writer.TestFolders.Add(folder);
        await writer.SaveChangesAsync(cancellationToken);
        return (folder.Id, [.. folder.Documents.Select(document => document.Id)]);
    }

    private async Task<Guid> ChildOfAsync(Guid parentId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestChildren.Where(row => row.ParentId == parentId).Select(row => row.Id).SingleAsync(cancellationToken);
    }

    private async Task HoldAsync<TRow>(Guid id, CancellationToken cancellationToken)
        where TRow : class
    {
        await using var other = this.database.CreateSystemContext();
        await other.Set<TRow>().Where(row => EF.Property<Guid>(row, "Id") == id).ExecuteUpdateAsync(set => set.SetProperty(row => EF.Property<bool>(row, "IsInTransition"), true), cancellationToken);
    }

    private async Task<TestChild> LoadChildAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestChildren.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken);
    }

    private async Task<TestParent> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestParents.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken);
    }

    private async Task<List<StateHistoryEntry>> HistoryOfAsync(Guid entityId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == entityId).ToListAsync(cancellationToken);
    }

    private sealed class TimingOutLock : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            LockStatement().IsMatch(command.CommandText)
                ? throw new TaskCanceledException("A command timeout, as SqlClient reports one.")
                : ValueTask.FromResult(result);
    }

    private sealed class FailingSave : SaveChangesInterceptor
    {
        private readonly CancellationTokenSource? caller;

        public FailingSave(CancellationTokenSource? caller)
        {
            this.caller = caller;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (this.caller is null)
            {
                throw new InvalidOperationException("The save failed in a way nobody planned for.");
            }

            await this.caller.CancelAsync();
            throw new OperationCanceledException(this.caller.Token);
        }
    }

    private sealed class CancellingDispatcher : IOutgoingMessageDispatcher
    {
        private readonly CommitProbe probe;
        private readonly CancellationTokenSource caller;

        public CancellingDispatcher(CommitProbe probe, CancellationTokenSource caller)
        {
            this.probe = probe;
            this.caller = caller;
        }

        public async Task DispatchAsync(IReadOnlyList<OutgoingMessage> messages, Guid historyEntryId, CancellationToken cancellationToken)
        {
            await this.caller.CancelAsync();
            foreach (var message in messages)
            {
                await this.probe.RecordAsync($"dispatch {message.MessageType}", message.EntityId, cancellationToken);
            }
        }
    }

    private sealed class RecordingCleanup : IDeleteCleanup
    {
        private readonly ConcurrentQueue<Guid> removed = new();
        private readonly Type entityType;

        public RecordingCleanup(Type entityType)
        {
            this.entityType = entityType;
        }

        public IReadOnlyList<Guid> Removed => [.. this.removed];

        public Type EntityType => this.entityType;

        public IReadOnlyList<Type> Dependents => [];

        public Task BeforeRemoveAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
        {
            this.removed.Enqueue(entity.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class DocumentProxies : IMaterializationInterceptor
    {
        private int created;

        public int Created => this.created;

        public InterceptionResult<object> CreatingInstance(MaterializationInterceptionData materializationData, InterceptionResult<object> result)
        {
            if (materializationData.EntityType.ClrType != typeof(TestDocument))
            {
                return result;
            }

            Interlocked.Increment(ref this.created);
            return InterceptionResult<object>.SuppressWithResult(new ProxiedDocument());
        }
    }

    private sealed class ProxiedDocument : TestDocument;

    [GeneratedRegex(@"^UPDATE \[\w+\]\s+SET \[\w+\]\.\[IsInTransition\] = ")]
    private static partial Regex LockStatement();
}
