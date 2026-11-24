using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Diagnostics;
using Staffora.Services.Scheduling;
using Staffora.Services.Settings;

namespace Staffora.Services.Resources;

public sealed class ResourceService
{
    public const string NoResourcesView = "You do not have permission to view staff members.";

    public const string ReadFailed = "The staff members could not be read. Try again.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly TenantClock clock;
    private readonly ILogger<ResourceService> log;

    public ResourceService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, TenantClock clock, ILogger<ResourceService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.clock = clock;
        this.log = log;
    }

    public async Task<Result<ListPage<StaffListItem>>> QueryStaffMembersAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ResourcesView, cancellationToken))
            return Result<ListPage<StaffListItem>>.Failure(NoResourcesView);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<ListPage<StaffListItem>>.Failure(IRuntimeExecutionContext.NoTenant);

        var list = StaffList.Definition.Normalize(query);
        var today = await this.clock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<ListPage<StaffListItem>>.Failure(today.Error);

        return await ReadAsync("the staff members", token => ReadStaffMembersAsync(tenantId, list, today.Value, token), cancellationToken);
    }

    private async Task<ListPage<StaffListItem>> ReadStaffMembersAsync(Guid tenantId, ListQuery list, DateOnly today, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var records = context.Resources.AsNoTracking().Where(resource => resource.TenantId == tenantId);
        if (list.Search is { Length: > 0 } search)
        {
            records = records.Where(resource => (resource.User.FirstName + " " + resource.User.LastName).Contains(search) || (resource.User.Email != null && resource.User.Email.Contains(search)));
        }

        if (list.Filter(StaffList.Status) is { } status && ListOption.TryParseEnum<ResourceState>(status, out var state))
        {
            records = records.Where(resource => resource.State == state);
        }

        records = list.Filter(StaffList.Pattern) switch
        {
            StaffList.PatternCurrent => records.WithCurrent(context, today),
            StaffList.PatternNone => records.WithoutPattern(context, today),
            StaffList.PatternEnding => records.EndingSoon(context, today),
            _ => records,
        };

        var total = await records.CountAsync(cancellationToken);
        var page = ListPage<StaffListItem>.PageWithin(list, total);
        var order = new ListOrder<Resource>(records);
        _ = list.Sort switch
        {
            StaffList.SortEmail => order.By(resource => resource.User.Email, list.Descending),
            StaffList.SortPattern => order.By(StaffPatternCoverage.PatternName(context, today), list.Descending).By(resource => resource.User.FirstName).By(resource => resource.User.LastName),
            _ => order.By(resource => resource.User.FirstName, list.Descending).By(resource => resource.User.LastName, list.Descending).By(resource => resource.User.Email, list.Descending),
        };
        var rows = await order.By(resource => resource.Id).Query
            .Skip((page - 1) * list.PageSize)
            .Take(list.PageSize)
            .Select(resource => new
            {
                resource.Id,
                resource.UserId,
                Name = (resource.User.FirstName + " " + resource.User.LastName).Trim(),
                resource.User.Email,
                resource.State,
            })
            .ToListAsync(cancellationToken);

        var ids = rows.Select(row => row.Id).ToList();
        var units = (await context.ResourceUnitMemberships.AsNoTracking()
                .Where(membership => membership.TenantId == tenantId && ids.Contains(membership.ResourceId))
                .Select(membership => new { membership.ResourceId, membership.OrganizationUnit.Name })
                .ToListAsync(cancellationToken))
            .ToLookup(membership => membership.ResourceId, membership => membership.Name);
        var covers = await StaffPatternCoverage.CoversAsync(context, ids, today, cancellationToken);
        List<StaffListItem> items = [.. rows.Select(row => new StaffListItem(row.Id, row.UserId, row.Name.Length > 0 ? row.Name : row.Email ?? "Unknown user", row.Email ?? string.Empty, row.State)
        {
            Units = [.. units[row.Id].Order(StringComparer.CurrentCultureIgnoreCase)],
            WorkPattern = covers.GetValueOrDefault(row.Id),
        })];
        return ListPage<StaffListItem>.Ungrouped(items, total, page, list.PageSize);
    }

    internal Task<Result<StaffSummary?>> SummaryOfAsync(Guid userId, CancellationToken cancellationToken) =>
        ReadAsync("a user's staff record", async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            return await context.Resources.AsNoTracking()
                .Where(resource => resource.UserId == userId)
                .Select(resource => new StaffSummary(resource.Id, resource.State))
                .FirstOrDefaultAsync(token);
        }, cancellationToken);

    internal Task<Result<ListPage<StaffUnitRow>>> UnitsOfAsync(Guid userId, ListQuery query, CancellationToken cancellationToken)
    {
        var list = StaffUnitList.Definition.Normalize(query);
        return ReadAsync("a staff member's units", async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var memberships = context.ResourceUnitMemberships.AsNoTracking().Where(membership => membership.Resource.UserId == userId);
            if (list.Search is { Length: > 0 } search)
            {
                memberships = memberships.Where(membership => membership.OrganizationUnit.Name.Contains(search));
            }

            var total = await memberships.CountAsync(token);
            var page = ListPage<StaffUnitRow>.PageWithin(list, total);
            var rows = await new ListOrder<ResourceUnitMembership>(memberships)
                .By(membership => membership.OrganizationUnit.Name, list.Descending)
                .By(membership => membership.OrganizationUnitId)
                .Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(membership => new StaffUnitRow(membership.OrganizationUnitId, membership.OrganizationUnit.Name, membership.OrganizationUnit.State, membership.OrganizationUnit.UnitType == OrganizationUnitType.Company))
                .ToListAsync(token);
            return ListPage<StaffUnitRow>.Ungrouped(rows, total, page, list.PageSize);
        }, cancellationToken);
    }

    private Task<Result<T>> ReadAsync<T>(string what, Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken) =>
        DeadlockRetry.ReadAsync(async token => Result<T>.Success(await read(token)), this.log, $"{what} of tenant {this.runtime.TenantId}", ReadFailed, cancellationToken);
}
