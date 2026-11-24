namespace Staffora.Core.Entities.Comments;

public class Comment : TenantEntityBase
{
    public const int MaxTextLength = 2000;

    public Guid CommentContainerId { get; set; }

    public CommentContainer? CommentContainer { get; set; }

    public CommentAuthorType AuthorType { get; set; }

    public CommentKind Kind { get; set; }

    public string? TransitionKey { get; set; }

    public string Text { get; set; } = string.Empty;

    public Guid? CrossTenantId { get; set; }

    public Guid? LinkedCrossTenantId { get; set; }

    public Guid? AuthorContactId { get; set; }

    public Guid? AuthorSupplierId { get; set; }
}
