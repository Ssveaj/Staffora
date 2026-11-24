namespace Staffora.Core.Models;

public sealed record RoleHoldingChoices(IReadOnlyList<RoleHoldingOption> Roles, IReadOnlyList<RoleHoldingOption> Units, IReadOnlyList<RoleHoldingOption> People)
{
    public static RoleHoldingChoices None { get; } = new([], [], []);

    public IReadOnlySet<Guid> RolesBeyondCeiling { get; init; } = new HashSet<Guid>();

    public bool WithinCeiling(Guid roleId) => !RolesBeyondCeiling.Contains(roleId);

    public IReadOnlySet<Guid> StaffRoles { get; init; } = new HashSet<Guid>();

    public RoleHoldingChoices ForNewStaff() => this with { Roles = [.. Roles.Where(role => StaffRoles.Contains(role.Id))] };
}

public sealed record RoleHoldingOption(Guid Id, string Name);
