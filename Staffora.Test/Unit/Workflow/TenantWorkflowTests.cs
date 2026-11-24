using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Staffora.Core;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class TenantWorkflowTests
{
    public static TheoryData<string, TenantState, TenantState> Edges => new()
    {
        { "tenant-save-setup", TenantState.Setup, TenantState.Setup },
        { "tenant-activate", TenantState.Setup, TenantState.Active },
        { "tenant-save", TenantState.Active, TenantState.Active },
        { "tenant-suspend", TenantState.Active, TenantState.Suspended },
        { "tenant-reactivate", TenantState.Suspended, TenantState.Active },
        { "tenant-schedule-deletion", TenantState.Active, TenantState.PendingDeletion },
        { "tenant-schedule-deletion-suspended", TenantState.Suspended, TenantState.PendingDeletion },
        { "tenant-cancel-deletion", TenantState.PendingDeletion, TenantState.Suspended },
        { "tenant-archive", TenantState.PendingDeletion, TenantState.Archived },
        { "tenant-purged", TenantState.PendingDeletion, TenantState.Archived },
    };

    private static Workflow<TenantState, Tenant> Workflow => WorkflowRegistry.Default.Get<TenantState, Tenant>();

    [Fact]
    public void The_tenant_entity_resolves_its_workflow_with_all_five_states()
    {
        var workflow = new Tenant().GetWorkflow<TenantState, Tenant>();

        Assert.Same(Workflow, workflow);
        Assert.Equal(Enum.GetValues<TenantState>(), workflow.States.Select(state => state.State).Order());
    }

    [Fact]
    public void The_state_values_keep_their_stored_integers()
    {
        Assert.Equal(
            [(TenantState.Setup, 0), (TenantState.Active, 1), (TenantState.Suspended, 2), (TenantState.PendingDeletion, 3), (TenantState.Archived, 4)],
            Enum.GetValues<TenantState>().Select(state => (state, (int)state)));
    }

    [Fact]
    public void The_declared_transitions_are_exactly_the_lifecycle_edges()
    {
        var expected = Edges.Select(row => (row.Data.Item1, row.Data.Item2, row.Data.Item3));

        Assert.Equal(expected, Workflow.Transitions.Select(t => (t.Key, t.From, t.To)));
    }

    [Theory]
    [MemberData(nameof(Edges))]
    public async Task Every_edge_of_the_lifecycle_executes(string key, TenantState from, TenantState to)
    {
        var tenant = NewTenant(from);
        tenant.IsInTransition = true;

        var outcome = await Workflow.ExecuteTransitionAsync(tenant, key, WithSignOut(new RecordingSignOut()), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(to, tenant.State);
        Assert.False(tenant.IsInTransition);
        var step = Assert.Single(outcome.Steps);
        Assert.Equal((key, (Enum)from, (Enum)to), (step.TransitionKey, step.From, step.To));
    }

    [Fact]
    public async Task A_tenant_walks_its_whole_lifecycle_from_setup_to_archived()
    {
        var tenant = NewTenant(TenantState.Setup);
        string[] walk = ["tenant-activate", "tenant-suspend", "tenant-reactivate", "tenant-schedule-deletion", "tenant-cancel-deletion", "tenant-schedule-deletion-suspended", "tenant-archive"];

        foreach (var key in walk)
        {
            await Workflow.ExecuteTransitionAsync(tenant, key, WithSignOut(new RecordingSignOut()), cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(TenantState.Archived, tenant.State);
        Assert.Empty(Workflow.GetTransitionsFrom(TenantState.Archived));
    }

    [Fact]
    public void Every_user_step_is_for_the_platform_owner_only_and_no_tenant_permission_grants_it()
    {
        Assert.All(Workflow.Transitions.Where(transition => transition.Key != TenantWorkflowAttribute.PurgedStep), transition =>
        {
            Assert.True(transition.RequiresPlatformOwner, transition.Key);
            Assert.Null(transition.RequiredPermission);
            Assert.False(transition.IsSystemDriven, transition.Key);
        });
    }

    [Fact]
    public void Only_the_retention_pass_archives_a_purged_tenant_through_its_system_step_from_Pending_deletion()
    {
        var purged = Workflow.GetTransition(TenantWorkflowAttribute.PurgedStep);

        Assert.Equal((TenantState.PendingDeletion, TenantState.Archived, true, false, (string?)null), (purged.From, purged.To, purged.IsSystemDriven, purged.RequiresPlatformOwner, purged.ConfirmationMessage));
        Assert.Null(purged.RequiredPermission);
        Assert.Equal([TenantWorkflowAttribute.PurgedStep], Workflow.Transitions.Where(transition => transition.IsSystemDriven).Select(transition => transition.Key));
        Assert.Null(Staffora.Services.Workflow.WorkflowService.SystemStepProblem(Workflow, purged));
    }

    [Fact]
    public void Setup_and_Active_have_a_save_step_and_the_read_only_states_none()
    {
        var saves = Workflow.Transitions.Where(t => t.IsSelfTransition).ToList();

        Assert.Equal([TenantState.Setup, TenantState.Active], saves.Select(t => t.From));
        Assert.All(saves, save => Assert.Equal(("Save", "save", 20), (save.Label, save.Icon, save.SortOrder)));
    }

    [Fact]
    public void Buttons_go_back_on_the_leading_side_and_forward_on_the_trailing_side()
    {
        Assert.Equal(["tenant-suspend", "tenant-schedule-deletion", "tenant-save"], Workflow.GetTransitionsFrom(TenantState.Active).Select(t => t.Key));
        Assert.Equal(["tenant-schedule-deletion-suspended", "tenant-reactivate"], Workflow.GetTransitionsFrom(TenantState.Suspended).Select(t => t.Key));
        Assert.Equal(["tenant-cancel-deletion", "tenant-archive", "tenant-purged"], Workflow.GetTransitionsFrom(TenantState.PendingDeletion).Select(t => t.Key));
        Assert.All(Workflow.Transitions, t => Assert.Equal(t.SortOrder == 10 ? TransitionPlacement.Leading : TransitionPlacement.Trailing, t.Placement));
    }

    [Fact]
    public void Every_step_that_takes_access_away_asks_for_confirmation()
    {
        string[] destructive = ["tenant-suspend", "tenant-schedule-deletion", "tenant-schedule-deletion-suspended", "tenant-archive"];

        Assert.Equal(destructive, Workflow.Transitions.Where(t => t.ConfirmationMessage is not null).Select(t => t.Key));
    }

    [Theory]
    [InlineData("tenant-suspend", TenantState.Active)]
    [InlineData("tenant-schedule-deletion", TenantState.Active)]
    [InlineData("tenant-schedule-deletion-suspended", TenantState.Suspended)]
    public async Task The_platform_tenant_is_never_suspended_or_scheduled_for_deletion(string key, TenantState from)
    {
        var platform = NewTenant(from);
        platform.IsRootTenant = true;

        Assert.Equal(["The platform tenant is never suspended or deleted"], Workflow.GetBlockingReasons(platform, key, Environments.NothingLoaded()));
        await Assert.ThrowsAsync<TransitionRefusedException>(() => Workflow.ExecuteTransitionAsync(platform, key, Environments.NothingLoaded(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(from, platform.State);
    }

    [Theory]
    [InlineData("tenant-save-setup", TenantState.Setup)]
    [InlineData("tenant-activate", TenantState.Setup)]
    [InlineData("tenant-save", TenantState.Active)]
    public void A_tenant_cannot_be_saved_or_activated_without_a_name(string key, TenantState from)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), State = from, Name = " " };

        Assert.Equal(["Name is required"], Workflow.GetBlockingReasons(tenant, key, WithOwner(true)));
    }

    [Theory]
    [InlineData("tenant-save-setup", TenantState.Setup)]
    [InlineData("tenant-activate", TenantState.Setup)]
    [InlineData("tenant-save", TenantState.Active)]
    public void A_named_tenant_without_a_description_can_be_saved_and_activated(string key, TenantState from)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), State = from, Name = "Company A" };

        Assert.Empty(Workflow.GetBlockingReasons(tenant, key, WithOwner(true)));
    }

    [Fact]
    public async Task Activate_needs_an_active_system_owner_and_fails_closed_without_its_guard()
    {
        var tenant = NewTenant(TenantState.Setup);

        Assert.Equal([Staffora.Core.Workflow.WorkflowAttributes.TenantWorkflowAttribute.ActiveOwnerReason], Workflow.GetBlockingReasons(tenant, "tenant-activate", WithOwner(false)));
        await Assert.ThrowsAsync<TransitionRefusedException>(() => Workflow.ExecuteTransitionAsync(tenant, "tenant-activate", WithOwner(false), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(TenantState.Setup, tenant.State);

        Assert.Empty(Workflow.GetBlockingReasons(tenant, "tenant-activate", WithOwner(true)));
        Assert.Throws<InvalidOperationException>(() => Workflow.GetBlockingReasons(tenant, "tenant-activate", Environments.NothingLoaded()));

        foreach (var transition in Workflow.Transitions.Where(transition => transition.Key != "tenant-activate"))
        {
            Assert.Empty(Workflow.GetBlockingReasons(NewTenant(transition.From), transition.Key, WithOwner(false)));
        }
    }

    [Fact]
    public void A_blocked_activation_is_shown_with_its_reasons()
    {
        Assert.True(Workflow.GetTransition("tenant-activate").ShowWhenBlocked);
    }

    [Fact]
    public void Field_access_follows_the_state()
    {
        Assert.All([TenantState.Setup, TenantState.Active], state =>
        {
            var config = Workflow.GetFieldConfig(state);
            Assert.Equal(FieldAccessFlags.Required, config.GetFieldAccess(e => e.Name));
            Assert.Equal(FieldAccessFlags.ReadWrite, config.GetFieldAccess(e => e.Description));
            Assert.Equal(FieldAccessFlags.ReadOnly, config.GetFieldAccess(e => e.IsRootTenant));
        });

        Assert.All([TenantState.Suspended, TenantState.PendingDeletion, TenantState.Archived], state =>
        {
            var config = Workflow.GetFieldConfig(state);
            Assert.Equal(FieldAccessFlags.ReadOnly, config.GetFieldAccess(e => e.Name));
            Assert.Equal(FieldAccessFlags.ReadOnly, config.GetFieldAccess(e => e.Description));
        });
    }

    [Fact]
    public void The_state_labels_are_English()
    {
        Assert.Equal(
            ["Setup", "Active", "Suspended", "Pending deletion", "Archived"],
            Enum.GetValues<TenantState>().Select(state => Field(state).GetCustomAttribute<DisplayAttribute>()?.Name));
    }

    [Fact]
    public void The_transition_labels_are_English()
    {
        Assert.Equal(
            [
                ("tenant-save-setup", "Save"),
                ("tenant-activate", "Activate"),
                ("tenant-save", "Save"),
                ("tenant-suspend", "Suspend"),
                ("tenant-reactivate", "Reactivate"),
                ("tenant-schedule-deletion", "Schedule deletion"),
                ("tenant-schedule-deletion-suspended", "Schedule deletion"),
                ("tenant-cancel-deletion", "Cancel deletion"),
                ("tenant-archive", "Archive"),
                ("tenant-purged", "Purged"),
            ],
            Workflow.Transitions.Select(t => (t.Key, t.Label)));
    }

    [Fact]
    public void Setup_is_the_entry_state()
    {
        Assert.Equal([TenantState.Setup], Enum.GetValues<TenantState>().Where(state => state.IsEntryState()));
        Assert.DoesNotContain(Enum.GetValues<TenantState>(), state => state.IsDeleteState());
    }

    [Fact]
    public async Task Suspend_and_schedule_deletion_from_Active_sign_every_user_out_in_the_step_and_fail_without_a_sign_out()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var (key, from) in Workflow.Transitions.Select(transition => (transition.Key, transition.From)).ToList())
        {
            var tenant = NewTenant(from);
            var signOut = new RecordingSignOut();
            var outcome = await Workflow.ExecuteTransitionAsync(tenant, key, WithSignOut(signOut), cancellationToken: cancellationToken);
            foreach (var write in outcome.BeforeSave)
            {
                await write(null!, cancellationToken);
            }

            var signsOut = key is "tenant-suspend" or "tenant-schedule-deletion";
            Assert.Equal(signsOut ? [tenant.Id] : [], signOut.Tenants);
        }

        foreach (var key in new[] { "tenant-suspend", "tenant-schedule-deletion" })
        {
            var unregistered = NewTenant(TenantState.Active);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Workflow.ExecuteTransitionAsync(unregistered, key, Environments.NothingLoaded(), cancellationToken: cancellationToken));
        }
    }

    private static Tenant NewTenant(TenantState state) =>
        new() { Id = Guid.NewGuid(), State = state, Name = "Company A", Description = "Staff member staffing in the north" };

    private static FieldInfo Field(TenantState state) => typeof(TenantState).GetField(state.ToString())!;

    private static WorkflowEnvironment WithSignOut(ITenantSignOut signOut) =>
        new(new FakeClock(Environments.Noon), NavigationLoadState.None, new SignOutServices(signOut, new FixedOwnerGuard(true)));

    private static WorkflowEnvironment WithOwner(bool hasOwner) =>
        new(new FakeClock(Environments.Noon), NavigationLoadState.None, new SignOutServices(new RecordingSignOut(), new FixedOwnerGuard(hasOwner)));

    private sealed class FixedOwnerGuard : ITenantOwnerGuard
    {
        private readonly bool hasOwner;

        public FixedOwnerGuard(bool hasOwner)
        {
            this.hasOwner = hasOwner;
        }

        public bool HasActiveSystemOwner(Tenant tenant) => this.hasOwner;
    }

    private sealed class RecordingSignOut : ITenantSignOut
    {
        public List<Guid> Tenants { get; } = [];

        public Task SignOutEveryoneAsync(Microsoft.EntityFrameworkCore.DbContext context, Guid tenantId, CancellationToken cancellationToken)
        {
            Tenants.Add(tenantId);
            return Task.CompletedTask;
        }
    }

    private sealed class SignOutServices : IServiceProvider
    {
        private readonly ITenantSignOut signOut;
        private readonly ITenantOwnerGuard owners;

        public SignOutServices(ITenantSignOut signOut, ITenantOwnerGuard owners)
        {
            this.signOut = signOut;
            this.owners = owners;
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ITenantSignOut) ? this.signOut : serviceType == typeof(ITenantOwnerGuard) ? this.owners : null;
    }
}
