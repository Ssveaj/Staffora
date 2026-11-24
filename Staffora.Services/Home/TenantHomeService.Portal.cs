using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Services.Authorization;

namespace Staffora.Services.Home;

public sealed partial class TenantHomeService
{
    public static readonly IReadOnlyList<RolePermission> PortalReaders = RolePermissions.Portal;

    public const string PortalNoView = "You do not have permission to use the portal.";

    public const string InsidePortalOnly = "The platform console reads no portal: impersonate one of its people to see it.";

    public const string PortalReadFailed = "The portal could not be read. Try again.";

    public const int RecentRequests = 5;

    public async Task<Result<PortalOverview>> ReadPortalAsync(CancellationToken cancellationToken = default)
    {
        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<PortalOverview>.Failure(InsidePortalOnly);

        if (!await this.permissionService.HasAnyPermissionAsync(PortalReaders, cancellationToken))
            return Result<PortalOverview>.Failure(PortalNoView);

        if (session.TenantId is not { } tenantId)
            return Result<PortalOverview>.Failure(IRuntimeExecutionContext.NoTenant);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var supplier = await context.Suppliers.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.State == SupplierState.Active)
                .OrderBy(row => row.Name)
                .Select(row => row.Name)
                .FirstOrDefaultAsync(cancellationToken);
            var follows = await this.permissionService.HasPermissionAsync(RolePermission.OrdersView, cancellationToken);
            IReadOnlyList<OrderListItem> recent = [];
            var own = 0;
            if (follows && session.EffectiveUserId is { } userId)
            {
                var mine = context.Orders.AsNoTracking().Where(request => request.TenantId == tenantId && request.OrdererId == userId);
                own = await mine.CountAsync(cancellationToken);
                recent = await mine
                    .OrderByDescending(request => request.Created)
                    .ThenBy(request => request.Id)
                    .Take(RecentRequests)
                    .Select(request => new OrderListItem(request.Id, request.Name, request.PeriodStart, request.PeriodEnd, request.State, request.Orderer.FirstName + " " + request.Orderer.LastName, true, request.Created)
                    {
                        Times = new JobTimes(request.Schedule, request.PeriodStart, request.PeriodEnd, request.StartTime, request.EndTime, new List<OccurrenceTimes>()),
                    })
                    .ToListAsync(cancellationToken);
            }

            return Result<PortalOverview>.Success(new PortalOverview(
                supplier,
                MayFollowRequests: follows,
                MayRequest: await this.permissionService.HasPermissionAsync(RolePermission.OrdersCreate, cancellationToken),
                MayManageUsers: await this.permissionService.HasPermissionAsync(RolePermission.CustomerUsersManage, cancellationToken))
            {
                RecentRequests = recent,
                OwnRequests = own,
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the portal home of tenant {TenantId} failed.", tenantId);
            return Result<PortalOverview>.Failure(PortalReadFailed);
        }
    }
}

public sealed record PortalOverview(string? Supplier, bool MayFollowRequests, bool MayRequest, bool MayManageUsers)
{
    public IReadOnlyList<OrderListItem> RecentRequests { get; init; } = [];

    public int OwnRequests { get; init; }
}
