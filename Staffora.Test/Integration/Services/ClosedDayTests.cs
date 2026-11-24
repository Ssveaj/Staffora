using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class ClosedDayTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 3, 26);
    private static readonly DateOnly Friday = new(2027, 3, 12);

    private readonly RealDatabase database;

    public ClosedDayTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_administrator_adds_changes_and_deletes_a_closed_day_and_the_list_and_the_cached_calendar_follow_every_save()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Closed Days Kept", Ct);
        Assert.Contains(RolePermission.SettingsEdit, RolePresets.Administrator.Permissions);
        await using var admin = scenario.HostOf(this.database, scenario.Absence.Schedule.Admin);
        var closedDays = admin.Get<ClosedDayService>();

        var fresh = Made(await closedDays.GetEditorAsync(null, Ct));
        Assert.True(fresh.CanEdit);
        Assert.Equal((string.Empty, new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 9), (Guid?)null), (fresh.Draft.Name, fresh.Draft.FirstDay, fresh.Draft.LastDay, fresh.Draft.OrganizationUnitId));
        Assert.Equal([ClosedDayUnitChoice.WholeOrganization.Name, "Team North"], fresh.Units.Select(unit => unit.Name));

        var id = Made(await closedDays.CreateClosedDayAsync(new ClosedDayDraft("  Founders' Day ", Friday, Friday, null), Ct));
        Assert.Equal(["Founders' Day"], (await CalendarAsync(closedDays)).Select(closed => closed.Name));
        var listed = Assert.Single(Made(await closedDays.QueryClosedDaysAsync(new ListQuery(), Ct)).Rows);
        Assert.Equal((id, "Founders' Day", "1 day", ClosedDayDraft.WholeOrganization, "Fri 12 Mar 2027"), (listed.Id, listed.Name, listed.DaysText, listed.AppliesToText, listed.PeriodText));

        Made(await closedDays.UpdateClosedDayAsync(id, new ClosedDayDraft("Spring break", Friday, Friday.AddDays(3), scenario.StaffMembers.TeamId), Ct));
        var changed = Assert.Single(await CalendarAsync(closedDays));
        Assert.Equal(("Spring break", Friday, Friday.AddDays(3), "Team North"), (changed.Name, changed.FirstDay, changed.LastDay, changed.Unit));
        var opened = Made(await closedDays.GetEditorAsync(id, Ct));
        Assert.Equal(new ClosedDayDraft("Spring break", Friday, Friday.AddDays(3), scenario.StaffMembers.TeamId), opened.Draft);

        Assert.Equal(ClosedDayRules.RangeReversed, (await closedDays.UpdateClosedDayAsync(id, new ClosedDayDraft("Spring break", Friday, Friday.AddDays(-1), null), Ct)).Error);
        Assert.Equal("A closed day needs a name.", (await closedDays.CreateClosedDayAsync(new ClosedDayDraft("  ", Friday, Friday, null), Ct)).Error);
        Assert.Equal(ClosedDayService.UnitNotInUse, (await closedDays.CreateClosedDayAsync(new ClosedDayDraft("Unknown unit", Friday, Friday, Guid.NewGuid()), Ct)).Error);
        var root = Made(await closedDays.CreateClosedDayAsync(new ClosedDayDraft("Root day", Friday, Friday, scenario.StaffMembers.RootId), Ct));
        Assert.Null(await StoredUnitAsync(root));

        Assert.Equal([root], Made(await closedDays.QueryClosedDaysAsync(new ListQuery { Search = "Root" }, Ct)).Rows.Select(row => row.Id));
        Assert.Empty(Made(await closedDays.QueryClosedDaysAsync(new ListQuery { Filters = new Dictionary<string, string> { [ClosedDayList.When] = ClosedDayList.Past } }, Ct)).Rows);

        Made(await closedDays.DeleteClosedDayAsync(id, Ct));
        Made(await closedDays.DeleteClosedDayAsync(root, Ct));
        Assert.Empty(await CalendarAsync(closedDays));
        Assert.Equal(ClosedDayService.NotFound, (await closedDays.DeleteClosedDayAsync(id, Ct)).Error);
    }

    [Fact]
    public async Task A_coordinator_reads_the_calendar_of_closed_days_and_changes_none_and_a_reader_of_settings_reads_without_changing()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Closed Days Readers", Ct);
        Guid id;
        await using (var admin = scenario.HostOf(this.database, scenario.Absence.Schedule.Admin))
        {
            id = Made(await admin.Get<ClosedDayService>().CreateClosedDayAsync(new ClosedDayDraft("Founders' Day", Friday, Friday, null), Ct));
        }

        Assert.DoesNotContain(RolePermission.SettingsView, RolePresets.Coordinator.Permissions);
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var closedDays = coordinator.Get<ClosedDayService>();
            Assert.Equal(["Founders' Day"], (await CalendarAsync(closedDays)).Select(closed => closed.Name));
            Assert.Equal(ClosedDayService.NoView, (await closedDays.QueryClosedDaysAsync(new ListQuery(), Ct)).Error);
            Assert.Equal(ClosedDayService.NoView, (await closedDays.GetEditorAsync(id, Ct)).Error);
            Assert.Equal(ClosedDayService.NoEdit, (await closedDays.GetEditorAsync(null, Ct)).Error);
            Assert.Equal(ClosedDayService.NoEdit, (await closedDays.CreateClosedDayAsync(new ClosedDayDraft("Mine", Friday, Friday, null), Ct)).Error);
            Assert.Equal(ClosedDayService.NoEdit, (await closedDays.UpdateClosedDayAsync(id, new ClosedDayDraft("Mine", Friday, Friday, null), Ct)).Error);
            Assert.Equal(ClosedDayService.NoEdit, (await closedDays.DeleteClosedDayAsync(id, Ct)).Error);
        }

        var viewer = scenario.Absence.Viewer;
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, viewer, Ct, RolePermission.SettingsView);
        await using (var reader = scenario.HostOf(this.database, viewer))
        {
            var closedDays = reader.Get<ClosedDayService>();
            Assert.Single(Made(await closedDays.QueryClosedDaysAsync(new ListQuery(), Ct)).Rows);
            Assert.False(Made(await closedDays.GetEditorAsync(id, Ct)).CanEdit);
            Assert.Equal(ClosedDayService.NoEdit, (await closedDays.DeleteClosedDayAsync(id, Ct)).Error);
        }

        Assert.Single(await CalendarRowsAsync(scenario.TenantId));
    }

    [Fact]
    public async Task Another_tenant_never_reads_or_changes_a_closed_day_never_names_its_unit_and_its_staff_stay_free_on_it()
    {
        var first = await AssignmentScenario.CreateAsync(this.database, "Closed Days First", Ct);
        var second = await AssignmentScenario.CreateAsync(this.database, "Closed Days Second", Ct);
        Guid id;
        await using (var admin = first.HostOf(this.database, first.Absence.Schedule.Admin))
        {
            id = Made(await admin.Get<ClosedDayService>().CreateClosedDayAsync(new ClosedDayDraft("Founders' Day", Friday, Friday, first.StaffMembers.TeamId), Ct));
        }

        var ada = await second.Absence.AddWorkingStaffAsync(this.database, "Ada", new DateOnly(2026, 11, 2), Ct);
        await using (var admin = second.HostOf(this.database, second.Absence.Schedule.Admin))
        {
            var closedDays = admin.Get<ClosedDayService>();
            Assert.Empty(Made(await closedDays.QueryClosedDaysAsync(new ListQuery(), Ct)).Rows);
            Assert.Empty(await CalendarAsync(closedDays));
            Assert.Equal(ClosedDayService.NotFound, (await closedDays.GetEditorAsync(id, Ct)).Error);
            Assert.Equal(ClosedDayService.NotFound, (await closedDays.UpdateClosedDayAsync(id, new ClosedDayDraft("Taken", Friday, Friday, null), Ct)).Error);
            Assert.Equal(ClosedDayService.NotFound, (await closedDays.DeleteClosedDayAsync(id, Ct)).Error);
            Assert.Equal(ClosedDayService.UnitNotInUse, (await closedDays.CreateClosedDayAsync(new ClosedDayDraft("Borrowed unit", Friday, Friday, first.StaffMembers.TeamId), Ct)).Error);
        }

        await using (var coordinator = second.HostOf(this.database, second.Coordinator))
        {
            var answer = Made(await coordinator.Get<StaffAvailabilityService>().ReadAsync([await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct)], Friday, Friday, Ct));
            Assert.All(answer.StaffMembers.Values, staff => Assert.Empty(staff.Blocks));
            Assert.All(answer.StaffMembers.Values, staff => Assert.Empty(staff.Closed));
        }

        await using var context = this.database.CreateSystemContext();
        var stored = await context.ClosedDays.AsNoTracking().SingleAsync(closedDay => closedDay.Id == id, Ct);
        Assert.Equal(("Founders' Day", first.TenantId, first.StaffMembers.TeamId), (stored.Name, stored.TenantId, stored.OrganizationUnitId!.Value));
        Assert.Empty(await CalendarRowsAsync(second.TenantId));
    }

    [Fact]
    public async Task A_closed_day_of_a_unit_blocks_the_staff_members_of_the_unit_and_of_the_units_below_it_in_the_candidate_list_and_on_assign_and_leaves_the_others_free()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Closed Days Units", Ct);
        Guid west;
        Guid westTeam;
        var from = new DateOnly(2026, 11, 2);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", from, Ct);
        var wes = await scenario.Absence.AddWorkingStaffAsync(this.database, "Wes", from, Ct);
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            west = await owner.Get<OrganizationService>().CreateUnitIdAsync("Region West", OrganizationUnitType.Branch, scenario.StaffMembers.RootId, Ct);
            westTeam = await owner.Get<OrganizationService>().CreateUnitIdAsync("Team West", OrganizationUnitType.Team, west, Ct);
            var holdings = owner.Get<RoleHoldingService>();
            Made(await holdings.AssignUserToRoleAsync(wes, westTeam, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.TenantId));
            Made(await holdings.RemoveUserFromRoleAsync(await StaffScenario.HoldingIdAsync(this.database, wes, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.StaffMembers.TeamId, Ct)));
        }

        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead"], AssignmentScenario.Occurrence(Weekday.Friday, new TimeOnly(9, 0), new TimeOnly(11, 0)));
        await using (var admin = scenario.HostOf(this.database, scenario.Absence.Schedule.Admin))
        {
            Made(await admin.Get<ClosedDayService>().CreateClosedDayAsync(new ClosedDayDraft("West study day", Friday, Friday, west), Ct));
        }

        var (adaRecord, wesRecord) = (await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), await AssignmentScenario.ResourceOfAsync(this.database, wes, Ct));
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var candidates = Made(await coordinator.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slots[0], new ListQuery { PageSize = 100 }, Ct)).Rows.ToDictionary(row => row.ResourceId);
            Assert.True(candidates[wesRecord].Verdict.IsBlocked);
            Assert.Equal(1, candidates[wesRecord].Verdict.BlockedOccurrences);
            Assert.Equal(["Closed for West study day: 1 occurrence, 2027-03-12"], candidates[wesRecord].Verdict.BlockGroups.Select(group => group.Text));
            Assert.False(candidates[adaRecord].Verdict.IsBlocked);

            var answer = Made(await coordinator.Get<StaffAvailabilityService>().ReadAsync([adaRecord, wesRecord], Friday, Friday, Ct));
            Assert.Equal(["West study day"], answer.StaffMembers[wesRecord].ClosedOn(Friday).Select(closed => closed.Name));
            Assert.Empty(answer.StaffMembers[adaRecord].Closed);
        }

        var refused = await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], wes, Ct);
        Assert.False(refused.IsSuccess);
        Assert.Contains("Closed for West study day", refused.Error, StringComparison.Ordinal);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));

        await using var owner2 = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime());
        var units = owner2.Get<OrganizationService>();
        var empty = await units.CreateUnitIdAsync("Region East", OrganizationUnitType.Branch, scenario.StaffMembers.RootId, Ct);
        await using (var admin = scenario.HostOf(this.database, scenario.Absence.Schedule.Admin))
        {
            Made(await admin.Get<ClosedDayService>().CreateClosedDayAsync(new ClosedDayDraft("East closed", Friday, Friday, empty), Ct));
        }

        var blocked = await units.StepAsync(empty, OrganizationUnitWorkflowAttribute.DeleteStep, Ct);
        Assert.Contains(ClosedDaysBlockUnitDelete.Reason, blocked.Error, StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<ClosedDayFact>> CalendarAsync(ClosedDayService closedDays) => Made(await closedDays.GetCalendarAsync(Ct));

    private async Task<List<Guid>> CalendarRowsAsync(Guid tenantId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.ClosedDays.AsNoTracking().Where(closedDay => closedDay.TenantId == tenantId).Select(closedDay => closedDay.Id).ToListAsync(Ct);
    }

    private async Task<Guid?> StoredUnitAsync(Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.ClosedDays.AsNoTracking().Where(closedDay => closedDay.Id == id).Select(closedDay => closedDay.OrganizationUnitId).SingleAsync(Ct);
    }

    private static T Made<T>(Core.Models.Result<T> result)
    {
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    private static void Made(Core.Models.Result result) => Assert.True(result.IsSuccess, result.Error);
}
