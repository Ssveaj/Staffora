using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public class OrganizationPickerTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public OrganizationPickerTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task The_pickers_name_units_by_their_path_and_offer_only_units_in_use()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken,
            RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate, RolePermission.OrganizationUnitsEdit, RolePermission.RolesView, RolePermission.RolesEdit, RolePermission.UsersView);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var leaver = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Leaver", cancellationToken);
        await UserEdits.SetStateInTableAsync(this.database, leaver, UserState.Inactive, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();
        var north = await units.CreateUnitIdAsync("North", OrganizationUnitType.Region, rootId, cancellationToken);
        var sales = await units.CreateUnitIdAsync("Sales", OrganizationUnitType.Branch, north, cancellationToken);
        await units.CreateUnitIdAsync("Specialists", OrganizationUnitType.Grouping, rootId, cancellationToken);
        var closed = await units.CreateUnitIdAsync("Closed office", OrganizationUnitType.Branch, rootId, cancellationToken);
        Assert.True((await units.StepAsync(closed, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).IsSuccess);

        var salesOptions = (await units.GetOrganizationUnitEditorOptionsAsync(sales, cancellationToken)).Value!;
        Assert.Equal([scenario.A.Name, $"{scenario.A.Name} › North", $"{scenario.A.Name} › Specialists"], salesOptions.Parents.Select(parent => parent.Name));
        Assert.DoesNotContain(salesOptions.Types, type => type.Value == OrganizationUnitType.Company);
        Assert.False(salesOptions.FieldsLocked);

        Assert.Equal([scenario.A.Name, $"{scenario.A.Name} › Specialists"], (await units.GetOrganizationUnitEditorOptionsAsync(north, cancellationToken)).Value!.Parents.Select(parent => parent.Name));

        var rootOptions = (await units.GetOrganizationUnitEditorOptionsAsync(rootId, cancellationToken)).Value!;
        Assert.Empty(rootOptions.Parents);
        Assert.Equal([OrganizationUnitType.Company], rootOptions.Types.Select(type => type.Value));

        var choices = (await host.Get<RoleHoldingService>().GetRoleHoldingChoicesAsync(scenario.A.TenantId, cancellationToken)).Value!;
        Assert.Equal([scenario.A.Name, $"{scenario.A.Name} › North", $"{scenario.A.Name} › North › Sales"], choices.Units.Select(unit => unit.Name));
        Assert.Contains(choices.People, person => person.Name.StartsWith("Colleague", StringComparison.Ordinal));
        Assert.DoesNotContain(choices.People, person => person.Name.StartsWith("Leaver", StringComparison.Ordinal));
    }
}
