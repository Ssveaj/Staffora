#pragma warning disable xUnit1069

using Staffora.Core.Entities.Organization;

namespace Staffora.Test.Unit.Organization;

public class OrganizationStructurePolicyTests
{
    private static readonly Guid tenant = Guid.NewGuid();
    private static readonly Guid otherTenant = Guid.NewGuid();
    private static readonly Guid root = Guid.NewGuid();
    private static readonly Guid north = Guid.NewGuid();
    private static readonly Guid south = Guid.NewGuid();
    private static readonly Guid harbour = Guid.NewGuid();
    private static readonly Guid team = Guid.NewGuid();

    private static OrganizationStructure Tree() => OrganizationStructure.Of(
    [
        (root, tenant, "Company A", OrganizationUnitType.Company, null),
        (north, tenant, "North", OrganizationUnitType.Region, root),
        (south, tenant, "South", OrganizationUnitType.Region, root),
        (harbour, tenant, "Harbour office", OrganizationUnitType.Branch, north),
        (team, tenant, "Harbour team", OrganizationUnitType.Team, harbour),
    ]);

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_well_formed_tree_breaks_no_rule()
    {
        Assert.Empty(OrganizationStructurePolicy.Validate(Tree()));

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_unit_has_one_parent_link_and_a_move_replaces_it()
    {
        var tree = Tree();

        var moved = tree.Moved(harbour, south);

        Assert.Null(OrganizationStructurePolicy.FirstNewViolation(tree, moved));
        Assert.Equal(south, moved.Find(harbour)!.ParentId);
        Assert.DoesNotContain(harbour, moved.SelfAndDescendants(north));
        Assert.Equal(typeof(Guid?), typeof(OrganizationStructureUnit).GetProperty(nameof(OrganizationStructureUnit.ParentId))!.PropertyType);

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_stored_cycle_ends_every_walk_and_breaks_no_judgement_of_another_change()
    {
        var cycle = OrganizationStructure.Of(
        [
            (root, tenant, "Company A", OrganizationUnitType.Company, null),
            (north, tenant, "North", OrganizationUnitType.Region, south),
            (south, tenant, "South", OrganizationUnitType.Region, north),
            (harbour, tenant, "Harbour office", OrganizationUnitType.Branch, north),
        ]);

        Assert.Equal(
            [OrganizationStructurePolicy.CycleReason("North"), OrganizationStructurePolicy.CycleReason("South"), OrganizationStructurePolicy.CutOffReason("Harbour office")],
            OrganizationStructurePolicy.Validate(cycle).Select(violation => violation.Reason));
        Assert.Equal(
            [OrganizationStructureViolationKind.InCycle, OrganizationStructureViolationKind.InCycle, OrganizationStructureViolationKind.CutOff],
            OrganizationStructurePolicy.Validate(cycle).Select(violation => violation.Kind));
        Assert.Equal(new[] { north, south, harbour }.Order(), cycle.SelfAndDescendants(north).Order());
        Assert.Null(OrganizationStructurePolicy.FirstNewViolation(cycle, cycle.Edited(root, "Company A Ltd", OrganizationUnitType.Company)));
        Assert.Equal(
            OrganizationStructurePolicy.CutOffReason("Lost"),
            OrganizationStructurePolicy.FirstNewViolation(cycle, cycle.WithUnit(Guid.NewGuid(), tenant, "Lost", OrganizationUnitType.Team, south)));
        Assert.Null(OrganizationStructurePolicy.FirstNewViolation(cycle, cycle.Moved(north, root)));

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_unit_in_or_below_a_stored_cycle_is_renamed_without_the_cycle_counting_as_new_but_takes_no_role()
    {
        var cycle = OrganizationStructure.Of(
        [
            (root, tenant, "Company A", OrganizationUnitType.Company, null),
            (north, tenant, "North", OrganizationUnitType.Region, south),
            (south, tenant, "South", OrganizationUnitType.Region, north),
            (harbour, tenant, "Harbour office", OrganizationUnitType.Branch, north),
        ]);

        Assert.Null(OrganizationStructurePolicy.FirstNewViolation(cycle, cycle.Edited(north, "North coast", OrganizationUnitType.Region)));
        Assert.Null(OrganizationStructurePolicy.FirstNewViolation(cycle, cycle.Edited(harbour, "Harbour", OrganizationUnitType.Team)));
        Assert.Equal(OrganizationStructurePolicy.CutOffReason("South"), OrganizationStructurePolicy.FirstNewViolation(cycle, cycle.Moved(north, harbour)));

        Assert.Equal(OrganizationStructurePolicy.CutOffHoldsNoRoles("North"), OrganizationStructurePolicy.CheckRoleHolding(cycle, north));
        Assert.Equal(OrganizationStructurePolicy.CutOffHoldsNoRoles("Harbour office"), OrganizationStructurePolicy.CheckRoleHolding(cycle, harbour));
        Assert.Null(OrganizationStructurePolicy.CheckRoleHolding(cycle, root));
        Assert.Null(OrganizationStructurePolicy.CheckRoleHolding(Tree(), team));
        var grouping = Guid.NewGuid();
        Assert.Equal(
            OrganizationStructurePolicy.GroupingHoldsNoRoles,
            OrganizationStructurePolicy.CheckRoleHolding(Tree().WithUnit(grouping, tenant, "Specialists", OrganizationUnitType.Grouping, root), grouping));

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_move_below_one_of_its_own_units_is_refused_as_a_cycle()
    {
        var tree = Tree();

        var reason = OrganizationStructurePolicy.FirstNewViolation(tree, tree.Moved(north, team));

        Assert.Equal("North would sit below itself. A unit cannot be moved below one of its own units.", reason);

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_unit_is_never_its_own_parent()
    {
        var tree = Tree();

        Assert.Equal(
            "Harbour team would sit below itself. A unit cannot be moved below one of its own units.",
            OrganizationStructurePolicy.FirstNewViolation(tree, tree.Moved(team, team)));

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_second_Company_unit_is_refused()
    {
        var tree = Tree();

        var reason = OrganizationStructurePolicy.FirstNewViolation(tree, tree.WithUnit(Guid.NewGuid(), tenant, "Company B", OrganizationUnitType.Company, null));

        Assert.Equal("Company B would be a second Company unit. A tenant has one, the root of its tree.", reason);

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task The_first_Company_unit_of_a_tenant_is_its_root()
    {
        var empty = OrganizationStructure.Of([]);

        Assert.Null(OrganizationStructurePolicy.FirstNewViolation(empty, empty.WithUnit(root, tenant, "Company A", OrganizationUnitType.Company, null)));

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task The_Company_root_takes_no_parent_and_keeps_its_type()
    {
        var tree = Tree();

        Assert.Equal("The Company unit is the root of the tree and has no parent.", OrganizationStructurePolicy.FirstNewViolation(tree, tree.Moved(root, south)));
        Assert.Equal(
            "Company A needs a parent: every unit except the Company root sits below another unit.",
            OrganizationStructurePolicy.FirstNewViolation(tree, tree.Edited(root, "Company A", OrganizationUnitType.Region)));
        Assert.Equal(
            "The Company unit is the root of the tree and has no parent.",
            OrganizationStructurePolicy.FirstNewViolation(tree, tree.Edited(north, "North", OrganizationUnitType.Company)));

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task Every_other_unit_needs_a_parent()
    {
        var tree = Tree();

        var reason = OrganizationStructurePolicy.FirstNewViolation(tree, tree.WithUnit(Guid.NewGuid(), tenant, "Floating", OrganizationUnitType.Team, null));

        Assert.Equal("Floating needs a parent: every unit except the Company root sits below another unit.", reason);

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_parent_of_another_tenant_or_none_at_all_is_refused()
    {
        var foreignRoot = Guid.NewGuid();
        var tree = OrganizationStructure.Of(
        [
            (root, tenant, "Company A", OrganizationUnitType.Company, null),
            (foreignRoot, otherTenant, "Company B", OrganizationUnitType.Company, null),
        ]);

        Assert.Equal(
            "The parent of Planted belongs to another tenant.",
            OrganizationStructurePolicy.FirstNewViolation(tree, tree.WithUnit(Guid.NewGuid(), tenant, "Planted", OrganizationUnitType.Branch, foreignRoot)));
        Assert.Equal(
            "The parent of Lost was not found in this tenant.",
            OrganizationStructurePolicy.FirstNewViolation(tree, tree.WithUnit(Guid.NewGuid(), tenant, "Lost", OrganizationUnitType.Branch, Guid.NewGuid())));

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task Names_are_unique_among_siblings_but_not_across_the_tree()
    {
        var tree = Tree();

        Assert.Equal(
            "Company A already has a unit named North.",
            OrganizationStructurePolicy.FirstNewViolation(tree, tree.WithUnit(Guid.NewGuid(), tenant, " north ", OrganizationUnitType.Region, root)));
        Assert.Null(OrganizationStructurePolicy.FirstNewViolation(tree, tree.WithUnit(Guid.NewGuid(), tenant, "North", OrganizationUnitType.Team, south)));

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_grouping_holds_no_roles_and_a_unit_with_roles_does_not_become_one()
    {
        Assert.Equal(OrganizationStructurePolicy.GroupingHoldsNoRoles, OrganizationStructurePolicy.CheckRoleHolding(OrganizationUnitType.Grouping));
        Assert.All(
            Enum.GetValues<OrganizationUnitType>().Where(type => type != OrganizationUnitType.Grouping),
            type => Assert.Null(OrganizationStructurePolicy.CheckRoleHolding(type)));

        Assert.Equal(
            "Harbour team holds 2 role assignments, and a grouping holds none. Remove them before making it a grouping.",
            OrganizationStructurePolicy.CheckTypeChange("Harbour team", OrganizationUnitType.Team, OrganizationUnitType.Grouping, holdings: 2));
        Assert.Null(OrganizationStructurePolicy.CheckTypeChange("Harbour team", OrganizationUnitType.Team, OrganizationUnitType.Grouping, holdings: 0));
        Assert.Null(OrganizationStructurePolicy.CheckTypeChange("Harbour team", OrganizationUnitType.Team, OrganizationUnitType.Branch, holdings: 2));

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_grouping_may_order_units_below_it()
    {
        var tree = Tree();
        var grouping = Guid.NewGuid();
        var grouped = tree.WithUnit(grouping, tenant, "Specialists", OrganizationUnitType.Grouping, root);

        Assert.Null(OrganizationStructurePolicy.FirstNewViolation(tree, grouped));
        Assert.Null(OrganizationStructurePolicy.FirstNewViolation(grouped, grouped.WithUnit(Guid.NewGuid(), tenant, "Language team", OrganizationUnitType.Team, grouping)));
        Assert.DoesNotContain(OrganizationUnitType.Company, OrganizationStructurePolicy.TypesBelowRoot);

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_change_is_judged_by_what_it_makes_worse_not_by_what_was_already_wrong()
    {
        var broken = Tree().WithUnit(Guid.NewGuid(), tenant, "Stray company", OrganizationUnitType.Company, null);
        Assert.NotEmpty(OrganizationStructurePolicy.Validate(broken));

        Assert.Null(OrganizationStructurePolicy.FirstNewViolation(broken, broken.Edited(south, "South coast", OrganizationUnitType.Region)));

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task The_units_a_unit_may_not_move_below_are_itself_and_its_own_units()
    {
        Assert.Equal(new[] { north, harbour, team }.Order(), Tree().SelfAndDescendants(north).Order());
        Assert.Equal([south], Tree().SelfAndDescendants(south));

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_change_leaves_the_structure_it_was_made_on_as_it_was()
    {
        var tree = Tree();

        _ = tree.Moved(harbour, south).Moved(team, north).Edited(root, "Renamed", OrganizationUnitType.Company);

        Assert.Equal(north, tree.Find(harbour)!.ParentId);
        Assert.Equal(harbour, tree.Find(team)!.ParentId);
        Assert.Equal("Company A", tree.Find(root)!.Name);

        return Task.CompletedTask;
    }
}
