using Staffora.Core.Jobs;
using Staffora.Core.Models;

namespace Staffora.Test.Unit.Staffing;

public class StaffConversationRulesTests
{
    private static readonly DateTime At = new(2027, 3, 10, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_thread_awaits_the_coordinators_only_while_the_staff_member_wrote_last()
    {
        Assert.False(StaffConversation.AwaitsCoordinatorReply(null, null));
        Assert.False(StaffConversation.AwaitsCoordinatorReply(null, At));
        Assert.True(StaffConversation.AwaitsCoordinatorReply(At, null));
        Assert.True(StaffConversation.AwaitsCoordinatorReply(At.AddMinutes(1), At));
        Assert.False(StaffConversation.AwaitsCoordinatorReply(At, At.AddMilliseconds(1)));
        Assert.False(StaffConversation.AwaitsCoordinatorReply(At, At));
    }

    [Fact]
    public void A_reply_is_unseen_until_the_staff_member_reads_the_thread_after_it()
    {
        Assert.False(StaffConversation.HasUnseenReply(null, null));
        Assert.False(StaffConversation.HasUnseenReply(null, At));
        Assert.True(StaffConversation.HasUnseenReply(At, null));
        Assert.True(StaffConversation.HasUnseenReply(At.AddSeconds(1), At));
        Assert.False(StaffConversation.HasUnseenReply(At, At));
        Assert.False(StaffConversation.HasUnseenReply(At, At.AddSeconds(1)));
    }

    [Fact]
    public void The_newest_comment_is_cut_at_a_word_on_one_line()
    {
        Assert.Equal("Short and sweet.", StaffConversationRow.Excerpt("  Short and\nsweet.  "));
        var cut = StaffConversationRow.Excerpt(string.Join(' ', Enumerable.Repeat("word", 60)));
        Assert.True(cut.Length <= StaffConversationRow.ExcerptLength);
        Assert.EndsWith("word…", cut, StringComparison.Ordinal);
    }
}
