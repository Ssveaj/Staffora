using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Core.Models;

public sealed record PlatformTenantRow(Guid Id, string Name, string? Description, TenantState State, int UserCount, DateTime Created)
{
    public IReadOnlyList<string> ManagedCustomers { get; init; } = [];

    public Guid? ManagedById { get; init; }

    public string? ManagedBy { get; init; }
}

public sealed record TenantConsoleLink(Guid Id, string Name, TenantState State);

public sealed record PlatformDeadLetterRow(
    Guid Id,
    string Kind,
    bool Sent,
    Guid OtherTenantId,
    string OtherTenant,
    Guid CorrelationId,
    long Sequence,
    int Attempts,
    DateTime Written,
    DateTime? GivenUp,
    string? LastFailure);

public sealed record TenantStateCount(TenantState State, int Count);

public sealed record PlatformRecentError(Guid Id, DateTime Occurred, ErrorLogLevel Level, string Message, string? TenantName);

public sealed record PlatformRecentImpersonation(Guid Id, string Impersonator, string Target, string TenantName, DateTime Started, DateTime? Ended);

public sealed record PlatformOverviewTenant(Guid Id, string Name, string? Description, TenantState State, int Users, DateTime Created)
{
    public int JobsThisWeek { get; init; }

    public int OpenSlots { get; init; }
}

public sealed record PlatformRecentTarget(Guid UserId, string Name, Guid TenantId, string TenantName);

public sealed record PlatformOverview(IReadOnlyList<TenantStateCount> TenantsByState, IReadOnlyList<PlatformRecentError> RecentErrors, IReadOnlyList<PlatformRecentImpersonation> RecentImpersonations)
{
    public int TenantCount => TenantsByState.Sum(count => count.Count);

    public IReadOnlyList<PlatformOverviewTenant> Tenants { get; init; } = [];

    public DateTime ReadAt { get; init; }

    public int ErrorsLastDay { get; init; }

    public int ErrorsLastWeek { get; init; }

    public DateTime? LastError { get; init; }

    public int DeadLetters { get; init; }

    public int DeliveredToday { get; init; }

    public int ActiveUsers { get; init; }

    public int ImpersonationsToday { get; init; }

    public PlatformRecentImpersonation? OpenImpersonation { get; init; }

    public IReadOnlyList<PlatformRecentTarget> RecentTargets { get; init; } = [];
}

public sealed record TenantConsoleOwner(Guid Id, string Name, string Email, UserState State);

public sealed record TenantConsoleSummary(
    Guid Id,
    string Name,
    string? Description,
    TenantState State,
    bool IsPlatformTenant,
    DateTime Created,
    int ActiveUsers,
    int InactiveUsers,
    IReadOnlyList<TenantConsoleOwner> SystemOwners,
    IReadOnlyList<OrganizationUnitNode> Units,
    int RoleCount)
{
    public int UnitsInUse => Units.Count(unit => unit.InUse);

    public IReadOnlyList<TenantConsoleLink> ManagedCustomers { get; init; } = [];

    public TenantConsoleLink? ManagedBy { get; init; }

    public int DeadLetters { get; init; }
}

public sealed record TenantConsoleRole(Guid Id, string Name, string? Description, TenantRoleState State, bool IsSystemOwner, int PermissionCount, int HolderCount);

