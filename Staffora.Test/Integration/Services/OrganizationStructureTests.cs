using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Execution;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class OrganizationStructureTests : IClassFixture<RealDatabase>
{
    private const int Races = 25;

    private static readonly RolePermission[] unitPermissions =
        [RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate, RolePermission.OrganizationUnitsEdit, RolePermission.RolesView, RolePermission.RolesEdit, RolePermission.UsersView];

    private readonly RealDatabase database;

    public OrganizationStructureTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Crossing_moves_run_at_the_same_time_never_store_a_cycle_and_one_gets_the_cycle_reason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var outcomes = new List<string>();
        for (var race = 0; race < Races; race++)
        {
            Guid north, south;
            await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
            {
                var units = host.Get<OrganizationService>();
                north = (await units.CreateUnitAsync(Draft($"North {race}", OrganizationUnitType.Region, rootId))).Value!.Id;
                south = (await units.CreateUnitAsync(Draft($"South {race}", OrganizationUnitType.Region, rootId))).Value!.Id;
            }

            await using var first = ServiceHost.Create(this.database, scenario.A.Runtime());
            await using var second = ServiceHost.Create(this.database, scenario.A.Runtime());
            var northMove = (await first.Get<OrganizationService>().LoadUnitAsync(north)).Value!;
            northMove.ParentId = south;
            var southMove = (await second.Get<OrganizationService>().LoadUnitAsync(south)).Value!;
            southMove.ParentId = north;

            var results = await Task.WhenAll(
                Task.Run(() => first.Get<OrganizationService>().UpdateUnitAsync(northMove), cancellationToken),
                Task.Run(() => second.Get<OrganizationService>().UpdateUnitAsync(southMove), cancellationToken));

            outcomes.Add(string.Join(" | ", results.Select(result => result.IsSuccess ? "saved" : result.Error)));
            var refused = Assert.Single(results, result => !result.IsSuccess);
            Assert.Contains(refused.Error, new[]
            {
                OrganizationStructurePolicy.CycleReason($"North {race}"), OrganizationStructurePolicy.CycleReason($"South {race}"),
                OrganizationStructurePolicy.CutOffReason($"North {race}"), OrganizationStructurePolicy.CutOffReason($"South {race}"),
            });
        }

        Assert.Empty(await UnitsCutOffFromTheRootAsync(scenario.A, cancellationToken));
        Assert.Equal(Races, outcomes.Count);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_unit_turned_into_a_grouping_while_a_role_is_given_on_it_never_keeps_the_holding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var role = await AddRoleAsync(scenario.A, "Coordinator", cancellationToken, RolePermission.UsersView);
        for (var race = 0; race < Races; race++)
        {
            Guid branch;
            await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
            {
                branch = (await host.Get<OrganizationService>().CreateUnitAsync(Draft($"Branch {race}", OrganizationUnitType.Branch, rootId))).Value!.Id;
            }

            await using var editor = ServiceHost.Create(this.database, scenario.A.Runtime());
            await using var assigner = ServiceHost.Create(this.database, scenario.A.Runtime());
            var regrouped = (await editor.Get<OrganizationService>().LoadUnitAsync(branch)).Value!;
            regrouped.UnitType = OrganizationUnitType.Grouping;

            var results = await Task.WhenAll(
                Task.Run(async () => (Result)await editor.Get<OrganizationService>().UpdateUnitAsync(regrouped), cancellationToken),
                Task.Run(async () => (Result)await assigner.Get<RoleHoldingService>().AssignUserToRoleAsync(colleague, branch, role, scenario.A.TenantId), cancellationToken));

            var refused = Assert.Single(results, result => !result.IsSuccess);
            Assert.Contains(refused.Error, new[]
            {
                OrganizationStructurePolicy.GroupingHoldsNoRoles,
                OrganizationStructurePolicy.CheckTypeChange($"Branch {race}", OrganizationUnitType.Branch, OrganizationUnitType.Grouping, 1),
            });
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(0, await check.OrganizationUnitUserRoles.CountAsync(holding => holding.TenantId == scenario.A.TenantId && holding.OrganizationUnit.UnitType == OrganizationUnitType.Grouping, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_cycle_or_a_holding_on_a_grouping_written_straight_through_the_context_is_refused_and_rolled_back()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        Guid grouping, north, south, branch;
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var units = host.Get<OrganizationService>();
            grouping = (await units.CreateUnitAsync(Draft("Specialists", OrganizationUnitType.Grouping, rootId))).Value!.Id;
            north = (await units.CreateUnitAsync(Draft("North", OrganizationUnitType.Region, rootId))).Value!.Id;
            south = (await units.CreateUnitAsync(Draft("South", OrganizationUnitType.Region, rootId))).Value!.Id;
            branch = (await units.CreateUnitAsync(Draft("Harbour office", OrganizationUnitType.Branch, rootId))).Value!.Id;
        }

        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var role = await AddRoleAsync(scenario.A, "Coordinator", cancellationToken, RolePermission.UsersView);

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            writer.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { UserId = colleague, TenantRoleId = role, OrganizationUnitId = grouping });
            var refused = await Assert.ThrowsAsync<OrganizationStructureException>(() => writer.SaveChangesAsync(cancellationToken));
            Assert.Equal(OrganizationStructurePolicy.GroupingHoldsNoRoles, refused.Message);
        }

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            (await writer.OrganizationUnits.SingleAsync(unit => unit.Id == north, cancellationToken)).ParentId = south;
            await writer.SaveChangesAsync(cancellationToken);
        }

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            (await writer.OrganizationUnits.SingleAsync(unit => unit.Id == south, cancellationToken)).ParentId = north;
            var refused = await Assert.ThrowsAsync<OrganizationStructureException>(() => writer.SaveChangesAsync(cancellationToken));
            Assert.Equal(OrganizationStructurePolicy.CutOffReason("South"), refused.Message);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            await using var transaction = await system.Database.BeginTransactionAsync(cancellationToken);
            system.TenantRoles.Add(new TenantRole { TenantId = scenario.A.TenantId, Name = "Kept", CreatedById = scenario.A.OwnerId });
            await system.SaveChangesAsync(cancellationToken);

            var southRow = await system.OrganizationUnits.SingleAsync(unit => unit.Id == south, cancellationToken);
            southRow.ParentId = north;
            system.NameEditor(southRow, scenario.A.OwnerId);
            var refused = await Assert.ThrowsAsync<OrganizationStructureException>(() => system.SaveChangesAsync(cancellationToken));
            Assert.Equal(OrganizationStructurePolicy.CutOffReason("South"), refused.Message);
            system.Entry(southRow).State = EntityState.Unchanged;
            await transaction.CommitAsync(cancellationToken);
        }

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            writer.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { UserId = colleague, TenantRoleId = role, OrganizationUnitId = branch });
            await writer.SaveChangesAsync(cancellationToken);
            (await writer.OrganizationUnits.SingleAsync(unit => unit.Id == branch, cancellationToken)).UnitType = OrganizationUnitType.Grouping;
            var refused = await Assert.ThrowsAsync<OrganizationStructureException>(() => writer.SaveChangesAsync(cancellationToken));
            Assert.Equal(OrganizationStructurePolicy.CheckTypeChange("Harbour office", OrganizationUnitType.Branch, OrganizationUnitType.Grouping, 1), refused.Message);
        }

        await using var check = this.database.CreateSystemContext();
        var parents = await check.OrganizationUnits.Where(unit => unit.TenantId == scenario.A.TenantId).ToDictionaryAsync(unit => unit.Id, unit => (unit.ParentId, unit.UnitType), cancellationToken);
        Assert.Equal((rootId, OrganizationUnitType.Region), parents[south]);
        Assert.Equal((south, OrganizationUnitType.Region), parents[north]);
        Assert.Equal((rootId, OrganizationUnitType.Branch), parents[branch]);
        Assert.False(await check.OrganizationUnitUserRoles.AnyAsync(holding => holding.OrganizationUnitId == grouping, cancellationToken));
        Assert.True(await check.TenantRoles.AnyAsync(row => row.TenantId == scenario.A.TenantId && row.Name == "Kept", cancellationToken));
        Assert.Empty(await UnitsCutOffFromTheRootAsync(scenario.A, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_tree_stored_with_a_cycle_is_repaired_through_the_service_one_move_at_a_time()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        Guid north, south;
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var units = host.Get<OrganizationService>();
            north = (await units.CreateUnitAsync(Draft("North", OrganizationUnitType.Region, rootId))).Value!.Id;
            south = (await units.CreateUnitAsync(Draft("South", OrganizationUnitType.Region, north))).Value!.Id;
        }

        await using (var system = this.database.CreateSystemContext())
        {
            await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE OrganizationUnits SET ParentId = {south} WHERE Id = {north}", cancellationToken);
        }

        Assert.Equal(new[] { north, south }.Order(), (await UnitsCutOffFromTheRootAsync(scenario.A, cancellationToken)).Order());
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var units = host.Get<OrganizationService>();

            Assert.True((await units.CreateUnitAsync(Draft("West", OrganizationUnitType.Region, rootId))).IsSuccess);
            Assert.Equal(OrganizationStructurePolicy.CutOffReason("Lost"), (await units.CreateUnitAsync(Draft("Lost", OrganizationUnitType.Team, south))).Error);
            var repaired = (await units.LoadUnitAsync(north)).Value!;
            repaired.ParentId = rootId;
            var result = await units.UpdateUnitAsync(repaired);
            Assert.True(result.IsSuccess, result.Error);
        }

        Assert.Empty(await UnitsCutOffFromTheRootAsync(scenario.A, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_Company_root_stays_active()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();

        Assert.Equal(OrganizationStructurePolicy.RootStaysActive, (await units.StepAsync(rootId, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).Error);
        Assert.Equal(OrganizationUnitWorkflowAttribute.RootIsNeverArchived, (await units.StepAsync(rootId, OrganizationUnitWorkflowAttribute.ArchiveStep, cancellationToken)).Error);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.OrganizationUnitsDelete);
        await using (var deleter = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            Assert.Contains(OrganizationStructurePolicy.RootIsNeverDeleted, (await deleter.Get<OrganizationService>().DeleteAsync(rootId, cancellationToken)).Error, StringComparison.Ordinal);
        }

        var root = (await units.LoadUnitAsync(rootId)).Value!;
        root.Name = "Company A Ltd";
        Assert.True((await units.UpdateUnitAsync(root)).IsSuccess);

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(OrganizationUnitState.Active, await check.OrganizationUnits.Where(unit => unit.Id == rootId).Select(unit => unit.State).SingleAsync(cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Texts_over_their_length_are_refused_with_the_reason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();

        Assert.Equal(OrganizationService.TextRule.NameTooLong, (await units.CreateUnitAsync(Draft(new string('n', OrganizationUnit.NameMaxLength + 1), OrganizationUnitType.Team, rootId))).Error);
        Assert.True((await units.CreateUnitAsync(Draft(new string('n', OrganizationUnit.NameMaxLength), OrganizationUnitType.Team, rootId))).IsSuccess);

        var described = Draft("Described", OrganizationUnitType.Team, rootId);
        described.Description = new string('d', OrganizationUnit.DescriptionMaxLength + 1);
        Assert.Equal(OrganizationService.TextRule.DescriptionTooLong, (await units.CreateUnitAsync(described)).Error);

        Guid fieldId;
        await using (var system = this.database.CreateSystemContext())
        {
            var field = new OrganizationUnitAttributeDefinition { State = CustomFieldState.Active, Name = "Notes", ValueType = AttributeValueType.Text, TenantId = scenario.A.TenantId, CreatedById = scenario.A.OwnerId };
            system.Add(field);
            await system.SaveChangesAsync(cancellationToken);
            fieldId = field.Id;
        }

        var fielded = Draft("Fielded", OrganizationUnitType.Team, rootId);
        fielded.Attributes.Add(new OrganizationUnitAttribute { EntityAttributeDefinitionId = fieldId, StringValue = new string('f', AttributeValueRules.TextMaxLength + 1) });
        Assert.Equal($"Notes holds at most {AttributeValueRules.TextMaxLength} characters.", (await units.CreateUnitAsync(fielded)).Error);
        fielded.Attributes.Single().StringValue = new string('f', AttributeValueRules.TextMaxLength);
        Assert.True((await units.CreateUnitAsync(fielded)).IsSuccess);

        var root = (await units.LoadUnitAsync(rootId)).Value!;
        root.Name = new string('x', OrganizationUnit.NameMaxLength + 1);
        Assert.Equal(OrganizationService.TextRule.NameTooLong, (await units.UpdateUnitAsync(root)).Error);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_name_the_database_takes_as_a_siblings_is_refused_with_the_policys_reason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var units = host.Get<OrganizationService>();
            Assert.True((await units.CreateUnitAsync(Draft("Twin", OrganizationUnitType.Team, rootId))).IsSuccess);

            const string fullWidth = "Ｔｗｉｎ";
            Assert.Equal(OrganizationStructurePolicy.SiblingNameReason(scenario.A.Name, "Twin"), (await units.CreateUnitAsync(Draft(fullWidth, OrganizationUnitType.Team, rootId))).Error);

            var other = (await units.CreateUnitAsync(Draft("Other", OrganizationUnitType.Team, rootId))).Value!;
            var renamed = (await units.LoadUnitAsync(other.Id)).Value!;
            renamed.Name = fullWidth;
            Assert.Equal(OrganizationStructurePolicy.SiblingNameReason(scenario.A.Name, "Twin"), (await units.UpdateUnitAsync(renamed)).Error);
        }

        for (var race = 0; race < Races; race++)
        {
            await using var first = ServiceHost.Create(this.database, scenario.A.Runtime());
            await using var second = ServiceHost.Create(this.database, scenario.A.Runtime());
            var name = $"Pair {race}";
            var results = await Task.WhenAll(
                Task.Run(() => first.Get<OrganizationService>().CreateUnitAsync(Draft(name, OrganizationUnitType.Team, rootId)), cancellationToken),
                Task.Run(() => second.Get<OrganizationService>().CreateUnitAsync(Draft(name, OrganizationUnitType.Team, rootId)), cancellationToken));
            Assert.Equal(OrganizationStructurePolicy.SiblingNameReason(scenario.A.Name, name), Assert.Single(results, result => !result.IsSuccess).Error);
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task An_inactive_unit_or_user_takes_no_new_role_and_the_pickers_reason_is_the_services()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var leaver = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Leaver", cancellationToken);
        var role = await AddRoleAsync(scenario.A, "Coordinator", cancellationToken, RolePermission.UsersView);
        await UserEdits.SetStateInTableAsync(this.database, leaver, UserState.Inactive, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();
        var tenants = host.Get<RoleHoldingService>();
        var closedId = (await units.CreateUnitAsync(Draft("Closed office", OrganizationUnitType.Branch, rootId))).Value!.Id;
        Assert.True((await units.StepAsync(closedId, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).IsSuccess);

        Assert.Equal(RoleHoldingService.InactiveUnitTakesNoRoles, (await tenants.AssignUserToRoleAsync(colleague, closedId, role, scenario.A.TenantId)).Error);
        Assert.Equal(RoleHoldingService.InactiveUserTakesNoRoles, (await tenants.AssignUserToRoleAsync(leaver, rootId, role, scenario.A.TenantId)).Error);
        Assert.True((await tenants.AssignUserToRoleAsync(colleague, rootId, role, scenario.A.TenantId)).IsSuccess);

        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.OrganizationUnitUserRoles.AnyAsync(holding => holding.OrganizationUnitId == closedId || holding.UserId == leaver, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_holding_on_a_deactivated_unit_grants_nothing_until_the_unit_is_active_again()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var role = await AddRoleAsync(scenario.A, "Role viewer", cancellationToken, RolePermission.RolesView);
        Guid branch;
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            branch = (await host.Get<OrganizationService>().CreateUnitAsync(Draft("Harbour office", OrganizationUnitType.Branch, rootId))).Value!.Id;
            Assert.True((await host.Get<RoleHoldingService>().AssignUserToRoleAsync(colleague, branch, role, scenario.A.TenantId)).IsSuccess);
        }

        Assert.True(await HoldsAsync(scenario.A, colleague, RolePermission.RolesView, cancellationToken));

        await SetActiveAsync(scenario.A, branch, active: false);
        Assert.False(await HoldsAsync(scenario.A, colleague, RolePermission.RolesView, cancellationToken));
        await using (var check = this.database.CreateSystemContext())
        {
            Assert.True(await check.OrganizationUnitUserRoles.AnyAsync(holding => holding.OrganizationUnitId == branch && holding.UserId == colleague, cancellationToken));
        }

        await SetActiveAsync(scenario.A, branch, active: true);
        Assert.True(await HoldsAsync(scenario.A, colleague, RolePermission.RolesView, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task An_open_circuit_keeps_its_cached_permissions_until_the_time_to_live_and_the_deactivating_users_own_drop_at_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var colleagueRole = await AddRoleAsync(scenario.A, "Role viewer", cancellationToken, RolePermission.RolesView);
        var ownRole = await AddRoleAsync(scenario.A, "Settings viewer", cancellationToken, RolePermission.SettingsView);
        var clock = new FakeClock(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));

        await using var owner = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddSingleton<TimeProvider>(clock));
        var units = owner.Get<OrganizationService>();
        var branch = (await units.CreateUnitAsync(Draft("Harbour office", OrganizationUnitType.Branch, rootId))).Value!.Id;
        Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(colleague, branch, colleagueRole, scenario.A.TenantId)).IsSuccess);

        await using (var fixture = this.database.CreateSystemContext())
        {
            fixture.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { TenantId = scenario.A.TenantId, UserId = scenario.A.OwnerId, OrganizationUnitId = branch, TenantRoleId = ownRole, CreatedById = scenario.A.OwnerId });
            await fixture.SaveChangesAsync(cancellationToken);
        }

        await owner.Get<PermissionService>().InvalidateCacheAsync(cancellationToken);

        await using var colleagueCircuit = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague), services => services.AddSingleton<TimeProvider>(clock));
        var colleaguePermissions = colleagueCircuit.Get<PermissionService>();
        Assert.True(await colleaguePermissions.HasPermissionAsync(RolePermission.RolesView, cancellationToken));
        Assert.True(await owner.Get<PermissionService>().HasPermissionAsync(RolePermission.SettingsView, cancellationToken));

        Assert.True((await units.StepAsync(branch, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).IsSuccess);

        Assert.False(await owner.Get<PermissionService>().HasPermissionAsync(RolePermission.SettingsView, cancellationToken));

        clock.Advance(PermissionService.TimeToLive - TimeSpan.FromSeconds(1));
        Assert.True(await colleaguePermissions.HasPermissionAsync(RolePermission.RolesView, cancellationToken));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await colleaguePermissions.HasPermissionAsync(RolePermission.RolesView, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_platform_tenants_tree_is_its_root_alone_and_a_platform_owner_writes_no_unit_anywhere()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootOfA) = await ArrangeAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var platformRoot = await TwoTenantScenario.RootUnitIdAsync(this.database, platform, cancellationToken);

        await using (var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var units = host.Get<OrganizationService>();
            Assert.Equal(ApplicationDbContext.PlatformTreeIsRootOnlyReason, (await units.CreateUnitAsync(Draft("Platform team", OrganizationUnitType.Team, platformRoot))).Error);
            Assert.Equal(ApplicationDbContext.PlatformTreeIsRootOnlyReason, (await units.CreateUnitAsync(Draft("Planted", OrganizationUnitType.Team, rootOfA))).Error);

            var root = (await units.LoadUnitAsync(platformRoot)).Value!;
            root.Name = "Renamed platform";
            Assert.Equal(ApplicationDbContext.PlatformTreeIsRootOnlyReason, (await units.UpdateUnitAsync(root)).Error);

            var role = await AddRoleAsync(platform, "Platform role", cancellationToken, RolePermission.UsersView);
            Assert.Equal(ApplicationDbContext.PlatformTreeIsRootOnlyReason, (await host.Get<RoleHoldingService>().AssignUserToRoleAsync(platform.OwnerId, platformRoot, role, platform.TenantId)).Error);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            system.OrganizationUnits.Add(new OrganizationUnit { TenantId = platform.TenantId, ParentId = platformRoot, Name = "Platform team", UnitType = OrganizationUnitType.Team, CreatedById = platform.OwnerId });
            Assert.Equal(ApplicationDbContext.PlatformTreeIsRootOnlyReason, (await Assert.ThrowsAsync<OrganizationStructureException>(() => system.SaveChangesAsync(cancellationToken))).Message);
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal([platformRoot], await check.OrganizationUnits.Where(unit => unit.TenantId == platform.TenantId).Select(unit => unit.Id).ToListAsync(cancellationToken));
        Assert.False(await check.OrganizationUnitUserRoles.AnyAsync(holding => holding.TenantId == platform.TenantId, cancellationToken));
        Assert.False(await check.OrganizationUnits.AnyAsync(unit => unit.TenantId == scenario.A.TenantId && unit.Name == "Planted", cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Ensuring_a_root_at_the_same_time_from_several_starts_adds_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var race = 0; race < Races; race++)
        {
            var tenant = new Tenant { Name = $"Race {race} {Guid.NewGuid():N}", State = TenantState.Active };
            await using (var context = this.database.CreateSystemContext())
            {
                context.Tenants.Add(tenant);
                await context.SaveChangesAsync(cancellationToken);
                await SystemAccountService.EnsureAsync(context, tenant.Id, cancellationToken);
            }

            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
            {
                await using var context = this.database.CreateSystemContext();
                return await CompanyRoot.EnsureAsync(context, tenant.Id, cancellationToken);
            }, cancellationToken)));

            Assert.Single(results, result => result.Created);
            Assert.Single(results.Select(result => result.Id).Distinct());
            await using var check = this.database.CreateSystemContext();
            Assert.Equal(1, await check.OrganizationUnits.CountAsync(unit => unit.TenantId == tenant.Id, cancellationToken));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Moves_in_snapshot_transactions_never_store_a_unit_cut_off_by_a_move_committed_after_their_snapshot_began()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var isolated = await this.database.CreateIsolatedAsync("snapshot", cancellationToken);
        await AllowSnapshotIsolationAsync(isolated, cancellationToken);
        var (scenario, rootId) = await ArrangeAsync(isolated, cancellationToken);
        for (var run = 0; run < 10; run++)
        {
            Guid a, a1, b, b1;
            await using (var host = ServiceHost.Create(isolated, scenario.A.Runtime()))
            {
                var units = host.Get<OrganizationService>();
                a = (await units.CreateUnitAsync(Draft($"A {run}", OrganizationUnitType.Region, rootId))).Value!.Id;
                a1 = (await units.CreateUnitAsync(Draft($"A1 {run}", OrganizationUnitType.Branch, a))).Value!.Id;
                b = (await units.CreateUnitAsync(Draft($"B {run}", OrganizationUnitType.Region, rootId))).Value!.Id;
                b1 = (await units.CreateUnitAsync(Draft($"B1 {run}", OrganizationUnitType.Branch, b))).Value!.Id;
            }

            await using var first = isolated.CreateContext(scenario.A.Runtime());
            await using var second = isolated.CreateContext(scenario.A.Runtime());
            await using var firstTransaction = await first.Database.BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);
            await using var secondTransaction = await second.Database.BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);
            var aRow = await first.OrganizationUnits.SingleAsync(unit => unit.Id == a, cancellationToken);
            var bRow = await second.OrganizationUnits.SingleAsync(unit => unit.Id == b, cancellationToken);

            aRow.ParentId = b1;
            await first.SaveChangesAsync(cancellationToken);
            await firstTransaction.CommitAsync(cancellationToken);

            bRow.ParentId = a1;
            var refused = await Assert.ThrowsAsync<OrganizationStructureException>(() => second.SaveChangesAsync(cancellationToken));
            Assert.Equal(OrganizationStructurePolicy.CutOffReason($"B {run}"), refused.Message);
            await secondTransaction.CommitAsync(cancellationToken);
        }

        Assert.Empty(await UnitsCutOffFromTheRootAsync(isolated, scenario.A, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_structure_write_after_a_snapshot_transaction_on_the_same_pool_runs_at_read_committed_and_still_refuses_crossing_moves()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var isolated = await this.database.CreateIsolatedAsync("snapshot", cancellationToken);
        await AllowSnapshotIsolationAsync(isolated, cancellationToken);
        var (scenario, rootId) = await ArrangeAsync(isolated, cancellationToken);
        var probe = new IsolationProbe();
        await using var host = ServiceHost.Create(isolated, scenario.A.Runtime(), interceptors: [probe]);
        var units = host.Get<OrganizationService>();
        var north = (await units.CreateUnitAsync(Draft("North", OrganizationUnitType.Region, rootId))).Value!.Id;
        var south = (await units.CreateUnitAsync(Draft("South", OrganizationUnitType.Region, rootId))).Value!.Id;

        await LeaveSnapshotIsolationOnThePoolAsync(isolated, 8, cancellationToken);
        Assert.Equal(SnapshotIsolation, await PooledSessionIsolationLevelAsync(isolated, cancellationToken));
        probe.Levels.Clear();

        var northMove = (await units.LoadUnitAsync(north)).Value!;
        northMove.ParentId = south;
        var moved = await units.UpdateUnitAsync(northMove);
        Assert.True(moved.IsSuccess, moved.Error);
        var southMove = (await units.LoadUnitAsync(south)).Value!;
        southMove.ParentId = north;
        Assert.Equal(OrganizationStructurePolicy.CycleReason("North"), (await units.UpdateUnitAsync(southMove)).Error);

        await LeaveSnapshotIsolationOnThePoolAsync(isolated, 8, cancellationToken);
        await using (var writer = host.Get<IApplicationDbContextFactory<ApplicationDbContext>>().CreateDbContext(scenario.A.Runtime()))
        {
            (await writer.OrganizationUnits.SingleAsync(unit => unit.Id == south, cancellationToken)).ParentId = north;
            var refused = await Assert.ThrowsAsync<OrganizationStructureException>(() => writer.SaveChangesAsync(cancellationToken));
            Assert.Equal(OrganizationStructurePolicy.CutOffReason("South"), refused.Message);
        }

        Assert.Equal(3, probe.Levels.Count);
        Assert.All(probe.Levels, level => Assert.Equal(ReadCommittedIsolation, level));
        Assert.Empty(await UnitsCutOffFromTheRootAsync(isolated, scenario.A, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_check_that_fails_inside_a_callers_transaction_undoes_the_save_before_the_caller_sees_the_failure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        Guid north, south;
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var units = host.Get<OrganizationService>();
            north = (await units.CreateUnitAsync(Draft("North", OrganizationUnitType.Region, rootId))).Value!.Id;
            south = (await units.CreateUnitAsync(Draft("South", OrganizationUnitType.Region, north))).Value!.Id;
        }

        using var failing = new FailingCheck();
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime(), interceptors: [failing]))
        {
            var contexts = host.Get<IApplicationDbContextFactory<ApplicationDbContext>>();
            await using (var writer = contexts.CreateDbContext(scenario.A.Runtime()))
            {
                await using var transaction = await writer.Database.BeginTransactionAsync(cancellationToken);
                writer.TenantRoles.Add(new TenantRole { Name = "Kept" });
                await writer.SaveChangesAsync(cancellationToken);
                (await writer.OrganizationUnits.SingleAsync(unit => unit.Id == north, cancellationToken)).ParentId = south;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.SaveChangesAsync(failing.Token));
                await transaction.CommitAsync(cancellationToken);
            }

            await using (var writer = contexts.CreateDbContext(scenario.A.Runtime()))
            {
                using var transaction = writer.Database.BeginTransaction();
                writer.OrganizationUnits.Single(unit => unit.Id == north).ParentId = south;
                Assert.Throws<InvalidOperationException>(() => writer.SaveChanges());
                transaction.Commit();
            }
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(rootId, await check.OrganizationUnits.Where(unit => unit.Id == north).Select(unit => unit.ParentId).SingleAsync(cancellationToken));
        Assert.True(await check.TenantRoles.AnyAsync(role => role.TenantId == scenario.A.TenantId && role.Name == "Kept", cancellationToken));
        Assert.Empty(await UnitsCutOffFromTheRootAsync(scenario.A, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_Company_root_is_never_deleted_by_a_save_or_a_bulk_delete_while_another_unit_still_is()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate, RolePermission.OrganizationUnitsDelete);
        var rootOfB = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.B, cancellationToken);

        await using (var writer = this.database.CreateContext(scenario.B.Runtime()))
        {
            writer.OrganizationUnits.Remove(await writer.OrganizationUnits.SingleAsync(unit => unit.Id == rootOfB, cancellationToken));
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => writer.SaveChangesAsync(cancellationToken));
            Assert.Throws<WorkflowFieldEditException>(() => writer.SaveChanges());
        }

        await using (var system = this.database.CreateSystemContext())
        {
            system.OrganizationUnits.Remove(new OrganizationUnit { Id = rootOfB, TenantId = scenario.B.TenantId, Name = "Stub", UnitType = OrganizationUnitType.Team });
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => system.SaveChangesAsync(cancellationToken));
        }

        await using (var writer = this.database.CreateContext(scenario.B.Runtime()))
        {
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => writer.OrganizationUnits.Where(unit => unit.Id == rootOfB).ExecuteDeleteAsync(cancellationToken));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => system.OrganizationUnits.Where(unit => unit.TenantId == scenario.B.TenantId && unit.Id == rootOfB).ExecuteDeleteAsync(cancellationToken));
        }

        await using var host = ServiceHost.Create(this.database, scenario.B.Runtime());
        var units = host.Get<OrganizationService>();
        Assert.Equal(OrganizationStructurePolicy.RootIsNeverDeleted, (await units.DeleteAsync(rootOfB, cancellationToken)).Error);
        var leaf = await units.CreateUnitIdAsync("Leaf", OrganizationUnitType.Team, rootOfB, cancellationToken);
        Assert.True((await units.DeleteAsync(leaf, cancellationToken)).IsSuccess);

        await using var check = this.database.CreateSystemContext();
        Assert.Equal([rootOfB], await check.OrganizationUnits.Where(unit => unit.TenantId == scenario.B.TenantId).Select(unit => unit.Id).ToListAsync(cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_unit_in_or_below_a_stored_cycle_is_renamed_through_the_service_but_takes_no_new_role_until_the_tree_is_repaired()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var role = await AddRoleAsync(scenario.A, "Coordinator", cancellationToken, RolePermission.UsersView);
        Guid north, south, harbour;
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var units = host.Get<OrganizationService>();
            north = (await units.CreateUnitAsync(Draft("North", OrganizationUnitType.Region, rootId))).Value!.Id;
            south = (await units.CreateUnitAsync(Draft("South", OrganizationUnitType.Region, north))).Value!.Id;
            harbour = (await units.CreateUnitAsync(Draft("Harbour office", OrganizationUnitType.Branch, south))).Value!.Id;
        }

        await using (var system = this.database.CreateSystemContext())
        {
            await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE OrganizationUnits SET ParentId = {south} WHERE Id = {north}", cancellationToken);
        }

        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var units = host.Get<OrganizationService>();
            var tenants = host.Get<RoleHoldingService>();

            var renamed = (await units.LoadUnitAsync(north)).Value!;
            renamed.Name = "North coast";
            var rename = await units.UpdateUnitAsync(renamed);
            Assert.True(rename.IsSuccess, rename.Error);
            var below = (await units.LoadUnitAsync(harbour)).Value!;
            below.Name = "Harbour";
            Assert.True((await units.UpdateUnitAsync(below)).IsSuccess);

            host.ClearCommandLog();
            Assert.Equal(OrganizationStructurePolicy.CutOffHoldsNoRoles("North coast"), (await tenants.AssignUserToRoleAsync(colleague, north, role, scenario.A.TenantId)).Error);
            Assert.Equal(OrganizationStructurePolicy.CutOffHoldsNoRoles("Harbour"), (await tenants.AssignUserToRoleAsync(colleague, harbour, role, scenario.A.TenantId)).Error);
            Assert.DoesNotContain(host.CommandLog, command => command.Contains("INSERT INTO [OrganizationUnitUserRoles]", StringComparison.Ordinal));
        }

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            writer.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { UserId = colleague, TenantRoleId = role, OrganizationUnitId = south });
            var refused = await Assert.ThrowsAsync<OrganizationStructureException>(() => writer.SaveChangesAsync(cancellationToken));
            Assert.Equal(OrganizationStructurePolicy.CutOffHoldsNoRoles("South"), refused.Message);
        }

        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var repaired = (await host.Get<OrganizationService>().LoadUnitAsync(north)).Value!;
            repaired.ParentId = rootId;
            Assert.True((await host.Get<OrganizationService>().UpdateUnitAsync(repaired)).IsSuccess);
            var given = await host.Get<RoleHoldingService>().AssignUserToRoleAsync(colleague, harbour, role, scenario.A.TenantId);
            Assert.True(given.IsSuccess, given.Error);
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal([harbour], await check.OrganizationUnitUserRoles.Where(holding => holding.UserId == colleague).Select(holding => holding.OrganizationUnitId).ToListAsync(cancellationToken));
        Assert.Empty(await UnitsCutOffFromTheRootAsync(scenario.A, cancellationToken));
    }

    private const int ReadCommittedIsolation = 2;

    private const int SnapshotIsolation = 5;

    private const string SessionIsolationLevelSql = "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID";

    private static async Task AllowSnapshotIsolationAsync(RealDatabase isolated, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(isolated.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task LeaveSnapshotIsolationOnThePoolAsync(RealDatabase isolated, int count, CancellationToken cancellationToken)
    {
        var connections = new List<SqlConnection>();
        try
        {
            for (var index = 0; index < count; index++)
            {
                var connection = new SqlConnection(isolated.ConnectionString);
                connections.Add(connection);
                await connection.OpenAsync(cancellationToken);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);
                await using var read = new SqlCommand("SELECT COUNT(*) FROM OrganizationUnits", connection, transaction);
                await read.ExecuteScalarAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }

    private static async Task<int> PooledSessionIsolationLevelAsync(RealDatabase isolated, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(isolated.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(SessionIsolationLevelSql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private sealed class IsolationProbe : DbCommandInterceptor
    {
        public ConcurrentQueue<int> Levels { get; } = new();

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (IsLock(command))
            {
                using var read = Read(command);
                Levels.Enqueue(Convert.ToInt32(read.ExecuteScalar()));
            }

            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (IsLock(command))
            {
                await using var read = Read(command);
                Levels.Enqueue(Convert.ToInt32(await read.ExecuteScalarAsync(cancellationToken)));
            }

            return result;
        }

        private static bool IsLock(DbCommand command) => command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal);

        private static DbCommand Read(DbCommand lockCommand)
        {
            var read = lockCommand.Connection!.CreateCommand();
            read.Transaction = lockCommand.Transaction;
            read.CommandText = SessionIsolationLevelSql;
            return read;
        }
    }

    private sealed class FailingCheck : DbCommandInterceptor, IDisposable
    {
        private readonly CancellationTokenSource caller = new();

        public CancellationToken Token => this.caller.Token;

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
            IsCheck(command) ? throw new InvalidOperationException("The structure check failed in a way nobody planned for.") : result;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (IsCheck(command))
            {
                await this.caller.CancelAsync();
            }

            return result;
        }

        public void Dispose() => this.caller.Dispose();

        private static bool IsCheck(DbCommand command) => command.CommandText.Contains("[Reached]", StringComparison.Ordinal);
    }

    private Task<(TwoTenantScenario Scenario, Guid RootId)> ArrangeAsync(CancellationToken cancellationToken) => ArrangeAsync(this.database, cancellationToken);

    private static async Task<(TwoTenantScenario Scenario, Guid RootId)> ArrangeAsync(RealDatabase target, CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(target, cancellationToken);
        await TwoTenantScenario.GrantAsync(target, scenario.A, scenario.A.OwnerId, cancellationToken, unitPermissions);
        return (scenario, await TwoTenantScenario.RootUnitIdAsync(target, scenario.A, cancellationToken));
    }

    private async Task<Guid> AddRoleAsync(ScenarioTenant tenant, string name, CancellationToken cancellationToken, params RolePermission[] permissions)
    {
        await using var context = this.database.CreateSystemContext();
        var role = new TenantRole
        {
            TenantId = tenant.TenantId,
            Name = name,
            State = TenantRoleState.Active,
            CreatedById = tenant.OwnerId,
            Permissions = [.. permissions.Select(permission => new TenantRolePermission { TenantId = tenant.TenantId, Permission = permission, CreatedById = tenant.OwnerId })],
        };
        context.TenantRoles.Add(role);
        await context.SaveChangesAsync(cancellationToken);
        return role.Id;
    }

    private async Task<bool> HoldsAsync(ScenarioTenant tenant, Guid userId, RolePermission permission, CancellationToken cancellationToken)
    {
        await using var host = ServiceHost.Create(this.database, tenant.RuntimeOf(userId));
        return await host.Get<PermissionService>().HasPermissionAsync(permission, cancellationToken);
    }

    private async Task SetActiveAsync(ScenarioTenant tenant, Guid unitId, bool active)
    {
        await using var host = ServiceHost.Create(this.database, tenant.Runtime());
        var units = host.Get<OrganizationService>();
        var result = await units.StepAsync(unitId, active ? OrganizationUnitWorkflowAttribute.ReactivateStep : OrganizationUnitWorkflowAttribute.DeactivateStep, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.Error);
    }

    private Task<List<Guid>> UnitsCutOffFromTheRootAsync(ScenarioTenant tenant, CancellationToken cancellationToken) =>
        UnitsCutOffFromTheRootAsync(this.database, tenant, cancellationToken);

    private static async Task<List<Guid>> UnitsCutOffFromTheRootAsync(RealDatabase target, ScenarioTenant tenant, CancellationToken cancellationToken)
    {
        await using var check = target.CreateSystemContext();
        var rows = await check.OrganizationUnits.Where(unit => unit.TenantId == tenant.TenantId)
            .Select(unit => new OrganizationUnitNode(unit.Id, unit.ParentId, unit.Name, null, unit.UnitType, unit.State))
            .ToListAsync(cancellationToken);
        return OrganizationUnitOutline.Of(rows).Where(entry => !entry.IsLinked).Select(entry => entry.Unit.Id).ToList();
    }

    private static OrganizationUnit Draft(string name, OrganizationUnitType type, Guid? parentId) => UnitSteps.Draft(name, type, parentId);
}
