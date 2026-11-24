using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Execution;
using Staffora.Core.Messaging;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Settings;

namespace Staffora.Services.Messaging;

public sealed class NoticeService
{
    public const int MaxNotices = 50;

    public const string ImpersonatingRefusal = "Notices stay unread for the person you are signed in as. They mark them read themselves.";

    private const string NoReader = "Only a signed-in user of an organization has notices.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly NoticeBus bus;
    private readonly TenantClock tenantClock;
    private readonly TimeProvider clock;
    private readonly ILogger<NoticeService> log;

    public NoticeService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, NoticeBus bus, TenantClock tenantClock, TimeProvider clock, ILogger<NoticeService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.bus = bus;
        this.tenantClock = tenantClock;
        this.clock = clock;
        this.log = log;
    }

    public bool MayMarkRead => this.runtime.Current is { EffectiveUserId: not null, TenantId: not null, IsImpersonating: false } current && !current.HasPlatformReach();

    public bool HasNotices => Reader() is not null;

    public IDisposable? Subscribe(Func<NoticeArrived, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Reader() is { } reader ? this.bus.Subscribe(reader.TenantId, reader.UserId, handler) : null;
    }

    public async Task<Result<int>> GetUnreadCountAsync(CancellationToken cancellationToken = default)
    {
        if (Reader() is not { } reader)
        {
            return Result<int>.Failure(NoReader);
        }

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var count = await context.MessageRecipients.AsNoTracking()
                .CountAsync(copy => copy.TenantId == reader.TenantId && copy.UserId == reader.UserId && !copy.IsRead, cancellationToken);
            return Result<int>.Success(count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Counting the unread notices of user {UserId} failed.", reader.UserId);
            return Result<int>.Failure("Your notices could not be read. Try again.");
        }
    }

    public async Task<Result<NoticePage>> GetNoticesAsync(NoticeFilter filter, CancellationToken cancellationToken = default)
    {
        if (Reader() is not { } reader)
        {
            return Result<NoticePage>.Failure(NoReader);
        }

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var mine = context.MessageRecipients.AsNoTracking().Where(copy => copy.TenantId == reader.TenantId && copy.UserId == reader.UserId);
            var unread = await mine.CountAsync(copy => !copy.IsRead, cancellationToken);
            var shown = filter == NoticeFilter.Unread ? mine.Where(copy => !copy.IsRead) : mine;
            var rows = await shown
                .OrderByDescending(copy => copy.Created)
                .ThenByDescending(copy => copy.Id)
                .Take(MaxNotices)
                .Select(copy => new
                {
                    copy.Id,
                    copy.IsRead,
                    copy.Created,
                    copy.StoredMessage!.Subject,
                    copy.StoredMessage.Text,
                    copy.StoredMessage.Link,
                    copy.StoredMessage.Category,
                })
                .ToListAsync(cancellationToken);

            var zone = await this.tenantClock.GetTimeZoneAsync(cancellationToken);
            if (!zone.IsSuccess || zone.Value is null)
            {
                return Result<NoticePage>.Failure(zone.Error ?? "Your organization's time zone could not be read.");
            }

            var notices = rows.Select(row => new Notice(row.Id, row.Subject, row.Text, row.Link, row.Category, TenantClock.ToLocal(row.Created, zone.Value), row.IsRead)).ToList();
            return Result<NoticePage>.Success(new NoticePage(notices, unread, zone.Value.Id));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the notices of user {UserId} failed.", reader.UserId);
            return Result<NoticePage>.Failure("Your notices could not be read. Try again.");
        }
    }

    public Task<Result> MarkReadAsync(Guid noticeId, CancellationToken cancellationToken = default) =>
        MarkAsync(noticeId, cancellationToken);

    public Task<Result> MarkAllReadAsync(CancellationToken cancellationToken = default) =>
        MarkAsync(noticeId: null, cancellationToken);

    private async Task<Result> MarkAsync(Guid? noticeId, CancellationToken cancellationToken)
    {
        var session = this.runtime.Current;
        if (session is not { EffectiveUserId: { } userId, TenantId: { } tenantId } || session.HasPlatformReach())
        {
            return Result.Failure(NoReader);
        }

        if (session.IsImpersonating)
        {
            return Result.Failure(ImpersonatingRefusal);
        }

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var unread = context.MessageRecipients.Where(copy => copy.TenantId == tenantId && copy.UserId == userId && !copy.IsRead);
            if (noticeId is { } id)
            {
                unread = unread.Where(copy => copy.Id == id);
            }

            var now = this.clock.GetUtcNow().UtcDateTime;
            await unread.ExecuteUpdateAsync(
                set => set
                    .SetProperty(copy => copy.IsRead, true)
                    .SetProperty(copy => copy.ReadAt, now)
                    .SetProperty(copy => copy.Modified, now)
                    .SetProperty(copy => copy.ModifiedById, userId),
                cancellationToken);
            return Result.Success();
        }
        catch (TenantHeldException held)
        {
            return Result.Failure(held.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Marking notices of user {UserId} read failed.", userId);
            return Result.Failure("Your notices could not be marked read. Try again.");
        }
    }

    private (Guid TenantId, Guid UserId)? Reader() =>
        this.runtime.Current is { TenantId: { } tenantId, EffectiveUserId: { } userId } current && !current.HasPlatformReach() ? (tenantId, userId) : null;
}

public enum NoticeFilter
{
    Unread = 0,
    All = 1,
}

public sealed record Notice(Guid Id, string Subject, string Text, string? Link, MessageCategory Category, DateTimeOffset At, bool IsRead);

public sealed record NoticePage(IReadOnlyList<Notice> Notices, int UnreadCount, string TimeZoneId);
