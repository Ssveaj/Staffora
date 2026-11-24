using Staffora.Core.Entities.Comments;

namespace Staffora.Core.Workflow;

public sealed record TransitionCommentPrompt(string Title)
{
    public string? Message { get; init; }

    public string? Placeholder { get; init; }

    public CommentAudience Audience { get; init; } = CommentAudience.Internal;

    public bool Required { get; init; } = true;

    public string RequiredMessage { get; init; } = "Write a reason first.";
}
