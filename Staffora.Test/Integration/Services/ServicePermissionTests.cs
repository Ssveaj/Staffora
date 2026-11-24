using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Settings;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.CustomFields;
using Staffora.Services.Platform;
using Staffora.Services.Services;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class ServicePermissionTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public ServicePermissionTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Editing_a_colleague_requires_UsersEdit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, colleague, _) = await ArrangeAsync(cancellationToken);
        var target = await StoredUserAsync(scenario.A.OwnerId, cancellationToken);

        await GrantAsync(scenario.A, colleague, cancellationToken, RolePermission.UsersView);
        await using (var refusedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            var refused = await refusedHost.Get<UserService>().SaveUserAsync(scenario.A.OwnerId, user => user.FirstName = "Renamed");
            Assert.False(refused.IsSuccess);
            Assert.Equal("You do not have permission to edit users.", refused.Error);
        }

        Assert.Equal(target.FirstName, (await StoredUserAsync(scenario.A.OwnerId, cancellationToken)).FirstName);

        await GrantAsync(scenario.A, colleague, cancellationToken, RolePermission.UsersEdit);
        await using var allowedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        var allowed = await allowedHost.Get<UserService>().SaveUserAsync(scenario.A.OwnerId, user => user.FirstName = "Renamed");
        Assert.True(allowed.IsSuccess, allowed.Error);
        Assert.Equal("Renamed", (await StoredUserAsync(scenario.A.OwnerId, cancellationToken)).FirstName);
    }

    [Fact]
    public async Task Deactivating_a_colleague_requires_UsersEdit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, colleague, _) = await ArrangeAsync(cancellationToken);

        await GrantAsync(scenario.A, colleague, cancellationToken, RolePermission.UsersView);
        await using (var refusedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            var refused = await refusedHost.Get<UserService>().StepUserAsync(scenario.A.OwnerId, ApplicationUserWorkflowAttribute.DeactivateStep);
            Assert.False(refused.IsSuccess);
        }

        Assert.Equal(UserState.Active, (await StoredUserAsync(scenario.A.OwnerId, cancellationToken)).State);

        await GrantAsync(scenario.A, colleague, cancellationToken, RolePermission.UsersEdit);
        await using var allowedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        var allowed = await allowedHost.Get<UserService>().StepUserAsync(scenario.A.OwnerId, ApplicationUserWorkflowAttribute.DeactivateStep);
        Assert.True(allowed.IsSuccess, allowed.Error);
        Assert.Equal(UserState.Inactive, (await StoredUserAsync(scenario.A.OwnerId, cancellationToken)).State);
    }

    [Fact]
    public async Task Giving_a_colleague_a_new_password_requires_UsersEdit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, colleague, _) = await ArrangeAsync(cancellationToken);
        var before = (await StoredUserAsync(scenario.A.OwnerId, cancellationToken)).PasswordHash;

        await GrantAsync(scenario.A, colleague, cancellationToken, RolePermission.UsersView);
        await using (var refusedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            var refused = await refusedHost.Get<UserService>().StepUserAsync(scenario.A.OwnerId, ApplicationUserWorkflowAttribute.NewPasswordStep);
            Assert.False(refused.IsSuccess);
            Assert.Equal("You do not have permission to edit users.", refused.Error);
        }

        Assert.Equal(before, (await StoredUserAsync(scenario.A.OwnerId, cancellationToken)).PasswordHash);

        await GrantAsync(scenario.A, colleague, cancellationToken, RolePermission.UsersEdit);
        await using var allowedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        var allowed = await allowedHost.Get<UserService>().StepUserAsync(scenario.A.OwnerId, ApplicationUserWorkflowAttribute.NewPasswordStep);
        Assert.True(allowed.IsSuccess, allowed.Error);
        Assert.Contains("The new password is ", allowed.Message, StringComparison.Ordinal);
        Assert.NotEqual(before, (await StoredUserAsync(scenario.A.OwnerId, cancellationToken)).PasswordHash);
    }

    [Fact]
    public async Task Creating_a_user_requires_UsersCreate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, colleague, _) = await ArrangeAsync(cancellationToken);

        await using (var refusedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            var refused = await refusedHost.Get<UserService>().CreateUserAsync(NewUser("refused"), scenario.A.TenantId, "Password123!");
            Assert.False(refused.IsSuccess);
            Assert.Equal("You do not have permission to create users.", refused.Error);
        }

        Assert.Equal(0, await CountUsersAsync(scenario.A.TenantId, "refused.", cancellationToken));

        await GrantAsync(scenario.A, colleague, cancellationToken, RolePermission.UsersCreate);
        await using var allowedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        var allowed = await allowedHost.Get<UserService>().CreateUserAsync(NewUser("allowed"), scenario.A.TenantId, "Password123!");
        Assert.True(allowed.IsSuccess, allowed.Error);
        Assert.Equal(1, await CountUsersAsync(scenario.A.TenantId, "allowed.", cancellationToken));
    }

    [Fact]
    public async Task Reading_users_requires_UsersView_except_the_callers_own_profile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, colleague, _) = await ArrangeAsync(cancellationToken);

        await using (var refusedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            var users = refusedHost.Get<UserService>();
            Assert.Equal("You do not have permission to view users.", (await users.QueryUsersAsync(UserList.Definition.Default)).Error);
            Assert.Equal(PlatformConsoleService.ConsoleOnly, (await refusedHost.Get<PlatformConsoleService>().QueryTenantUsersAsync(scenario.A.TenantId, new ListQuery())).Error);
            Assert.False((await users.GetUserByIdAsync(scenario.A.OwnerId)).IsSuccess);
            Assert.True((await users.GetUserByIdAsync(colleague)).IsSuccess);
        }

        await GrantAsync(scenario.A, colleague, cancellationToken, RolePermission.UsersView);
        await using var allowedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        var allowed = allowedHost.Get<UserService>();
        Assert.Contains(scenario.A.OwnerId, (await allowed.QueryUsersAsync(UserList.Definition.Default)).Value!.Rows.Select(user => user.Id));
        Assert.True((await allowed.GetUserByIdAsync(scenario.A.OwnerId)).IsSuccess);
    }

    [Fact]
    public async Task Without_UsersEdit_a_user_changes_nothing_of_their_own_account()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, colleague, _) = await ArrangeAsync(cancellationToken);
        var own = await StoredUserAsync(colleague, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        var users = host.Get<UserService>();

        Assert.False((await users.SaveUserAsync(colleague, user => (user.FirstName, user.LastName) = ("Self", "Named"))).IsSuccess);
        Assert.False((await users.SaveUserAsync(colleague, user => user.Email = "taken.over@company-a.example")).IsSuccess);
        Assert.False((await users.SaveUserAsync(colleague, user => user.UserName = "taken.over@company-a.example")).IsSuccess);
        Assert.False((await users.StepUserAsync(colleague, ApplicationUserWorkflowAttribute.NewPasswordStep)).IsSuccess);
        Assert.False((await users.StepUserAsync(colleague, ApplicationUserWorkflowAttribute.DeactivateStep)).IsSuccess);

        var stored = await StoredUserAsync(colleague, cancellationToken);
        Assert.Equal((own.FirstName, own.LastName, own.Email, own.UserName, own.PasswordHash, UserState.Active), (stored.FirstName, stored.LastName, stored.Email, stored.UserName, stored.PasswordHash, stored.State));
    }

    [Fact]
    public async Task Reading_and_writing_units_requires_the_matching_unit_permission()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, colleague, rootId) = await ArrangeAsync(cancellationToken);
        var unitId = await AddUnitAsync(scenario.A, rootId, cancellationToken);

        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.OrganizationUnitsView);
        await using var ownerHost = ServiceHost.Create(this.database, scenario.A.Runtime());
        var renamed = (await ownerHost.Get<OrganizationService>().LoadOrganizationUnitUpdaterAsync(unitId, cancellationToken)).Value!;
        renamed.Entity.Name = "Refused";

        await using (var refusedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            var organizations = refusedHost.Get<OrganizationService>();
            Assert.Equal("You do not have permission to view organization units.", (await organizations.FetchUnitTreeAsync(scenario.A.TenantId)).Error);
            Assert.Equal("You do not have permission to view organization units.", (await organizations.LoadOrganizationUnitUpdaterAsync(unitId, cancellationToken)).Error);
            Assert.Equal("You do not have permission to view organization units.", (await organizations.QueryUnitsAsync(OrganizationUnitList.Definition.Default)).Error);
            Assert.Equal("You do not have permission to create organization units.", (await organizations.CreateAsync(NewUnit(scenario.A, rootId, "Refused"), cancellationToken)).Error);
            Assert.Equal("You do not have permission to edit organization units.", (await organizations.UpdateOrganizationUnitAndTransitionAsync(renamed, OrganizationUnitWorkflowAttribute.SaveStep, null, cancellationToken)).Error);

            Assert.Equal("You do not have permission to delete organization units.", (await organizations.UpdateOrganizationUnitAndTransitionAsync(renamed, OrganizationUnitWorkflowAttribute.DeleteStep, null, cancellationToken)).Error);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            Assert.False(await system.OrganizationUnits.AnyAsync(row => row.TenantId == scenario.A.TenantId && row.Name == "Refused", cancellationToken));
            Assert.True(await system.OrganizationUnits.AnyAsync(row => row.Id == unitId, cancellationToken));
        }

        await GrantAsync(scenario.A, colleague, cancellationToken, [.. Enum.GetValues<RolePermission>().Where(RolePermissions.IsObsolete)]);
        await using (var obsoleteHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            Assert.False((await obsoleteHost.Get<OrganizationService>().FetchUnitTreeAsync(scenario.A.TenantId)).IsSuccess);
        }

        await GrantAsync(scenario.A, colleague, cancellationToken,
            RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate, RolePermission.OrganizationUnitsEdit);
        await using (var allowedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            var organizations = allowedHost.Get<OrganizationService>();
            Assert.True((await organizations.FetchUnitTreeAsync(scenario.A.TenantId)).IsSuccess);
            Assert.True((await organizations.LoadOrganizationUnitUpdaterAsync(unitId, cancellationToken)).IsSuccess);
            Assert.Contains(unitId, (await organizations.QueryUnitsAsync(OrganizationUnitList.Definition.Default with { Group = null })).Value!.Rows.Select(row => row.Id));
            var created = await organizations.CreateAsync(NewUnit(scenario.A, rootId, "Created unit"), cancellationToken);
            Assert.True(created.IsSuccess, created.Error);
            var edited = await organizations.EditAsync(unitId, unit => unit.Name = "Edited unit", cancellationToken);
            Assert.True(edited.IsSuccess, edited.Error);

            var gate = await allowedHost.Get<Staffora.Services.Workflow.WorkflowService>().TransitionEntityAsync<OrganizationUnitState, OrganizationUnit>(created.Value, OrganizationUnitWorkflowAttribute.DeleteStep, OrganizationUnitWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
            Assert.Contains("Delete Organization Units", gate.Error, StringComparison.Ordinal);
        }

        await using var check = this.database.CreateSystemContext();
        var names = await check.OrganizationUnits.Where(row => row.TenantId == scenario.A.TenantId).Select(row => row.Name).ToListAsync(cancellationToken);
        Assert.Equivalent(new[] { scenario.A.Name, "Created unit", "Edited unit" }, names, strict: true);
    }

    [Fact]
    public async Task Tenants_and_custom_field_definitions_require_their_permission()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, colleague, _) = await ArrangeAsync(cancellationToken);
        var name = $"Refused {Guid.NewGuid():N}";
        Guid stored;
        await using (var system = this.database.CreateSystemContext())
        {
            var field = new ApplicationUserAttributeDefinition { Name = $"Stored {Guid.NewGuid():N}", ValueType = AttributeValueType.Text, TenantId = scenario.A.TenantId, CreatedById = scenario.A.OwnerId, State = CustomFieldState.Active };
            system.Add(field);
            await system.SaveChangesAsync(cancellationToken);
            stored = field.Id;
        }

        IEntityAttributeDefinition NewField() => CustomFieldSteps.New(CustomFieldTarget.User, name);

        await using (var refusedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            var tenants = refusedHost.Get<TenantService>();
            var attributes = refusedHost.Get<CustomFieldService>();
            Assert.False((await tenants.FetchTenantContentAsync()).IsSuccess);
            Assert.Equal(PlatformConsoleService.ConsoleOnly, (await refusedHost.Get<PlatformConsoleService>().QueryTenantsAsync(new ListQuery())).Error);
            Assert.Equal("Only the platform console creates tenants.", (await tenants.CreateTenantAsync(new Tenant { Name = "Refused" }, "tenant-save-setup", cancellationToken)).Error);
            Assert.Equal(CustomFieldService.NoViewPermission, (await attributes.QueryDefinitionsAsync(CustomFieldList.Definition.Default)).Error);
            Assert.Equal(CustomFieldService.NoViewPermission, (await attributes.LoadFieldAsync(CustomFieldTarget.User, stored, cancellationToken)).Error);
            Assert.Equal(CustomFieldService.NoViewPermission, (await attributes.GetApplicationUserAttributeDefinitionEditorOptionsAsync(stored, cancellationToken)).Error);
            Assert.Equal(CustomFieldService.NoViewPermission, (await attributes.GetApplicationUserAttributeDefinitionHistoryAsync(stored, Staffora.Services.Workflow.StateHistoryQuery.Newest(3), cancellationToken)).Error);
            Assert.Equal(CustomFieldService.NoViewPermission, (await attributes.GetDefinitionsAsync<ApplicationUserAttributeDefinition>(scenario.A.TenantId)).Error);
            Assert.Equal(CustomFieldService.NoEditPermission, (await attributes.CreateFieldAsync(CustomFieldTarget.User, NewField(), cancellationToken: cancellationToken)).Error);
            Assert.Equal(CustomFieldService.NoEditPermission, (await attributes.NewApplicationUserAttributeDefinitionAsync(cancellationToken)).Error);
        }

        await GrantAsync(scenario.A, colleague, cancellationToken, RolePermission.SettingsView);
        await using (var viewerHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            var attributes = viewerHost.Get<CustomFieldService>();
            Assert.Contains(stored, (await attributes.QueryDefinitionsAsync(CustomFieldList.Definition.Default)).Value!.Rows.Select(row => row.Id));
            Assert.All((await attributes.QueryDefinitionsAsync(CustomFieldList.Definition.Default)).Value!.Rows, row => Assert.False(row.CanEdit));
            Assert.True((await attributes.GetApplicationUserAttributeDefinitionEditorOptionsAsync(stored, cancellationToken)).Value!.FieldsLocked);
            Assert.Equal(CustomFieldService.NoEditPermission, (await attributes.StepFieldAsync(CustomFieldTarget.User, stored, CustomFieldSteps.SaveActive, field => field.Name = "Renamed by a viewer", cancellationToken)).Error);
            Assert.Equal(CustomFieldService.NoEditPermission, (await attributes.CreateFieldAsync(CustomFieldTarget.User, NewField(), cancellationToken: cancellationToken)).Error);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            Assert.False(await system.ApplicationUserAttributeDefinitions.AnyAsync(row => row.Name == name || row.Name == "Renamed by a viewer", cancellationToken));
            Assert.True(await system.ApplicationUserAttributeDefinitions.AnyAsync(row => row.Id == stored, cancellationToken));
        }

        await GrantAsync(scenario.A, colleague, cancellationToken, RolePermission.TenantView, RolePermission.UsersView, RolePermission.SettingsEdit);
        await using var allowedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        Assert.Equal(scenario.A.TenantId, (await allowedHost.Get<TenantService>().FetchTenantContentAsync()).Value!.Id);
        Assert.Equal(PlatformConsoleService.ConsoleOnly, (await allowedHost.Get<PlatformConsoleService>().QueryTenantsAsync(new ListQuery())).Error);
        var allowed = allowedHost.Get<CustomFieldService>();
        Assert.Contains(stored, (await allowed.GetDefinitionsAsync<ApplicationUserAttributeDefinition>(scenario.A.TenantId)).Value!.Select(row => row.Id));

        Assert.Equal(CustomFieldService.FieldsAreThePlatforms, (await allowed.CreateFieldAsync(CustomFieldTarget.User, NewField(), cancellationToken: cancellationToken)).Error);
        Assert.Equal(CustomFieldService.FieldsAreThePlatforms, (await allowed.StepFieldAsync(CustomFieldTarget.User, stored, CustomFieldSteps.Deactivate, cancellationToken: cancellationToken)).Error);
        Assert.True((await allowed.GetApplicationUserAttributeDefinitionEditorOptionsAsync(stored, cancellationToken)).Value!.FieldsLocked);
        await using var after = this.database.CreateSystemContext();
        Assert.False(await after.ApplicationUserAttributeDefinitions.AnyAsync(row => row.Name == name, cancellationToken));
    }

    [Fact]
    public async Task Tenant_settings_require_their_permission()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, colleague, _) = await ArrangeAsync(cancellationToken);
        var key = TenantSettingDefinitions.TimeZone.Key;

        await using (var refusedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            var settings = refusedHost.Get<TenantSettingsService>();
            Assert.Equal(TenantSettingsService.NoViewPermission, (await settings.LoadAsync(cancellationToken)).Error);
            Assert.Equal(TenantSettingsService.NoEditPermission, (await settings.SaveAsync(key, "Asia/Tokyo", null, cancellationToken)).Error);
            Assert.Equal(ServiceHost.PlatformTimeZone, (await settings.GetAsync(TenantSettingDefinitions.TimeZone, cancellationToken)).Value!.Id);
        }

        await GrantAsync(scenario.A, colleague, cancellationToken, RolePermission.SettingsView);
        await using (var viewerHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            var settings = viewerHost.Get<TenantSettingsService>();
            Assert.False((await settings.LoadAsync(cancellationToken)).Value!.CanEdit);
            Assert.Equal(TenantSettingsService.NoEditPermission, (await settings.SaveAsync(key, "Asia/Tokyo", null, cancellationToken)).Error);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            Assert.False(await system.TenantSettings.AnyAsync(row => row.TenantId == scenario.A.TenantId, cancellationToken));
        }

        await GrantAsync(scenario.A, colleague, cancellationToken, RolePermission.SettingsEdit);
        await using var allowedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        var allowed = allowedHost.Get<TenantSettingsService>();
        Assert.True((await allowed.LoadAsync(cancellationToken)).Value!.CanEdit);
        var saved = await allowed.SaveAsync(key, "Asia/Tokyo", null, cancellationToken);
        Assert.True(saved.IsSuccess, saved.Error);
        Assert.Equal("Asia/Tokyo", (await allowed.GetAsync(TenantSettingDefinitions.TimeZone, cancellationToken)).Value!.Id);
    }

    private async Task<(TwoTenantScenario Scenario, Guid Colleague, Guid RootId)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        return (scenario, colleague, await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken));
    }

    private Task GrantAsync(ScenarioTenant tenant, Guid userId, CancellationToken cancellationToken, params RolePermission[] permissions) =>
        TwoTenantScenario.GrantAsync(this.database, tenant, userId, cancellationToken, permissions);

    private async Task<Guid> AddUnitAsync(ScenarioTenant tenant, Guid rootId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var unit = NewUnit(tenant, rootId, "Unit");
        unit.CreatedById = tenant.OwnerId;
        context.OrganizationUnits.Add(unit);
        await context.SaveChangesAsync(cancellationToken);
        return unit.Id;
    }

    private async Task<ApplicationUser> StoredUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Users.AsNoTracking().SingleAsync(user => user.Id == userId, cancellationToken);
    }

    private async Task<int> CountUsersAsync(Guid tenantId, string emailPrefix, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Users.CountAsync(user => user.TenantId == tenantId && user.Email!.StartsWith(emailPrefix), cancellationToken);
    }

    private static ApplicationUser NewUser(string name)
    {
        var email = $"{name}.{Guid.NewGuid():N}@company-a.example";
        return new ApplicationUser { UserName = email, Email = email, FirstName = "New", LastName = "User" };
    }

    private static OrganizationUnit NewUnit(ScenarioTenant tenant, Guid parentId, string name) =>
        new() { TenantId = tenant.TenantId, ParentId = parentId, Name = name, UnitType = OrganizationUnitType.Branch, State = OrganizationUnitState.Active };
}
