using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Coordination;
using Staffora.Services.Messaging;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;

namespace Staffora.Services.Comments;

public sealed class CommentService
{
    public const int MaxThreadComments = 200;

    public const string NoAccess = "You do not have permission to read or write these comments.";

    public const string OutsideAuthor = "Someone outside this organization";

    public const string SystemAuthor = "System";

    public const string Busy = "Someone else was writing a comment here at the same moment, so nothing was saved. Try again.";

    public const string Posted = "Comment posted.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly TenantClock tenantClock;
    private readonly TimeProvider clock;
    private readonly IOutgoingMessageDispatcher dispatcher;
    private readonly TenantChangeBus changes;
    private readonly ILogger<CommentService> log;

    public CommentService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, TenantClock tenantClock, TimeProvider clock, IOutgoingMessageDispatcher dispatcher, TenantChangeBus changes, ILogger<CommentService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.tenantClock = tenantClock;
        this.clock = clock;
        this.dispatcher = dispatcher;
        this.changes = changes;
        this.log = log;
    }

    public async Task<Result<IReadOnlyList<CommentThread>>> GetThreadsAsync<TEntity>(Guid entityId, IReadOnlyList<CommentThreadAccess> audiences, CancellationToken cancellationToken = default)
        where TEntity : class, ICommentedEntity
    {
        ArgumentNullException.ThrowIfNull(audiences);
        if (this.runtime.EffectiveUserId is null)
        {
            return Result<IReadOnlyList<CommentThread>>.Failure(NoAccess);
        }

        try
        {
            var readable = new List<(CommentThreadAccess Access, bool CanPost)>();
            foreach (var access in audiences.DistinctBy(access => access.Audience))
            {
                if (await this.permissionService.HasPermissionAsync(access.ReadPermission, cancellationToken))
                {
                    readable.Add((access, access.ClosedReason is null && await this.permissionService.HasPermissionAsync(access.PostPermission, cancellationToken)));
                }
            }

            if (readable.Count == 0)
            {
                return Result<IReadOnlyList<CommentThread>>.Success([]);
            }

            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            if (await TenantOfAsync<TEntity>(context, entityId, cancellationToken) is not { } tenantId)
            {
                return Result<IReadOnlyList<CommentThread>>.Failure(NotFound<TEntity>());
            }

            var zone = await this.tenantClock.GetTimeZoneAsync(cancellationToken);
            if (!zone.IsSuccess || zone.Value is null)
            {
                return Result<IReadOnlyList<CommentThread>>.Failure(zone.Error ?? "The organization's time zone could not be read.");
            }

            var threads = new List<CommentThread>();
            foreach (var (access, canPost) in readable)
            {
                var read = await ReadThreadAsync<TEntity>(context, tenantId, entityId, access.Audience, canPost, zone.Value, cancellationToken);
                threads.Add(read with { ClosedReason = access.ShowsClosedReason ? access.ClosedReason : null });
            }

            return Result<IReadOnlyList<CommentThread>>.Success(threads);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the comments of {EntityType} {EntityId} failed.", typeof(TEntity).Name, entityId);
            return Result<IReadOnlyList<CommentThread>>.Failure("The comments could not be read. Try again.");
        }
    }

    public async Task<Result<CommentThread>> AddCommentAsync<TEntity>(Guid entityId, CommentThreadAccess access, string? text, CancellationToken cancellationToken = default)
        where TEntity : class, ICommentedEntity
    {
        ArgumentNullException.ThrowIfNull(access);
        if (this.runtime.EffectiveUserId is null
            || !await this.permissionService.HasPermissionAsync(access.ReadPermission, cancellationToken)
            || !await this.permissionService.HasPermissionAsync(access.PostPermission, cancellationToken))
        {
            return Result<CommentThread>.Failure(NoAccess);
        }

        if (access.ClosedReason is { } closed)
        {
            return Result<CommentThread>.Failure(closed);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Result<CommentThread>.Failure("Write a comment first.");
        }

        if (text.Trim().Length > Comment.MaxTextLength)
        {
            return Result<CommentThread>.Failure($"A comment holds at most {Comment.MaxTextLength} characters.");
        }

        try
        {
            return await WriteCommentAsync<TEntity>(entityId, access.Audience, access.Effects, text, cancellationToken);
        }
        catch (AppLockBusyException exception) when (exception.Lock == AppLock.CommentThread)
        {
            this.log.LogWarning(exception, "Posting a comment on {EntityType} {EntityId} waited too long for its thread lock.", typeof(TEntity).Name, entityId);
            return Result<CommentThread>.Failure(Busy);
        }
        catch (TenantHeldException held)
        {
            return Result<CommentThread>.Failure(held.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Posting a comment on {EntityType} {EntityId} failed.", typeof(TEntity).Name, entityId);
            return Result<CommentThread>.Failure("The comment could not be posted, so nothing was saved. Try again.");
        }
    }

    private async Task<Result<CommentThread>> WriteCommentAsync<TEntity>(Guid entityId, CommentAudience audience, CommentPostEffects? effects, string text, CancellationToken cancellationToken)
        where TEntity : class, ICommentedEntity
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        TEntity? entity;
        IReadOnlyList<OutgoingMessage> notices = [];
        Guid commentId;
        TenantChange? change = null;
        await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken))
        {
            if (effects?.Claim is { } claim && !await claim(context, this.clock.GetUtcNow().UtcDateTime, cancellationToken))
            {
                return Result<CommentThread>.Failure(NotFound<TEntity>());
            }

            entity = await context.Set<TEntity>().AsNoTracking().FirstOrDefaultAsync(row => EF.Property<Guid>(row, nameof(ICommentedEntity.Id)) == entityId, cancellationToken);
            if (entity is null)
            {
                return Result<CommentThread>.Failure(NotFound<TEntity>());
            }

            var crossing = effects is { Crosses: true } ? new CommentCrossing(Guid.NewGuid()) : null;
            var comment = await CommentThreadWriter.AddAsync(context, entity, audience, CommentKind.Comment, CommentAuthorType.User, text, transitionKey: null, cancellationToken, crossing);
            commentId = comment.Id;
            if (effects is not null)
            {
                var outcome = await effects.After(context, comment, cancellationToken);
                notices = OutgoingMessageWriter.AddForComment(context, outcome.Notices, commentId);
                change = outcome.Change;
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
        }

        if (notices.Count > 0)
        {
            await this.dispatcher.DispatchAsync(notices, commentId, CancellationToken.None);
        }

        if (change is not null)
        {
            try
            {
                this.changes.Publish(change);
            }
            catch (Exception exception)
            {
                this.log.LogError(exception, "Publishing the change of a comment on {EntityType} {EntityId} failed; the comment stands.", typeof(TEntity).Name, entityId);
            }
        }

        context.ChangeTracker.Clear();
        var zone = await this.tenantClock.GetTimeZoneAsync(CancellationToken.None);
        if (!zone.IsSuccess || zone.Value is null)
        {
            return Result<CommentThread>.Failure(zone.Error ?? "The comment was posted, but the organization's time zone could not be read. Reload to see it.");
        }

        var thread = await ReadThreadAsync<TEntity>(context, entity.TenantId, entityId, audience, canPost: true, zone.Value, CancellationToken.None);
        return Result<CommentThread>.Success(thread, Posted);
    }

    private static Task<Guid?> TenantOfAsync<TEntity>(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
        where TEntity : class, ICommentedEntity =>
        context.Set<TEntity>().AsNoTracking()
            .Where(row => EF.Property<Guid>(row, nameof(ICommentedEntity.Id)) == entityId)
            .Select(row => (Guid?)row.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<CommentThread> ReadThreadAsync<TEntity>(ApplicationDbContext context, Guid tenantId, Guid entityId, CommentAudience audience, bool canPost, TimeZoneInfo zone, CancellationToken cancellationToken)
        where TEntity : class, ICommentedEntity
    {
        var entityType = CommentThreadWriter.EntityTypeOf(context, typeof(TEntity));
        var comments = context.Comments.AsNoTracking()
            .Where(comment => comment.TenantId == tenantId
                && comment.CommentContainer!.EntityType == entityType
                && comment.CommentContainer.EntityId == entityId
                && comment.CommentContainer.Audience == audience);
        var total = await comments.CountAsync(cancellationToken);
        var newest = await comments
            .OrderByDescending(comment => comment.Created)
            .ThenByDescending(comment => comment.Id)
            .Take(MaxThreadComments)
            .Select(comment => new { comment.Id, comment.Kind, comment.AuthorType, comment.CreatedById, comment.Created, comment.Text, comment.TransitionKey, comment.AuthorContactId, comment.AuthorSupplierId })
            .ToListAsync(cancellationToken);
        newest.Reverse();

        var contactIds = newest.Select(comment => comment.AuthorContactId).OfType<Guid>().Distinct().ToList();
        var contacts = contactIds.Count == 0
            ? []
            : await context.CustomerContacts.AsNoTracking()
                .Where(contact => contactIds.Contains(contact.Id) && contact.TenantId == tenantId)
                .Select(contact => new { contact.Id, contact.FirstName, contact.LastName, Customer = contact.Customer.Name })
                .ToDictionaryAsync(contact => contact.Id, contact => $"{NameOf(contact.FirstName, contact.LastName)}, {contact.Customer}", cancellationToken);
        var supplierIds = newest.Select(comment => comment.AuthorSupplierId).OfType<Guid>().Distinct().ToList();
        var suppliers = supplierIds.Count == 0
            ? []
            : await context.Suppliers.AsNoTracking()
                .Where(supplier => supplierIds.Contains(supplier.Id) && supplier.TenantId == tenantId)
                .ToDictionaryAsync(supplier => supplier.Id, supplier => supplier.Name, cancellationToken);

        var ids = newest.Select(comment => comment.CreatedById).Distinct().ToList();
        var people = ids.Count == 0
            ? []
            : await context.Users.AsNoTracking()
                .Where(user => ids.Contains(user.Id) && user.TenantId == tenantId)
                .Select(user => new { user.Id, user.FirstName, user.LastName, user.IsSystemAccount })
                .ToDictionaryAsync(user => user.Id, user => user.IsSystemAccount ? StateHistoryService.SystemAccountName : NameOf(user.FirstName, user.LastName), cancellationToken);

        var reader = this.runtime.EffectiveUserId;
        var entries = newest.Select(comment => new CommentEntry(
            comment.Id,
            comment.Kind,
            comment.AuthorType,
            comment.AuthorType switch
            {
                CommentAuthorType.System => SystemAuthor,
                CommentAuthorType.Contact => comment.AuthorContactId is { } contact && contacts.TryGetValue(contact, out var named) ? named : OutsideAuthor,
                CommentAuthorType.Supplier => comment.AuthorSupplierId is { } supplier && suppliers.TryGetValue(supplier, out var company) ? company : OutsideAuthor,
                _ => people.TryGetValue(comment.CreatedById, out var name) ? name : OutsideAuthor,
            },
            comment.AuthorType == CommentAuthorType.User && comment.CreatedById == reader,
            TenantClock.ToLocal(comment.Created, zone),
            comment.Text)).ToList();
        return new CommentThread(audience, canPost, total, entries, zone.Id);
    }

    private static string NotFound<TEntity>() => $"The {typeof(TEntity).Name} was not found.";

    private static string NameOf(string firstName, string lastName) =>
        $"{firstName} {lastName}".Trim() is { Length: > 0 } name ? name : "A user of this organization";
}
