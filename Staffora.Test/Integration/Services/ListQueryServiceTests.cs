using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Services.Services;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class ListQueryServiceTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public ListQueryServiceTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_user_list_searches_filters_groups_sorts_and_pages_the_tenants_users_in_the_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.RolesView);
        var ada = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Ada", cancellationToken);
        var bob = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Bob", cancellationToken);
        var zeds = new List<Guid>();
        for (var number = 1; number <= 25; number++)
        {
            zeds.Add(await TwoTenantScenario.AddUserAsync(this.database, scenario.A, $"Zed{number:00}", cancellationToken));
        }

        var otherAda = await TwoTenantScenario.AddUserAsync(this.database, scenario.B, "Ada", cancellationToken);
        await DeactivateAsync(bob, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var users = host.Get<UserService>();
        var query = UserList.Definition.Default;

        var first = Page(await users.QueryUsersAsync(query));
        Assert.Equal((28, 1, 2), (first.TotalCount, first.Page, first.PageCount));
        Assert.Equal(["Ada", "Bob", "Owner", "Zed01"], first.Rows.Take(4).Select(FirstName));
        Assert.Equal(25, first.Rows.Count());
        Assert.DoesNotContain(first.Rows, row => row.Id == otherAda || row.Id == scenario.A.SystemAccountId);
        Assert.Equal([null], first.Sections.Select(section => section.Key));

        Assert.Equal(["Zed23", "Zed24", "Zed25"], Page(await users.QueryUsersAsync(query with { Page = 2 })).Rows.Select(FirstName));
        var past = Page(await users.QueryUsersAsync(query with { Page = 40 }));
        Assert.Equal(2, past.Page);
        Assert.Equal(["Zed23", "Zed24", "Zed25"], past.Rows.Select(FirstName));

        var byName = Page(await users.QueryUsersAsync(query with { Search = "ada company" }));
        Assert.Equal((1, ada), (byName.TotalCount, byName.Rows.Single().Id));
        var byAddress = Page(await users.QueryUsersAsync(query with { Search = "zed07." }));
        Assert.Equal(zeds[6], byAddress.Rows.Single().Id);

        Assert.Equal(0, Page(await users.QueryUsersAsync(query with { Search = "%" })).TotalCount);
        Assert.Equal(0, Page(await users.QueryUsersAsync(query with { Search = "Zed_1" })).TotalCount);

        var inactive = Page(await users.QueryUsersAsync(query.WithFilter(UserList.Status, UserList.Inactive)));
        Assert.Equal((1, bob, UserState.Inactive), (inactive.TotalCount, inactive.Rows.Single().Id, inactive.Rows.Single().State));
        Assert.Equal(27, Page(await users.QueryUsersAsync(query.WithFilter(UserList.Status, "ACTIVE"))).TotalCount);

        var byEmail = Page(await users.QueryUsersAsync(query with { Sort = UserList.SortEmail, Descending = true }));
        Assert.Equal(["Zed25", "Zed24"], byEmail.Rows.Take(2).Select(FirstName));

        var grouped = Page(await users.QueryUsersAsync(query with { Group = UserList.ByStatus, Page = 2 }));
        Assert.Equal([(UserList.Active, "Active", 27), (UserList.Inactive, "Inactive", 1)], grouped.Sections.Select(section => (section.Key!, section.Label!, section.TotalCount)));
        Assert.Equal([["Zed24", "Zed25"], ["Bob"]], grouped.Sections.Select(section => section.Rows.Select(FirstName).ToList()));
        var groupedFirst = Page(await users.QueryUsersAsync(query with { Group = UserList.ByStatus }));
        Assert.Equal(["Ada", "Owner", "Zed01"], groupedFirst.Rows.Take(3).Select(FirstName));

        var crafted = Page(await users.QueryUsersAsync(query.WithFilter("tenant", scenario.B.TenantId.ToString()) with { PageSize = 1000 }));
        Assert.Equal((28, 25), (crafted.TotalCount, crafted.Rows.Count()));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_user_list_names_role_holdings_only_to_a_reader_of_them_and_refuses_a_caller_without_UsersView()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.RolesView);
        var viewer = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Viewer", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, viewer, cancellationToken, RolePermission.UsersView);
        var outsider = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Outsider", cancellationToken);
        var role = await RoleNameAsync(scenario.A.OwnerId, cancellationToken);
        var search = UserList.Definition.Default with { Search = "Owner" };

        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var owner = Page(await host.Get<UserService>().QueryUsersAsync(search)).Rows.Single();
            Assert.Equal([$"{role} on {scenario.A.Name}"], owner.Roles);
            var none = Page(await host.Get<UserService>().QueryUsersAsync(UserList.Definition.Default with { Search = "Outsider" })).Rows.Single();
            Assert.Equal([], none.Roles!);
        }

        await using (var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(viewer)))
        {
            Assert.Null(Page(await host.Get<UserService>().QueryUsersAsync(search)).Rows.Single().Roles);
        }

        await using (var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(outsider)))
        {
            var refused = await host.Get<UserService>().QueryUsersAsync(search);
            Assert.False(refused.IsSuccess);
            Assert.Equal("You do not have permission to view users.", refused.Error);
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_user_list_of_one_tenant_never_shows_another_tenants_users()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, RolePermission.UsersView);
        var aUsers = new[] { scenario.A.OwnerId, await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Shared", cancellationToken) };
        var bShared = await TwoTenantScenario.AddUserAsync(this.database, scenario.B, "Shared", cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.B.Runtime());
        var all = Page(await host.Get<UserService>().QueryUsersAsync(UserList.Definition.Default));
        var shared = Page(await host.Get<UserService>().QueryUsersAsync(UserList.Definition.Default with { Search = "Shared" }));

        Assert.Equal(2, all.TotalCount);
        Assert.Equal(new[] { scenario.B.OwnerId, bShared }.Order(), all.Rows.Select(row => row.Id).Order());
        Assert.DoesNotContain(all.Rows, row => aUsers.Contains(row.Id));
        Assert.Equal(bShared, shared.Rows.Single().Id);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_unit_list_searches_filters_toggles_groups_and_sorts_the_tenants_units_in_the_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, units) = await ArrangeTreeAsync(cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<OrganizationService>();
        var query = OrganizationUnitList.Definition.Default;

        var page = Page(await service.QueryUnitsAsync(query));
        Assert.Equal((4, 2), (page.TotalCount, page.HiddenCounts[OrganizationUnitList.ShowInactive]));
        Assert.Equal(
            [(OrganizationService.TopOfTreeLabel, 1, scenario.A.Name), ($"Below {scenario.A.Name}", 1, "North"), ($"Below {scenario.A.Name} › North", 2, "Sales Support")],
            page.Sections.Select(section => (section.Label!, section.TotalCount, string.Join(' ', section.Rows.Select(row => row.Name)))));
        var sales = page.Rows.Single(row => row.Name == "Sales");
        Assert.Equal(($"{scenario.A.Name} › North", $"{scenario.A.Name} › North › Sales", OrganizationUnitType.Branch, "Sells jobs", 0, 1), (sales.ParentPath, sales.Path, sales.UnitType, sales.Description, sales.UnitsBelow, sales.RoleHoldings));
        var root = page.Rows.Single(row => row.IsRoot);
        Assert.Equal((string.Empty, 2, 1), (root.ParentPath, root.UnitsBelow, root.RoleHoldings));
        Assert.DoesNotContain(page.Rows, row => row.Id == units.OtherNorth);

        var withInactive = Page(await service.QueryUnitsAsync(query.WithToggle(OrganizationUnitList.ShowInactive, true)));
        Assert.Equal((6, false), (withInactive.TotalCount, withInactive.HiddenCounts.ContainsKey(OrganizationUnitList.ShowInactive)));
        Assert.Equal(["North", "South"], withInactive.Sections.Single(section => section.Label == $"Below {scenario.A.Name}").Rows.Select(row => row.Name));
        Assert.Equal(OrganizationUnitState.Inactive, withInactive.Rows.Single(row => row.Name == "South").State);

        var teams = Page(await service.QueryUnitsAsync(query.WithFilter(OrganizationUnitList.Type, "TEAM") with { Group = null }));
        Assert.Equal(["Support"], teams.Rows.Select(row => row.Name));
        Assert.Equal(1, teams.HiddenCounts[OrganizationUnitList.ShowInactive]);
        var regions = Page(await service.QueryUnitsAsync(query.WithFilter(OrganizationUnitList.Type, "region") with { Group = null }));
        Assert.Equal(["North"], regions.Rows.Select(row => row.Name));
        Assert.Equal(1, regions.HiddenCounts[OrganizationUnitList.ShowInactive]);

        Assert.Equal(["Sales"], Page(await service.QueryUnitsAsync(query with { Search = "sells" })).Rows.Select(row => row.Name));
        Assert.Equal([units.North], Page(await service.QueryUnitsAsync(query with { Search = "nor" })).Rows.Select(row => row.Id));

        var byType = Page(await service.QueryUnitsAsync(query with { Group = OrganizationUnitList.ByType }));
        Assert.Equal([("company", "Company", 1), ("region", "Region", 1), ("branch", "Branch", 1), ("team", "Team", 1)], byType.Sections.Select(section => (section.Key!, section.Label!, section.TotalCount)));
        var typeDescending = Page(await service.QueryUnitsAsync(query with { Group = null, Sort = OrganizationUnitList.SortType, Descending = true }));
        Assert.Equal(["Support", "Sales", "North", scenario.A.Name], typeDescending.Rows.Select(row => row.Name));
        var nameDescending = Page(await service.QueryUnitsAsync(query with { Group = null, Descending = true }));
        Assert.Equal(["Support", "Sales", "North", scenario.A.Name], nameDescending.Rows.Select(row => row.Name));

        await using var other = ServiceHost.Create(this.database, scenario.B.Runtime());
        var theirs = Page(await other.Get<OrganizationService>().QueryUnitsAsync(query));
        Assert.Equal(2, theirs.TotalCount);
        Assert.Equal([units.OtherRoot, units.OtherNorth], theirs.Rows.Select(row => row.Id));
        Assert.Equal($"Below {scenario.B.Name}", theirs.Sections[1].Label);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_unit_list_pages_in_the_database_and_refuses_a_caller_without_OrganizationUnitsView()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, units) = await ArrangeTreeAsync(cancellationToken);
        await using (var system = this.database.CreateSystemContext())
        {
            for (var number = 1; number <= 22; number++)
            {
                system.OrganizationUnits.Add(new OrganizationUnit { TenantId = scenario.A.TenantId, ParentId = units.Support, Name = $"Crew {number:00}", UnitType = OrganizationUnitType.Team, State = OrganizationUnitState.Active, CreatedById = scenario.A.OwnerId });
            }

            await system.SaveChangesAsync(cancellationToken);
        }

        var outsider = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Outsider", cancellationToken);
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var service = host.Get<OrganizationService>();
            var query = OrganizationUnitList.Definition.Default with { Group = null };

            var first = Page(await service.QueryUnitsAsync(query));
            Assert.Equal((26, 2, 25), (first.TotalCount, first.PageCount, first.Rows.Count()));
            Assert.Equal([scenario.A.Name, "Crew 01"], first.Rows.Take(2).Select(row => row.Name));
            var second = Page(await service.QueryUnitsAsync(query with { Page = 2 }));
            Assert.Equal(["Support"], second.Rows.Select(row => row.Name));
            Assert.Equal(2, Page(await service.QueryUnitsAsync(query with { Page = 99 })).Page);
            Assert.Empty(first.Rows.Select(row => row.Id).Intersect(second.Rows.Select(row => row.Id)));

            Assert.Equal(22, second.Rows.Single(row => row.Name == "Support").UnitsBelow);
        }

        await using (var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(outsider)))
        {
            var refused = await host.Get<OrganizationService>().QueryUnitsAsync(OrganizationUnitList.Definition.Default);
            Assert.Equal((false, "You do not have permission to view organization units."), (refused.IsSuccess, refused.Error));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_stored_cycle_neither_hangs_the_unit_list_nor_loses_its_units()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, units) = await ArrangeTreeAsync(cancellationToken);

        await using (var system = this.database.CreateSystemContext())
        {
            await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE OrganizationUnits SET ParentId = {units.Sales} WHERE Id = {units.North}", cancellationToken);
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var page = Page(await host.Get<OrganizationService>().QueryUnitsAsync(OrganizationUnitList.Definition.Default));

        Assert.Equal(4, page.TotalCount);
        Assert.Equal("North", page.Rows.Single(row => row.Name == "Sales").ParentPath);
        Assert.Equal("Sales", page.Rows.Single(row => row.Name == "North").ParentPath);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Both_lists_cut_their_page_in_SQL_with_a_fixed_number_of_commands()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, units) = await ArrangeTreeAsync(cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView);
        for (var number = 1; number <= 3; number++)
        {
            await TwoTenantScenario.AddUserAsync(this.database, scenario.A, $"Paged{number}", cancellationToken);
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var users = host.Get<UserService>();
        var service = host.Get<OrganizationService>();

        Page(await users.QueryUsersAsync(UserList.Definition.Default));
        Page(await service.QueryUnitsAsync(OrganizationUnitList.Definition.Default));

        host.ClearCommandLog();
        Page(await users.QueryUsersAsync(UserList.Definition.Default with { Page = 1 }));
        var userCommands = host.CommandLog;
        Assert.Equal(3, userCommands.Count);
        Assert.Contains("OFFSET", RowsCommand(userCommands, "FROM [AspNetUsers]"));
        Assert.Contains("FETCH NEXT", RowsCommand(userCommands, "FROM [AspNetUsers]"));

        host.ClearCommandLog();
        Page(await service.QueryUnitsAsync(OrganizationUnitList.Definition.Default));
        var grouped = host.CommandLog;
        Assert.Equal(5, grouped.Count);
        Assert.Contains("FETCH NEXT", RowsCommand(grouped, "FROM [OrganizationUnits]"));
        host.ClearCommandLog();
        Page(await service.QueryUnitsAsync(OrganizationUnitList.Definition.Default with { Group = null }));
        var ungrouped = host.CommandLog;
        Assert.Equal(4, ungrouped.Count);
        Assert.Contains("FETCH NEXT", RowsCommand(ungrouped, "FROM [OrganizationUnits]"));

        await using (var system = this.database.CreateSystemContext())
        {
            for (var number = 1; number <= 22; number++)
            {
                system.OrganizationUnits.Add(new OrganizationUnit { TenantId = scenario.A.TenantId, ParentId = units.Support, Name = $"Crew {number:00}", UnitType = OrganizationUnitType.Team, State = OrganizationUnitState.Active, CreatedById = scenario.A.OwnerId });
            }

            await system.SaveChangesAsync(cancellationToken);
        }

        host.ClearCommandLog();
        var second = Page(await service.QueryUnitsAsync(OrganizationUnitList.Definition.Default with { Page = 2 }));
        Assert.Equal((2, 5), (second.Page, host.CommandLog.Count));
        Assert.Contains("FETCH NEXT", RowsCommand(host.CommandLog, "FROM [OrganizationUnits]"));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_unit_list_counts_role_holdings_only_for_a_reader_of_them()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _) = await ArrangeTreeAsync(cancellationToken);
        var viewer = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Viewer", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, viewer, cancellationToken, RolePermission.OrganizationUnitsView);
        var query = OrganizationUnitList.Definition.Default with { Group = null };

        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var rows = Page(await host.Get<OrganizationService>().QueryUnitsAsync(query)).Rows.ToList();
            Assert.Equal(1, rows.Single(row => row.Name == "Sales").RoleHoldings);
            Assert.All(rows, row => Assert.NotNull(row.RoleHoldings));
        }

        await using (var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(viewer)))
        {
            var rows = Page(await host.Get<OrganizationService>().QueryUnitsAsync(query)).Rows.ToList();
            Assert.Equal(4, rows.Count);
            Assert.All(rows, row => Assert.Null(row.RoleHoldings));
            Assert.Equal(0, rows.Single(row => row.Name == "Sales").UnitsBelow);
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Grouped_by_parent_the_groups_follow_their_parents_paths_across_pages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, units) = await ArrangeTreeAsync(cancellationToken);
        var southId = await UnitIdAsync(scenario.A.TenantId, "South", cancellationToken);

        Guid zeta;
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var service = host.Get<OrganizationService>();
            Assert.True((await service.StepAsync(southId, OrganizationUnitWorkflowAttribute.ReactivateStep, cancellationToken)).IsSuccess);
            zeta = await CreateAsync(service, "Zeta", OrganizationUnitType.Branch, units.North);
            var beta = await CreateAsync(service, "Beta", OrganizationUnitType.Branch, southId);
            await CreateAsync(service, "Beta crew", OrganizationUnitType.Team, beta);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            for (var number = 1; number <= 24; number++)
            {
                system.OrganizationUnits.Add(new OrganizationUnit { TenantId = scenario.A.TenantId, ParentId = zeta, Name = $"Crew {number:00}", UnitType = OrganizationUnitType.Team, State = OrganizationUnitState.Active, CreatedById = scenario.A.OwnerId });
            }

            await system.SaveChangesAsync(cancellationToken);
        }

        await using var reader = ServiceHost.Create(this.database, scenario.A.Runtime());
        var list = reader.Get<OrganizationService>();
        var query = OrganizationUnitList.Definition.Default;

        var first = Page(await list.QueryUnitsAsync(query));
        Assert.Equal((32, 2), (first.TotalCount, first.PageCount));
        Assert.Equal(
            [(OrganizationService.TopOfTreeLabel, 1, 1), ($"Below {scenario.A.Name}", 2, 2), ($"Below {scenario.A.Name} › North", 3, 3), ($"Below {scenario.A.Name} › North › Zeta", 24, 19)],
            first.Sections.Select(section => (section.Label!, section.TotalCount, section.Rows.Count)));

        var second = Page(await list.QueryUnitsAsync(query with { Page = 2 }));
        Assert.Equal(
            [($"Below {scenario.A.Name} › North › Zeta", 24, "Crew 20 Crew 21 Crew 22 Crew 23 Crew 24"), ($"Below {scenario.A.Name} › South", 1, "Beta"), ($"Below {scenario.A.Name} › South › Beta", 1, "Beta crew")],
            second.Sections.Select(section => (section.Label!, section.TotalCount, string.Join(' ', section.Rows.Select(row => row.Name)))));

        var ids = first.Rows.Concat(second.Rows).Select(row => row.Id).ToList();
        Assert.Equal(32, ids.Distinct().Count());

        var descending = Page(await list.QueryUnitsAsync(query with { Descending = true }));
        Assert.Equal(["Zeta", "Support", "Sales"], descending.Sections[2].Rows.Select(row => row.Name));
        Assert.Equal("Crew 24", descending.Sections[3].Rows[0].Name);
    }

    private static string RowsCommand(IReadOnlyList<string> commands, string table) =>
        Assert.Single(commands, command => command.Contains(table, StringComparison.Ordinal) && command.Contains("ORDER BY", StringComparison.Ordinal) && !command.Contains("GROUP BY", StringComparison.Ordinal));

    private async Task<Guid> UnitIdAsync(Guid tenantId, string name, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.OrganizationUnits.Where(unit => unit.TenantId == tenantId && unit.Name == name).Select(unit => unit.Id).SingleAsync(cancellationToken);
    }

    private async Task<(TwoTenantScenario Scenario, TreeIds Units)> ArrangeTreeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken,
            RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate, RolePermission.OrganizationUnitsEdit, RolePermission.RolesView);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate);
        var rootA = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        var rootB = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.B, cancellationToken);

        Guid north, sales, support, otherNorth;
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var service = host.Get<OrganizationService>();
            north = await CreateAsync(service, "North", OrganizationUnitType.Region, rootA);
            var south = await CreateAsync(service, "South", OrganizationUnitType.Region, rootA);
            sales = await CreateAsync(service, "Sales", OrganizationUnitType.Branch, north, "Sells jobs");
            support = await CreateAsync(service, "Support", OrganizationUnitType.Team, north);
            await CreateAsync(service, "Helpdesk", OrganizationUnitType.Team, south);
            Assert.True((await service.StepAsync(south, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).IsSuccess);
        }

        await using (var host = ServiceHost.Create(this.database, scenario.B.Runtime()))
        {
            otherNorth = await CreateAsync(host.Get<OrganizationService>(), "North", OrganizationUnitType.Region, rootB);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            var role = new TenantRole { TenantId = scenario.A.TenantId, Name = "Sales lead", State = TenantRoleState.Active, CreatedById = scenario.A.OwnerId };
            system.TenantRoles.Add(role);
            await system.SaveChangesAsync(cancellationToken);
            system.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { TenantId = scenario.A.TenantId, UserId = scenario.A.OwnerId, OrganizationUnitId = sales, TenantRoleId = role.Id, CreatedById = scenario.A.OwnerId });
            await system.SaveChangesAsync(cancellationToken);
        }

        return (scenario, new TreeIds(north, sales, support, rootB, otherNorth));
    }

    private static async Task<Guid> CreateAsync(OrganizationService service, string name, OrganizationUnitType type, Guid parentId, string? description = null)
    {
        var created = await service.CreateAsync(UnitSteps.Draft(name, type, parentId, description), TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private Task DeactivateAsync(Guid userId, CancellationToken cancellationToken) =>
        UserEdits.SetStateInTableAsync(this.database, userId, UserState.Inactive, cancellationToken);

    private async Task<string> RoleNameAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.OrganizationUnitUserRoles.Where(holding => holding.UserId == userId).Select(holding => holding.TenantRole.Name).SingleAsync(cancellationToken);
    }

    private static ListPage<T> Page<T>(Result<ListPage<T>> result)
    {
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    private static string FirstName(UserListItemDto row) => row.DisplayName.Split(' ')[0];

    private sealed record TreeIds(Guid North, Guid Sales, Guid Support, Guid OtherRoot, Guid OtherNorth);
}
