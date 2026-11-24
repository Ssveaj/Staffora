using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Caching;
using Staffora.Services.Settings;

namespace Staffora.Services.Scheduling;

public sealed record ClosedDayRow(Guid Id, Guid? TenantId, string Name, DateOnly FirstDay, DateOnly LastDay, Guid? UnitId, string? Unit) : ILookupRow
{
    public ClosedDayFact Fact => new(Id, Name, FirstDay, LastDay, Unit);
}

public sealed class ClosedDayService
{
    public static readonly IReadOnlyList<RolePermission> CalendarReaders = [RolePermission.SettingsView, RolePermission.ViewCoordination];

    public const string NoView = "You do not have permission to view closed days.";

    public const string NoEdit = "You do not have permission to change closed days.";

    public const string NotFound = "Closed day not found.";

    public const string UnitNotInUse = "Choose a unit of the organization that is in use, or the whole organization.";

    public const string OwnTenantOnly = "Closed days are changed inside the organization, not from the platform console.";

    public const string ReadFailed = "The closed days could not be read. Try again.";

    public const string SaveFailed = "The closed day could not be saved. Try again.";

    public const string DeleteFailed = "The closed day could not be deleted. Try again.";

    internal static readonly LookupCatalogue<ClosedDayRow> Catalogue = new("closed days", LoadCatalogueAsync);

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly TenantLookupCache lookupCache;
    private readonly TenantClock tenantClock;
    private readonly ILogger<ClosedDayService> log;

