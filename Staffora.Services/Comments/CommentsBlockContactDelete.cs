using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Comments;

public sealed class CommentsBlockContactDelete : IDeleteBlocker
{
    public Type EntityType => typeof(CustomerContact);

    public IReadOnlyList<Type> Dependents { get; } = [typeof(Comment)];

    public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        var contactId = entity.Id;
        return await context.Comments.AsNoTracking().AnyAsync(comment => comment.AuthorContactId == contactId, cancellationToken) ? Reason : null;
    }

    public const string Reason = "This contact wrote in a job's customer thread, which is kept: keep the contact inactive instead.";
}
