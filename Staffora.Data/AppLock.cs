using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Staffora.Data;

public sealed class AppLock
{
    public const int DefaultTimeoutMilliseconds = 15_000;

    public static readonly AppLock Structure = new(1, "org-structure", "Someone else is changing the organization tree right now. Try again in a moment.");

    public static readonly AppLock Schedules = new(2, "schedules", "Someone else is changing the staff members' schedules right now. Try again in a moment.");

    public static readonly AppLock Customers = new(3, "customers", "Someone else is changing the customers right now. Try again in a moment.");

    public static readonly AppLock Jobs = new(4, "jobs", "Someone else is changing the jobs right now. Try again in a moment.");

    public static readonly AppLock CommentThread = new(5, "comment-threads", "Someone else is writing a comment on this record right now. Try again in a moment.", 10_000);

    public static readonly AppLock TenantCreate = new(0, "tenants:create", "Another tenant is being created right now. Try again in a moment.");

    public static readonly AppLock PlatformBootstrap = new(0, "Staffora.PlatformBootstrap", "Another bootstrap is running on this database.", 60_000);

    private const string TakeSql = "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = @owner, @LockTimeout = @timeout";

    private const string ReleaseSql = "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = N'Session'";

    private AppLock(int rank, string name, string busyReason, int timeoutMilliseconds = DefaultTimeoutMilliseconds)
    {
        Rank = rank;
        Name = name;
        BusyReason = busyReason;
        TimeoutMilliseconds = timeoutMilliseconds;
    }

    public int Rank { get; }

    public string Name { get; }

    public string BusyReason { get; }

    public int TimeoutMilliseconds { get; }

    public string Resource(Guid tenantId, string? record = null) =>
        record is null ? $"{Name}:{tenantId:N}" : $"{Name}:{tenantId:N}:{record}";

    public Task TakeAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken) =>
        TakeAsync(context, tenantId, record: null, synchronously: false, cancellationToken);

    public Task TakeAsync(ApplicationDbContext context, Guid tenantId, string record, CancellationToken cancellationToken) =>
        TakeAsync(context, tenantId, record, synchronously: false, cancellationToken);

    public static async Task TakeInOrderAsync(ApplicationDbContext context, Guid tenantId, IEnumerable<AppLock> locks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(locks);
        foreach (var appLock in locks.Distinct().OrderBy(appLock => appLock.Rank))
        {
            if (ReferenceEquals(appLock, Structure))
            {
                await context.LockOrganizationStructureAsync(tenantId, cancellationToken);
            }
            else
            {
                await appLock.TakeAsync(context, tenantId, cancellationToken);
            }
        }
    }

    public Task TakeForPlatformAsync(ApplicationDbContext context, CancellationToken cancellationToken) =>
        TakeAsync(context, tenantId: null, record: null, synchronously: false, cancellationToken);

    internal async Task TakeAsync(ApplicationDbContext context, Guid? tenantId, string? record, bool synchronously, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException($"The {Name} lock belongs to a transaction: begin one before taking it.");
        }

        if (tenantId is { } tenant)
        {
            await context.HoldTenantBeforeLockAsync(tenant, synchronously, cancellationToken);
        }

        var resource = tenantId is { } owner ? Resource(owner, record) : Name;
        if (await RequestAsync(context, resource, "Transaction", synchronously, cancellationToken) < 0)
        {
            throw new AppLockBusyException(this);
        }
    }

    public async Task<bool> TryTakeForSessionAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await RequestAsync(context, Name, "Session", synchronously: false, cancellationToken) >= 0;
    }

    public Task ReleaseForSessionAsync(ApplicationDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Database.ExecuteSqlRawAsync(ReleaseSql, [new SqlParameter("@resource", Name)], CancellationToken.None);
    }

    private async Task<int> RequestAsync(ApplicationDbContext context, string resource, string owner, bool synchronously, CancellationToken cancellationToken)
    {
        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        object[] parameters =
        [
            result,
            new SqlParameter("@resource", resource),
            new SqlParameter("@owner", owner),
            new SqlParameter("@timeout", TimeoutMilliseconds),
        ];
        if (synchronously)
        {
            context.Database.ExecuteSqlRaw(TakeSql, parameters);
        }
        else
        {
            await context.Database.ExecuteSqlRawAsync(TakeSql, parameters, cancellationToken);
        }

        return result.Value is int granted ? granted : -999;
    }
}

public sealed class AppLockBusyException : InvalidOperationException
{
    public AppLockBusyException(AppLock appLock) : base(appLock?.BusyReason)
    {
        ArgumentNullException.ThrowIfNull(appLock);
        Lock = appLock;
    }

    public AppLock Lock { get; }
}
