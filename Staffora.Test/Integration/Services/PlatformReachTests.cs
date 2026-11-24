using Staffora.Services.Platform;
using Staffora.Core.Lists;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class PlatformReachTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public PlatformReachTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task An_impersonating_platform_owner_sees_only_the_impersonated_tenant_through_the_services()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken,
            RolePermission.TenantView, RolePermission.UsersView, RolePermission.OrganizationUnitsView);
        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);

        await using var host = ServiceHost.Create(this.database, impersonating);
        var tenants = host.Get<TenantService>();
        var users = host.Get<UserService>();
        var organizations = host.Get<OrganizationService>();

        Assert.Equal(scenario.A.TenantId, (await tenants.FetchTenantContentAsync()).Value!.Id);
        Assert.Equal(PlatformConsoleService.ConsoleOnly, (await host.Get<PlatformConsoleService>().QueryTenantsAsync(new ListQuery())).Error);
        Assert.Equal([scenario.A.OwnerId], (await users.QueryUsersAsync(new ListQuery())).Value!.Rows.Select(user => user.Id));
        Assert.False((await users.GetUserByIdAsync(scenario.B.OwnerId)).IsSuccess);
        Assert.Equal(PlatformConsoleService.ConsoleOnly, (await host.Get<PlatformConsoleService>().QueryTenantUsersAsync(scenario.B.TenantId, new ListQuery())).Error);
        Assert.Equal([scenario.A.Name], (await organizations.FetchUnitTreeAsync(scenario.A.TenantId)).Value!.Select(unit => unit.Name));
        Assert.Empty((await organizations.FetchUnitTreeAsync(scenario.B.TenantId)).Value!);
        Assert.DoesNotContain(host.ContextLog, message => message.Contains("System scope opened", StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_the_platform_console_the_services_read_every_tenant_under_a_lease_logged_with_its_reason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);

        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var tenantIds = (await host.Get<PlatformConsoleService>().QueryTenantsAsync(new ListQuery())).Value!.Rows.Select(tenant => tenant.Id).ToList();
        var userIdsOfB = (await host.Get<PlatformConsoleService>().QueryTenantUsersAsync(scenario.B.TenantId, new ListQuery())).Value!.Rows.Select(user => user.Id).ToList();
        var treeOfB = (await host.Get<OrganizationService>().FetchUnitTreeAsync(scenario.B.TenantId)).Value!;

        Assert.Superset(new HashSet<Guid> { scenario.A.TenantId, scenario.B.TenantId }, tenantIds.ToHashSet());
        Assert.DoesNotContain(platform.TenantId, tenantIds);
        Assert.Equal([scenario.B.OwnerId], userIdsOfB);
        Assert.Contains(host.ContextLog, message => message.Contains("System scope opened for Platform console: list a tenant's users on its console", StringComparison.Ordinal));
        Assert.Equal([scenario.B.Name], treeOfB.Select(unit => unit.Name));
        Assert.Contains(host.ContextLog, message => message.Contains("System scope opened for Platform console: list every tenant", StringComparison.Ordinal));
        Assert.Contains(host.ContextLog, message => message.Contains("System scope closed for Platform console: list every tenant", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tenant_user_sees_only_their_own_tenant_and_is_refused_what_their_permissions_do_not_grant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _) = await ArrangeAsync(cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.TenantView, RolePermission.UsersView);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var tenants = host.Get<TenantService>();

        Assert.Equal(scenario.A.TenantId, (await tenants.FetchTenantContentAsync()).Value!.Id);
        Assert.Equal(PlatformConsoleService.ConsoleOnly, (await host.Get<PlatformConsoleService>().QueryTenantsAsync(new ListQuery())).Error);
        Assert.Equal([scenario.A.OwnerId], (await host.Get<UserService>().QueryUsersAsync(new ListQuery())).Value!.Rows.Select(user => user.Id));
        Assert.Equal(PlatformConsoleService.ConsoleOnly, (await host.Get<PlatformConsoleService>().QueryTenantUsersAsync(scenario.B.TenantId, new ListQuery())).Error);

        var roles = await host.Get<TenantRoleService>().GetTenantRolesAsync(scenario.A.TenantId);
        Assert.False(roles.IsSuccess);
        Assert.Equal("You do not have permission to view roles.", roles.Error);
    }

    [Fact]
    public async Task A_user_with_custom_field_values_is_created_only_inside_its_tenant_never_from_the_console_or_another_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);
        var definitionId = await AddGlobalUserFieldAsync(platform.OwnerId, cancellationToken);

        await using (var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var refused = await console.Get<UserService>().CreateUserAsync(NewUser("created.by.console", definitionId), scenario.B.TenantId, "Password123!");
            Assert.Equal(UserService.CreateInOwnTenantOnly, refused.Error);
        }

        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, RolePermission.UsersCreate);
        await using (var tenantB = ServiceHost.Create(this.database, scenario.B.Runtime()))
        {
            var created = await tenantB.Get<UserService>().CreateUserAsync(NewUser("created.in.b", definitionId), scenario.B.TenantId, "Password123!");
            Assert.True(created.IsSuccess, created.Error);
        }

        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersCreate);
        await using (var tenantUser = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var refused = await tenantUser.Get<UserService>().CreateUserAsync(NewUser("created.from.a", definitionId), scenario.B.TenantId, "Password123!");
            Assert.False(refused.IsSuccess);
        }

        await using var system = this.database.CreateSystemContext();
        var stored = system.Users.Where(user => user.Email!.StartsWith("created.")).Select(user => new { user.Email, user.TenantId, Values = user.Attributes.Select(value => value.StringValue).ToList() }).ToList();
        var single = Assert.Single(stored);
        Assert.StartsWith("created.in.b", single.Email);
        Assert.Equal(scenario.B.TenantId, single.TenantId);
        Assert.Equal(["Lyon"], single.Values);
    }

    private async Task<(TwoTenantScenario Scenario, ScenarioTenant Platform)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        return (scenario, platform);
    }

    private async Task<Guid> AddGlobalUserFieldAsync(Guid platformOwnerId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var definition = new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = $"Office {Guid.NewGuid():N}", ValueType = AttributeValueType.Text, CreatedById = platformOwnerId };
        context.ApplicationUserAttributeDefinitions.Add(definition);
        await context.SaveChangesAsync(cancellationToken);
        return definition.Id;
    }

    private static ApplicationUser NewUser(string name, Guid definitionId)
    {
        var email = $"{name}.{Guid.NewGuid():N}@company-b.example";
        return new ApplicationUser
        {
            UserName = email,
            Email = email,
            FirstName = "New",
            LastName = "User",
            Attributes = [new ApplicationUserAttribute { EntityAttributeDefinitionId = definitionId, StringValue = "Lyon" }],
        };
    }
}
