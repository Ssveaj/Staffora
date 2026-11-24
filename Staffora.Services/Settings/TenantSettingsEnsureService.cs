using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Settings;
using Staffora.Data;
using Staffora.Services.Caching;

namespace Staffora.Services.Settings;

public sealed class TenantSettingsEnsureService
{
    private readonly IApplicationDbContextFactory<ApplicationDbContext> contexts;
    private readonly IOptions<PlatformOptions> platform;
    private readonly TenantLookupCache lookupCache;
    private readonly ILogger<TenantSettingsEnsureService> logger;

    public TenantSettingsEnsureService(IApplicationDbContextFactory<ApplicationDbContext> contexts, IOptions<PlatformOptions> platform, TenantLookupCache lookupCache, ILogger<TenantSettingsEnsureService> logger)
    {
        this.contexts = contexts;
        this.platform = platform;
        this.lookupCache = lookupCache;
        this.logger = logger;
    }

    public async Task<Result<bool>> EnsureTimeZoneAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var zone = PlatformZone();
        try
        {
            var created = await EnsureOneAsync(tenantId, zone, cancellationToken);
            if (created)
            {
                this.logger.LogInformation("Pinned the time zone of tenant {TenantId} to the platform's, {TimeZone}.", tenantId, zone);
            }

            return Result<bool>.Success(created);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException)
        {
            this.logger.LogError(exception, "Could not pin the time zone of tenant {TenantId}.", tenantId);
            return Result<bool>.Failure("The tenant's time zone could not be pinned.");
        }
    }

    public async Task<bool> EnsureTimeZoneAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var zone = PlatformZone();
        var key = TenantSettingDefinitions.TimeZone.Key;
        using var lease = context.BeginSystemScope("tenant settings: pin a new tenant's time zone to the platform's");
        var tenant = await context.Tenants.AsNoTracking().Where(candidate => candidate.Id == tenantId).Select(candidate => new { candidate.IsRootTenant }).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("A time zone is pinned only for an existing tenant.");
        if (tenant.IsRootTenant || await context.TenantSettings.AnyAsync(setting => setting.TenantId == tenantId && setting.Key == key, cancellationToken))
        {
            return false;
        }

        context.TenantSettings.Add(new TenantSetting { TenantId = tenantId, Key = key, Value = zone, CreatedById = SystemAccount.IdFor(tenantId) });
        await context.SaveChangesAsync(cancellationToken);
        this.logger.LogInformation("Pinned the time zone of new tenant {TenantId} to the platform's, {TimeZone}.", tenantId, zone);
        return true;
    }

    public void ForgetCachedSettings(Guid tenantId) => this.lookupCache.Invalidate(TenantSettingsService.Catalogue, tenantId);

    public async Task<Result<TenantSettingsEnsureSummary>> EnsureTimeZoneForEveryTenantAsync(CancellationToken cancellationToken = default)
    {
        var zone = PlatformZone();
        List<Guid> tenantIds;
        try
        {
            await using var context = this.contexts.CreateSystemDbContext();
            using var lease = context.BeginSystemScope("tenant settings: list the tenants whose time zone is pinned");
            tenantIds = await context.Tenants.Where(tenant => !tenant.IsRootTenant).OrderBy(tenant => tenant.Id).Select(tenant => tenant.Id).ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException or SqlException)
        {
            this.logger.LogError(exception, "Could not list the tenants whose time zones are pinned.");
            return Result<TenantSettingsEnsureSummary>.Failure("The tenants' time zones could not be pinned.");
        }

        var created = 0;
        var failed = new List<Guid>();
        foreach (var tenantId in tenantIds)
        {
            try
            {
                if (await EnsureOneAsync(tenantId, zone, cancellationToken))
                {
                    created++;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException)
            {
                this.logger.LogError(exception, "Could not pin the time zone of tenant {TenantId}.", tenantId);
                failed.Add(tenantId);
            }
        }

        if (failed.Count > 0)
        {
            this.logger.LogError("Pinned the time zones of {TenantCount} tenants to {TimeZone}: {Created} written, {FailedCount} failed ({FailedTenantIds}).", tenantIds.Count, zone, created, failed.Count, failed);
        }
        else
        {
            this.logger.LogInformation("Pinned the time zones of {TenantCount} tenants to {TimeZone}: {Created} written.", tenantIds.Count, zone, created);
        }

        return Result<TenantSettingsEnsureSummary>.Success(new TenantSettingsEnsureSummary(created, failed));
    }

    private string PlatformZone() => TenantSettingDefinitions.TimeZone.DefaultText(this.platform.Value.Defaults);

    private async Task<bool> EnsureOneAsync(Guid tenantId, string zone, CancellationToken cancellationToken)
    {
        var key = TenantSettingDefinitions.TimeZone.Key;
        await using var context = this.contexts.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("tenant settings: pin one tenant's time zone to the platform's");
        try
        {
            var tenant = await context.Tenants.Where(candidate => candidate.Id == tenantId).Select(candidate => new { candidate.IsRootTenant }).FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("A time zone is pinned only for an existing tenant.");
            if (tenant.IsRootTenant || await context.TenantSettings.AnyAsync(setting => setting.TenantId == tenantId && setting.Key == key, cancellationToken))
            {
                return false;
            }

            var row = new TenantSetting { TenantId = tenantId, Key = key, Value = zone, CreatedById = SystemAccount.IdFor(tenantId) };
            context.TenantSettings.Add(row);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
            {
                context.Entry(row).State = EntityState.Detached;
                return false;
            }
        }
        finally
        {
            this.lookupCache.Invalidate(TenantSettingsService.Catalogue, tenantId);
        }
    }
}

public sealed record TenantSettingsEnsureSummary(int Created, IReadOnlyList<Guid> FailedTenantIds);
