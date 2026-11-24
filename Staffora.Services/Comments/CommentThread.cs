using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Services.Comments;

public sealed record CommentThreadAccess(CommentAudience Audience, RolePermission ReadPermission, RolePermission PostPermission)
{
    public CommentPostEffects? Effects { get; init; }

    public string? ClosedReason { get; init; }

    public bool ShowsClosedReason { get; init; } = true;
}

public sealed record CommentThread(CommentAudience Audience, bool CanPost, int TotalCount, IReadOnlyList<CommentEntry> Comments, string TimeZoneId)
{
    public string? ClosedReason { get; init; }

    public bool AwaitsReply => Comments.Count > 0 && Comments[^1] is { IsOwn: false, Kind: CommentKind.Comment, AuthorType: CommentAuthorType.User or CommentAuthorType.Contact or CommentAuthorType.Supplier };

    public string? Title { get; init; }

    public string? Readers { get; init; }

    public CommentTurn? Turn { get; init; }

    public bool RequiresAttention => Turn?.RequiresAttention ?? AwaitsReply;

    public string AttentionText => Turn?.AttentionText ?? CommentTurn.AwaitsYourReply;

    public Guid? FirstMarkedCommentId => Turn is { RequiresAttention: true } turn
        ? Comments.FirstOrDefault(comment => !comment.IsOwn && comment.Kind == CommentKind.Comment && (turn.MarkAfter is not { } after || comment.At.UtcDateTime > after))?.Id
        : null;

    public CommentThread JudgedBySide()
    {
        var awaits = Comments.Count > 0 && Comments[^1] is { Kind: CommentKind.Comment, AuthorType: CommentAuthorType.Contact or CommentAuthorType.Supplier };
        var ownSideLast = Comments.LastOrDefault(comment => comment.Kind == CommentKind.Comment && comment.AuthorType == CommentAuthorType.User);
        return this with { Turn = awaits ? new CommentTurn(true, CommentTurn.AwaitsYourReply, "Unanswered", ownSideLast?.At.UtcDateTime) : CommentTurn.Settled };
    }
}

public sealed record CommentTurn(bool RequiresAttention, string AttentionText, string MarkerLabel, DateTime? MarkAfter)
{
    public const string AwaitsYourReply = "Awaits your reply";

    public const string NewReply = "New reply";

    public static CommentTurn Settled { get; } = new(false, AwaitsYourReply, string.Empty, null);
}

public sealed record CommentEntry(Guid Id, CommentKind Kind, CommentAuthorType AuthorType, string Author, bool IsOwn, DateTimeOffset At, string Text);
