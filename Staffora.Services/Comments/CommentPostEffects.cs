using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Messaging;
using Staffora.Data;
using Staffora.Services.Coordination;

namespace Staffora.Services.Comments;

public sealed record CommentPostEffects(
    Func<ApplicationDbContext, DateTime, CancellationToken, Task<bool>>? Claim,
    Func<ApplicationDbContext, Comment, CancellationToken, Task<CommentPostOutcome>> After)
{
    public bool Crosses { get; init; }
}

public sealed record CommentPostOutcome(IReadOnlyList<OutgoingMessage> Notices, TenantChange? Change)
{
    public static CommentPostOutcome None { get; } = new([], null);
}
