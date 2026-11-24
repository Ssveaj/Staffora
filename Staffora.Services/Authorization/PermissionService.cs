using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;

namespace Staffora.Services.Authorization;

public class PermissionService
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);

    private static readonly Lazy<Dictionary<PermissionArea, HashSet<RolePermission>>> permissionsByArea = new(BuildPermissionsByArea);

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly TimeProvider clock;

    private readonly SemaphoreSlim cacheLock = new(1, 1);

    private volatile CachedPermissions? cache;

    public PermissionService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, TimeProvider clock)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.clock = clock;
    }

    private static bool HasPlatformReach(RuntimeSnapshot session) => session.HasPlatformReach();

    public async Task<bool> HasPermissionAsync(RolePermission permission, CancellationToken ct = default)
    {
        var session = this.runtime.Current;
        if (HasPlatformReach(session))
            return true;

        var permissions = await LoadAsync(session, this.runtime, ct);
        return permissions.Contains(permission);
    }

    public async Task<bool> HasAnyPermissionAsync(IEnumerable<RolePermission> permissions, CancellationToken ct = default)
    {
        var session = this.runtime.Current;
        if (HasPlatformReach(session))
            return true;

        var userPermissions = await LoadAsync(session, this.runtime, ct);
        return permissions.Any(p => userPermissions.Contains(p));
    }

    public async Task<IReadOnlySet<RolePermission>> MissingAsync(IEnumerable<RolePermission> permissions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var session = this.runtime.Current;
        if (HasPlatformReach(session))
            return new HashSet<RolePermission>();

        var held = await LoadAsync(session, this.runtime, ct);
        return permissions.Where(permission => permission != RolePermission.None && !RolePermissions.IsPersonal(permission) && !held.Contains(permission)).ToHashSet();
    }

    public async Task<bool> HasAnyPermissionInAreaAsync(PermissionArea area, CancellationToken ct = default)
    {
        var session = this.runtime.Current;
        if (HasPlatformReach(session))
            return true;

        if (!permissionsByArea.Value.TryGetValue(area, out var areaPermissions) || areaPermissions.Count == 0)
            return false;

        var userPermissions = await LoadAsync(session, this.runtime, ct);
        return userPermissions.Overlaps(areaPermissions);
    }

    public bool HasPermission(RolePermission permission)
    {
        var session = this.runtime.Current;
        if (HasPlatformReach(session))
            return true;

        return LoadedOrThrow(session).Contains(permission);
    }

    public async Task LoadPermissionsAsync(CancellationToken ct = default)
    {
        await GetUserPermissionsAsync(ct);
    }

    public bool IsLoaded => Fresh(KeyOf(this.runtime.Current)) is not null;

    public async Task<IReadOnlySet<RolePermission>> GetUserPermissionsAsync(CancellationToken ct = default) => await LoadAsync(this.runtime.Current, this.runtime, ct);

    public async Task<IReadOnlySet<RolePermission>> GetUserPermissionsAsync(RuntimeSnapshot session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return await LoadAsync(session, this.runtime, ct);
    }

    public async Task<TenantEdition> GetEditionAsync(RuntimeSnapshot session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return (await LoadEntryAsync(session, this.runtime, ct)).Edition;
    }

    public Task<TenantEdition> GetEditionAsync(CancellationToken ct = default) => GetEditionAsync(this.runtime.Current, ct);

    public async Task PrimeAsync(IRuntimeExecutionContext resolved, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        await LoadAsync(resolved.Current, resolved, ct);
    }

    public async Task InvalidateCacheAsync(CancellationToken ct = default)
    {
        await this.cacheLock.WaitAsync(ct);
        try
        {
            this.cache = null;
        }
        finally
        {
            this.cacheLock.Release();
        }
    }

    public static Dictionary<string, List<(RolePermission Permission, string DisplayName, string? Description)>> GetGroupedPermissions()
    {
        var grouped = new Dictionary<string, List<(RolePermission, string, string?)>>(StringComparer.OrdinalIgnoreCase);

        foreach (RolePermission permission in Enum.GetValues<RolePermission>())
        {
            if (permission == RolePermission.None || RolePermissions.IsObsolete(permission))
                continue;

            var field = typeof(RolePermission).GetField(permission.ToString());
            if (field is null) continue;

            var displayAttr = field.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.DisplayAttribute), false)
                .OfType<System.ComponentModel.DataAnnotations.DisplayAttribute>()
                .FirstOrDefault();

            var descAttr = field.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .OfType<System.ComponentModel.DescriptionAttribute>()
                .FirstOrDefault();

            var groupName = displayAttr?.GroupName ?? "Other";
            var displayName = displayAttr?.Name ?? permission.ToString();
            var description = descAttr?.Description;

            if (!grouped.ContainsKey(groupName))
            {
                grouped[groupName] = new List<(RolePermission, string, string?)>();
            }

            grouped[groupName].Add((permission, displayName, description));
        }

        return grouped;
    }

    public static IReadOnlyCollection<RolePermission> GetPermissionsByArea(PermissionArea area)
    {
        if (!permissionsByArea.Value.TryGetValue(area, out var permissions))
        {
            return Array.Empty<RolePermission>();
        }

        return permissions;
    }

    private static Dictionary<PermissionArea, HashSet<RolePermission>> BuildPermissionsByArea()
    {
        var result = new Dictionary<PermissionArea, HashSet<RolePermission>>();

        foreach (RolePermission permission in Enum.GetValues<RolePermission>())
        {
            if (permission == RolePermission.None || RolePermissions.IsObsolete(permission))
                continue;

            var field = typeof(RolePermission).GetField(permission.ToString());
            if (field is null)
                continue;

            var areaAttr = field.GetCustomAttributes(typeof(PermissionAreaAttribute), false)
                .OfType<PermissionAreaAttribute>()
                .FirstOrDefault();

            if (areaAttr is null)
                continue;

            if (!result.TryGetValue(areaAttr.Area, out var set))
            {
                set = new HashSet<RolePermission>();
                result[areaAttr.Area] = set;
            }

            set.Add(permission);
        }

        return result;
    }

    private static CacheKey KeyOf(RuntimeSnapshot session) => new(session.TenantId, session.EffectiveUserId);

    private HashSet<RolePermission>? Fresh(CacheKey key) => FreshEntry(key)?.Permissions;

    private CachedPermissions? FreshEntry(CacheKey key) =>
        this.cache is { } cached && cached.Key == key && this.clock.GetUtcNow() - cached.LoadedAt < TimeToLive ? cached : null;

    private HashSet<RolePermission> LoadedOrThrow(RuntimeSnapshot session) =>
        Fresh(KeyOf(session)) ?? throw new InvalidOperationException("Permissions not loaded. Call LoadPermissionsAsync() first or use the asynchronous checks.");

    private async Task<HashSet<RolePermission>> LoadAsync(RuntimeSnapshot session, IRuntimeExecutionContext of, CancellationToken ct) =>
        (await LoadEntryAsync(session, of, ct)).Permissions;

    private async Task<CachedPermissions> LoadEntryAsync(RuntimeSnapshot session, IRuntimeExecutionContext of, CancellationToken ct)
    {
        var key = KeyOf(session);
        if (FreshEntry(key) is { } cached)
        {
            return cached;
        }

        await this.cacheLock.WaitAsync(ct);
        try
        {
            if (FreshEntry(key) is { } loaded)
            {
                return loaded;
            }

            var (permissions, edition) = await ReadAsync(of, key, ct);
            var entry = new CachedPermissions(key, permissions, edition, this.clock.GetUtcNow());
            this.cache = entry;
            return entry;
        }
        finally
        {
            this.cacheLock.Release();
        }
    }

    private async Task<(HashSet<RolePermission> Permissions, TenantEdition Edition)> ReadAsync(IRuntimeExecutionContext of, CacheKey key, CancellationToken ct)
    {
        if (key.TenantId is not { } tenantId || key.UserId is not { } userId)
        {
            return ([], TenantEdition.Staffing);
        }

        using var context = this.dbFactory.CreateDbContext(of);

        var tenant = await context.Tenants.AsNoTracking()
            .Where(row => row.Id == tenantId)
            .Select(row => new { row.ParentTenantId })
            .FirstOrDefaultAsync(ct);
        if (tenant is null)
        {
            return ([], TenantEdition.Staffing);
        }

        var edition = TenantEditions.Of(tenant.ParentTenantId);

        var roleIds = await context.OrganizationUnitUserRoles
            .Where(ur => ur.UserId == userId && ur.TenantId == tenantId
                && (ur.OrganizationUnit.State == OrganizationUnitState.Active || ur.OrganizationUnit.UnitType == OrganizationUnitType.Company)
                && ur.TenantRole.State == TenantRoleState.Active
                && ur.User.State == UserState.Active)
            .Select(ur => ur.TenantRoleId)
            .Distinct()
            .ToListAsync(ct);

        if (roleIds.Count == 0)
        {
            return ([], edition);
        }

        var permissions = await context.TenantRolePermissions
            .Where(rp => roleIds.Contains(rp.TenantRoleId))
            .Select(rp => rp.Permission)
            .Distinct()
            .ToListAsync(ct);

        return (TenantEditions.Caps(edition, permissions), edition);
    }

    private readonly record struct CacheKey(Guid? TenantId, Guid? UserId);

    private sealed record CachedPermissions(CacheKey Key, HashSet<RolePermission> Permissions, TenantEdition Edition, DateTimeOffset LoadedAt);
}