    public ClosedDayService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, TenantLookupCache lookupCache, TenantClock tenantClock, ILogger<ClosedDayService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.lookupCache = lookupCache;
        this.tenantClock = tenantClock;
        this.log = log;
    }

    public async Task<Result<ListPage<ClosedDayListItem>>> QueryClosedDaysAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.SettingsView, cancellationToken))
            return Result<ListPage<ClosedDayListItem>>.Failure(NoView);

        var tenant = TenantOf(this.runtime.Current);
        if (!tenant.IsSuccess)
            return Result<ListPage<ClosedDayListItem>>.Failure(tenant.Error);

        var list = ClosedDayList.Definition.Normalize(query);
        var today = await this.tenantClock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<ListPage<ClosedDayListItem>>.Failure(today.Error);

        var tenantId = tenant.Value;
        return await ReadAsync("the closed days", async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var entries = context.ClosedDays.AsNoTracking().Where(closedDay => closedDay.TenantId == tenantId);
            if (list.Search is { Length: > 0 } search)
            {
                entries = entries.Where(closedDay => closedDay.Name.Contains(search));
            }

            var day = today.Value;
            entries = list.Filter(ClosedDayList.When) switch
            {
                ClosedDayList.Upcoming => entries.Where(closedDay => closedDay.LastDay >= day),
                ClosedDayList.Past => entries.Where(closedDay => closedDay.LastDay < day),
                _ => entries,
            };

            var total = await entries.CountAsync(token);
            var page = ListPage<ClosedDayListItem>.PageWithin(list, total);
            var ordered = list.Sort == ClosedDayList.SortName
                ? new ListOrder<ClosedDay>(entries).By(closedDay => closedDay.Name, list.Descending).By(closedDay => closedDay.FirstDay, list.Descending)
                : new ListOrder<ClosedDay>(entries).By(closedDay => closedDay.FirstDay, list.Descending).By(closedDay => closedDay.Name, list.Descending);
            var rows = await ordered
                .By(closedDay => closedDay.Id)
                .Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(closedDay => new ClosedDayListItem(closedDay.Id, closedDay.Name, closedDay.FirstDay, closedDay.LastDay, closedDay.OrganizationUnit == null ? null : closedDay.OrganizationUnit.Name))
                .ToListAsync(token);
            return ListPage<ClosedDayListItem>.Ungrouped(rows, total, page, list.PageSize);
        }, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<ClosedDayFact>>> GetCalendarAsync(CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(CalendarReaders, cancellationToken))
            return Result<IReadOnlyList<ClosedDayFact>>.Failure(NoView);

        var tenant = TenantOf(this.runtime.Current);
        if (!tenant.IsSuccess)
            return Result<IReadOnlyList<ClosedDayFact>>.Failure(tenant.Error);

        return await ReadAsync("the closed days", async token =>
        {
            var rows = await this.lookupCache.GetAsync(Catalogue, tenant.Value, token);
            IReadOnlyList<ClosedDayFact> facts = [.. rows.Select(row => row.Fact)];
            return facts;
        }, cancellationToken);
    }

    public async Task<Result<ClosedDayEditor>> GetEditorAsync(Guid? id, CancellationToken cancellationToken = default)
    {
        var edits = await this.permissionService.HasPermissionAsync(RolePermission.SettingsEdit, cancellationToken);
        if (id is null ? !edits : !edits && !await this.permissionService.HasPermissionAsync(RolePermission.SettingsView, cancellationToken))
            return Result<ClosedDayEditor>.Failure(id is null ? NoEdit : NoView);

        var session = this.runtime.Current;
        var tenant = TenantOf(session);
        if (!tenant.IsSuccess)
            return Result<ClosedDayEditor>.Failure(tenant.Error);

        var today = await this.tenantClock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<ClosedDayEditor>.Failure(today.Error);

        var tenantId = tenant.Value;
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var stored = id is { } closedDayId
                ? await context.ClosedDays.AsNoTracking()
                    .Where(closedDay => closedDay.Id == closedDayId && closedDay.TenantId == tenantId)
                    .Select(closedDay => new ClosedDayDraft(closedDay.Name, closedDay.FirstDay, closedDay.LastDay, closedDay.OrganizationUnitId))
                    .FirstOrDefaultAsync(cancellationToken)
                : new ClosedDayDraft(string.Empty, today.Value, today.Value, null);
            if (stored is null)
                return Result<ClosedDayEditor>.Failure(NotFound);

            var named = stored.OrganizationUnitId;
            var units = await context.OrganizationUnits.AsNoTracking()
                .Where(unit => unit.TenantId == tenantId
                    && unit.UnitType != OrganizationUnitType.Company
                    && (unit.State == OrganizationUnitState.Active || unit.Id == named))
                .OrderBy(unit => unit.Name)
                .ThenBy(unit => unit.Id)
                .Select(unit => new ClosedDayUnitChoice(unit.Id, unit.Name))
                .ToListAsync(cancellationToken);
            return Result<ClosedDayEditor>.Success(new ClosedDayEditor(id, stored, edits && !session.HasPlatformReach(), [ClosedDayUnitChoice.WholeOrganization, .. units]));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the editor of closed day {ClosedDayId} of tenant {TenantId} failed.", id, tenantId);
            return Result<ClosedDayEditor>.Failure(ReadFailed);
        }
    }

    public async Task<Result<Guid>> CreateClosedDayAsync(ClosedDayDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.SettingsEdit, cancellationToken))
            return Result<Guid>.Failure(NoEdit);

        var tenant = TenantOf(this.runtime.Current);
        if (!tenant.IsSuccess)
            return Result<Guid>.Failure(tenant.Error);

        var name = draft.Name?.Trim() ?? string.Empty;
        if (ClosedDayRules.Problem(name, draft.FirstDay, draft.LastDay) is { } problem)
            return Result<Guid>.Failure(problem);

        var tenantId = tenant.Value;
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var unit = await UnitInUseAsync(context, tenantId, draft.OrganizationUnitId, cancellationToken);
            if (!unit.IsSuccess)
                return Result<Guid>.Failure(unit.Error);

            var row = new ClosedDay { TenantId = tenantId, Name = name, FirstDay = draft.FirstDay, LastDay = draft.LastDay, OrganizationUnitId = unit.Value };
            context.ClosedDays.Add(row);
            await context.SaveChangesAsync(cancellationToken);
            return Result<Guid>.Success(row.Id);
        }
        catch (TenantHeldException held)
        {
            return Result<Guid>.Failure(held.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Saving a new closed day of tenant {TenantId} failed.", tenantId);
            return Result<Guid>.Failure(SaveFailed);
        }
        finally
        {
            this.lookupCache.Invalidate(Catalogue, tenantId);
        }
    }

    public async Task<Result> UpdateClosedDayAsync(Guid id, ClosedDayDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.SettingsEdit, cancellationToken))
            return Result.Failure(NoEdit);

        var tenant = TenantOf(this.runtime.Current);
        if (!tenant.IsSuccess)
            return Result.Failure(tenant.Error);

        var name = draft.Name?.Trim() ?? string.Empty;
        if (ClosedDayRules.Problem(name, draft.FirstDay, draft.LastDay) is { } problem)
            return Result.Failure(problem);

        var tenantId = tenant.Value;
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var row = await context.ClosedDays.FirstOrDefaultAsync(closedDay => closedDay.Id == id && closedDay.TenantId == tenantId, cancellationToken);
            if (row is null)
                return Result.Failure(NotFound);

            var unit = row.OrganizationUnitId == draft.OrganizationUnitId ? Result<Guid?>.Success(row.OrganizationUnitId) : await UnitInUseAsync(context, tenantId, draft.OrganizationUnitId, cancellationToken);
            if (!unit.IsSuccess)
                return Result.Failure(unit.Error);

            (row.Name, row.FirstDay, row.LastDay, row.OrganizationUnitId) = (name, draft.FirstDay, draft.LastDay, unit.Value);
            await context.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (TenantHeldException held)
        {
            return Result.Failure(held.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Saving the closed day {ClosedDayId} of tenant {TenantId} failed.", id, tenantId);
            return Result.Failure(SaveFailed);
        }
        finally
        {
            this.lookupCache.Invalidate(Catalogue, tenantId);
        }
    }

    public async Task<Result> DeleteClosedDayAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.SettingsEdit, cancellationToken))
            return Result.Failure(NoEdit);

        var tenant = TenantOf(this.runtime.Current);
        if (!tenant.IsSuccess)
            return Result.Failure(tenant.Error);

        var tenantId = tenant.Value;
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var row = await context.ClosedDays.FirstOrDefaultAsync(closedDay => closedDay.Id == id && closedDay.TenantId == tenantId, cancellationToken);
            if (row is null)
                return Result.Failure(NotFound);

            context.ClosedDays.Remove(row);
            await context.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (TenantHeldException held)
        {
            return Result.Failure(held.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Deleting the closed day {ClosedDayId} of tenant {TenantId} failed.", id, tenantId);
            return Result.Failure(DeleteFailed);
        }
        finally
        {
            this.lookupCache.Invalidate(Catalogue, tenantId);
        }
    }

    private static async Task<Result<Guid?>> UnitInUseAsync(ApplicationDbContext context, Guid tenantId, Guid? unitId, CancellationToken cancellationToken)
    {
        if (unitId is not { } asked)
            return Result<Guid?>.Success(null);

        var unit = await context.OrganizationUnits.AsNoTracking()
            .Where(candidate => candidate.Id == asked && candidate.TenantId == tenantId)
            .Select(candidate => new { candidate.UnitType, candidate.State })
            .FirstOrDefaultAsync(cancellationToken);
        return unit switch
        {
            { UnitType: OrganizationUnitType.Company } => Result<Guid?>.Success(null),
            { State: OrganizationUnitState.Active } => Result<Guid?>.Success(asked),
            _ => Result<Guid?>.Failure(UnitNotInUse),
        };
    }

    private static Result<Guid> TenantOf(RuntimeSnapshot session) =>
        session.HasPlatformReach() ? Result<Guid>.Failure(OwnTenantOnly)
        : session.TenantId is { } tenantId ? Result<Guid>.Success(tenantId)
        : Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

    private static async Task<List<ClosedDayRow>> LoadCatalogueAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken) =>
        await context.ClosedDays.AsNoTracking()
            .Where(closedDay => closedDay.TenantId == tenantId)
            .OrderBy(closedDay => closedDay.FirstDay)
            .ThenBy(closedDay => closedDay.Id)
            .Select(closedDay => new ClosedDayRow(closedDay.Id, closedDay.TenantId, closedDay.Name, closedDay.FirstDay, closedDay.LastDay, closedDay.OrganizationUnitId, closedDay.OrganizationUnit == null ? null : closedDay.OrganizationUnit.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    private async Task<Result<T>> ReadAsync<T>(string what, Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            return Result<T>.Success(await read(cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading {What} of tenant {TenantId} failed.", what, this.runtime.TenantId);
            return Result<T>.Failure(ReadFailed);
        }
    }
}
