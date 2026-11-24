using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;

namespace Staffora.Test.Unit.Staffing;

public class PriorityRulesTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private static readonly DateTime Now = new(2027, 1, 25, 9, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<OccurrenceInstant> Occurrences = StaffingRules.RemainingOccurrences([new OccurrenceTimes(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0))], new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 14), Now, Stockholm);

    [Fact]
    public void A_slot_whose_assigned_staff_member_the_availability_blocks_is_at_risk_with_the_blocks_occurrences_and_dates()
    {
        var absent = Holder("Ada Example", new AvailabilityBlock(Occurrences[1].StartUtc.AddHours(-1), Occurrences[1].EndUtc.AddHours(1), "On sick leave (reported)", Guid.NewGuid()));
        var slot = new SlotFacts(Occurrences, PeriodStarted: false, absent, Accepted: 0, Requested: 0);
        Assert.Equal(PriorityKind.AtRisk, PriorityRules.Classify(slot));
        Assert.Equal(["Ada Example, the assigned staff member, is not free for 1 of its 2 remaining occurrences.", "On sick leave (reported): 1 occurrence, 2027-02-08."], PriorityRules.Reasons(slot));

        var inactive = Holder("Bo Example", new AvailabilityBlock(Occurrences[0].StartUtc.AddDays(-1), Occurrences[1].EndUtc.AddDays(1), AbsenceRules.NotInServiceReason));
        Assert.Equal(PriorityKind.AtRisk, PriorityRules.Classify(new SlotFacts(Occurrences, false, inactive, 0, 0)));
        Assert.Contains("not free for 2 of its 2 remaining occurrences", PriorityRules.Reasons(new SlotFacts(Occurrences, false, inactive, 0, 0))[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_slot_whose_assigned_staff_member_is_free_is_covered_and_never_listed_whatever_else_waits_on_it()
    {
        var slot = new SlotFacts(Occurrences, false, Holder("Ada Example"), Accepted: 2, Requested: 1);
        Assert.Equal(PriorityKind.Covered, PriorityRules.Classify(slot));
        Assert.Empty(PriorityRules.Reasons(slot));
        Assert.Empty(PriorityRules.Order([Facts("Algebra", "Lead", PriorityKind.Covered, Occurrences[0].StartUtc)], Now));

        var warned = Holder("Ada Example") with { Verdict = new StaffingVerdict(2, [], [new OccurrenceFinding(Occurrences[0], "Asked to work a job, not answered yet", Guid.NewGuid())]) };
        Assert.Equal(PriorityKind.Covered, PriorityRules.Classify(new SlotFacts(Occurrences, false, warned, 0, 0)));
    }

    [Fact]
    public void A_slot_without_a_holder_waits_for_a_pick_when_someone_said_yes_is_unanswered_when_staff_members_are_asked_and_uncovered_otherwise()
    {
        Assert.Equal(PriorityKind.WaitingForPick, PriorityRules.Classify(new SlotFacts(Occurrences, false, null, Accepted: 1, Requested: 3)));
        Assert.Equal(["1 staff member said yes: pick who works."], PriorityRules.Reasons(new SlotFacts(Occurrences, false, null, 1, 3)));
        Assert.Equal(["2 staff members said yes: pick who works."], PriorityRules.Reasons(new SlotFacts(Occurrences, false, null, 2, 0)));

        Assert.Equal(PriorityKind.Unanswered, PriorityRules.Classify(new SlotFacts(Occurrences, false, null, Accepted: 0, Requested: 2)));
        Assert.Equal(["2 staff members are asked and have not answered."], PriorityRules.Reasons(new SlotFacts(Occurrences, false, null, 0, 2)));
        Assert.Equal(["1 staff member is asked and has not answered."], PriorityRules.Reasons(new SlotFacts(Occurrences, false, null, 0, 1)));

        Assert.Equal(PriorityKind.Uncovered, PriorityRules.Classify(new SlotFacts(Occurrences, false, null, 0, 0)));
        Assert.Equal([PriorityRules.NobodyAskedReason], PriorityRules.Reasons(new SlotFacts(Occurrences, false, null, 0, 0)));
    }

    [Fact]
    public void The_time_to_start_is_the_time_to_the_next_occurrence_in_one_coarse_unit()
    {
        Assert.Equal(Occurrences[0].StartUtc, PriorityRules.NextOccurrenceUtc(new SlotFacts(Occurrences, false, null, 0, 0)));
        Assert.Null(PriorityRules.NextOccurrenceUtc(new SlotFacts([], false, null, 0, 0)));

        var next = new DateTime(2027, 2, 1, 7, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Starts in 30 minutes", PriorityRules.StartsLabel(next, periodStarted: false, next.AddMinutes(-30)));
        Assert.Equal("Starts in 1 minute", PriorityRules.StartsLabel(next, false, next.AddSeconds(-20)));
        Assert.Equal("Starts in 5 hours", PriorityRules.StartsLabel(next, false, next.AddHours(-5)));
        Assert.Equal("Starts in 47 hours", PriorityRules.StartsLabel(next, false, next.AddHours(-47.5)));
        Assert.Equal("Starts in 3 days", PriorityRules.StartsLabel(next, false, next.AddDays(-3)));
        Assert.Equal("Starts in 3 weeks", PriorityRules.StartsLabel(next, false, next.AddDays(-22)));
        Assert.Equal("Starts in 4 months", PriorityRules.StartsLabel(next, false, next.AddDays(-125)));

        Assert.Equal("Next occurrence in 2 days", PriorityRules.StartsLabel(next, periodStarted: true, next.AddDays(-2)));
        Assert.Equal("A time is on now", PriorityRules.StartsLabel(next, true, next.AddMinutes(10)));
        Assert.Equal("No occurrences left", PriorityRules.StartsLabel(null, true, next));
    }

    [Fact]
    public void Urgent_slots_come_first_whatever_their_kind()
    {
        var soonUnanswered = Facts("Biology", "Lead", PriorityKind.Unanswered, Now.AddDays(3));
        var laterUncovered = Facts("Algebra", "Lead", PriorityKind.Uncovered, Now.AddDays(30));
        Assert.Equal([soonUnanswered, laterUncovered], PriorityRules.Order([laterUncovered, soonUnanswered], Now));

        Assert.True(PriorityRules.IsUrgent(Now.Add(PriorityRules.UrgentWithin), Now));
        Assert.False(PriorityRules.IsUrgent(Now.Add(PriorityRules.UrgentWithin).AddMinutes(1), Now));
        Assert.True(PriorityRules.IsUrgent(Now.AddMinutes(-30), Now));
        Assert.False(PriorityRules.IsUrgent(null, Now));
    }

    [Fact]
    public void Among_equally_urgent_slots_at_risk_comes_before_uncovered_before_waiting_for_a_pick_before_unanswered()
    {
        var start = Now.AddDays(3);
        var unanswered = Facts("A job", "Slot", PriorityKind.Unanswered, start.AddHours(-3));
        var waiting = Facts("B job", "Slot", PriorityKind.WaitingForPick, start.AddHours(-2));
        var uncovered = Facts("C job", "Slot", PriorityKind.Uncovered, start.AddHours(-1));
        var atRisk = Facts("D job", "Slot", PriorityKind.AtRisk, start);
        Assert.Equal([atRisk, uncovered, waiting, unanswered], PriorityRules.Order([unanswered, waiting, uncovered, atRisk], Now));

        var later = Now.AddDays(40);
        var lateUnanswered = Facts("A job", "Slot", PriorityKind.Unanswered, later);
        var lateAtRisk = Facts("B job", "Slot", PriorityKind.AtRisk, later.AddDays(10));
        Assert.Equal([lateAtRisk, lateUnanswered], PriorityRules.Order([lateUnanswered, lateAtRisk], Now));
    }

    [Fact]
    public void Among_slots_of_one_urgency_and_kind_the_soonest_occurrence_comes_first_then_the_job_the_slot_and_the_id()
    {
        var sooner = Facts("Zoology", "Slot", PriorityKind.Uncovered, Now.AddDays(2));
        var later = Facts("Algebra", "Slot", PriorityKind.Uncovered, Now.AddDays(4));
        var none = Facts("Aardvarks", "Slot", PriorityKind.Uncovered, null);
        Assert.Equal([sooner, later, none], PriorityRules.Order([none, later, sooner], Now));

        var at = Now.AddDays(2);
        var algebra = Facts("algebra", "Lead", PriorityKind.Uncovered, at);
        var biologyAssistant = Facts("Biology", "Assistant", PriorityKind.Uncovered, at);
        var biologyMain = Facts("Biology", "Lead", PriorityKind.Uncovered, at);
        Assert.Equal([algebra, biologyAssistant, biologyMain], PriorityRules.Order([biologyMain, biologyAssistant, algebra], Now));

        var first = new PriorityFacts(new Guid("00000000-0000-0000-0000-000000000001"), "Biology", "Lead", PriorityKind.Uncovered, at);
        var second = first with { SlotId = new Guid("00000000-0000-0000-0000-000000000002") };
        Assert.Equal([first, second], PriorityRules.Order([second, first], Now));
    }

    [Fact]
    public void A_question_awaiting_a_reply_raises_a_covered_slot_and_is_said_last_on_any_other()
    {
        var asking = new SlotFacts(Occurrences, false, Holder("Ada Example"), Accepted: 0, Requested: 0, AwaitingReply: ["Ada Example"]);
        Assert.Equal(PriorityKind.AwaitsReply, PriorityRules.Classify(asking));
        Assert.Equal(["Ada Example wrote and waits for your reply."], PriorityRules.Reasons(asking));

        var unanswered = new SlotFacts(Occurrences, false, null, Accepted: 0, Requested: 2, AwaitingReply: ["Ada Example", "Bo Example"]);
        Assert.Equal(PriorityKind.Unanswered, PriorityRules.Classify(unanswered));
        Assert.Equal(["2 staff members are asked and have not answered.", "Ada Example and Bo Example wrote and wait for your reply."], PriorityRules.Reasons(unanswered));
        var absent = Holder("Cy Example", new AvailabilityBlock(Occurrences[0].StartUtc.AddHours(-1), Occurrences[0].EndUtc.AddHours(1), "On sick leave (reported)", Guid.NewGuid()));
        var atRisk = new SlotFacts(Occurrences, false, absent, 0, 0, ["Cy Example"]);
        Assert.Equal(PriorityKind.AtRisk, PriorityRules.Classify(atRisk));
        Assert.Equal("Cy Example wrote and waits for your reply.", PriorityRules.Reasons(atRisk)[^1]);
        Assert.Equal(string.Empty, PriorityRules.AwaitingText([]));

        var at = Now.AddDays(3);
        var awaits = Facts("A job", "Slot", PriorityKind.AwaitsReply, at);
        var waiting = Facts("B job", "Slot", PriorityKind.WaitingForPick, at);
        var unansweredRow = Facts("C job", "Slot", PriorityKind.Unanswered, at);
        Assert.Equal([waiting, awaits, unansweredRow], PriorityRules.Order([unansweredRow, awaits, waiting], Now));
    }

    private static HolderFacts Holder(string name, params AvailabilityBlock[] blocks)
    {
        var resource = Guid.NewGuid();
        var availability = new StaffAvailability(resource, [.. Occurrences.Select(occurrence => new WorkingTime(occurrence.Date, occurrence.Start, occurrence.End, occurrence.StartUtc, occurrence.EndUtc, Guid.NewGuid(), Guid.NewGuid()))], blocks, []);
        return new HolderFacts(resource, name, StaffingRules.Judge(availability, Occurrences));
    }

    private static PriorityFacts Facts(string job, string slot, PriorityKind kind, DateTime? next) => new(Guid.NewGuid(), job, slot, kind, next);
}
