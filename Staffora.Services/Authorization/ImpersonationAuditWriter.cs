using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Data;

namespace Staffora.Services.Authorization;

public sealed class ImpersonationAuditWriter
{
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly TimeProvider clock;
    private readonly ILogger<ImpersonationAuditWriter> logger;

    public ImpersonationAuditWriter(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider clock, ILogger<ImpersonationAuditWriter> logger)
    {
        this.dbFactory = dbFactory;
        this.clock = clock;
        this.logger = logger;
    }

    public async Task<Result<Guid>> RecordStartAsync(Guid impersonatorId, ImpersonationTarget target, Guid? endingSessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            using var context = this.dbFactory.CreateSystemDbContext();
            using var lease = context.BeginSystemScope("impersonation audit: record the start of a platform owner's session");
            var now = this.clock.GetUtcNow().UtcDateTime;
            if (endingSessionId is { } previousId && await OpenRowAsync(context, impersonatorId, previousId, cancellationToken) is { } previous)
            {
                previous.Ended = now;
            }

            var started = new ImpersonationAudit
            {
                ImpersonatorId = impersonatorId,
                TargetUserId = target.UserId,
                TargetTenantId = target.TenantId,
                Started = now,
            };
            context.ImpersonationAudits.Add(started);
            await context.SaveChangesAsync(cancellationToken);
            return Result<Guid>.Success(started.Id);
        }
        catch (Exception exception)
        {
            this.logger.LogError(exception, "Could not record the start of impersonation of user {TargetUserId} by user {ImpersonatorId}.", target.UserId, impersonatorId);
            return Result<Guid>.Failure("The impersonation could not be recorded, so it was not started.");
        }
    }

    public async Task<Result> RecordEndAsync(Guid impersonatorId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var context = this.dbFactory.CreateSystemDbContext();
            using var lease = context.BeginSystemScope("impersonation audit: record the end of a platform owner's session");
            if (await OpenRowAsync(context, impersonatorId, sessionId, cancellationToken) is not { } open)
            {
                return Result.Success();
            }

            open.Ended = this.clock.GetUtcNow().UtcDateTime;
            await context.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (Exception exception)
        {
            this.logger.LogError(exception, "Could not record the end of impersonation session {SessionId} by user {ImpersonatorId}.", sessionId, impersonatorId);
            return Result.Failure("The end of the impersonation could not be recorded.");
        }
    }

    public async Task<bool> IsOpenAsync(Guid impersonatorId, Guid targetUserId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("impersonation audit: check that a cookie's session row is open before its impersonation is applied");
        return await context.ImpersonationAudits.AsNoTracking()
            .AnyAsync(audit => audit.Id == sessionId && audit.ImpersonatorId == impersonatorId && audit.TargetUserId == targetUserId && audit.Ended == null, cancellationToken);
    }

    private static Task<ImpersonationAudit?> OpenRowAsync(ApplicationDbContext context, Guid impersonatorId, Guid sessionId, CancellationToken cancellationToken) =>
        context.ImpersonationAudits
            .Where(audit => audit.Id == sessionId && audit.ImpersonatorId == impersonatorId && audit.Ended == null)
            .FirstOrDefaultAsync(cancellationToken);
}
