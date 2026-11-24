using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Settings;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Caching;

namespace Staffora.Services.Settings;

public sealed record TenantSettingRow(Guid? TenantId, string Key, string Value, ulong Version) : ILookupRow;

public enum TenantSettingSource
{
    Stored = 1,

    Missing = 2,

    NotAccepted = 3,
}

public sealed record TenantSettingState(TenantSettingDefinition Definition, string Value, string? StoredValue, string DefaultValue, TenantSettingSource Source, ulong? Version);

public sealed record TenantSettingsSnapshot(IReadOnlyList<TenantSettingState> Settings, bool CanEdit)
{
    public TenantSettingState Of(TenantSettingDefinition definition) => Settings.Single(setting => setting.Definition == definition);
}

public class TenantSettingsService
{
    public const string NoViewPermission = "You do not have permission to view settings.";
    public const string NoEditPermission = "You do not have permission to change settings.";
    public const string UnknownSetting = "There is no such setting.";

    public const string ChangedMeanwhile = "Someone else changed this setting after you opened it. Open the settings again to see their change, then make yours.";

    public const string PlatformConsoleRefused = "Settings belong to a tenant: change them by impersonating one of the tenant's users.";

    public const string ConsoleOnly = "Only a platform owner on the platform console, not impersonating, reads another tenant's settings.";

    public const string UnknownTenant = "There is no such tenant.";

    public const string NotYourTenant = "Only a session of the organization itself, or the platform owner on the platform console, reads its settings.";

    internal static readonly LookupCatalogue<TenantSettingRow> Catalogue = new("tenant settings", LoadRowsAsync);

    private static readonly ConditionalWeakTable<IReadOnlyList<TenantSettingRow>, ConcurrentDictionary<(Guid TenantId, string Key), byte>> loggedFallbacks = new();

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly TenantLookupCache lookupCache;
    private readonly IOptions<PlatformOptions> platform;
    private readonly ILogger<TenantSettingsService> logger;

