using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Data;

namespace Staffora.Services.Services;

public sealed class SystemOwnerPermissionsEnsure
{
    private readonly IApplicationDbContextFactory<ApplicationDbContext> contexts;
    private readonly ILogger<SystemOwnerPermissionsEnsure> logger;

    public SystemOwnerPermissionsEnsure(IApplicationDbContextFactory<ApplicationDbContext> contexts, ILogger<SystemOwnerPermissionsEnsure> logger)
    {
        this.contexts = contexts;
        this.logger = logger;
    }

    public static IReadOnlyList<TenantState> KeptStates { get; } = [TenantState.Setup, TenantState.Active, TenantState.Suspended];

    public async Task<Result<SystemOwnerPermissionsSummary>> EnsureForEveryTenantAsync(CancellationToken cancellationToken = default)
    {
        List<Guid> tenantIds;
        try
        {
            tenantIds = await TenantsMissingAPermissionAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException or SqlException)
        {
            this.logger.LogError(exception, "Could not list the tenants whose System owner role is kept complete.");
            return Result<SystemOwnerPermissionsSummary>.Failure("The System owner roles could not be brought up to date.");
        }

        var added = 0;
        var failed = new List<Guid>();
        foreach (var tenantId in tenantIds)
        {
            try
            {
                added += await EnsureOneAsync(tenantId, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException or SqlException or OrganizationStructureException)
            {
                this.logger.LogError(exception, "Could not bring the System owner role of tenant {TenantId} up to date.", tenantId);
                failed.Add(tenantId);
            }
        }

        if (failed.Count > 0)
        {
            this.logger.LogError("Brought the System owner roles of {TenantCount} tenants up to date: {Added} permissions added, {FailedCount} tenants failed ({FailedTenantIds}).", tenantIds.Count, added, failed.Count, failed);
        }
        else
        {
            this.logger.LogInformation("Brought the System owner roles of {TenantCount} tenants up to date: {Added} permissions added.", tenantIds.Count, added);
        }

        return Result<SystemOwnerPermissionsSummary>.Success(new SystemOwnerPermissionsSummary(added, failed));
    }

    private async Task<List<Guid>> TenantsMissingAPermissionAsync(CancellationToken cancellationToken)
    {
        var preset = RolePresets.SystemOwner.Permissions.Distinct().ToList();
        var kept = KeptStates.ToList();
        await using var context = this.contexts.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("system owners: find the tenants whose System owner role misses a permission");
        return await context.TenantRoles
            .Where(role => role.IsSystemOwner
                && context.Tenants.Any(tenant => tenant.Id == role.TenantId && kept.Contains(tenant.State))
                && role.Permissions.Count(permission => preset.Contains(permission.Permission)) < preset.Count)
            .Select(role => role.TenantId)
            .Distinct()
            .OrderBy(tenantId => tenantId)
            .ToListAsync(cancellationToken);
    }

    private async Task<int> EnsureOneAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AddMissingAsync(tenantId, cancellationToken);
            }
            catch (DbUpdateException exception) when (attempt == 1 && exception.InnerException is SqlException { Number: 2601 or 2627 })
            {
                this.logger.LogWarning(exception, "Another writer added a permission to the System owner role of tenant {TenantId} at the same time; reading it once more.", tenantId);
            }
        }
    }

    private async Task<int> AddMissingAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var context = this.contexts.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("system owners: give one tenant's System owner role every permission outside own work, on its behalf");
        await using var transaction = await context.Database.BeginTransactionAsync(ApplicationDbContext.OrganizationStructureIsolationLevel, cancellationToken);
        await context.LockOrganizationStructureAsync(tenantId, cancellationToken);

        var kept = KeptStates.ToList();
        var role = await context.TenantRoles
            .Include(row => row.Permissions)
            .Where(row => row.TenantId == tenantId && row.IsSystemOwner && context.Tenants.Any(tenant => tenant.Id == tenantId && kept.Contains(tenant.State)))
            .FirstOrDefaultAsync(cancellationToken);
        if (role is null)
        {
            return 0;
        }

        var missing = RolePresets.SystemOwner.Permissions.Except(role.Permissions.Select(row => row.Permission)).ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        var account = SystemAccount.IdFor(tenantId);
        foreach (var permission in missing)
        {
            role.Permissions.Add(new TenantRolePermission { TenantId = tenantId, Permission = permission, CreatedById = account });
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        this.logger.LogInformation("Gave the System owner role of tenant {TenantId} the permissions a release added: {Permissions}.", tenantId, missing);
        return missing.Count;
    }
}

public sealed record SystemOwnerPermissionsSummary(int Added, IReadOnlyList<Guid> FailedTenantIds);
