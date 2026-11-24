using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Data;

namespace Staffora.Services.Authorization;

public sealed record ImpersonationTarget(Guid UserId, string? UserName, string? Email, Guid TenantId);

public sealed record ImpersonationCandidate(Guid UserId, string FirstName, string LastName, string Email, string TenantName, string Roles = "", Guid TenantId = default);

public sealed class ImpersonationAuthorizer
{
    public const string SelfRefused = "You cannot impersonate yourself.";
    public const string NotPlatformOwner = "Only a platform owner can impersonate users.";
    public const string TargetNotFound = "The user to impersonate was not found.";
    public const string SystemAccountRefused = "A tenant's system account cannot be impersonated.";
    public const string PlatformUserRefused = "A platform user cannot be impersonated.";
    public const string InactiveUserRefused = "An inactive user cannot be impersonated.";
    public const string TenantNotActive = "The user's tenant is not active.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly ILogger<ImpersonationAuthorizer> logger;

    public ImpersonationAuthorizer(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, ILogger<ImpersonationAuthorizer> logger)
    {
        this.dbFactory = dbFactory;
        this.logger = logger;
    }

    public async Task<Result<ImpersonationTarget>> AuthorizeAsync(Guid actingUserId, Guid targetUserId, CancellationToken cancellationToken = default)
    {
        if (actingUserId == targetUserId)
        {
            return Refuse(actingUserId, targetUserId, SelfRefused);
        }

        using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("impersonation: authorize a platform owner and a target user of any tenant");
        var users = await FactsOf(context).Where(user => user.Id == actingUserId || user.Id == targetUserId).ToListAsync(cancellationToken);

        if (!IsPlatformOwner(users.FirstOrDefault(user => user.Id == actingUserId)))
        {
            return Refuse(actingUserId, targetUserId, NotPlatformOwner);
        }

        var target = users.FirstOrDefault(user => user.Id == targetUserId);
        if (RefusalOf(target) is { } refusal)
        {
            return Refuse(actingUserId, targetUserId, refusal);
        }

        return Result<ImpersonationTarget>.Success(new ImpersonationTarget(target!.Id, target.UserName, target.Email, target.TenantId));
    }

    public async Task<Result<IReadOnlyList<ImpersonationCandidate>>> ListEligibleTargetsAsync(Guid actingUserId, CancellationToken cancellationToken = default)
    {
        using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("impersonation: list the users of every tenant a platform owner may act as");
        var facts = FactsOf(context);
        if (!IsPlatformOwner(await facts.FirstOrDefaultAsync(user => user.Id == actingUserId, cancellationToken)))
        {
            this.logger.LogWarning("Refused the list of impersonation targets to user {ActingUserId}: {Reason}", actingUserId, NotPlatformOwner);
            return Result<IReadOnlyList<ImpersonationCandidate>>.Failure(NotPlatformOwner);
        }

        var candidates = await facts
            .Where(user => user.Id != actingUserId && user.State == UserState.Active && !user.IsSystemAccount && !user.IsAdmin && !user.IsInPlatformTenant
                && (user.TenantState == TenantState.Active || (user.TenantState == TenantState.Setup && user.IsActiveSystemOwner)))
            .ToListAsync(cancellationToken);
        var allowed = candidates.Where(user => RefusalOf(user) is null).ToList();

        var ids = allowed.Select(user => user.Id).ToList();
        var holdings = await context.OrganizationUnitUserRoles
            .Where(holding => ids.Contains(holding.UserId) && holding.TenantRole.TenantId == holding.TenantId && holding.TenantRole.State == TenantRoleState.Active)
            .Select(holding => new { holding.UserId, holding.TenantRole.Name })
            .ToListAsync(cancellationToken);
        var rolesOf = holdings
            .GroupBy(holding => holding.UserId)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(holding => holding.Name).Distinct().Order(StringComparer.CurrentCultureIgnoreCase)));

        IReadOnlyList<ImpersonationCandidate> eligible = [.. allowed
            .OrderBy(user => user.LastName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(user => user.FirstName, StringComparer.CurrentCultureIgnoreCase)
            .Select(user => new ImpersonationCandidate(user.Id, user.FirstName, user.LastName, user.Email ?? string.Empty, user.TenantName ?? string.Empty, rolesOf.GetValueOrDefault(user.Id, string.Empty), user.TenantId))];
        return Result<IReadOnlyList<ImpersonationCandidate>>.Success(eligible);
    }

    public async Task<bool> IsPlatformOwnerAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("impersonation: check that a user of the platform tenant is a platform owner");
        return IsPlatformOwner(await FactsOf(context).FirstOrDefaultAsync(user => user.Id == userId, cancellationToken));
    }

    private static IQueryable<UserFacts> FactsOf(ApplicationDbContext context) =>
        context.Users
            .AsNoTracking()
            .Select(user => new UserFacts
            {
                Id = user.Id,
                UserName = user.UserName,
                Email = user.Email,
                FirstName = user.FirstName,
                LastName = user.LastName,
                TenantId = user.TenantId,
                TenantName = context.Tenants.Where(tenant => tenant.Id == user.TenantId).Select(tenant => tenant.Name).FirstOrDefault(),
                State = user.State,
                IsSystemAccount = user.IsSystemAccount,
                TenantState = context.Tenants.Where(tenant => tenant.Id == user.TenantId).Select(tenant => (TenantState?)tenant.State).FirstOrDefault(),

                IsActiveSystemOwner = user.State == UserState.Active && !user.IsSystemAccount && context.OrganizationUnitUserRoles.Any(holding => holding.UserId == user.Id
                    && holding.TenantId == user.TenantId && holding.TenantRole.IsSystemOwner && holding.TenantRole.State == TenantRoleState.Active),
                IsInPlatformTenant = context.Tenants.Any(tenant => tenant.Id == user.TenantId && tenant.IsRootTenant),
                IsAdmin = context.UserRoles.Any(holding => holding.UserId == user.Id
                    && context.Roles.Any(role => role.Id == holding.RoleId && role.Name == AppRoles.Admin)),
            });

    private static bool IsPlatformOwner(UserFacts? acting) =>
        acting is { State: UserState.Active, IsSystemAccount: false, IsAdmin: true, IsInPlatformTenant: true };

    private static string? RefusalOf(UserFacts? target) => target switch
    {
        null => TargetNotFound,
        { IsSystemAccount: true } => SystemAccountRefused,
        { IsInPlatformTenant: true } or { IsAdmin: true } => PlatformUserRefused,
        { State: not UserState.Active } => InactiveUserRefused,
        _ when !TenantStates.AdmitsUser(target.TenantState, target.IsActiveSystemOwner) => TenantNotActive,
        _ => null,
    };

    private Result<ImpersonationTarget> Refuse(Guid actingUserId, Guid targetUserId, string reason)
    {
        this.logger.LogWarning("Refused impersonation of user {TargetUserId} by user {ActingUserId}: {Reason}", targetUserId, actingUserId, reason);
        return Result<ImpersonationTarget>.Failure(reason);
    }

    private sealed class UserFacts
    {
        public Guid Id { get; init; }
        public string? UserName { get; init; }
        public string? Email { get; init; }
        public string FirstName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public Guid TenantId { get; init; }
        public string? TenantName { get; init; }
        public UserState State { get; init; }
        public bool IsSystemAccount { get; init; }
        public TenantState? TenantState { get; init; }
        public bool IsActiveSystemOwner { get; init; }
        public bool IsInPlatformTenant { get; init; }
        public bool IsAdmin { get; init; }
    }
}
