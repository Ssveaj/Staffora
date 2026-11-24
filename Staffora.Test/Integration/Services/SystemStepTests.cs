using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class SystemStepTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public SystemStepTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_public_path_moves_a_staff_record_not_even_for_someone_who_manages_staff()
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct, "Gate Staffing");
        var onboarded = scenario.Onboarded;
        var kim = await scenario.AddPersonAsync(this.database, "Kim", Ct);
        var casey = await scenario.AddPersonAsync(this.database, "Casey", Ct);
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            Assert.True((await host.Get<RoleHoldingService>().AssignUserToRoleAsync(kim, scenario.TeamId, onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
            Assert.True((await host.Get<RoleHoldingService>().AssignUserToRoleAsync(casey, scenario.RootId, onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
        }

        var record = await StaffScenario.RecordOfAsync(this.database, kim, Ct);
        Assert.Equal(ResourceState.Active, record.State);
        var refusal = WorkflowService.SystemDrivenRefusal("Deactivate");

        foreach (var runtime in new[] { onboarded.RuntimeOf(casey), onboarded.OwnerRuntime() })
        {
            await using var host = ServiceHost.Create(this.database, runtime);
            var workflows = host.Get<WorkflowService>();
            Assert.Equal((false, refusal), Answer(await workflows.TransitionEntityAsync<ResourceState, Resource>(record.Id, ResourceWorkflowAttribute.DeactivateStep, cancellationToken: Ct)));
            Assert.Equal((false, refusal), Answer(await workflows.TransitionEntityToStateAsync<ResourceState, Resource>(record.Id, ResourceState.Inactive, cancellationToken: Ct)));

            await using var context = this.database.CreateContext(runtime);
            var loaded = await context.Resources.AsNoTracking().SingleAsync(row => row.Id == record.Id, Ct);
            var updater = new DetachedEntityUpdater<Resource>(loaded, context);
            Assert.Equal((false, refusal), Answer(await workflows.SaveAndTransitionAsync<ResourceState, Resource>(updater, ResourceWorkflowAttribute.DeactivateStep, cancellationToken: Ct)));
        }

        var after = await StaffScenario.RecordOfAsync(this.database, kim, Ct);
        Assert.Equal((ResourceState.Active, scenario.TeamId), (after.State, Assert.Single(after.Memberships).OrganizationUnitId));
        Assert.Equal([ResourceWorkflowAttribute.ActivateStep], (await StaffScenario.HistoryOfAsync(this.database, record.Id, Ct)).Select(entry => entry.TransitionKey));
    }

    [Fact]
    public async Task The_in_transaction_runner_refuses_a_users_step_and_a_record_of_a_held_tenant()
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct, "Runner Staffing");
        var onboarded = scenario.Onboarded;
        var lee = await scenario.AddPersonAsync(this.database, "Lee", Ct);
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            Assert.True((await host.Get<RoleHoldingService>().AssignUserToRoleAsync(lee, scenario.TeamId, onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        }

        var recordId = (await StaffScenario.RecordOfAsync(this.database, lee, Ct)).Id;

        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        await using (var context = host.Get<IApplicationDbContextFactory<ApplicationDbContext>>().CreateDbContext(onboarded.OwnerRuntime()))
        await using (var transaction = await context.Database.BeginTransactionAsync(ApplicationDbContext.OrganizationStructureIsolationLevel, Ct))
        {
            var team = await context.OrganizationUnits.SingleAsync(unit => unit.Id == scenario.TeamId, Ct);
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Get<WorkflowService>().RunSystemStepInTransactionAsync<OrganizationUnitState, OrganizationUnit>(context, team, OrganizationUnitWorkflowAttribute.SaveStep, Ct));
            Assert.Equal($"Only a system-driven step without cascades or a delete runs inside another write; '{OrganizationUnitWorkflowAttribute.SaveStep}' is a user's step, not a system-driven one.", thrown.Message);
        }

        await using (var platform = ServiceHost.Create(this.database, onboarded.Platform.PlatformOwnerRuntime()))
        {
            Assert.True((await platform.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(scenario.TenantId, "tenant-suspend", cancellationToken: Ct)).IsSuccess);
        }

        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        await using (var context = host.Get<IApplicationDbContextFactory<ApplicationDbContext>>().CreateDbContext(onboarded.OwnerRuntime()))
        await using (var transaction = await context.Database.BeginTransactionAsync(ApplicationDbContext.OrganizationStructureIsolationLevel, Ct))
        {
            var record = await context.Resources.SingleAsync(row => row.Id == recordId, Ct);
            var refused = await Assert.ThrowsAsync<SystemStepRefusedException>(() => host.Get<WorkflowService>().RunSystemStepInTransactionAsync<ResourceState, Resource>(context, record, ResourceWorkflowAttribute.DeactivateStep, Ct));
            Assert.Equal([WorkflowService.HeldTenant], refused.Reasons);
        }

        Assert.Equal(ResourceState.Active, (await StaffScenario.RecordOfAsync(this.database, lee, Ct)).State);
        Assert.Single(await StaffScenario.HistoryOfAsync(this.database, recordId, Ct));
    }

    private static (bool IsSuccess, string? Error) Answer(Staffora.Core.Models.Result result) => (result.IsSuccess, result.Error);
}
