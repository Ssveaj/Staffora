using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Services.Home;
using Staffora.Services.Authorization;
using Staffora.Services.Customers;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class PortalEditionTests : IClassFixture<RealDatabase>
{
    private static readonly RolePermission[] StaffingWork = [RolePermission.UsersCreate, RolePermission.RolesEdit, RolePermission.CustomersView, RolePermission.CustomersManage, RolePermission.JobsView, RolePermission.PerformCoordination];

    private readonly RealDatabase database;

    public PortalEditionTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_customer_administrators_permissions_are_capped_by_the_edition_even_when_a_role_names_more()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Ceiling Staffing", Ct);

        await NameMoreOnTheCustomerAdminRoleAsync(scenario.PortalTenantId, StaffingWork);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Portal with { OwnerId = SystemAccountOf(scenario.PortalTenantId) }, scenario.AdministratorId, Ct, StaffingWork);

        var session = await SessionCircuit.ResolveAsync(this.database, scenario.AdministratorId);
        await using var host = ServiceHost.Create(this.database, session);
        var permissions = host.Get<PermissionService>();
        Assert.Equal(TenantEdition.CustomerPortal, await permissions.GetEditionAsync(Ct));
        Assert.Equal(RolePresets.CustomerAdmin.Permissions.Order(), (await permissions.GetUserPermissionsAsync(Ct)).Order());
        foreach (var permission in StaffingWork)
        {
            Assert.False(await permissions.HasPermissionAsync(permission, Ct), permission.ToString());
        }

        Assert.False(await permissions.HasAnyPermissionInAreaAsync(PermissionArea.Customers, Ct));
        foreach (var permission in RolePermissions.Portal)
        {
            Assert.True(await permissions.HasPermissionAsync(permission, Ct), permission.ToString());
        }
        Assert.Equal(StaffingWork.Order(), (await permissions.MissingAsync(StaffingWork, Ct)).Order());

        await permissions.LoadPermissionsAsync(Ct);
        Assert.False(permissions.HasPermission(RolePermission.CustomersView));
        Assert.True(permissions.HasPermission(RolePermission.CustomerUsersManage));

        Assert.Equal(CustomerService.NoView, (await host.Get<CustomerService>().QueryCustomersAsync(new ListQuery(), Ct)).Error);
        Assert.False((await host.Get<UserService>().CreateApplicationUserAsync(new Staffora.Core.Entities.Identity.ApplicationUser { FirstName = "Al", LastName = "Bee", Email = "al.bee@ceiling.example", UserName = "al.bee@ceiling.example" }, Staffora.Core.Workflow.WorkflowAttributes.ApplicationUserWorkflowAttribute.CreateStep, Ct)).IsSuccess);
        Assert.True((await host.Get<TenantHomeService>().ReadPortalAsync(Ct)).IsSuccess);
    }

    [Fact]
    public async Task A_staffing_companys_system_owner_given_a_portal_permission_holds_none_of_them_and_the_portal_refuses_them()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Staffing Ceiling", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Supplier.Tenant, scenario.Supplier.OwnerId, Ct, [.. RolePermissions.Portal]);

        var session = await SessionCircuit.ResolveAsync(this.database, scenario.Supplier.OwnerId);
        await using var host = ServiceHost.Create(this.database, session);
        var permissions = host.Get<PermissionService>();
        Assert.Equal(TenantEdition.Staffing, await permissions.GetEditionAsync(Ct));
        Assert.Equal(RolePresets.SystemOwner.Permissions.Order(), (await permissions.GetUserPermissionsAsync(Ct)).Order());
        Assert.False(await permissions.HasAnyPermissionAsync(RolePermissions.Portal, Ct));
        Assert.Equal(TenantHomeService.PortalNoView, (await host.Get<TenantHomeService>().ReadPortalAsync(Ct)).Error);

        var tenants = host.Get<TenantRoleService>();
        Assert.Equal(TenantEditions.Ceiling(TenantEdition.Staffing).Order(), (await tenants.GetTenantRoleEditorOptionsAsync(null, Ct)).Value!.Offered.Order());
        var anyRole = (await tenants.GetTenantRolesAsync(scenario.Supplier.TenantId)).Value!.First().Id;
        Assert.DoesNotContain((await tenants.GetTenantRoleEditorOptionsAsync(anyRole, Ct)).Value!.Offered, RolePermissions.IsPortal);
    }

    [Fact]
    public async Task A_platform_owner_impersonating_the_customer_administrator_holds_what_they_hold_capped_the_same_and_the_console_bypasses_it()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Impersonated Staffing", Ct);
        await NameMoreOnTheCustomerAdminRoleAsync(scenario.PortalTenantId, StaffingWork);
        var platformOwner = scenario.Supplier.Platform.OwnerId;

        ClaimsPrincipal cookie;
        await using (var endpoint = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var started = await endpoint.Get<ImpersonationService>().StartAsync(SessionCircuit.Cookie(platformOwner, AppRoles.Admin), scenario.AdministratorId, Ct);
            Assert.True(started.IsSuccess, started.Error);
            cookie = started.Value!;
        }

        var impersonating = await SessionCircuit.ResolveAsync(this.database, cookie);
        Assert.Equal((true, scenario.AdministratorId, (Guid?)scenario.PortalTenantId, false), (impersonating.IsImpersonating, impersonating.EffectiveUserId, impersonating.TenantId, impersonating.Current.HasPlatformReach()));
        await using (var host = ServiceHost.Create(this.database, impersonating))
        {
            var permissions = host.Get<PermissionService>();
            Assert.Equal(TenantEdition.CustomerPortal, await permissions.GetEditionAsync(Ct));
            Assert.Equal(RolePresets.CustomerAdmin.Permissions.Order(), (await permissions.GetUserPermissionsAsync(Ct)).Order());
            Assert.False(await permissions.HasPermissionAsync(RolePermission.CustomersView, Ct));
            Assert.Equal(CustomerService.NoView, (await host.Get<CustomerService>().LoadCustomerUpdaterAsync(scenario.CustomerId, Ct)).Error);
            Assert.True((await host.Get<TenantHomeService>().ReadPortalAsync(Ct)).IsSuccess);
        }

        var console = await SessionCircuit.ResolveAsync(this.database, platformOwner, AppRoles.Admin);
        await using (var host = ServiceHost.Create(this.database, console))
        {
            Assert.True(await host.Get<PermissionService>().HasPermissionAsync(RolePermission.CustomersView, Ct));
            Assert.Equal(TenantEdition.Staffing, await host.Get<PermissionService>().GetEditionAsync(Ct));
            Assert.Equal(TenantHomeService.InsidePortalOnly, (await host.Get<TenantHomeService>().ReadPortalAsync(Ct)).Error);
        }
    }

    [Fact]
    public async Task An_orderer_holds_the_requests_and_no_user_administration_and_a_tenant_whose_row_is_not_found_grants_nothing()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Orderer Staffing", Ct);
        var orderer = await TwoTenantScenario.AddUserAsync(this.database, scenario.Portal with { OwnerId = SystemAccountOf(scenario.PortalTenantId) }, "Olu", Ct);
        await HoldPortalRoleAsync(scenario.PortalTenantId, orderer, RolePresets.Orderer.Name);

        await using (var host = ServiceHost.Create(this.database, await SessionCircuit.ResolveAsync(this.database, orderer)))
        {
            Assert.Equal(RolePresets.Orderer.Permissions.Order(), (await host.Get<PermissionService>().GetUserPermissionsAsync(Ct)).Order());
            var home = await host.Get<TenantHomeService>().ReadPortalAsync(Ct);
            Assert.Equal((true, true, false), (home.Value!.MayFollowRequests, home.Value.MayRequest, home.Value.MayManageUsers));
        }

        await using (var host = ServiceHost.Create(this.database, new RuntimeExecutionContext(Guid.NewGuid(), orderer)))
        {
            Assert.Empty(await host.Get<PermissionService>().GetUserPermissionsAsync(Ct));
        }
    }

    private static Guid SystemAccountOf(Guid tenantId) => Staffora.Core.Entities.Identity.SystemAccount.IdFor(tenantId);

    private async Task NameMoreOnTheCustomerAdminRoleAsync(Guid portalTenantId, IEnumerable<RolePermission> more)
    {
        await using var system = this.database.CreateSystemContext();
        var role = await system.TenantRoles.SingleAsync(row => row.TenantId == portalTenantId && row.Name == RolePresets.CustomerAdmin.Name, Ct);
        foreach (var permission in more)
        {
            system.TenantRolePermissions.Add(new TenantRolePermission { TenantId = portalTenantId, TenantRoleId = role.Id, Permission = permission, CreatedById = SystemAccountOf(portalTenantId) });
        }

        await system.SaveChangesAsync(Ct);
    }

    private async Task HoldPortalRoleAsync(Guid portalTenantId, Guid userId, string roleName)
    {
        await using var system = this.database.CreateSystemContext();
        var role = await system.TenantRoles.SingleAsync(row => row.TenantId == portalTenantId && row.Name == roleName, Ct);
        var root = await Staffora.Data.CompanyRoot.FindAsync(system, portalTenantId, Ct);
        system.OrganizationUnitUserRoles.Add(new Staffora.Core.Entities.Organization.OrganizationUnitUserRole { TenantId = portalTenantId, UserId = userId, TenantRoleId = role.Id, OrganizationUnitId = root!.Value, CreatedById = SystemAccountOf(portalTenantId) });
        await system.SaveChangesAsync(Ct);
    }
}
