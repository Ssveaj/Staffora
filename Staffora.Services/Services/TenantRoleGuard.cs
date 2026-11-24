using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Tenancy;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;

namespace Staffora.Services.Services;

public sealed class TenantRoleGuard : ITenantRoleGuard, IStepPreparation
{
    private readonly Dictionary<Guid, Facts> facts = [];
    private readonly Lock gate = new();
    private readonly PermissionService permissions;

    public TenantRoleGuard(PermissionService permissions)
    {
        this.permissions = permissions;
    }

    public Type EntityType => typeof(TenantRole);

    public async Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!await context.TenantRoles.AsNoTracking().AnyAsync(role => role.Id == entityId, cancellationToken))
        {
            lock (this.gate)
            {
                this.facts.Remove(entityId);
            }

            return;
        }

        var stored = (await context.TenantRolePermissions.AsNoTracking()
            .Where(permission => permission.TenantRoleId == entityId)
            .Select(permission => permission.Permission)
            .ToListAsync(cancellationToken)).ToHashSet();
        var storedFlag = await context.TenantRoles.AsNoTracking().Where(role => role.Id == entityId).Select(role => role.ApplyResourceMembershipToUser).FirstAsync(cancellationToken);
        var every = Enum.GetValues<RolePermission>().Where(permission => permission != RolePermission.None);
        var notHeld = await this.permissions.MissingAsync(every, cancellationToken);

        lock (this.gate)
        {
            this.facts[entityId] = new Facts(stored, notHeld, storedFlag);
        }
    }

    public bool StoredBeyondTheSession(TenantRole role)
    {
        var facts = FactsOf(role);
        return facts.Stored.Overlaps(facts.NotHeld);
    }

    public bool ChangesBeyondTheSession(TenantRole role)
    {
        var facts = FactsOf(role);
        var now = role.Permissions.Select(permission => permission.Permission).ToHashSet();
        now.SymmetricExceptWith(facts.Stored);
        return now.Overlaps(facts.NotHeld);
    }

    public bool ResourceFlagChangeBeyondTheSession(TenantRole role)
    {
        var facts = FactsOf(role);
        return role.ApplyResourceMembershipToUser != facts.StoredFlag && facts.NotHeld.Contains(RolePermission.ResourcesManage);
    }

    public bool NewRoleFlagBeyondTheSession(TenantRole role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return role.ApplyResourceMembershipToUser && !this.permissions.HasPermission(RolePermission.ResourcesManage);
    }

    private Facts FactsOf(TenantRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        lock (this.gate)
        {
            return this.facts.TryGetValue(role.Id, out var facts)
                ? facts
                : throw new InvalidOperationException($"Nothing was read about role {role.Id} before its steps were judged, so the grant ceiling cannot be checked.");
        }
    }

    private sealed record Facts(HashSet<RolePermission> Stored, IReadOnlySet<RolePermission> NotHeld, bool StoredFlag);
}
