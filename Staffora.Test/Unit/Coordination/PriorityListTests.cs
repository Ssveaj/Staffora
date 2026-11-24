using Staffora.Services.Coordination;

namespace Staffora.Test.Unit.Coordination;

public class PriorityListTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_mixed_set_runs_from_the_most_urgent_to_the_least_the_overdue_first_by_kind_then_by_when_each_is_due()
    {
        var items = new[]
        {
            Item(PriorityItemKind.JobsBehind, "behind", Now),
            Item(PriorityItemKind.StaffQuestions, "old-question", PriorityList.DueUtc(null, Now.AddDays(-2))),
            Item(PriorityItemKind.OrdersToAnswer, "started-order", PriorityList.DueUtc(Now.AddHours(-1), Now.AddHours(-3))),

            Item(PriorityItemKind.ToRequestOrAssign, "uncovered-today", Now.AddHours(4)),
            Item(PriorityItemKind.CustomerMessages, "message", PriorityList.DueUtc(null, Now.AddHours(-1))),
            Item(PriorityItemKind.AbsenceImpact, "sick", Now.AddHours(25)),
            Item(PriorityItemKind.AtRisk, "risk", Now.AddHours(25)),

            Item(PriorityItemKind.WaitingForPick, "pick", Now.AddDays(3)),
            Item(PriorityItemKind.AbsenceToApprove, "leave", Now.AddDays(2)),
        };

        var ordered = PriorityList.Order(items, Now).Select(item => item.Key);

        Assert.Equal(["started-order", "old-question", "behind", "uncovered-today", "message", "risk", "sick", "leave", "pick"], ordered);
        Assert.Equal(ordered, PriorityList.Order(Enumerable.Reverse(items), Now).Select(item => item.Key));
    }

    [Fact]
    public void At_the_same_time_the_kinds_fixed_order_decides_and_then_the_key()
    {
        var kinds = Enum.GetValues<PriorityItemKind>();
        var items = Enumerable.Reverse(kinds).Select(kind => Item(kind, kind.ToString(), Now.AddHours(2))).ToList();
        items.Add(Item(PriorityItemKind.AtRisk, "AtRisk-2", Now.AddHours(2)));

        Assert.Equal(
            [
                nameof(PriorityItemKind.AtRisk),
                "AtRisk-2",
                nameof(PriorityItemKind.AbsenceImpact),
                nameof(PriorityItemKind.ToRequestOrAssign),
                nameof(PriorityItemKind.OrdersToAnswer),
                nameof(PriorityItemKind.UnansweredRequests),
                nameof(PriorityItemKind.StaffQuestions),
                nameof(PriorityItemKind.CustomerMessages),
                nameof(PriorityItemKind.WaitingForPick),
                nameof(PriorityItemKind.AbsenceToApprove),
                nameof(PriorityItemKind.JobsBehind),
            ],
            PriorityList.Order(items, Now).Select(item => item.Key));
    }

    [Fact]
    public void An_item_is_due_when_it_starts_or_a_day_after_its_reply_was_asked_for_the_earlier_when_it_has_both()
    {
        var start = Now.AddHours(30);
        var asked = Now.AddHours(-2);

        Assert.Equal(start, PriorityList.DueUtc(start, null));
        Assert.Equal(asked.AddDays(1), PriorityList.DueUtc(null, asked));
        Assert.Equal(asked.AddDays(1), PriorityList.DueUtc(start, asked));
        Assert.Equal(Now.AddHours(3), PriorityList.DueUtc(Now.AddHours(3), asked));
        Assert.Throws<ArgumentException>(() => PriorityList.DueUtc(null, null));
    }

    [Fact]
    public void Every_kind_has_a_short_reason_pill_and_a_tab()
    {
        Assert.Equal(
            ["Absent", "Order", "At risk", "Uncovered", "Unanswered", "Waiting for a pick", "Question", "Message", "Absence", "Behind"],
            Enum.GetValues<PriorityItemKind>().Select(PriorityList.Label));
        Assert.Equal(
            [PriorityTab.Absence, PriorityTab.Orders, PriorityTab.Slots, PriorityTab.Slots, PriorityTab.Slots, PriorityTab.Slots, PriorityTab.Questions, PriorityTab.Questions, PriorityTab.Absence, PriorityTab.Orders],
            Enum.GetValues<PriorityItemKind>().Select(PriorityList.TabOf));
        Assert.Equal(Enum.GetValues<PriorityItemKind>().Length, Enum.GetValues<PriorityItemKind>().Select(PriorityList.Rank).Distinct().Count());
    }

    [Fact]
    public void A_query_shows_a_page_at_first_and_never_more_than_the_most_the_dock_shows()
    {
        Assert.Equal(PriorityList.PageSize, new PriorityQuery().Shown);
        Assert.Equal(PriorityList.MaxShown, new PriorityQuery(Take: 10_000).Shown);
        Assert.Equal(1, new PriorityQuery(Take: 0).Shown);

        var counts = new Dictionary<PriorityTab, int> { [PriorityTab.All] = 3, [PriorityTab.Slots] = 1 };
        Assert.True(new PriorityBoard(PriorityTab.All, [Item(PriorityItemKind.AtRisk, "risk", Now)], counts).HasMore);
        Assert.False(new PriorityBoard(PriorityTab.Slots, [Item(PriorityItemKind.AtRisk, "risk", Now)], counts).HasMore);
    }

    private static PriorityItem Item(PriorityItemKind kind, string key, DateTime dueUtc) =>
        new(kind, key, dueUtc, "When", "Title", "Text", "Meta", PriorityTarget.Job, Guid.Empty);
}
