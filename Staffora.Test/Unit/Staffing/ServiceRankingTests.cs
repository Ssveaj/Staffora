using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Jobs;
using Staffora.Core.Models;
using Staffora.Core.Staffing;

namespace Staffora.Test.Unit.Staffing;

public class ServiceRankingTests
{
    private static readonly Guid Cleaning = Guid.Parse("00000000-0000-0000-0000-00000000c1ea");
    private static readonly Guid FirstAid = Guid.Parse("00000000-0000-0000-0000-0000000f1a1d");
    private static readonly Guid Windows = Guid.Parse("00000000-0000-0000-0000-00000000a1d0");

    private static readonly SlotRequirement[] Asked =
    [
        new(Cleaning, RequirementLevel.Required),
        new(FirstAid, RequirementLevel.Preferred),
        new(Windows, RequirementLevel.Preferred),
    ];

    [Fact]
    public void A_preferred_qualification_ranks_after_the_required_ones_and_is_never_missing()
    {
        var a = StaffingRules.MatchAtLevels(Asked, new HashSet<Guid> { Cleaning });
        var b = StaffingRules.MatchAtLevels(Asked, new HashSet<Guid> { Cleaning, FirstAid, Windows });
        var c = StaffingRules.MatchAtLevels(Asked, new HashSet<Guid> { FirstAid, Windows });

        Assert.Equal((1, 1, 2, 0), (a.Required, a.Matching, a.Preferred, a.PreferredMatching));
        Assert.True(a.MeetsAll);
        Assert.Empty(a.Missing);
        Assert.Equal((1, 1, 2, 2), (b.Required, b.Matching, b.Preferred, b.PreferredMatching));
        Assert.True(b.MeetsAll);

        Assert.False(c.MeetsAll);
        Assert.Equal([Cleaning], c.Missing);
        Assert.DoesNotContain(FirstAid, a.Missing);

        var ranked = StaffingRules.Rank(
        [
            Facts("A", a, heldJobs: 0),
            Facts("B", b, heldJobs: 3),
            Facts("C", c, heldJobs: 0),
        ]);
        Assert.Equal(["B", "A", "C"], ranked.Select(fact => fact.Name));
    }

    [Fact]
    public void Equal_preferred_matches_fall_back_to_the_held_jobs_and_then_the_name()
    {
        var one = StaffingRules.MatchAtLevels(Asked, new HashSet<Guid> { Cleaning, FirstAid });
        var other = StaffingRules.MatchAtLevels(Asked, new HashSet<Guid> { Cleaning, Windows });
        var ranked = StaffingRules.Rank(
        [
            Facts("Zoe", one, heldJobs: 1),
            Facts("Yan", other, heldJobs: 2),
            Facts("Ada", other, heldJobs: 1),
        ]);
        Assert.Equal(["Ada", "Zoe", "Yan"], ranked.Select(fact => fact.Name));
    }

    [Fact]
    public void A_qualification_asked_at_both_levels_counts_as_required_and_a_slot_asking_only_preferred_ones_warns_nobody()
    {
        var both = StaffingRules.MatchAtLevels([new(Cleaning, RequirementLevel.Preferred), new(Cleaning, RequirementLevel.Required)], new HashSet<Guid>());
        Assert.Equal((1, 0, 0), (both.Required, both.Matching, both.Preferred));
        Assert.Equal([Cleaning], both.Missing);

        var preferredOnly = StaffingRules.MatchAtLevels([new(FirstAid, RequirementLevel.Preferred)], new HashSet<Guid>());
        Assert.True(preferredOnly.MeetsAll);
        Assert.Empty(preferredOnly.Missing);
        Assert.Equal("None required, 0 of 1 preferred", Row(preferredOnly).MatchLabel);
        Assert.Equal("The required one, 2 of 2 preferred", Row(StaffingRules.MatchAtLevels(Asked, new HashSet<Guid> { Cleaning, FirstAid, Windows })).MatchLabel);

        Assert.Equal("No qualification required", Row(StaffingRules.MatchAtLevels([], new HashSet<Guid>())).MatchLabel);
        Assert.Equal("The required one", Row(StaffingRules.MatchAtLevels([new(Cleaning, RequirementLevel.Required)], new HashSet<Guid> { Cleaning })).MatchLabel);
    }

    [Fact]
    public void A_slot_made_from_a_service_takes_its_texts_and_its_active_qualifications_at_their_levels()
    {
        var tenant = Guid.NewGuid();
        var job = Guid.NewGuid();
        var service = new ServiceTemplate(Guid.NewGuid(), "Cleaning supervisor", "Leads the crew", Asked);

        var active = new HashSet<Guid> { Cleaning, FirstAid };
        var slot = ServiceSlotRules.Build(tenant, job, service, active);

        Assert.Equal((tenant, job, service.ServiceId, JobSlotState.Active), (slot.TenantId, slot.JobId, slot.ServiceId, slot.State));
        Assert.Equal(("Cleaning supervisor", "Leads the crew"), (slot.Name, slot.Description));
        Assert.Equal(
            [(Cleaning, RequirementLevel.Required), (FirstAid, RequirementLevel.Preferred)],
            slot.Qualifications.OrderBy(row => row.QualificationId).Select(row => (row.QualificationId, row.Level)));
        Assert.All(slot.Qualifications, row => Assert.Equal(tenant, row.TenantId));
        Assert.DoesNotContain(slot.Qualifications, row => row.QualificationId == Windows);
    }

    private static RankFacts Facts(string name, QualificationMatch match, int heldJobs) =>
        new(Guid.NewGuid(), name, match.MeetsAll, match.Matching, heldJobs, match.PreferredMatching);

    private static StaffingCandidateRow Row(QualificationMatch match) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, "Kim", match, [.. match.Missing.Select(id => id == Cleaning ? "Cleaning" : "Other")], 0, StaffingVerdict.None, [], []);
}
