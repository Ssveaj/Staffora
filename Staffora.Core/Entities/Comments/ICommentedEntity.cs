namespace Staffora.Core.Entities.Comments;

public interface ICommentedEntity : ITenantEntity
{
    Guid Id { get; }
}
