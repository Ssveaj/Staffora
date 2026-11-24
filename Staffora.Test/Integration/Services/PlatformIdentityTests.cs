using Staffora.Services.Platform;
using Staffora.Core.Lists;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Entities.Workflow;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class PlatformIdentityTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public PlatformIdentityTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task An_Admin_outside_the_platform_tenant_has_no_platform_reach()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var platformMember = await TwoTenantScenario.AddUserAsync(this.database, platform, "Member", cancellationToken);
        await TwoTenantScenario.GiveAdminRoleAsync(this.database, scenario.A.OwnerId, cancellationToken);

        var adminOfA = await SessionCircuit.ResolveAsync(this.database, scenario.A.OwnerId, AppRoles.Admin);
        var memberClaimingAdmin = await SessionCircuit.ResolveAsync(this.database, platformMember, AppRoles.Admin);
        var owner = await SessionCircuit.ResolveAsync(this.database, platform.OwnerId, AppRoles.Admin);

        Assert.Equal((true, false, false), (adminOfA.IsSuperAdmin, adminOfA.IsInPlatformTenant, adminOfA.HasPlatformReach()));
        Assert.Equal((false, true, false), (memberClaimingAdmin.IsSuperAdmin, memberClaimingAdmin.IsInPlatformTenant, memberClaimingAdmin.HasPlatformReach()));
        Assert.Equal((true, true, true), (owner.IsSuperAdmin, owner.IsInPlatformTenant, owner.HasPlatformReach()));

        await using (var host = ServiceHost.Create(this.database, adminOfA))
        {
            Assert.False(await host.Get<PermissionService>().HasPermissionAsync(RolePermission.UsersView, cancellationToken));
            Assert.Equal(PlatformConsoleService.ConsoleOnly, (await host.Get<PlatformConsoleService>().QueryTenantsAsync(new ListQuery())).Error);
            Assert.DoesNotContain(host.ContextLog, message => message.Contains("Platform console", StringComparison.Ordinal));
        }

        await using (var host = ServiceHost.Create(this.database, owner))
        {
            Assert.True(await host.Get<PermissionService>().HasPermissionAsync(RolePermission.UsersView, cancellationToken));
            var console = host.Get<PlatformConsoleService>();
            foreach (var (tenantId, ownerId) in new[] { (scenario.A.TenantId, scenario.A.OwnerId), (scenario.B.TenantId, scenario.B.OwnerId), (platform.TenantId, platform.OwnerId) })
            {
                Assert.Contains(ownerId, (await console.QueryTenantUsersAsync(tenantId, new ListQuery())).Value!.Rows.Select(user => user.Id));
            }
        }
    }

    [Fact]
    public async Task A_platform_owner_suspends_Tenant_A_through_WorkflowService_while_an_Admin_outside_the_platform_tenant_and_a_holder_of_every_permission_are_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Holder", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, holder, cancellationToken, [.. Enum.GetValues<RolePermission>().Where(permission => permission != RolePermission.None)]);
        await TwoTenantScenario.GiveAdminRoleAsync(this.database, scenario.A.OwnerId, cancellationToken);

        foreach (var refusedUser in new[] { scenario.A.OwnerId, holder })
        {
            var runtime = await SessionCircuit.ResolveAsync(this.database, refusedUser, AppRoles.Admin);
            await using var host = ServiceHost.Create(this.database, runtime);
            var refused = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(scenario.A.TenantId, "tenant-suspend", cancellationToken: cancellationToken);

            Assert.False(refused.IsSuccess);
            Assert.Equal("Only a platform owner may take the step 'Suspend'.", refused.Error);
        }

        Assert.Equal(TenantState.Active, await StateOfAsync(scenario.A.TenantId, cancellationToken));
        Assert.Empty(await HistoryOfAsync(scenario.A.TenantId, cancellationToken));

        var owner = await SessionCircuit.ResolveAsync(this.database, platform.OwnerId);
        await using (var host = ServiceHost.Create(this.database, owner))
        {
            var suspended = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(scenario.A.TenantId, "tenant-suspend", cancellationToken: cancellationToken);
            Assert.True(suspended.IsSuccess, suspended.Error);
        }

        Assert.Equal(TenantState.Suspended, await StateOfAsync(scenario.A.TenantId, cancellationToken));
        var entry = Assert.Single(await HistoryOfAsync(scenario.A.TenantId, cancellationToken));
        Assert.Equal(
            ("tenant-suspend", (int)TenantState.Active, (int)TenantState.Suspended, scenario.A.TenantId, scenario.A.SystemAccountId, platform.OwnerId),
            (entry.TransitionKey, entry.FromState, entry.ToState, entry.TenantId, entry.CreatedById, entry.ActorId));
        Assert.Equal(TenantState.Active, await StateOfAsync(scenario.B.TenantId, cancellationToken));
    }

    private async Task<TenantState> StateOfAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Tenants.Where(tenant => tenant.Id == tenantId).Select(tenant => tenant.State).SingleAsync(cancellationToken);
    }

    private async Task<List<StateHistoryEntry>> HistoryOfAsync(Guid entityId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == entityId).ToListAsync(cancellationToken);
    }
}
