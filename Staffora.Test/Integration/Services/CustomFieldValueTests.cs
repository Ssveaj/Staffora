using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Data;
using Staffora.Services.Services;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class CustomFieldValueTests : IClassFixture<RealDatabase>
{
    private const string Outside = "marketing";

    private readonly RealDatabase database;

    public CustomFieldValueTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_choice_outside_its_options_is_refused_on_every_save_path_and_a_key_of_it_is_stored()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var team = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, "Team", cancellationToken);
        var area = await AddFieldAsync<OrganizationUnitAttributeDefinition>(scenario.A, "Area", cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var users = host.Get<UserService>();
        var units = host.Get<OrganizationService>();
        const string refusal = "Team has no such option.";

        var email = $"new.{Guid.NewGuid():N}@company-a.example";
        Assert.Equal(refusal, (await users.CreateUserAsync(NewUser(email, team, Outside), scenario.A.TenantId, "Aa1!aaaaaaaa")).Error);
        Assert.False(await UserExistsAsync(email, cancellationToken));
        var created = await users.CreateUserAsync(NewUser(email, team, "planning"), scenario.A.TenantId, "Aa1!aaaaaaaa");
        Assert.True(created.IsSuccess, created.Error);
        Assert.Equal("planning", await UserValueAsync(created.Value!.Id, team, cancellationToken));

        Assert.True((await users.SaveUserAsync(colleague, user => user.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = team, StringValue = "teaching" }))).IsSuccess);
        Assert.Equal(refusal, (await users.SaveUserAsync(colleague, user => user.Attributes.Single().StringValue = Outside)).Error);
        Assert.Equal(refusal, (await users.SaveUserAsync(colleague, user => user.Attributes.Single().StringValue = "Working")).Error);
        Assert.Equal("teaching", await UserValueAsync(colleague, team, cancellationToken));

        var draft = UnitSteps.Draft("Fielded", OrganizationUnitType.Branch, rootId);
        draft.Attributes.Add(new OrganizationUnitAttribute { EntityAttributeDefinitionId = area, StringValue = Outside });
        Assert.Equal("Area has no such option.", (await units.CreateAsync(draft, cancellationToken)).Error);
        draft.Attributes.Single().StringValue = "teaching";
        var unit = await units.CreateAsync(draft, cancellationToken);
        Assert.True(unit.IsSuccess, unit.Error);
        Assert.Equal("Area has no such option.", (await units.EditAsync(unit.Value, loaded => loaded.Attributes.Single().StringValue = Outside, cancellationToken)).Error);
        Assert.Equal("teaching", await UnitValueAsync(unit.Value, area, cancellationToken));

        Assert.DoesNotContain(host.ContextLog, line => line.Contains("Refused custom field", StringComparison.Ordinal));

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.Add(new ApplicationUserAttribute { EntityId = scenario.A.OwnerId, EntityAttributeDefinitionId = team, StringValue = Outside });
            Assert.Equal(refusal, (await Assert.ThrowsAsync<CustomFieldException>(() => context.SaveChangesAsync(cancellationToken))).Message);
        }

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            (await context.ApplicationUserAttributes.SingleAsync(row => row.EntityId == colleague, cancellationToken)).StringValue = Outside;
            await Assert.ThrowsAsync<CustomFieldException>(() => context.SaveChangesAsync(cancellationToken));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            system.Update(new OrganizationUnitAttribute { EntityId = unit.Value, EntityAttributeDefinitionId = area, StringValue = Outside });
            Assert.Equal("Area has no such option.", (await Assert.ThrowsAsync<CustomFieldException>(() => system.SaveChangesAsync(cancellationToken))).Message);
        }

        Assert.Equal("teaching", await UserValueAsync(colleague, team, cancellationToken));
        Assert.Equal("teaching", await UnitValueAsync(unit.Value, area, cancellationToken));
        Assert.Null(await UserValueAsync(scenario.A.OwnerId, team, cancellationToken));
    }

    [Fact]
    public async Task A_retired_option_stays_on_a_value_that_holds_it_but_is_not_chosen_anew()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var team = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, "Team", cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var users = host.Get<UserService>();

        Assert.True((await users.SaveUserAsync(colleague, holder => holder.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = team, StringValue = "support" }))).IsSuccess);
        await RetireAsync(team, "support", cancellationToken);

        Assert.True((await users.SaveUserAsync(colleague, holder => holder.FirstName = "Renamed")).IsSuccess);
        Assert.Equal("support", await UserValueAsync(colleague, team, cancellationToken));

        Assert.Equal("Team's option \"Support\" is retired. Choose another.", (await users.SaveUserAsync(scenario.A.OwnerId, owner => owner.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = team, StringValue = "support" }))).Error);
        Assert.Null(await UserValueAsync(scenario.A.OwnerId, team, cancellationToken));

        var area = await AddFieldAsync<OrganizationUnitAttributeDefinition>(scenario.A, "Area", cancellationToken);
        var units = host.Get<OrganizationService>();
        var draft = UnitSteps.Draft("Support desk", OrganizationUnitType.Branch, rootId);
        draft.Attributes.Add(new OrganizationUnitAttribute { EntityAttributeDefinitionId = area, StringValue = "support" });
        var desk = await units.CreateAsync(draft, cancellationToken);
        Assert.True(desk.IsSuccess, desk.Error);
        await RetireUnitOptionAsync(area, "support", cancellationToken);

        var saved = await units.EditAsync(desk.Value, heldUnit => heldUnit.Description = "Renamed desk", cancellationToken);
        Assert.True(saved.IsSuccess, saved.Error);
        Assert.Equal("support", await UnitValueAsync(desk.Value, area, cancellationToken));

        var other = UnitSteps.Draft("Planning desk", OrganizationUnitType.Branch, rootId);
        other.Attributes.Add(new OrganizationUnitAttribute { EntityAttributeDefinitionId = area, StringValue = "planning" });
        var planning = await units.CreateAsync(other, cancellationToken);
        Assert.True(planning.IsSuccess, planning.Error);
        Assert.Equal("Area's option \"Support\" is retired. Choose another.", (await units.EditAsync(planning.Value, chooser => chooser.Attributes.Single().StringValue = "support", cancellationToken)).Error);
        Assert.Equal("planning", await UnitValueAsync(planning.Value, area, cancellationToken));
    }

    [Fact]
    public async Task A_cleared_value_is_stored_as_no_row_on_every_save_path()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var badge = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, "Badge", cancellationToken, AttributeValueType.Text);
        var team = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, "Team", cancellationToken);
        var floor = await AddFieldAsync<OrganizationUnitAttributeDefinition>(scenario.A, "Floor", cancellationToken, AttributeValueType.Text);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var users = host.Get<UserService>();
        var units = host.Get<OrganizationService>();

        var email = $"blank.{Guid.NewGuid():N}@company-a.example";
        var user = NewUser(email, team, "teaching");
        user.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = badge, StringValue = "   " });
        var created = await users.CreateUserAsync(user, scenario.A.TenantId, "Aa1!aaaaaaaa");
        Assert.True(created.IsSuccess, created.Error);
        Assert.Equal(["teaching"], await UserValuesAsync(created.Value!.Id, cancellationToken));

        Assert.True((await users.SaveUserAsync(created.Value.Id, loaded => loaded.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = badge, StringValue = "B-7" }))).IsSuccess);
        Assert.Equal(["B-7", "teaching"], await UserValuesAsync(created.Value.Id, cancellationToken));

        var cleared = await users.SaveUserAsync(created.Value.Id, loaded => loaded.Attributes.Single(value => value.EntityAttributeDefinitionId == badge).StringValue = " \t ");
        Assert.True(cleared.IsSuccess, cleared.Error);
        Assert.Equal(["teaching"], await UserValuesAsync(created.Value.Id, cancellationToken));

        Assert.True((await users.SaveUserAsync(created.Value.Id, loaded => loaded.Attributes.Single(value => value.EntityAttributeDefinitionId == team).StringValue = null)).IsSuccess);
        Assert.Empty(await UserValuesAsync(created.Value.Id, cancellationToken));

        var draft = UnitSteps.Draft("Blank floor", OrganizationUnitType.Branch, rootId);
        draft.Attributes.Add(new OrganizationUnitAttribute { EntityAttributeDefinitionId = floor, StringValue = " " });
        var unit = await units.CreateAsync(draft, cancellationToken);
        Assert.True(unit.IsSuccess, unit.Error);
        Assert.Null(await UnitValueAsync(unit.Value, floor, cancellationToken));

        var filled = await units.EditAsync(unit.Value, edited => edited.Attributes.Add(new OrganizationUnitAttribute { EntityAttributeDefinitionId = floor, StringValue = "3rd" }), cancellationToken);
        Assert.True(filled.IsSuccess, filled.Error);
        Assert.Equal("3rd", await UnitValueAsync(unit.Value, floor, cancellationToken));
        var unitCleared = await units.EditAsync(unit.Value, edited => edited.Attributes.Single().StringValue = string.Empty, cancellationToken);
        Assert.True(unitCleared.IsSuccess, unitCleared.Error);
        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.OrganizationUnitAttributes.AnyAsync(row => row.EntityId == unit.Value, cancellationToken));
    }

    [Fact]
    public async Task The_platform_console_creates_no_user_in_a_tenant_whatever_its_fields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _) = await ArrangeAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var foreign = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.B, "Team of B", cancellationToken);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());

        var email = $"console.{Guid.NewGuid():N}@company-a.example";
        var created = await host.Get<UserService>().CreateUserAsync(NewUser(email, foreign, "teaching"), scenario.A.TenantId, "Aa1!aaaaaaaa");
        Assert.Equal(UserService.CreateInOwnTenantOnly, created.Error);
        Assert.False(await UserExistsAsync(email, cancellationToken));
        Assert.DoesNotContain(host.ContextLog, line => line.Contains("Refused", StringComparison.Ordinal) && line.Contains(nameof(ApplicationUserAttribute), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_value_of_another_tenants_field_or_of_another_entitys_field_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var foreign = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.B, "Team", cancellationToken);
        var area = await AddFieldAsync<OrganizationUnitAttributeDefinition>(scenario.A, "Area", cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var users = host.Get<UserService>();

        Assert.Equal(AttributeValueRules.UnknownField, (await users.SaveUserAsync(scenario.A.OwnerId, owner => owner.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = foreign, StringValue = "teaching" }))).Error);
        Assert.Equal(AttributeValueRules.UnknownField, (await users.SaveUserAsync(scenario.A.OwnerId, owner => owner.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = area, StringValue = "teaching" }))).Error);

        var draft = UnitSteps.Draft("Borrowing", OrganizationUnitType.Branch, rootId);
        draft.Attributes.Add(new OrganizationUnitAttribute { EntityAttributeDefinitionId = foreign, StringValue = "teaching" });
        Assert.Equal(AttributeValueRules.UnknownField, (await host.Get<OrganizationService>().CreateAsync(draft, cancellationToken)).Error);

        foreach (var definition in new[] { foreign, area })
        {
            await using var context = this.database.CreateContext(scenario.A.Runtime());
            context.Add(new ApplicationUserAttribute { EntityId = scenario.A.OwnerId, EntityAttributeDefinitionId = definition, StringValue = "teaching" });
            await Assert.ThrowsAsync<TenantIsolationException>(() => context.SaveChangesAsync(cancellationToken));
        }

        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.ApplicationUserAttributes.AnyAsync(row => row.EntityId == scenario.A.OwnerId, cancellationToken));
        Assert.False(await check.OrganizationUnits.AnyAsync(row => row.Name == "Borrowing", cancellationToken));
    }

    [Fact]
    public async Task Each_type_round_trips_in_its_own_column_and_a_value_of_another_kind_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _) = await ArrangeAsync(cancellationToken);
        var fields = new Dictionary<AttributeValueType, Guid>();
        foreach (var type in AttributeDefinitionRules.Types)
        {
            fields[type] = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, $"{type} field", cancellationToken, type);
        }

        var member = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Member", cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var users = host.Get<UserService>();
        Assert.True((await users.SaveUserAsync(member, owner =>
        {
            owner.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = fields[AttributeValueType.Text], StringValue = "Left-handed" });
            owner.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = fields[AttributeValueType.Integer], Int32Value = 7 });
            owner.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = fields[AttributeValueType.Number], DoubleValue = 2.5 });
            owner.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = fields[AttributeValueType.Date], DateValue = new DateOnly(2026, 11, 3) });
            owner.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = fields[AttributeValueType.Boolean], BooleanValue = true });
            owner.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = fields[AttributeValueType.Choice], StringValue = "planning" });
        })).IsSuccess);

        var stored = (await users.GetUserByIdAsync(member)).Value!.Attributes.ToDictionary(value => value.EntityAttributeDefinitionId);
        Assert.Equal("Left-handed", stored[fields[AttributeValueType.Text]].StringValue);
        Assert.Equal(7, stored[fields[AttributeValueType.Integer]].Int32Value);
        Assert.Equal(2.5, stored[fields[AttributeValueType.Number]].DoubleValue);
        Assert.Equal(new DateOnly(2026, 11, 3), stored[fields[AttributeValueType.Date]].DateValue);
        Assert.True(stored[fields[AttributeValueType.Boolean]].BooleanValue);
        Assert.Equal("planning", stored[fields[AttributeValueType.Choice]].StringValue);

        Assert.Equal("Text field holds text only.", (await users.SaveUserAsync(member, crafted => crafted.Attributes.Single(value => value.EntityAttributeDefinitionId == fields[AttributeValueType.Text]).Int32Value = 3)).Error);
        await using var context = this.database.CreateContext(scenario.A.Runtime());
        (await context.ApplicationUserAttributes.SingleAsync(row => row.EntityAttributeDefinitionId == fields[AttributeValueType.Date], cancellationToken)).BooleanValue = false;
        Assert.Equal("Date field holds dates only.", (await Assert.ThrowsAsync<CustomFieldException>(() => context.SaveChangesAsync(cancellationToken))).Message);
    }

    private async Task<(TwoTenantScenario Scenario, Guid RootId)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken,
            RolePermission.UsersView, RolePermission.UsersCreate, RolePermission.UsersEdit, RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate, RolePermission.OrganizationUnitsEdit);
        return (scenario, await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken));
    }

    private async Task<Guid> AddFieldAsync<TDefinition>(ScenarioTenant tenant, string name, CancellationToken cancellationToken, AttributeValueType type = AttributeValueType.Choice)
        where TDefinition : class, IEntityAttributeDefinition, new()
    {
        await using var system = this.database.CreateSystemContext();
        var field = new TDefinition
        {
            State = CustomFieldState.Active,
            Name = name,
            ValueType = type,
            TenantId = tenant.TenantId,
            CreatedById = tenant.OwnerId,
            Options = type == AttributeValueType.Choice ? [new("teaching", "Working"), new("planning", "Planning"), new("support", "Support")] : [],
        };
        system.Add(field);
        await system.SaveChangesAsync(cancellationToken);
        return field.Id;
    }

    private async Task RetireAsync(Guid fieldId, string key, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        var field = await system.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == fieldId, cancellationToken);
        field.Options = [.. field.Options.Select(option => option.Key == key ? option with { IsRetired = true } : option)];
        field.ModifiedById = field.CreatedById;
        await system.SaveChangesAsync(cancellationToken);
    }

    private async Task RetireUnitOptionAsync(Guid fieldId, string key, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        var field = await system.OrganizationUnitAttributeDefinitions.SingleAsync(row => row.Id == fieldId, cancellationToken);
        field.Options = [.. field.Options.Select(option => option.Key == key ? option with { IsRetired = true } : option)];
        field.ModifiedById = field.CreatedById;
        await system.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<string?>> UserValuesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.ApplicationUserAttributes.Where(row => row.EntityId == userId).Select(row => row.StringValue).OrderBy(text => text).ToListAsync(cancellationToken);
    }

    private static ApplicationUser NewUser(string email, Guid fieldId, string key) => new()
    {
        UserName = email,
        Email = email,
        FirstName = "New",
        LastName = "User",
        Attributes = [new ApplicationUserAttribute { EntityAttributeDefinitionId = fieldId, StringValue = key }],
    };

    private async Task<bool> UserExistsAsync(string email, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Users.AnyAsync(user => user.Email == email, cancellationToken);
    }

    private async Task<string?> UserValueAsync(Guid userId, Guid fieldId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.ApplicationUserAttributes.Where(row => row.EntityId == userId && row.EntityAttributeDefinitionId == fieldId).Select(row => row.StringValue).SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<string?> UnitValueAsync(Guid unitId, Guid fieldId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.OrganizationUnitAttributes.Where(row => row.EntityId == unitId && row.EntityAttributeDefinitionId == fieldId).Select(row => row.StringValue).SingleOrDefaultAsync(cancellationToken);
    }
}
