using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;

namespace Staffora.Services.Services;

public sealed class UserAccountGuard : IUserAccountGuard, IStepPreparation
{
    private readonly Dictionary<Guid, Facts> facts = [];
    private readonly Lock gate = new();
    private readonly PermissionService permissions;
    private readonly IRuntimeExecutionContext runtime;

    public UserAccountGuard(PermissionService permissions, IRuntimeExecutionContext runtime)
    {
        this.permissions = permissions;
        this.runtime = runtime;
    }

    public Type EntityType => typeof(ApplicationUser);

    public async Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stored = await context.Users.AsNoTracking()
            .Where(user => user.Id == entityId)
            .Select(user => new { user.UserName, user.Email, user.TenantId })
            .FirstOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            Forget(entityId);
            return;
        }

        var adminRoles = context.Roles.Where(role => role.Name == AppRoles.Admin).Select(role => role.Id);
        var isPlatformOwner = await context.UserRoles.AnyAsync(holding => holding.UserId == entityId && adminRoles.Contains(holding.RoleId), cancellationToken);
        var held = await context.OrganizationUnitUserRoles.AsNoTracking()
            .Where(holding => holding.UserId == entityId)
            .Join(context.TenantRolePermissions, holding => holding.TenantRoleId, permission => permission.TenantRoleId, (holding, permission) => permission.Permission)
            .Distinct()
            .ToListAsync(cancellationToken);
        var beyond = await this.permissions.MissingAsync(held, cancellationToken);

        var session = this.runtime.Current;
        var isOwner = await SystemOwners.HoldsTheRoleAsync(context, stored.TenantId, entityId, cancellationToken);
        var activeOwners = await SystemOwners.ActiveOwnerIds(context, stored.TenantId).OrderBy(id => id).Take(2).ToListAsync(cancellationToken);
        var sessionIsOwner = session.HasPlatformReach()
            || (session.TenantId == stored.TenantId && session.EffectiveUserId is { } self
                && await SystemOwners.IsActiveOwnerAsync(context, stored.TenantId, self, cancellationToken));

        var isPortal = await context.Tenants.AsNoTracking()
            .Where(tenant => tenant.Id == stored.TenantId)
            .Select(tenant => tenant.ParentTenantId != null)
            .FirstOrDefaultAsync(cancellationToken);
        var activeAdministrators = isPortal
            ? await context.OrganizationUnitUserRoles.AsNoTracking()
                .Where(holding => holding.TenantId == stored.TenantId
                    && holding.User.State == UserState.Active && !holding.User.IsSystemAccount
                    && holding.TenantRole.State == TenantRoleState.Active
                    && (holding.OrganizationUnit.State == OrganizationUnitState.Active || holding.OrganizationUnit.UnitType == OrganizationUnitType.Company)
                    && holding.TenantRole.Permissions.Any(permission => permission.Permission == RolePermission.CustomerUsersManage))
                .Select(holding => holding.UserId)
                .Distinct()
                .OrderBy(id => id)
                .Take(2)
                .ToListAsync(cancellationToken)
            : [];

        lock (this.gate)
        {
            this.facts[entityId] = new Facts(stored.UserName, stored.Email, isPlatformOwner, beyond.Count > 0, isOwner && !sessionIsOwner, sessionIsOwner, activeOwners is [var only] && only == entityId, activeAdministrators is [var sole] && sole == entityId);
        }
    }

    public bool IsPlatformOwner(ApplicationUser user) => FactsOf(user).IsPlatformOwner;

    public bool HoldsMoreThanTheSession(ApplicationUser user) => FactsOf(user).HoldsMore;

    public bool IsOwnerBeyondTheSession(ApplicationUser user) => FactsOf(user).OwnerBeyondTheSession;

    public bool SessionIsOwnerOf(ApplicationUser user) => FactsOf(user).SessionIsOwner;

    public bool IsLastActiveOwner(ApplicationUser user) => FactsOf(user).LastActiveOwner;

    public bool IsLastActiveCustomerAdministrator(ApplicationUser user) => FactsOf(user).LastActiveCustomerAdministrator;

    public bool SignInNameChanged(ApplicationUser user)
    {
        var facts = FactsOf(user);
        return !string.Equals(user.UserName, facts.UserName, StringComparison.Ordinal) || !string.Equals(user.Email, facts.Email, StringComparison.Ordinal);
    }

    private Facts FactsOf(ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        lock (this.gate)
        {
            return this.facts.TryGetValue(user.Id, out var facts)
                ? facts
                : throw new InvalidOperationException($"Nothing was read about user {user.Id} before its steps were judged, so the grant ceiling cannot be checked.");
        }
    }

    private void Forget(Guid userId)
    {
        lock (this.gate)
        {
            this.facts.Remove(userId);
        }
    }

    private sealed record Facts(string? UserName, string? Email, bool IsPlatformOwner, bool HoldsMore, bool OwnerBeyondTheSession, bool SessionIsOwner, bool LastActiveOwner, bool LastActiveCustomerAdministrator);
}
