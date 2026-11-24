using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;

namespace Staffora.Data;

public sealed class ApplicationUserStore : UserStore<ApplicationUser, ApplicationRole, ApplicationDbContext, Guid>
{
    private const string FindReason = "Identity: find a user before a tenant is known (sign-in, cookie validation)";
    private const string SaveReason = "Identity: save a user of its own tenant on that tenant's behalf";
    private const string CountReason = "Identity: count a failed sign-in of a user before a tenant is known, on that tenant's behalf";

    public const string OtherTenantRefused = "The user cannot be saved from this session.";

    public const string CreateOutsideSetupRefused = "A user is created through the user dialog, never from this session.";

    private readonly IRuntimeExecutionContext runtime;

    public ApplicationUserStore(ApplicationDbContext context, IRuntimeExecutionContext runtime, IdentityErrorDescriber? describer = null) : base(context, describer)
    {
        this.runtime = runtime;
    }

    public static IReadOnlySet<string> SignInBookkeepingColumns { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(ApplicationUser.AccessFailedCount),
        nameof(ApplicationUser.LockoutEnd),
        nameof(ApplicationUser.LastSignedIn),
    };

    public override async Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        using var lease = Context.BeginSystemScope(FindReason);
        return await base.FindByIdAsync(userId, cancellationToken);
    }

    public override async Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken = default)
    {
        using var lease = Context.BeginSystemScope(FindReason);
        return await base.FindByNameAsync(normalizedUserName, cancellationToken);
    }

    public override async Task<ApplicationUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        using var lease = Context.BeginSystemScope(FindReason);
        var matches = await Context.Users.Where(user => user.NormalizedEmail == normalizedEmail).OrderBy(user => user.Id).Take(2).ToListAsync(cancellationToken);
        return matches.Count == 1 ? matches[0] : null;
    }

    public async Task<TenantAdmissionFacts> FindTenantAdmissionAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        using var lease = Context.BeginSystemScope(FindReason);
        var facts = await Context.Users.AsNoTracking()
            .Where(row => row.Id == user.Id)
            .Select(row => new TenantAdmissionFacts(
                Context.Tenants.Where(tenant => tenant.Id == row.TenantId).Select(tenant => (TenantState?)tenant.State).FirstOrDefault(),
                row.State == UserState.Active && !row.IsSystemAccount && Context.OrganizationUnitUserRoles.Any(holding => holding.UserId == row.Id
                    && holding.TenantId == row.TenantId && holding.TenantRole.IsSystemOwner && holding.TenantRole.State == TenantRoleState.Active)))
            .FirstOrDefaultAsync(cancellationToken);
        return facts ?? new TenantAdmissionFacts(null, false);
    }

    public async Task<UserState?> FindStoredStateAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        using var lease = Context.BeginSystemScope(FindReason);
        return await Context.Users.AsNoTracking().Where(row => row.Id == user.Id).Select(row => (UserState?)row.State).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<UserCookieFacts?> FindCookieFactsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        using var lease = Context.BeginSystemScope(FindReason);
        return await Context.Users.AsNoTracking()
            .Where(row => row.Id == userId)
            .Select(row => new UserCookieFacts(
                row.SecurityStamp,
                row.State,
                row.IsSystemAccount,
                Context.Tenants.Where(tenant => tenant.Id == row.TenantId).Select(tenant => (TenantState?)tenant.State).FirstOrDefault(),

                row.State == UserState.Active && !row.IsSystemAccount && Context.OrganizationUnitUserRoles.Any(holding => holding.UserId == row.Id
                    && holding.TenantId == row.TenantId && holding.TenantRole.IsSystemOwner && holding.TenantRole.State == TenantRoleState.Active)))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> IsInPlatformTenantAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        using var lease = Context.BeginSystemScope(FindReason);
        return await Context.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == user.TenantId && tenant.IsRootTenant, cancellationToken);
    }

    protected override async Task<ApplicationUser?> FindUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var lease = Context.BeginSystemScope(FindReason);
        return await base.FindUserAsync(userId, cancellationToken);
    }

    public override async Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (this.runtime.Current.EffectiveUserId is not null)
        {
            return IdentityResult.Failed(new IdentityError { Code = nameof(CreateOutsideSetupRefused), Description = CreateOutsideSetupRefused });
        }

        if (user.TenantId != Guid.Empty && user.CreatedById == Guid.Empty)
        {
            user.CreatedById = SystemAccount.IdFor(user.TenantId);
        }

        using var lease = Context.BeginSystemScope(SaveReason);
        return await base.CreateAsync(user, cancellationToken);
    }

    public override async Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var bookkeeping = ChangesOnlySignInBookkeeping(user);
        var session = this.runtime.Current;
        if (!bookkeeping && RefuseOtherTenant(user, session) is { } refused)
        {
            return refused;
        }

        if (user.TenantId != Guid.Empty)
        {
            Context.NameEditor(user, bookkeeping ? SystemAccount.IdFor(user.TenantId) : WriterOf(user, session));
        }

        using var lease = Context.BeginSystemScope(SaveReason);
        try
        {
            return await base.UpdateAsync(user, cancellationToken);
        }
        catch (WorkflowFieldEditException)
        {
            Context.Entry(user).State = EntityState.Detached;
            return IdentityResult.Failed(ErrorDescriber.ConcurrencyFailure());
        }
    }

    public async Task<bool> CountFailedAccessAsync(ApplicationUser user, int maxFailedAccessAttempts, DateTimeOffset now, TimeSpan lockoutTimeSpan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var lockoutEnd = now.Add(lockoutTimeSpan);
        var editor = SystemAccount.IdFor(user.TenantId);
        var modified = now.UtcDateTime;
        using var lease = Context.BeginSystemScope(CountReason);
        var counted = await Context.Users
            .Where(row => row.Id == user.Id && row.TenantId == user.TenantId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(row => row.LockoutEnd, row => row.AccessFailedCount + 1 >= maxFailedAccessAttempts ? lockoutEnd : row.LockoutEnd)
                    .SetProperty(row => row.AccessFailedCount, row => row.AccessFailedCount + 1 >= maxFailedAccessAttempts ? 0 : row.AccessFailedCount + 1)
                    .SetProperty(row => row.Modified, modified)
                    .SetProperty(row => row.ModifiedById, editor),
                cancellationToken);
        if (counted == 0)
        {
            return false;
        }

        var stored = await Context.Users.AsNoTracking()
            .Where(row => row.Id == user.Id)
            .Select(row => new { row.AccessFailedCount, row.LockoutEnd })
            .FirstOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return false;
        }

        user.AccessFailedCount = stored.AccessFailedCount;
        user.LockoutEnd = stored.LockoutEnd;
        var entry = Context.Entry(user);
        if (entry.State is EntityState.Unchanged or EntityState.Modified)
        {
            entry.Property(row => row.AccessFailedCount).OriginalValue = stored.AccessFailedCount;
            entry.Property(row => row.AccessFailedCount).IsModified = false;
            entry.Property(row => row.LockoutEnd).OriginalValue = stored.LockoutEnd;
            entry.Property(row => row.LockoutEnd).IsModified = false;
        }

        return true;
    }

    private bool ChangesOnlySignInBookkeeping(ApplicationUser user)
    {
        var entry = Context.Entry(user);
        if (entry.State is not (EntityState.Unchanged or EntityState.Modified))
        {
            return false;
        }

        Context.ChangeTracker.DetectChanges();
        if (Context.ChangeTracker.Entries().Any(other => !ReferenceEquals(other.Entity, user) && other.State != EntityState.Unchanged))
        {
            return false;
        }

        var changed = entry.Properties.Where(property => !Equals(property.OriginalValue, property.CurrentValue)).Select(property => property.Metadata.Name).ToList();
        return changed.Count > 0 && changed.All(SignInBookkeepingColumns.Contains);
    }

    private static Guid WriterOf(ApplicationUser user, RuntimeSnapshot session) =>
        session.EffectiveUserId is { } actor && session.TenantId == user.TenantId ? actor : SystemAccount.IdFor(user.TenantId);

    private static IdentityResult? RefuseOtherTenant(ApplicationUser user, RuntimeSnapshot session)
    {
        var platformReach = session.IsSuperAdmin && session.IsInPlatformTenant && !session.IsImpersonating;
        if (session.EffectiveUserId is null || session.TenantId == user.TenantId || platformReach)
        {
            return null;
        }

        return IdentityResult.Failed(new IdentityError { Code = nameof(OtherTenantRefused), Description = OtherTenantRefused });
    }

    public override async Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (RefuseOtherTenant(user, this.runtime.Current) is { } refused)
        {
            return refused;
        }

        using var lease = Context.BeginSystemScope(SaveReason);
        return await base.DeleteAsync(user, cancellationToken);
    }
}

public sealed record UserCookieFacts(string? SecurityStamp, UserState State, bool IsSystemAccount, TenantState? TenantState, bool IsActiveSystemOwner = false);

public sealed record TenantAdmissionFacts(TenantState? TenantState, bool IsActiveSystemOwner);
