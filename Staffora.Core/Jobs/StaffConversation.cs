namespace Staffora.Core.Jobs;

public static class StaffConversation
{
    public static bool AwaitsCoordinatorReply(DateTime? lastStaffCommentAt, DateTime? lastCoordinatorCommentAt) =>
        lastStaffCommentAt is { } staff && (lastCoordinatorCommentAt is not { } coordinator || staff > coordinator);

    public static bool HasUnseenReply(DateTime? lastCoordinatorCommentAt, DateTime? staffSeenAt) =>
        lastCoordinatorCommentAt is { } coordinator && (staffSeenAt is not { } seen || coordinator > seen);
}
