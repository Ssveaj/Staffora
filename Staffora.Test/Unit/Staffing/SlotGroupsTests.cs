using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Jobs;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;

namespace Staffora.Test.Unit.Staffing;

public class SlotGroupsTests
{
    private static readonly Guid Job = Guid.NewGuid();
    private static readonly Guid Art = Guid.NewGuid();
    private static readonly Guid Music = Guid.NewGuid();
    private static readonly DateTime Made = new(2027, 1, 4, 8, 0, 0, DateTimeKind.Utc);

    private static SlotGroupInput Slot(string name, Guid[] qualifications, int minute = 0, Guid? job = null, JobSlotState state = JobSlotState.Active, Guid? id = null, Guid? service = null, Guid[]? preferred = null) =>
        new(id ?? Guid.NewGuid(), job ?? Job, state, name, service, [.. qualifications.Select(required => new SlotRequirement(required, RequirementLevel.Required)), .. (preferred ?? []).Select(wanted => new SlotRequirement(wanted, RequirementLevel.Preferred))], Made.AddMinutes(minute));

    [Fact]
    public void Slots_alike_in_job_name_and_qualifications_make_one_group_whatever_the_case_spacing_or_order_of_qualifications()
    {
        var first = Slot("Cleaner", [Art, Music], minute: 0);
        var second = Slot("  cleaner ", [Music, Art], minute: 1);
        var third = Slot("CLEANER", [Art, Music, Art], minute: 2);

        var group = Assert.Single(SlotGroups.Of([third, first, second]));
        Assert.Equal([first.SlotId, second.SlotId, third.SlotId], group.SlotIds);
        Assert.Equal(first.SlotId, group.Representative);
        Assert.Equal(3, group.Count);
    }

    [Fact]
    public void A_change_of_qualification_splits_a_group()
    {
        var plain = Slot("Cleaner", [Art], minute: 0);
        var alike = Slot("Cleaner", [Art], minute: 1);
        var more = Slot("Cleaner", [Art, Music], minute: 2);
        var none = Slot("Cleaner", [], minute: 3);

        var groups = SlotGroups.Of([plain, alike, more, none]);
        Assert.Equal(3, groups.Count);
        Assert.Equal([plain.SlotId, alike.SlotId], groups[0].SlotIds);
        Assert.Equal([more.SlotId], groups[1].SlotIds);
        Assert.Equal([none.SlotId], groups[2].SlotIds);
        Assert.NotEqual(SlotGroupKey.Of(plain), SlotGroupKey.Of(more));
    }

    [Fact]
    public void Another_service_or_another_level_of_a_qualification_splits_a_group()
    {
        var cleaning = Guid.NewGuid();
        var supervision = Guid.NewGuid();
        var made = Slot("Cleaner", [Art], minute: 0, service: cleaning);
        var alike = Slot("Cleaner", [Art], minute: 1, service: cleaning);
        var otherService = Slot("Cleaner", [Art], minute: 2, service: supervision);
        var byHand = Slot("Cleaner", [Art], minute: 3);
        var preferred = Slot("Cleaner", [], minute: 4, service: cleaning, preferred: [Art]);
        var atBoth = Slot("Cleaner", [Art], minute: 5, service: cleaning, preferred: [Art]);

        var groups = SlotGroups.Of([made, alike, otherService, byHand, preferred, atBoth]);
        Assert.Equal(4, groups.Count);
        Assert.Equal([made.SlotId, alike.SlotId, atBoth.SlotId], groups[0].SlotIds);
        Assert.Equal([otherService.SlotId], groups[1].SlotIds);
        Assert.Equal([byHand.SlotId], groups[2].SlotIds);
        Assert.Equal([preferred.SlotId], groups[3].SlotIds);
        Assert.NotEqual(SlotGroupKey.Of(made), SlotGroupKey.Of(preferred));
        Assert.NotEqual(SlotGroupKey.Of(made), SlotGroupKey.Of(otherService));
    }

    [Fact]
    public void Another_name_or_another_job_splits_a_group_and_a_description_does_not_count()
    {
        var cleaner = Slot("Cleaner", [Art]);
        var supervisor = Slot("Supervisor", [Art], minute: 1);
        var elsewhere = Slot("Cleaner", [Art], minute: 2, job: Guid.NewGuid());

        Assert.Equal(3, SlotGroups.Of([cleaner, supervisor, elsewhere]).Count);
        Assert.Null(SlotGroups.GroupOf(Guid.NewGuid(), [cleaner]));
    }

    [Fact]
    public void A_canceled_slot_is_never_grouped()
    {
        var active = Slot("Cleaner", [Art]);
        var canceled = Slot("Cleaner", [Art], minute: 1, state: JobSlotState.Canceled);

        Assert.Equal([active.SlotId], Assert.Single(SlotGroups.Of([active, canceled])).SlotIds);
        Assert.Null(SlotGroups.GroupOf(canceled.SlotId, [active, canceled]));
    }