    public TenantSettingsService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, TenantLookupCache lookupCache, IOptions<PlatformOptions> platform, ILogger<TenantSettingsService> logger)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.lookupCache = lookupCache;
        this.platform = platform;
        this.logger = logger;
    }

    public async Task<Result<TenantSettingsSnapshot>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.SettingsView, cancellationToken))
            return Result<TenantSettingsSnapshot>.Failure(NoViewPermission);

        var canEdit = await this.permissionService.HasPermissionAsync(RolePermission.SettingsEdit, cancellationToken);

        var session = this.runtime.Current;
        if (TenantOf(session) is { IsSuccess: false } refused)
            return Result<TenantSettingsSnapshot>.Failure(refused.Error);

        var tenantId = session.TenantId!.Value;
        try
        {
            var rows = await this.lookupCache.GetAsync(Catalogue, tenantId, cancellationToken);
            var defaults = this.platform.Value.Defaults;
            var settings = TenantSettingDefinitions.All.Select(definition =>
            {
                var row = rows.FirstOrDefault(candidate => candidate.Key == definition.Key);
                var fallback = definition.DefaultText(defaults);
                var source = row is null ? TenantSettingSource.Missing
                    : definition.DescribeInvalid(row.Value) is null ? TenantSettingSource.Stored
                    : TenantSettingSource.NotAccepted;
                LogFallback(tenantId, definition, rows, row, source);
                return new TenantSettingState(definition, source == TenantSettingSource.Stored ? row!.Value : fallback, row?.Value, fallback, source, row?.Version);
            }).ToList();

            return Result<TenantSettingsSnapshot>.Success(new TenantSettingsSnapshot(settings, canEdit));
        }
        catch (Exception)
        {
            return Result<TenantSettingsSnapshot>.Failure("An error occurred while reading the settings.");
        }
    }

    public async Task<Result> SaveAsync(string key, string? value, ulong? loadedVersion, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.SettingsEdit, cancellationToken))
            return Result.Failure(NoEditPermission);

        var session = this.runtime.Current;
        if (TenantOf(session) is { IsSuccess: false } refused)
            return refused;

        if (TenantSettingDefinitions.Find(key) is not { } definition)
            return Result.Failure(UnknownSetting);

        var text = value?.Trim() ?? string.Empty;
        if (definition.DescribeInvalid(text) is { } reason)
            return Result.Failure(reason);

        var tenantId = session.TenantId!.Value;
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            try
            {
                var row = await context.TenantSettings.FirstOrDefaultAsync(setting => setting.TenantId == tenantId && setting.Key == definition.Key, cancellationToken);
                if (VersionOf(row) != loadedVersion)
                    return Result.Failure(ChangedMeanwhile);

                if (row is null)
                {
                    context.TenantSettings.Add(new TenantSetting { TenantId = tenantId, Key = definition.Key, Value = text });
                }
                else if (row.Value != text)
                {
                    row.Value = text;
                }
                else
                {
                    return Result.Success();
                }

                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Result.Failure(ChangedMeanwhile);
                }
                catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
                {
                    return Result.Failure(ChangedMeanwhile);
                }

                return Result.Success();
            }
            finally
            {
                this.lookupCache.Invalidate(Catalogue, tenantId);
            }
        }
        catch (TenantHeldException held)
        {
            return Result.Failure(held.Message);
        }
        catch (Exception)
        {
            return Result.Failure("An error occurred while saving the setting.");
        }
    }

    public async Task<Result<T>> GetAsync<T>(TenantSettingDefinition<T> definition, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(definition);
        var session = this.runtime.Current;
        if (session.TenantId is not { } tenantId || IsPlatformTenant(session))
            return Result<T>.Success(definition.DefaultValue(this.platform.Value.Defaults));

        return await ValueInForceAsync(definition, tenantId, cancellationToken);
    }

    public async Task<Result<T>> GetOfTenantAsync<T>(TenantSettingDefinition<T> definition, Guid tenantId, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(definition);
        var session = this.runtime.Current;
        if (tenantId == Guid.Empty || (session.TenantId != tenantId && !session.HasPlatformReach()))
            return Result<T>.Failure(NotYourTenant);

        if (session.IsInPlatformTenant && session.BaseTenantId == tenantId)
            return Result<T>.Success(definition.DefaultValue(this.platform.Value.Defaults));

        return await ValueInForceAsync(definition, tenantId, cancellationToken);
    }

    private async Task<Result<T>> ValueInForceAsync<T>(TenantSettingDefinition<T> definition, Guid tenantId, CancellationToken cancellationToken) where T : notnull
    {
        var defaults = this.platform.Value.Defaults;
        try
        {
            var rows = await this.lookupCache.GetAsync(Catalogue, tenantId, cancellationToken);
            var stored = rows.FirstOrDefault(row => row.Key == definition.Key);
            if (stored is not null && definition.TryParse(stored.Value, out var value))
                return Result<T>.Success(value);

            LogFallback(tenantId, definition, rows, stored, stored is null ? TenantSettingSource.Missing : TenantSettingSource.NotAccepted);
            return Result<T>.Success(definition.DefaultValue(defaults));
        }
        catch (Exception)
        {
            return Result<T>.Failure("An error occurred while reading the settings.");
        }
    }

    private static Result TenantOf(RuntimeSnapshot session) =>
        session.HasPlatformReach() ? Result.Failure(PlatformConsoleRefused)
        : session.TenantId is null ? Result.Failure(IRuntimeExecutionContext.NoTenant)
        : Result.Success();

    private static bool IsPlatformTenant(RuntimeSnapshot session) => session.IsInPlatformTenant && !session.IsImpersonating;

    private void LogFallback(Guid tenantId, TenantSettingDefinition definition, IReadOnlyList<TenantSettingRow> entry, TenantSettingRow? row, TenantSettingSource source)
    {
        if (source == TenantSettingSource.Stored || (source == TenantSettingSource.Missing && !definition.IsPinned) || !loggedFallbacks.GetOrCreateValue(entry).TryAdd((tenantId, definition.Key), 0))
            return;

        if (source == TenantSettingSource.Missing)
        {
            this.logger.LogWarning("Tenant {TenantId} holds no value of {Setting}, which startup and onboarding pin; the platform's default is used until one is saved.", tenantId, definition.Key);
        }
        else
        {
            this.logger.LogWarning("Tenant {TenantId} holds the value {Value} of {Setting}, which this server does not accept (its time zone data may not know it); the platform's default is used until another is saved.", tenantId, row?.Value, definition.Key);
        }
    }

    private static ulong? VersionOf(TenantSetting? row) => row is null ? null : BinaryPrimitives.ReadUInt64BigEndian(row.RowVersion);

    private static async Task<List<TenantSettingRow>> LoadRowsAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await context.TenantSettings.AsNoTracking()
            .Where(setting => setting.TenantId == tenantId)
            .OrderBy(setting => setting.Key)
            .Select(setting => new { setting.TenantId, setting.Key, setting.Value, setting.RowVersion })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(row => new TenantSettingRow(row.TenantId, row.Key, row.Value, BinaryPrimitives.ReadUInt64BigEndian(row.RowVersion)))];
    }
}
