using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Comments;

public sealed class CommentThreadsBlockDelete : IDeleteBlocker
{
    public const string Reason = "It has comments, which are kept for the record, so it cannot be deleted.";

    public Type EntityType => typeof(ICommentedEntity);

    public IReadOnlyList<Type> Dependents => [];

    public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        if (entity is not ICommentedEntity commented)
        {
            return null;
        }

        var (tenantId, entityId) = (commented.TenantId, commented.Id);
        var entityType = CommentThreadWriter.EntityTypeOf(context, entity.GetType());

        if (context.Database.CurrentTransaction is not null)
        {
            await CommentThreadWriter.LockAsync(context, tenantId, entityType, entityId, cancellationToken);
        }

        var written = await context.Comments.AnyAsync(comment => comment.TenantId == tenantId && comment.CommentContainer!.EntityType == entityType && comment.CommentContainer.EntityId == entityId, cancellationToken);
        return written ? Reason : null;
    }
}