    [Fact]
    public void Members_made_at_the_same_time_are_ordered_by_id_and_groups_by_their_first_member()
    {
        var low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var later = Slot("Supervisor", [], minute: -5);
        var groups = SlotGroups.Of([Slot("Cleaner", [], id: high), Slot("Cleaner", [], id: low), later]);

        Assert.Equal([later.SlotId], groups[0].SlotIds);
        Assert.Equal([low, high], groups[1].SlotIds);
    }

    [Fact]
    public void A_new_holder_keeps_its_own_open_slot_else_takes_the_first_open_one_and_none_when_every_slot_is_held()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var group = new SlotGroup(default, ids);

        Assert.Equal(ids[2], SlotGroups.FirstOpen(group, ids[2], new HashSet<Guid>()));
        Assert.Equal(ids[1], SlotGroups.FirstOpen(group, ids[0], new HashSet<Guid> { ids[0] }));
        Assert.Equal(ids[0], SlotGroups.FirstOpen(group, preferred: null, new HashSet<Guid> { ids[1] }));
        Assert.Null(SlotGroups.FirstOpen(group, ids[0], new HashSet<Guid>(ids)));
    }

    [Fact]
    public void Held_counts_the_assigned_and_the_yeses_at_most_once_per_slot_and_reads_two_of_three()
    {
        Assert.Equal(2, SlotGroups.Held(3, assigned: 1, accepted: 1));
        Assert.Equal(3, SlotGroups.Held(3, assigned: 2, accepted: 4));
        Assert.Equal("2 of 3", SlotGroups.HeldText(2, 3));
        Assert.True(JobSlotRules.CountInRange(1));
        Assert.True(JobSlotRules.CountInRange(JobSlotRules.MaxAddCount));
        Assert.False(JobSlotRules.CountInRange(0));
        Assert.False(JobSlotRules.CountInRange(JobSlotRules.MaxAddCount + 1));
    }

    [Fact]
    public void A_groups_coverage_counts_its_holders_together_and_a_group_of_one_reads_as_its_slot()
    {
        Assert.Equal(JobCoverage.PartlyCovered, JobCoverageRules.OfGroup(3, 2));
        Assert.Equal(JobCoverage.Covered, JobCoverageRules.OfGroup(3, 3));
        Assert.Equal(JobCoverage.Uncovered, JobCoverageRules.OfGroup(3, 0));
        Assert.Equal(JobCoverage.PartlyCovered, JobCoverageRules.OfGroups([new SlotGroupCoverageInput(3, 3), new SlotGroupCoverageInput(1, 0)]));
        Assert.Equal(JobCoverage.Covered, JobCoverageRules.OfGroups([new SlotGroupCoverageInput(3, 5), new SlotGroupCoverageInput(1, 1)]));

        foreach (var held in new[] { true, false })
        {
            Assert.Equal(JobCoverageRules.Of([new SlotCoverageInput(JobSlotState.Active, held)]), JobCoverageRules.OfGroups([new SlotGroupCoverageInput(1, held ? 1 : 0)]));
        }
    }

    [Fact]
    public void A_group_is_covered_in_the_priority_list_only_once_each_slot_has_a_holder_and_at_risk_when_any_holder_is()
    {
        var occurrences = StaffingRules.RemainingOccurrences([new OccurrenceTimes(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0))], new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 14), Made, TimeZoneInfo.Utc);
        var free = new HolderFacts(Guid.NewGuid(), "Ada Example", StaffingVerdict.None);
        var blocked = new HolderFacts(Guid.NewGuid(), "Bo Example", StaffingRules.Judge(new StaffAvailability(Guid.NewGuid(), [], [new AvailabilityBlock(occurrences[0].StartUtc.AddHours(-1), occurrences[0].EndUtc.AddHours(1), "On sick leave (reported)", Guid.NewGuid())], []), occurrences));
        Assert.True(blocked.Verdict.IsBlocked);

        Assert.Equal(PriorityKind.Uncovered, PriorityRules.Classify(new SlotFacts(occurrences, false, free, 0, 0) { Count = 3, Holders = [free] }));
        Assert.Equal(PriorityKind.WaitingForPick, PriorityRules.Classify(new SlotFacts(occurrences, false, free, 1, 0) { Count = 3, Holders = [free] }));
        Assert.Equal(PriorityKind.Covered, PriorityRules.Classify(new SlotFacts(occurrences, false, free, 0, 0) { Count = 2, Holders = [free, free] }));
        Assert.Equal(PriorityKind.AtRisk, PriorityRules.Classify(new SlotFacts(occurrences, false, free, 0, 0) { Count = 3, Holders = [free, blocked] }));
        Assert.StartsWith("Bo Example, the assigned staff member", PriorityRules.Reasons(new SlotFacts(occurrences, false, free, 0, 0) { Count = 3, Holders = [free, blocked] })[0], StringComparison.Ordinal);

        Assert.Equal(PriorityKind.Covered, PriorityRules.Classify(new SlotFacts(occurrences, false, free, 0, 0)));
        Assert.Equal(PriorityKind.Uncovered, PriorityRules.Classify(new SlotFacts(occurrences, false, null, 0, 0)));
    }
}
