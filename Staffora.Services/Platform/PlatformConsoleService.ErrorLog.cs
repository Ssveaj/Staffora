using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Services.Authorization;

namespace Staffora.Services.Platform;

public sealed partial class PlatformConsoleService
{
    public const string ErrorLogNotPlatformOwner = "Only a platform owner who is not impersonating can read the error log.";

    public const int ErrorLogMaxDays = 90;

    public async Task<Result<ListPage<ErrorLogEntryDto>>> QueryErrorLogAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!this.runtime.HasPlatformReach())
        {
            return Result<ListPage<ErrorLogEntryDto>>.Failure(ErrorLogNotPlatformOwner);
        }

        var list = ErrorLogList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "read the error log");
            if (lease is null)
            {
                return Result<ListPage<ErrorLogEntryDto>>.Failure(ErrorLogNotPlatformOwner);
            }

            var now = this.clock.GetUtcNow().UtcDateTime;
            var since = now.AddDays(-Math.Clamp(ErrorLogList.DaysOf(list.Filter(ErrorLogList.Period)), 1, ErrorLogMaxDays));
            var entries = context.ErrorLogs.AsNoTracking().Where(log => log.Occurred >= since);
            entries = list.Filter(ErrorLogList.Level) switch
            {
                ErrorLogList.Warning => entries.Where(log => log.Level == ErrorLogLevel.Warning),
                ErrorLogList.Error => entries.Where(log => log.Level == ErrorLogLevel.Error),
                ErrorLogList.Critical => entries.Where(log => log.Level == ErrorLogLevel.Critical),
                _ => entries,
            };

            if (list.Search is { Length: > 0 } search)
            {
                entries = entries.Where(log => log.Message.Contains(search)
                    || log.Category.Contains(search)
                    || (log.RequestPath != null && log.RequestPath.Contains(search))
                    || (log.ExceptionType != null && log.ExceptionType.Contains(search))
                    || (log.CorrelationId != null && log.CorrelationId.Contains(search))
                    || context.Tenants.Any(tenant => tenant.Id == log.TenantId && tenant.Name.Contains(search)));
            }

            var total = await entries.CountAsync(cancellationToken);
            var page = ListPage<ErrorLogEntryDto>.PageWithin(list, total);
            var rows = await new ListOrder<ErrorLog>(entries).By(log => log.Occurred, list.Descending).By(log => log.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(log => new ErrorLogEntryDto
                {
                    Id = log.Id,
                    Occurred = log.Occurred,
                    Level = log.Level,
                    Category = log.Category,
                    EventId = log.EventId,
                    EventName = log.EventName,
                    Message = log.Message,
                    ExceptionType = log.ExceptionType,
                    CorrelationId = log.CorrelationId,
                    RequestPath = log.RequestPath,
                    TenantId = log.TenantId,
                    TenantName = context.Tenants.Where(tenant => tenant.Id == log.TenantId).Select(tenant => tenant.Name).FirstOrDefault(),
                    UserId = log.UserId,
                    UserName = context.Users.Where(user => user.Id == log.UserId).Select(user => user.UserName).FirstOrDefault(),
                    ImpersonatorId = log.ImpersonatorId,
                    ImpersonatorName = context.Users.Where(user => user.Id == log.ImpersonatorId).Select(user => user.UserName).FirstOrDefault(),
                })
                .ToListAsync(cancellationToken);

            if (list.Group != ErrorLogList.ByDay)
            {
                return Result<ListPage<ErrorLogEntryDto>>.Success(ListPage<ErrorLogEntryDto>.Ungrouped(rows, total, page, list.PageSize));
            }

            var days = await entries.GroupBy(log => log.Occurred.Date).Select(day => new { day.Key, Count = day.Count() }).ToListAsync(cancellationToken);
            var groups = days.ToDictionary(day => DayKey(day.Key), day => (DayLabel(day.Key, now), day.Count));
            return Result<ListPage<ErrorLogEntryDto>>.Success(ListPage<ErrorLogEntryDto>.Grouped(rows, total, page, list.PageSize, row => DayKey(row.Occurred.Date), groups));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            this.logger.LogError(exception, "Could not read the error log.");
            return Result<ListPage<ErrorLogEntryDto>>.Failure("The error log could not be read.");
        }
    }

    private static string DayKey(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string DayLabel(DateTime day, DateTime now)
    {
        var date = day.ToString("dddd d MMMM", CultureInfo.InvariantCulture);
        return day == now.Date ? $"Today · {date}" : day == now.Date.AddDays(-1) ? $"Yesterday · {date}" : date;
    }

    public async Task<Result<string?>> GetExceptionDetailAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!this.runtime.HasPlatformReach())
        {
            return Result<string?>.Failure(ErrorLogNotPlatformOwner);
        }

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "read one error's exception text");
            if (lease is null)
            {
                return Result<string?>.Failure(ErrorLogNotPlatformOwner);
            }

            var row = await context.ErrorLogs.AsNoTracking()
                .Where(log => log.Id == id)
                .Select(log => new { log.ExceptionDetail })
                .FirstOrDefaultAsync(cancellationToken);
            return row is null ? Result<string?>.Failure("The error is no longer in the error log.") : Result<string?>.Success(row.ExceptionDetail);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            this.logger.LogError(exception, "Could not read an error's exception text.");
            return Result<string?>.Failure("The exception could not be read.");
        }
    }

    public Result<long> DroppedSinceStart() =>
        this.runtime.HasPlatformReach() ? Result<long>.Success(this.queue.Dropped) : Result<long>.Failure(ErrorLogNotPlatformOwner);

    public Result<RetentionPass?> LastRetentionPass() =>
        this.runtime.HasPlatformReach() ? Result<RetentionPass?>.Success(this.retention.LastPass) : Result<RetentionPass?>.Failure(ErrorLogNotPlatformOwner);
}
