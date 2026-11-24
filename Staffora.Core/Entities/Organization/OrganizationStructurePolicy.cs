namespace Staffora.Core.Entities.Organization;

public static class OrganizationStructurePolicy
{
    public static string CycleReason(string unitName) => $"{unitName} would sit below itself. A unit cannot be moved below one of its own units.";

    public static string CutOffReason(string unitName) => $"{unitName} would no longer sit below the Company root: the units above it would form a cycle.";

    public static string SiblingNameReason(string parentName, string siblingName) => $"{parentName} already has a unit named {siblingName}.";

    public const string RootStaysActive = "The Company unit is the whole company and stays active.";

    public static string InUseBelowOutOfUseReason(string unitName) =>
        $"{unitName} is in use, so the unit above it must be in use as well. Reactivate that unit first, or choose another.";

    public static string OutOfUseAboveInUseReason(string unitName) =>
        $"{unitName} would be out of use while a unit below it is still in use. Deactivate the units below it with it.";

    public static string BelowArchivedReason(string unitName) =>
        $"{unitName} would sit below an archived unit, where only archived units sit. Restore that unit first, or choose another.";

    public static string ArchivedAboveUnarchivedReason(string unitName) =>
        $"{unitName} would be archived while a unit below it is not. Archive the units below it first.";

    public static string UnitsBelowReason(string unitName) =>
        $"{unitName} has units below it, so it is not deleted. Delete or move them first, or archive it instead.";

    public static string? CheckPlaceInUse(string unitName, OrganizationUnitState state, OrganizationUnitState parentState, OrganizationUnitType parentType)
    {
        var parentInUse = parentState == OrganizationUnitState.Active || parentType == OrganizationUnitType.Company;
        if (state == OrganizationUnitState.Active && !parentInUse)
        {
            return InUseBelowOutOfUseReason(unitName);
        }

        return state != OrganizationUnitState.Archived && parentState == OrganizationUnitState.Archived && parentType != OrganizationUnitType.Company
            ? BelowArchivedReason(unitName)
            : null;
    }

    public const string RootIsNeverDeleted = "The Company unit is the whole company and is never deleted.";

    public const string SystemOwnerOnRootOnly = "The System owner role is held only on the Company unit, the whole company.";

    public const string GroupingHoldsNoRoles = "A grouping only orders the tree: it holds no roles or members. Choose a unit below it, or the Company root.";

    public static string CutOffHoldsNoRoles(string unitName) =>
        $"{unitName} does not sit below the Company root: the units above it form a cycle. Move it back below the root before giving a role on it.";

    public static string OutOfUseHoldsNoRoles(string unitName) =>
        $"{unitName} is not in use, so it takes no new role holdings. Reactivate it first.";

    public static IReadOnlyList<OrganizationUnitType> TypesBelowRoot { get; } =
        [OrganizationUnitType.Region, OrganizationUnitType.Branch, OrganizationUnitType.Team, OrganizationUnitType.Grouping];

    public static bool IsGrouping(OrganizationUnitType type) => type == OrganizationUnitType.Grouping;

    public static string? CheckRoleHolding(OrganizationUnitType unitType) => IsGrouping(unitType) ? GroupingHoldsNoRoles : null;

    public static string? CheckRoleHolding(OrganizationStructure structure, Guid unitId)
    {
        ArgumentNullException.ThrowIfNull(structure);
        if (structure.Find(unitId) is not { } unit)
        {
            return null;
        }

        return CheckRoleHolding(unit.Type) ?? (structure.Cycles().CutOff.Contains(unitId) ? CutOffHoldsNoRoles(unit.Name) : null);
    }

    public static string? CheckTypeChange(string unitName, OrganizationUnitType from, OrganizationUnitType to, int holdings) =>
        from != to && IsGrouping(to) && holdings > 0
            ? $"{unitName} holds {holdings} role assignment{(holdings == 1 ? string.Empty : "s")}, and a grouping holds none. Remove them before making it a grouping."
            : null;

    public static IReadOnlyList<OrganizationStructureViolation> Validate(OrganizationStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        var (inCycles, cutOff) = structure.Cycles();
        var violations = new List<OrganizationStructureViolation>();
        foreach (var unit in structure.Units.OrderBy(unit => unit.Order))
        {
            if (Check(structure, unit, inCycles, cutOff) is { } violation)
            {
                violations.Add(new OrganizationStructureViolation(unit.Id, violation.Kind, violation.Reason));
            }
        }

        return violations;
    }

    public static string? FirstNewViolation(OrganizationStructure before, OrganizationStructure after)
    {
        var existing = Validate(before).Select(violation => (violation.UnitId, violation.Kind)).ToHashSet();
        return Validate(after).FirstOrDefault(violation => !existing.Contains((violation.UnitId, violation.Kind)))?.Reason;
    }

