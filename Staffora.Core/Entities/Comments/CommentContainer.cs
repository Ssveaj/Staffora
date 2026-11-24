namespace Staffora.Core.Entities.Comments;

public class CommentContainer : TenantEntityBase
{
    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public CommentAudience Audience { get; set; }

    public ICollection<Comment> Comments { get; set; } = [];
}
