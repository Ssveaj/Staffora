using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Comments;

namespace Staffora.Data;

public static class CommentThreadWriter
{
    public static async Task<Comment> AddAsync(ApplicationDbContext context, ICommentedEntity entity, CommentAudience audience, CommentKind kind, CommentAuthorType authorType, string text, string? transitionKey, CancellationToken cancellationToken, CommentCrossing? crossing = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var entityType = EntityTypeOf(context, entity.GetType());
        var (tenantId, entityId) = (entity.TenantId, entity.Id);
        await LockAsync(context, tenantId, entityType, entityId, cancellationToken);

        var container = context.CommentContainers.Local.FirstOrDefault(row => row.TenantId == tenantId && row.EntityType == entityType && row.EntityId == entityId && row.Audience == audience)
            ?? await context.CommentContainers.FirstOrDefaultAsync(row => row.TenantId == tenantId && row.EntityType == entityType && row.EntityId == entityId && row.Audience == audience, cancellationToken);
        if (container is null)
        {
            container = new CommentContainer { TenantId = tenantId, EntityType = entityType, EntityId = entityId, Audience = audience };
            context.CommentContainers.Add(container);
        }

        var comment = new Comment
        {
            TenantId = tenantId,
            CommentContainer = container,
            AuthorType = authorType,
            Kind = kind,
            TransitionKey = transitionKey,
            Text = text.Trim(),
            CrossTenantId = crossing?.CrossTenantId,
            LinkedCrossTenantId = crossing?.LinkedCrossTenantId,
            AuthorContactId = crossing?.AuthorContactId,
            AuthorSupplierId = crossing?.AuthorSupplierId,
        };
        context.Comments.Add(comment);
        return comment;
    }

    public static Task LockAsync(ApplicationDbContext context, Guid tenantId, string entityType, Guid entityId, CancellationToken cancellationToken) =>
        AppLock.CommentThread.TakeAsync(context, tenantId, ThreadOf(entityType, entityId), cancellationToken);

    public static string ThreadOf(string entityType, Guid entityId) => $"{entityType}:{entityId:N}";

    public static string EntityTypeOf(ApplicationDbContext context, Type clrType) =>
        (context.Model.FindRuntimeEntityType(clrType)?.ClrType ?? clrType).Name;
}

public sealed record CommentCrossing(Guid? CrossTenantId, Guid? LinkedCrossTenantId = null, Guid? AuthorContactId = null, Guid? AuthorSupplierId = null);