    private static (OrganizationStructureViolationKind Kind, string Reason)? Check(OrganizationStructure structure, OrganizationStructureUnit unit, HashSet<Guid> inCycles, HashSet<Guid> cutOff)
    {
        var parent = unit.ParentId is { } parentId ? structure.Find(parentId) : null;
        if (unit.ParentId is not null && parent is null)
        {
            return (OrganizationStructureViolationKind.ParentNotFound, $"The parent of {unit.Name} was not found in this tenant.");
        }

        if (parent is not null && parent.TenantId != unit.TenantId)
        {
            return (OrganizationStructureViolationKind.ParentInAnotherTenant, $"The parent of {unit.Name} belongs to another tenant.");
        }

        if (unit.Type == OrganizationUnitType.Company && parent is not null)
        {
            return (OrganizationStructureViolationKind.RootWithParent, "The Company unit is the root of the tree and has no parent.");
        }

        if (inCycles.Contains(unit.Id))
        {
            return (OrganizationStructureViolationKind.InCycle, CycleReason(unit.Name));
        }

        if (cutOff.Contains(unit.Id))
        {
            return (OrganizationStructureViolationKind.CutOff, CutOffReason(unit.Name));
        }

        if (unit.Type == OrganizationUnitType.Company)
        {
            var root = structure.Units.Where(other => other.TenantId == unit.TenantId && other.Type == OrganizationUnitType.Company).MinBy(other => other.Order);
            if (root is not null && root.Id != unit.Id)
            {
                return (OrganizationStructureViolationKind.SecondRoot, $"{unit.Name} would be a second Company unit. A tenant has one, the root of its tree.");
            }

            return null;
        }

        if (parent is null)
        {
            return (OrganizationStructureViolationKind.NoParent, $"{unit.Name} needs a parent: every unit except the Company root sits below another unit.");
        }

        var namesake = structure.Units
            .Where(other => other.Id != unit.Id && other.ParentId == parent.Id && string.Equals(other.Name.Trim(), unit.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            .MinBy(other => other.Order);
        if (namesake is not null && namesake.Order < unit.Order)
        {
            return (OrganizationStructureViolationKind.SiblingName, SiblingNameReason(parent.Name, namesake.Name));
        }

        return null;
    }
}

public sealed record OrganizationStructureViolation(Guid UnitId, OrganizationStructureViolationKind Kind, string Reason);

public enum OrganizationStructureViolationKind
{
    ParentNotFound = 0,

    ParentInAnotherTenant = 1,

    RootWithParent = 2,

    InCycle = 3,

    CutOff = 4,

    SecondRoot = 5,

    NoParent = 6,

    SiblingName = 7,
}

public sealed class OrganizationStructureUnit
{
    internal OrganizationStructureUnit(Guid id, Guid tenantId, string name, OrganizationUnitType type, int order)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Type = type;
        Order = order;
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public string Name { get; internal set; }
    public OrganizationUnitType Type { get; internal set; }

    public Guid? ParentId { get; internal set; }

    internal int Order { get; }
}

public sealed class OrganizationStructure
{
    private readonly Dictionary<Guid, OrganizationStructureUnit> units = [];

    public static OrganizationStructure Of(IEnumerable<(Guid Id, Guid TenantId, string Name, OrganizationUnitType Type, Guid? ParentId)> units)
    {
        var structure = new OrganizationStructure();
        foreach (var unit in units)
        {
            structure.Add(unit.Id, unit.TenantId, unit.Name, unit.Type, unit.ParentId);
        }

        return structure;
    }

    public IEnumerable<OrganizationStructureUnit> Units => this.units.Values;

    public OrganizationStructureUnit? Find(Guid id) => this.units.GetValueOrDefault(id);

    public OrganizationStructure WithUnit(Guid id, Guid tenantId, string name, OrganizationUnitType type, Guid? parentId)
    {
        var copy = Clone();
        copy.Add(id, tenantId, name, type, parentId);
        return copy;
    }

    public OrganizationStructure Moved(Guid unitId, Guid? parentId)
    {
        var copy = Clone();
        copy.Require(unitId).ParentId = parentId;
        return copy;
    }

    public OrganizationStructure Edited(Guid unitId, string name, OrganizationUnitType type)
    {
        var copy = Clone();
        var unit = copy.Require(unitId);
        unit.Name = name;
        unit.Type = type;
        return copy;
    }

    public HashSet<Guid> SelfAndDescendants(Guid unitId)
    {
        var found = new HashSet<Guid> { unitId };
        var pending = new Queue<Guid>([unitId]);
        while (pending.TryDequeue(out var id))
        {
            foreach (var child in this.units.Values.Where(unit => unit.ParentId == id))
            {
                if (found.Add(child.Id))
                {
                    pending.Enqueue(child.Id);
                }
            }
        }

        return found;
    }

    internal (HashSet<Guid> InCycles, HashSet<Guid> CutOff) Cycles()
    {
        var inCycles = new HashSet<Guid>();
        var cutOff = new HashSet<Guid>();
        foreach (var start in this.units.Values)
        {
            var seen = new HashSet<Guid>();
            for (var current = start.ParentId; current is { } id; current = Find(id)?.ParentId)
            {
                if (id == start.Id)
                {
                    inCycles.Add(start.Id);
                    cutOff.Add(start.Id);
                    break;
                }

                if (!seen.Add(id))
                {
                    cutOff.Add(start.Id);
                    break;
                }
            }
        }

        return (inCycles, cutOff);
    }

    private void Add(Guid id, Guid tenantId, string name, OrganizationUnitType type, Guid? parentId)
    {
        this.units.Add(id, new OrganizationStructureUnit(id, tenantId, name, type, this.units.Count) { ParentId = parentId });
    }

    private OrganizationStructureUnit Require(Guid id) =>
        Find(id) ?? throw new InvalidOperationException("The unit is not part of this structure.");

    private OrganizationStructure Clone()
    {
        var copy = new OrganizationStructure();
        foreach (var unit in this.units.Values.OrderBy(unit => unit.Order))
        {
            copy.units.Add(unit.Id, new OrganizationStructureUnit(unit.Id, unit.TenantId, unit.Name, unit.Type, unit.Order) { ParentId = unit.ParentId });
        }

        return copy;
    }
}
