using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Authorization;

public sealed class UserSessionService
{
    private const int KnownTenantsCapacity = 10_000;

    private const int UnknownUsersCapacity = 1_000;

    public static readonly TimeSpan UnknownUserLifetime = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<Guid, Guid> tenantOfUser = new();

    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> unknownUserUntil = new();

    private readonly Lock gate = new();
    private Task? initialization;
    private Guid? lastNotifiedTenantId;

    private volatile SessionView view = SessionView.NotReady;
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly AuthenticationStateProvider authStateProvider;
    private readonly ImpersonationAuthorizer authorizer;
    private readonly ImpersonationAuditWriter audit;
    private readonly PermissionService permissions;
    private readonly TimeProvider clock;
    private readonly ILogger<UserSessionService> logger;

    public UserSessionService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, AuthenticationStateProvider authStateProvider, ImpersonationAuthorizer authorizer, ImpersonationAuditWriter audit, PermissionService permissions, TimeProvider clock, ILogger<UserSessionService> logger)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.authStateProvider = authStateProvider;
        this.authorizer = authorizer;
        this.audit = audit;
        this.permissions = permissions;
        this.clock = clock;
        this.logger = logger;
    }

    public bool IsReady => this.view.IsReady;

    public Guid UserId => this.runtime.UserId ?? Guid.Empty;

    public string? UserName => this.view.UserName;
    public string? Email => this.view.Email;

    public string? EffectiveUserName => this.view is var view && view.ImpersonationSessionId is not null ? view.ImpersonatedUserName : view.UserName;
    public string? EffectiveEmail => this.view is var view && view.ImpersonationSessionId is not null ? view.ImpersonatedEmail : view.Email;

    public Guid TenantId => this.runtime.TenantId ?? Guid.Empty;

    public event Action? OnTenantChanged;

    public Guid? ImpersonatedUserId => this.runtime.ImpersonatedUserId;

    public bool IsImpersonating => this.runtime.IsImpersonating;

    public Guid CurrentUserId => this.runtime.EffectiveUserId ?? Guid.Empty;

    public string? ImpersonatedUserName => this.view.ImpersonatedUserName;
    public string? ImpersonatedEmail => this.view.ImpersonatedEmail;

    public Guid? ImpersonationSessionId => this.view.ImpersonationSessionId;

    public bool ImpersonationEnded => this.view.ImpersonationEnded;

    public string? ActiveApplicationRole => this.view.Roles.FirstOrDefault();

    public event Action? OnChanged;

    public Task InitializeFromClaimsAsync()
    {
        lock (this.gate)
        {
            return this.initialization ??= InitializeCoreAsync();
        }
    }

    public async Task<bool> RevalidateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        await InitializeFromClaimsAsync();

        var resolved = await ResolveAsync(principal, cancellationToken);
        Apply(resolved);
        return resolved.Runtime is not null || resolved.ImpersonationEnded;
    }

    public Task SignOutAsync()
    {
        Apply(SessionView.SignedOut);
        return Task.CompletedTask;
    }

    public async Task<LogIdentity> DescribeRequestAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated is not true || !Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            return LogIdentity.None;
        }

        var impersonation = ImpersonationClaims.Read(principal);
        var effectiveUserId = impersonation?.TargetUserId ?? userId;
        return new LogIdentity(await TenantOfUserAsync(effectiveUserId, cancellationToken), effectiveUserId, impersonation is null ? null : userId);
    }

    private async Task<Guid?> TenantOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (tenantOfUser.TryGetValue(userId, out var known))
        {
            return known;
        }

        var now = this.clock.GetUtcNow();
        if (unknownUserUntil.TryGetValue(userId, out var unknownUntil) && now < unknownUntil)
        {
            return null;
        }

        try
        {
            using var context = this.dbFactory.CreateSystemDbContext();
            using var lease = context.BeginSystemScope("log scope: read the tenant of the user a request's cookie names");
            var tenantId = await context.Users.AsNoTracking().Where(user => user.Id == userId).Select(user => (Guid?)user.TenantId).FirstOrDefaultAsync(cancellationToken);
            if (tenantId is { } found)
            {
                if (tenantOfUser.Count >= KnownTenantsCapacity)
                {
                    tenantOfUser.Clear();
                }

                tenantOfUser[userId] = found;
                unknownUserUntil.TryRemove(userId, out _);
            }
            else
            {
                if (unknownUserUntil.Count >= UnknownUsersCapacity)
                {
                    unknownUserUntil.Clear();
                }

                unknownUserUntil[userId] = now + UnknownUserLifetime;
            }

            return tenantId;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            this.logger.LogWarning(exception, "Could not read the tenant of user {UserId} for a request's log scope.", userId);
            return null;
        }
    }

    private async Task InitializeCoreAsync()
    {
        SessionView resolved;
        try
        {
            var state = await this.authStateProvider.GetAuthenticationStateAsync();
            resolved = await ResolveAsync(state.User, CancellationToken.None);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to initialize session from claims");
            resolved = SessionView.SignedOut;
        }

        Apply(resolved);
    }

    private async Task<SessionView> ResolveAsync(ClaimsPrincipal? principal, CancellationToken cancellationToken)
    {
        if (principal?.Identity?.IsAuthenticated is not true)
        {
            return SessionView.SignedOut;
        }

        var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value
            ?? principal.Identity?.Name;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return SessionView.SignedOut;
        }

        var appUser = await ReadSignedInUserAsync(userId, cancellationToken);

        var refusal = appUser switch
        {
            null => null,
            { IsSystemAccount: true } => "a tenant's system account never signs in",
            { State: not UserState.Active } => "the user is deactivated",
            _ when !TenantStates.AdmitsUser(appUser.TenantState, appUser.IsActiveSystemOwner) => $"the user's tenant does not admit them ({appUser.TenantState?.ToString() ?? "unknown"})",
            _ when !StampMatches(principal, appUser.SecurityStamp) => "the cookie's security stamp is not the user's current one",
            _ => null,
        };
        if (appUser is null || refusal is not null)
        {
            if (refusal is not null)
            {
                this.logger.LogWarning("Refused a session for user {UserId}: {Reason}.", appUser!.Id, refusal);
                await CloseRefusedImpersonationAsync(appUser.Id, principal, cancellationToken);
            }

            return SessionView.SignedOut;
        }

        ImpersonationTarget? target = null;
        var impersonation = ImpersonationClaims.Read(principal);
        if (ImpersonationClaims.IsPresent(principal))
        {
            target = impersonation is null ? null : await ResumeImpersonationAsync(appUser.Id, impersonation, cancellationToken);
            if (target is null)
            {
                return SessionView.Ended;
            }
        }

        var session = new RuntimeSession(appUser.Id, appUser.IsAdmin, appUser.TenantId, appUser.IsInPlatformTenant, target?.UserId, target?.TenantId);

        var roles = await GetRolesForUserAsync(target?.UserId ?? appUser.Id, cancellationToken);
        if (roles.Count == 0)
        {
            roles.Add(AppRoles.StandardUser);
        }

        var resolvedRuntime = new RuntimeExecutionContext();
        resolvedRuntime.ApplySession(session);
        await this.permissions.PrimeAsync(resolvedRuntime, cancellationToken);

        return new SessionView(
            IsReady: true,
            Runtime: session,
            UserName: appUser.UserName ?? principal.Identity?.Name,
            Email: appUser.Email ?? principal.FindFirst(ClaimTypes.Email)?.Value,
            ImpersonatedUserName: target?.UserName,
            ImpersonatedEmail: target?.Email,
            ImpersonationSessionId: target is null ? null : impersonation!.SessionId,
            ImpersonationEnded: false,
            Roles: roles);
    }

    private void Apply(SessionView view)
    {
        var before = this.runtime.Current;
        if (view.Runtime is { } session)
        {
            this.runtime.ApplySession(session);
        }
        else
        {
            this.runtime.Reset();
        }

        var changed = !view.SameAs(this.view) || this.runtime.Current != before;
        this.view = view;
        if (changed)
        {
            OnChanged?.Invoke();
        }

        NotifyTenantChangedIfNeeded();
    }

    private async Task<SignedInUser?> ReadSignedInUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("session: resolve the signed-in user before a tenant is known");
        return await context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new SignedInUser
            {
                Id = u.Id,
                UserName = u.UserName,
                Email = u.Email,
                TenantId = u.TenantId,
                IsSystemAccount = u.IsSystemAccount,
                State = u.State,
                SecurityStamp = u.SecurityStamp,
                TenantState = context.Tenants.Where(tenant => tenant.Id == u.TenantId).Select(tenant => (TenantState?)tenant.State).FirstOrDefault(),

                IsActiveSystemOwner = u.State == UserState.Active && !u.IsSystemAccount && context.OrganizationUnitUserRoles.Any(holding => holding.UserId == u.Id
                    && holding.TenantId == u.TenantId && holding.TenantRole.IsSystemOwner && holding.TenantRole.State == TenantRoleState.Active),
                IsInPlatformTenant = context.Tenants.Any(tenant => tenant.Id == u.TenantId && tenant.IsRootTenant),
                IsAdmin = context.UserRoles.Any(holding => holding.UserId == u.Id
                    && context.Roles.Any(role => role.Id == holding.RoleId && role.Name == AppRoles.Admin)),
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task CloseRefusedImpersonationAsync(Guid impersonatorId, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (ImpersonationClaims.Read(principal) is not { } impersonation
            || !await this.audit.IsOpenAsync(impersonatorId, impersonation.TargetUserId, impersonation.SessionId, cancellationToken))
        {
            return;
        }

        var recorded = await this.audit.RecordEndAsync(impersonatorId, impersonation.SessionId, cancellationToken);
        if (recorded.IsSuccess)
        {
            this.logger.LogInformation("Closed impersonation session {SessionId} of user {UserId}: the user's own session was refused.", impersonation.SessionId, impersonatorId);
        }
        else
        {
            this.logger.LogWarning("Impersonation session {SessionId} of user {UserId} stays open: the user's own session was refused, but the end could not be recorded ({Error}).", impersonation.SessionId, impersonatorId, recorded.Error);
        }
    }

    private async Task<ImpersonationTarget?> ResumeImpersonationAsync(Guid impersonatorId, ImpersonationClaim impersonation, CancellationToken cancellationToken)
    {
        var authorized = await this.authorizer.AuthorizeAsync(impersonatorId, impersonation.TargetUserId, cancellationToken);
        if (!authorized.IsSuccess)
        {
            this.logger.LogWarning("Ignored the impersonation of user {TargetUserId} on the cookie of user {UserId}: {Reason}", impersonation.TargetUserId, impersonatorId, authorized.Error);

            if (await this.audit.IsOpenAsync(impersonatorId, impersonation.TargetUserId, impersonation.SessionId, cancellationToken))
            {
                await this.audit.RecordEndAsync(impersonatorId, impersonation.SessionId, cancellationToken);
            }

            return null;
        }

        if (!await this.audit.IsOpenAsync(impersonatorId, impersonation.TargetUserId, impersonation.SessionId, cancellationToken))
        {
            this.logger.LogWarning("Ignored the impersonation of user {TargetUserId} on the cookie of user {UserId}: its audit row {SessionId} is not open.", impersonation.TargetUserId, impersonatorId, impersonation.SessionId);
            return null;
        }

        return authorized.Value;
    }

    public static readonly string SecurityStampClaimType = new Microsoft.AspNetCore.Identity.ClaimsIdentityOptions().SecurityStampClaimType;

    private static bool StampMatches(ClaimsPrincipal principal, string? stored) =>
        principal.FindFirst(SecurityStampClaimType)?.Value is { Length: > 0 } carried
        && !string.IsNullOrEmpty(stored)
        && string.Equals(carried, stored, StringComparison.Ordinal);

    private async Task<List<string>> GetRolesForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var context = this.dbFactory.CreateSystemDbContext();
        var roles = await context.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Join(context.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name!)
            .Distinct()
            .ToListAsync(cancellationToken);

        return roles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void NotifyTenantChangedIfNeeded()
    {
        var currentTenantId = this.runtime.TenantId;

        if (currentTenantId != this.lastNotifiedTenantId)
        {
            this.lastNotifiedTenantId = currentTenantId;
            OnTenantChanged?.Invoke();
        }
    }

    private sealed class SignedInUser
    {
        public Guid Id { get; init; }
        public string? UserName { get; init; }
        public string? Email { get; init; }
        public Guid TenantId { get; init; }
        public bool IsSystemAccount { get; init; }
        public UserState State { get; init; }
        public string? SecurityStamp { get; init; }
        public TenantState? TenantState { get; init; }
        public bool IsActiveSystemOwner { get; init; }
        public bool IsInPlatformTenant { get; init; }
        public bool IsAdmin { get; init; }
    }

    private sealed record SessionView(bool IsReady, RuntimeSession? Runtime, string? UserName, string? Email, string? ImpersonatedUserName, string? ImpersonatedEmail, Guid? ImpersonationSessionId, bool ImpersonationEnded, IReadOnlyList<string> Roles)
    {
        public static readonly SessionView NotReady = new(false, null, null, null, null, null, null, false, []);

        public static readonly SessionView SignedOut = NotReady with { IsReady = true };

        public static readonly SessionView Ended = SignedOut with { ImpersonationEnded = true };

        public bool SameAs(SessionView other) =>
            (IsReady, Runtime, UserName, Email, ImpersonatedUserName, ImpersonatedEmail, ImpersonationSessionId, ImpersonationEnded)
                == (other.IsReady, other.Runtime, other.UserName, other.Email, other.ImpersonatedUserName, other.ImpersonatedEmail, other.ImpersonationSessionId, other.ImpersonationEnded)
            && Roles.SequenceEqual(other.Roles);
    }
}
