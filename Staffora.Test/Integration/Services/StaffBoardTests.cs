using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Settings;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Coordination;
using Staffora.Services.Jobs;
using Staffora.Services.Customers;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class StaffBoardTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 3, 26);
    private static readonly DateOnly Monday = new(2027, 3, 8);
    private static readonly DateOnly Wednesday = new(2027, 3, 10);

    private readonly RealDatabase database;

    public StaffBoardTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_absent_staff_members_band_absence_and_timetable_slots_show_for_the_selected_week_and_her_jobs_open_slot_is_to_staff()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Absent", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", new DateOnly(2026, 11, 2), Ct);
        var bo = await scenario.AddStaffAsync(this.database, "Bo", Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"],
            AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0)),
            AssignmentScenario.Occurrence(Weekday.Wednesday, new TimeOnly(13, 0), new TimeOnly(14, 0)));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        await using (var staff = scenario.HostOf(this.database, ada))
        {
            Made(await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), ada, AbsenceKind.SickLeave, Monday, Monday, Ct));
        }

        await using (var registrar = scenario.HostOf(this.database, scenario.Absence.Registrar))
        {
            Made(await AbsenceScenario.RegisterAsync(registrar.Get<ResourceAbsenceService>(), ada, AbsenceKind.PlannedLeave, Wednesday, Wednesday, Ct, start: new TimeOnly(13, 0)));
        }

        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var (staffMembers, bookings) = await ReadAsync(host, Wednesday, new BoardFilter());
        var adaRecord = await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct);

        Assert.Equal(Monday, staffMembers.Monday);
        Assert.Equal(Monday.AddDays(6), staffMembers.Days[^1]);
        Assert.Equal(["Ada Example", "Bo Example"], staffMembers.StaffMembers.Select(row => row.Name));
        Assert.True(staffMembers.MayAddAbsence);
        var row = staffMembers.StaffMembers[0];
        Assert.Equal((adaRecord, ada, false), (row.ResourceId, row.UserId, row.IsReader));

        Assert.All(row.Days.Take(5), day => Assert.Equal(["08:00-16:00"], day.Bands.Select(band => band.Text)));
        Assert.All(row.Days.Skip(5), day => Assert.Empty(day.Bands));
        var sick = Assert.Single(row.Days[0].Absences);
        Assert.Equal(("Sick leave, reported", "All day", true), (sick.Text, sick.TimeText, sick.Blocks));
        var leave = Assert.Single(row.Days[2].Absences);
        Assert.Equal(("Planned leave, waiting for approval", "From 13:00", false), (leave.Text, leave.TimeText, leave.Blocks));
        Assert.Empty(row.Days[1].Absences);

        var monday = Assert.Single(bookings.OccurrencesOf(adaRecord, Monday));
        Assert.Equal((job, "Algebra", "Lead", "Brightpath Learning", "Assigned", "08:00-10:00"), (monday.JobId, monday.Job, monday.Slot, monday.Customer, monday.StateText, monday.Times));
        Assert.Equal("13:00-14:00", Assert.Single(bookings.OccurrencesOf(adaRecord, Wednesday)).Times);
        Assert.Empty(bookings.OccurrencesOf(adaRecord, Monday.AddDays(1)));
        var boRow = staffMembers.StaffMembers[1];
        Assert.All(boRow.Days, day => Assert.Empty(day.Bands));
        Assert.Empty(bookings.OccurrencesOf(boRow.ResourceId, Monday));

        Assert.Equal([(slots[1], Monday), (slots[1], Wednesday)], bookings.ToStaff.Select(occurrence => (occurrence.SlotId, occurrence.Date)));
        Assert.All(bookings.ToStaff, occurrence => Assert.Equal(("1 of 2 free", PriorityKind.Uncovered), (occurrence.FreeText, occurrence.Kind)));
        Assert.True(bookings.Pair(slots[1], adaRecord)!.Verdict.IsBlocked);
        Assert.True(bookings.Pair(slots[1], boRow.ResourceId)!.IsFree);
        Assert.Null(bookings.Pair(slots[0], adaRecord));

        var (later, laterBookings) = await ReadAsync(host, Monday.AddDays(7), new BoardFilter());
        Assert.All(later.StaffMembers[0].Days, day => Assert.Empty(day.Absences));
        Assert.Single(laterBookings.OccurrencesOf(adaRecord, Monday.AddDays(7)));
    }

    [Fact]
    public async Task The_board_and_the_staffing_dialog_agree_for_every_slot_and_staff_member_pair()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Parity", Ct);
        Guid physics;
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            physics = Made(await coordinator.Get<QualificationService>().CreateQualificationAsync(new Qualification { Name = "Physics" }, QualificationWorkflowAttribute.CreateStep, Ct));
        }

        var from = new DateOnly(2026, 11, 2);
        var (ada, bo, cy) = (await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", from, Ct), await scenario.Absence.AddWorkingStaffAsync(this.database, "Bo", from, Ct), await scenario.Absence.AddWorkingStaffAsync(this.database, "Cy", from, Ct));
        var (dee, eve, fay) = (await scenario.AddStaffAsync(this.database, "Dee", Ct), await scenario.Absence.AddWorkingStaffAsync(this.database, "Eve", from, Ct), await scenario.Absence.AddWorkingStaffAsync(this.database, "Fay", from, Ct));
        await using (var keeper = scenario.HostOf(this.database, scenario.Coordinator))
        {
            Made(await keeper.Get<QualificationService>().GiveQualificationAsync(eve, physics, Ct));
        }

        var (_, algebra) = await JobAsync(scenario, scenario.Customer, "Algebra", ("Lead", [physics]), ("Assistant", []), ("Tutor", []));
        var (_, other) = await scenario.StartedJobAsync(this.database, "Biology", PeriodStart, PeriodEnd, Ct, ["Staff", "Helper"]);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, other[0], ada, Ct));
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, other[1], cy, Ct));
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, algebra[1], fay, Ct));
        await using (var sickHost = scenario.HostOf(this.database, bo))
        {
            Made(await AbsenceScenario.ReportAsync(sickHost.Get<ResourceAbsenceService>(), bo, AbsenceKind.SickLeave, Monday.AddDays(7), Monday.AddDays(7), Ct));
        }

        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var (staffMembers, bookings) = await ReadAsync(host, Monday, new BoardFilter());
        var onBoard = staffMembers.StaffMembers.Select(staff => staff.ResourceId).ToList();
        Assert.Equal(6, onBoard.Count);

        Assert.Equal(new HashSet<Guid> { algebra[0], algebra[1], algebra[2], other[1] }, bookings.ToStaff.Select(occurrence => occurrence.SlotId).Distinct().ToList().ToHashSet());
        var compared = 0;
        foreach (var slot in bookings.ToStaff.Select(occurrence => occurrence.SlotId).Distinct().ToList())
        {
            var page = await host.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slot, new ListQuery { PageSize = 100 }, Ct);
            Assert.True(page.IsSuccess, page.Error);
            var candidates = page.Value!.Rows.ToDictionary(row => row.ResourceId);

            Assert.Subset(onBoard.ToHashSet(), candidates.Keys.ToHashSet());
            foreach (var staff in onBoard)
            {
                var pair = bookings.Pair(slot, staff);
                Assert.NotNull(pair);
                if (!candidates.TryGetValue(staff, out var row))
                {
                    Assert.True(pair.Asked, $"Staff {staff} is no candidate for slot {slot}, so the board names them asked.");
                    continue;
                }

                Assert.False(pair.Asked);
                Assert.Equal(row.Verdict.Summary, pair.Verdict.Summary);
                Assert.Equal(row.Verdict.IsBlocked, pair.Verdict.IsBlocked);
                Assert.Equal((row.Verdict.Occurrences, row.Verdict.BlockedOccurrences, row.Verdict.WarnedOccurrences), (pair.Verdict.Occurrences, pair.Verdict.BlockedOccurrences, pair.Verdict.WarnedOccurrences));
                Assert.Equal(row.Verdict.BlockGroups, pair.Verdict.BlockGroups);
                Assert.Equal(row.Verdict.WarningGroups, pair.Verdict.WarningGroups);
                Assert.Equal((row.Match.Required, row.Match.Matching), (pair.Match.Required, pair.Match.Matching));
                Assert.Equal(row.Match.Missing, pair.Match.Missing);
                compared++;
            }
        }

        var records = new Dictionary<string, Guid>();
        foreach (var (name, user) in new[] { ("ada", ada), ("bo", bo), ("cy", cy), ("dee", dee), ("eve", eve), ("fay", fay) })
        {
            records[name] = await AssignmentScenario.ResourceOfAsync(this.database, user, Ct);
        }

        Assert.Equal(4 * 6 - 2, compared);
        Assert.True(bookings.Pair(algebra[2], records["ada"])!.Verdict.IsBlocked);
        Assert.True(bookings.Pair(algebra[2], records["bo"])!.Verdict.IsBlocked);
        Assert.Contains(bookings.Pair(algebra[2], records["cy"])!.Verdict.WarningGroups, group => group.Reason.StartsWith("Asked to work", StringComparison.Ordinal));
        Assert.Contains(bookings.Pair(algebra[2], records["dee"])!.Verdict.WarningGroups, group => group.Reason == StaffingRules.OutsidePatternReason);
        Assert.True(bookings.Pair(algebra[0], records["eve"])!.Match.MeetsAll);
        Assert.False(bookings.Pair(algebra[0], records["dee"])!.Match.MeetsAll);
        Assert.True(bookings.Pair(algebra[1], records["fay"])!.Asked);
        Assert.True(bookings.Pair(other[1], records["cy"])!.Asked);
    }

    [Fact]
    public async Task The_board_and_the_staffing_dialog_agree_for_every_pair_on_a_closed_day_of_the_whole_organization_and_one_of_a_unit_and_the_rows_mark_them()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Closed Days", Ct);
        var from = new DateOnly(2026, 11, 2);
        var (ada, wes, cy) = (await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", from, Ct), await scenario.Absence.AddWorkingStaffAsync(this.database, "Wes", from, Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        Guid west;
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            west = await owner.Get<OrganizationService>().CreateUnitIdAsync("Region West", OrganizationUnitType.Branch, scenario.StaffMembers.RootId, Ct);
            var westTeam = await owner.Get<OrganizationService>().CreateUnitIdAsync("Team West", OrganizationUnitType.Team, west, Ct);
            var holdings = owner.Get<RoleHoldingService>();
            Made(await holdings.AssignUserToRoleAsync(wes, westTeam, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.TenantId));
            Made(await holdings.RemoveUserFromRoleAsync(await StaffScenario.HoldingIdAsync(this.database, wes, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.StaffMembers.TeamId, Ct)));
        }

        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"],
            AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0)),
            AssignmentScenario.Occurrence(Weekday.Wednesday, new TimeOnly(13, 0), new TimeOnly(14, 0)),
            AssignmentScenario.Occurrence(Weekday.Friday, new TimeOnly(9, 0), new TimeOnly(11, 0)));
        var friday = Monday.AddDays(4);
        await using (var admin = scenario.HostOf(this.database, scenario.Absence.Schedule.Admin))
        {
            var closedDays = admin.Get<ClosedDayService>();
            Made(await closedDays.CreateClosedDayAsync(new ClosedDayDraft("Founders' Day", Wednesday, Wednesday, null), Ct));
            Made(await closedDays.CreateClosedDayAsync(new ClosedDayDraft("West study day", friday, friday, west), Ct));
        }

        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var (staffMembers, bookings) = await ReadAsync(host, Monday, new BoardFilter());
        var records = new Dictionary<string, Guid>();
        foreach (var (name, user) in new[] { ("ada", ada), ("wes", wes), ("cy", cy) })
        {
            records[name] = await AssignmentScenario.ResourceOfAsync(this.database, user, Ct);
        }

        var compared = 0;
        foreach (var slot in algebra)
        {
            var page = await host.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slot, new ListQuery { PageSize = 100 }, Ct);
            Assert.True(page.IsSuccess, page.Error);
            var candidates = page.Value!.Rows.ToDictionary(row => row.ResourceId);
            Assert.Equal(staffMembers.StaffMembers.Select(staff => staff.ResourceId).ToHashSet(), candidates.Keys.ToHashSet());
            foreach (var (id, row) in candidates)
            {
                var pair = bookings.Pair(slot, id)!;
                Assert.Equal((row.Verdict.Summary, row.Verdict.IsBlocked, row.Verdict.Occurrences, row.Verdict.BlockedOccurrences, row.Verdict.WarnedOccurrences), (pair.Verdict.Summary, pair.Verdict.IsBlocked, pair.Verdict.Occurrences, pair.Verdict.BlockedOccurrences, pair.Verdict.WarnedOccurrences));
                Assert.Equal(row.Verdict.BlockGroups, pair.Verdict.BlockGroups);
                Assert.Equal(row.Verdict.WarningGroups, pair.Verdict.WarningGroups);
                compared++;
            }

            Assert.Equal(["Closed for Founders' Day: 1 occurrence, 2027-03-10"], candidates[records["ada"]].Verdict.BlockGroups.Select(group => group.Text));
            Assert.Equal(["Closed for Founders' Day: 1 occurrence, 2027-03-10", "Closed for West study day: 1 occurrence, 2027-03-12"], candidates[records["wes"]].Verdict.BlockGroups.Select(group => group.Text).Order(StringComparer.Ordinal));
            Assert.Equal(1, candidates[records["cy"]].Verdict.BlockedOccurrences);
        }

        Assert.Equal(2 * 3, compared);
        Assert.NotEmpty(bookings.ToStaff);
        Assert.All(bookings.ToStaff, occurrence => Assert.Equal(("None of 3 free", 0), (occurrence.FreeText, occurrence.Free)));

        var rows = staffMembers.StaffMembers.ToDictionary(staff => staff.ResourceId);
        Assert.All(rows.Values, staff => Assert.Equal(["Founders' Day"], staff.Days.Single(day => day.Date == Wednesday).Closed));
        Assert.Equal(["West study day"], rows[records["wes"]].Days.Single(day => day.Date == friday).Closed);
        Assert.Equal("Closed for West study day", rows[records["wes"]].Days.Single(day => day.Date == friday).ClosedText);
        Assert.Empty(rows[records["ada"]].Days.Single(day => day.Date == friday).Closed);
        Assert.False(rows[records["cy"]].Days.Single(day => day.Date == friday).IsClosed);
    }

    [Fact]
    public async Task The_board_and_the_staffing_dialog_agree_on_a_public_holiday_which_blocks_every_staff_member_only_while_the_tenant_is_closed_on_its_holidays()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Holidays", Ct);
        var from = new DateOnly(2026, 11, 2);
        var (ada, bo) = (await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", from, Ct), await scenario.Absence.AddWorkingStaffAsync(this.database, "Bo", from, Ct));
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead"],
            AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(9, 0), new TimeOnly(11, 0)),
            AssignmentScenario.Occurrence(Weekday.Friday, new TimeOnly(9, 0), new TimeOnly(11, 0)));
        var goodFriday = new DateOnly(2027, 3, 26);
        var week = new DateOnly(2027, 3, 22);
        var records = new Dictionary<string, Guid> { ["ada"] = await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), ["bo"] = await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct) };

        async Task<(Dictionary<Guid, StaffingCandidateRow> Candidates, Dictionary<Guid, BoardStaff> Rows)> AgreeAsync()
        {
            await using var host = scenario.HostOf(this.database, scenario.Coordinator);
            var (staffMembers, bookings) = await ReadAsync(host, week, new BoardFilter());
            var page = await host.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(algebra[0], new ListQuery { PageSize = 100 }, Ct);
            Assert.True(page.IsSuccess, page.Error);
            var candidates = page.Value!.Rows.ToDictionary(row => row.ResourceId);
            Assert.Equal(staffMembers.StaffMembers.Select(staff => staff.ResourceId).ToHashSet(), candidates.Keys.ToHashSet());
            foreach (var (id, row) in candidates)
            {
                var pair = bookings.Pair(algebra[0], id)!;
                Assert.Equal((row.Verdict.Summary, row.Verdict.IsBlocked, row.Verdict.Occurrences, row.Verdict.BlockedOccurrences), (pair.Verdict.Summary, pair.Verdict.IsBlocked, pair.Verdict.Occurrences, pair.Verdict.BlockedOccurrences));
                Assert.Equal(row.Verdict.BlockGroups, pair.Verdict.BlockGroups);
            }

            return (candidates, staffMembers.StaffMembers.ToDictionary(staff => staff.ResourceId));
        }

        var open = await AgreeAsync();
        Assert.All(records.Values, staff => Assert.False(open.Candidates[staff].Verdict.IsBlocked));
        Assert.All(records.Values, staff => Assert.False(open.Rows[staff].Days.Single(day => day.Date == goodFriday).IsClosed));

        await SaveClosedOnHolidaysAsync(scenario, true);
        var closed = await AgreeAsync();
        foreach (var staff in records.Values)
        {
            Assert.True(closed.Candidates[staff].Verdict.IsBlocked);
            Assert.Equal(["Closed for Good Friday: 1 occurrence, 2027-03-26"], closed.Candidates[staff].Verdict.BlockGroups.Select(group => group.Text));
            Assert.Equal(["Good Friday"], closed.Rows[staff].Days.Single(day => day.Date == goodFriday).Closed);
            Assert.Equal("Closed for Good Friday", closed.Rows[staff].Days.Single(day => day.Date == goodFriday).ClosedText);
        }

        var refused = await scenario.AssignAsync(this.database, scenario.Coordinator, algebra[0], ada, Ct);
        Assert.False(refused.IsSuccess);
        Assert.Contains("Closed for Good Friday", refused.Error, StringComparison.Ordinal);

        await SaveClosedOnHolidaysAsync(scenario, false);
        var reopened = await AgreeAsync();
        Assert.All(records.Values, staff => Assert.False(reopened.Candidates[staff].Verdict.IsBlocked));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, algebra[0], ada, Ct));
    }

    [Fact]
    public async Task Mid_period_across_the_clock_change_the_board_and_the_staffing_dialog_agree_on_the_remaining_occurrences_and_the_free_count()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Mid Period", Ct);
        var periodStart = new DateOnly(2027, 3, 1);
        var periodEnd = new DateOnly(2027, 4, 30);
        var from = new DateOnly(2026, 11, 2);
        var (ada, bo, cy, eve) = (await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", from, Ct), await scenario.Absence.AddWorkingStaffAsync(this.database, "Bo", from, Ct), await scenario.Absence.AddWorkingStaffAsync(this.database, "Cy", from, Ct), await scenario.Absence.AddWorkingStaffAsync(this.database, "Eve", from, Ct));
        var dee = await scenario.AddStaffAsync(this.database, "Dee", Ct);
        var monday = AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0));
        var wednesday = AssignmentScenario.Occurrence(Weekday.Wednesday, new TimeOnly(13, 0), new TimeOnly(14, 0));
        var friday = AssignmentScenario.Occurrence(Weekday.Friday, new TimeOnly(15, 0), new TimeOnly(16, 0));
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", periodStart, periodEnd, Ct, ["Lead", "Assistant"], monday, wednesday, friday);

        var (_, biology) = await scenario.StartedJobAsync(this.database, "Biology", periodStart, periodEnd, Ct, ["Staff"], AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(9, 0), new TimeOnly(10, 0)));
        var (_, drama) = await scenario.StartedJobAsync(this.database, "Drama", new DateOnly(2027, 3, 29), periodEnd, Ct, ["Staff"], AssignmentScenario.Occurrence(Weekday.Friday, new TimeOnly(15, 30), new TimeOnly(16, 30)));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, biology[0], ada, Ct));
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, drama[0], cy, Ct));
        await using (var sickHost = scenario.HostOf(this.database, bo))
        {
            Made(await AbsenceScenario.ReportAsync(sickHost.Get<ResourceAbsenceService>(), bo, AbsenceKind.SickLeave, new DateOnly(2027, 4, 2), new DateOnly(2027, 4, 2), Ct));
        }

        var midPeriod = new AnchoredClock(new DateTimeOffset(2027, 3, 24, 12, 30, 0, TimeSpan.Zero));
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Coordinator), services => services.AddSingleton<TimeProvider>(midPeriod));
        var (staffMembers, bookings) = await ReadAsync(host, new DateOnly(2027, 3, 24), new BoardFilter());
        Assert.Equal(new DateOnly(2027, 3, 22), staffMembers.Monday);
        Assert.Equal(new DateOnly(2027, 3, 24), staffMembers.Today);

        Assert.Equal(
            [(new DateOnly(2027, 3, 24), "13:00-14:00"), (new DateOnly(2027, 3, 26), "15:00-16:00")],
            bookings.ToStaff.Where(occurrence => occurrence.SlotId == algebra[0]).Select(occurrence => (occurrence.Date, occurrence.Times)));
        Assert.Equal(new HashSet<Guid> { algebra[0], algebra[1] }, bookings.ToStaff.Select(occurrence => occurrence.SlotId).Distinct().ToList().ToHashSet());

        var records = new Dictionary<string, Guid>();
        foreach (var (name, user) in new[] { ("ada", ada), ("bo", bo), ("cy", cy), ("dee", dee), ("eve", eve) })
        {
            records[name] = await AssignmentScenario.ResourceOfAsync(this.database, user, Ct);
        }

        foreach (var slot in algebra)
        {
            var page = await host.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slot, new ListQuery { PageSize = 100 }, Ct);
            Assert.True(page.IsSuccess, page.Error);
            var candidates = page.Value!.Rows.ToDictionary(row => row.ResourceId);
            Assert.Equal(staffMembers.StaffMembers.Select(staff => staff.ResourceId).ToHashSet(), candidates.Keys.ToHashSet());
            foreach (var (id, row) in candidates)
            {
                var pair = bookings.Pair(slot, id)!;
                Assert.False(pair.Asked);
                Assert.Equal((row.Verdict.Occurrences, row.Verdict.BlockedOccurrences, row.Verdict.WarnedOccurrences, row.Verdict.Summary), (pair.Verdict.Occurrences, pair.Verdict.BlockedOccurrences, pair.Verdict.WarnedOccurrences, pair.Verdict.Summary));
                Assert.Equal(row.Verdict.BlockGroups, pair.Verdict.BlockGroups);
                Assert.Equal(row.Verdict.WarningGroups, pair.Verdict.WarningGroups);
            }

            var occurrence = bookings.ToStaff.First(occurrence => occurrence.SlotId == slot);
            Assert.Equal((candidates.Values.Count(row => !row.Verdict.IsBlocked), candidates.Count), (occurrence.Free, occurrence.Judged));
        }

        var judged = bookings.Pair(algebra[0], records["eve"])!;
        Assert.Equal((17, 0, 0), (judged.Verdict.Occurrences, judged.Verdict.BlockedOccurrences, judged.Verdict.WarnedOccurrences));
        Assert.Equal(5, bookings.Pair(algebra[0], records["ada"])!.Verdict.BlockedOccurrences);
        Assert.Equal(1, bookings.Pair(algebra[0], records["bo"])!.Verdict.BlockedOccurrences);
        Assert.Equal(5, bookings.Pair(algebra[0], records["cy"])!.Verdict.WarnedOccurrences);
        Assert.Equal(17, bookings.Pair(algebra[0], records["dee"])!.Verdict.WarnedOccurrences);
        Assert.Equal(("3 of 5 free", 3), (bookings.ToStaff[0].FreeText, bookings.ToStaff[0].Free));
    }

    [Fact]
    public async Task Both_steps_read_a_fixed_number_of_queries_for_5_and_for_60_staff_members()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Counted", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", new DateOnly(2026, 11, 2), Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        await using (var staff = scenario.HostOf(this.database, ada))
        {
            Made(await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), ada, AbsenceKind.SickLeave, Monday, Monday, Ct));
        }

        for (var index = 1; index < 5; index++)
        {
            await scenario.AddStaffAsync(this.database, $"Few{index:000}", Ct);
        }

        var few = await CommandsOfAsync(scenario, 5);
        for (var index = 5; index < 60; index++)
        {
            await scenario.AddStaffAsync(this.database, $"Many{index:000}", Ct);
        }

        var many = await CommandsOfAsync(scenario, 60);
        Assert.Equal(few, many);
        Assert.InRange(few.StaffMembers, 1, 14);
        Assert.InRange(few.Bookings, 1, 7);
    }

    [Fact]
    public async Task A_board_of_60_staff_members_reads_and_judges_one_page_at_a_time_by_name_with_the_filters_parity_and_query_count_intact()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Paged", Ct);
        Guid physics;
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            physics = Made(await coordinator.Get<QualificationService>().CreateQualificationAsync(new Qualification { Name = "Physics" }, QualificationWorkflowAttribute.CreateStep, Ct));
        }

        var users = new List<Guid>();
        for (var index = 0; index < 60; index++)
        {
            users.Add(await scenario.AddStaffAsync(this.database, $"Paged{index:000}", Ct));
        }

        await using (var keeper = scenario.HostOf(this.database, scenario.Coordinator))
        {
            Made(await keeper.Get<QualificationService>().GiveQualificationAsync(users[30], physics, Ct));
            Made(await keeper.Get<QualificationService>().GiveQualificationAsync(users[55], physics, Ct));
        }

        var (_, slots) = await JobAsync(scenario, scenario.Customer, "Algebra", ("Lead", [physics]));
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], users[31], Ct));
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var board = host.Get<CoordinationService>();
        var snapshot = (await host.Get<CoordinationService>().ReadAsync(Ct)).Value!;

        async Task<(BoardStaffMembers StaffMembers, BoardBookings Bookings)> PageAsync(int page, BoardFilter filter)
        {
            var staffMembers = await board.ReadStaffMembersAsync(Monday, filter, page, Ct);
            Assert.True(staffMembers.IsSuccess, staffMembers.Error);
            var bookings = await board.ReadBookingsAsync(staffMembers.Value!, snapshot, Ct);
            Assert.True(bookings.IsSuccess, bookings.Error);
            return (staffMembers.Value!, bookings.Value!);
        }

        var (first, firstBookings) = await PageAsync(1, new BoardFilter());
        Assert.Equal((1, 60, 3, 1), (first.Page, first.TotalStaffMembers, first.PageCount, first.FirstShown));
        Assert.Equal(Enumerable.Range(0, 25).Select(index => $"Paged{index:000} Example"), first.StaffMembers.Select(staff => staff.Name));
        var record30 = await AssignmentScenario.ResourceOfAsync(this.database, users[30], Ct);
        Assert.Null(firstBookings.Pair(slots[0], record30));
        Assert.Equal((25, 25), (firstBookings.ToStaff[0].Free, firstBookings.ToStaff[0].Judged));

        var (second, secondBookings) = await PageAsync(2, new BoardFilter());
        Assert.Equal(Enumerable.Range(25, 25).Select(index => $"Paged{index:000} Example"), second.StaffMembers.Select(staff => staff.Name));
        Assert.Equal(26, second.FirstShown);
        Assert.All(first.StaffMembers, staff => Assert.Null(secondBookings.Pair(slots[0], staff.ResourceId)));
        var candidates = new Dictionary<Guid, StaffingCandidateRow>();
        for (var number = 1; ; number++)
        {
            var read = (await host.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slots[0], new ListQuery { PageSize = 50, Page = number }, Ct)).Value!;
            foreach (var candidate in read.Rows)
            {
                candidates[candidate.ResourceId] = candidate;
            }

            if (number >= read.PageCount)
            {
                break;
            }
        }

        Assert.Equal(59, candidates.Count);
        foreach (var staff in second.StaffMembers)
        {
            var pair = secondBookings.Pair(slots[0], staff.ResourceId)!;
            if (!candidates.TryGetValue(staff.ResourceId, out var row))
            {
                Assert.True(pair.Asked);
                continue;
            }

            Assert.Equal((row.Verdict.Summary, row.Verdict.IsBlocked, row.Match.MeetsAll), (pair.Verdict.Summary, pair.Verdict.IsBlocked, pair.Match.MeetsAll));
        }

        Assert.True(secondBookings.Pair(slots[0], await AssignmentScenario.ResourceOfAsync(this.database, users[31], Ct))!.Asked);
        Assert.True(secondBookings.Pair(slots[0], record30)!.Match.MeetsAll);
        Assert.Equal(24, secondBookings.ToStaff[0].Judged);

        var (third, _) = await PageAsync(3, new BoardFilter());
        Assert.Equal(10, third.StaffMembers.Count);
        var (past, _) = await PageAsync(9, new BoardFilter());
        Assert.Equal(3, past.Page);
        Assert.Equal(third.StaffMembers.Select(staff => staff.ResourceId), past.StaffMembers.Select(staff => staff.ResourceId));

        var (searched, _) = await PageAsync(2, new BoardFilter("Paged05"));
        Assert.Equal((1, 10), (searched.Page, searched.TotalStaffMembers));
        Assert.Equal(Enumerable.Range(50, 10).Select(index => $"Paged{index:000} Example"), searched.StaffMembers.Select(staff => staff.Name));

        await PageAsync(1, new BoardFilter());
        host.ClearCommandLog();
        await PageAsync(1, new BoardFilter());
        var onFirst = host.CommandLog.Count;
        host.ClearCommandLog();
        await PageAsync(3, new BoardFilter());
        Assert.Equal(onFirst, host.CommandLog.Count);
    }

    [Fact]
    public async Task The_name_search_narrows_the_staff_members_the_order_runs_them_Z_to_A_and_every_open_slot_stays_to_staff()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Name Search", Ct);
        await scenario.AddStaffAsync(this.database, "Ada", Ct);
        await scenario.AddStaffAsync(this.database, "Bo", Ct);
        await scenario.AddStaffAsync(this.database, "Cy", Ct);
        var other = Guid.Empty;
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            other = Made(await coordinator.Get<CustomerService>().CreateCustomerAsync(new Customer { Name = "Riverside College" }, CustomerWorkflowAttribute.ActivateStep, Ct));
        }

        var (_, algebra) = await JobAsync(scenario, scenario.Customer, "Algebra", ("Lead", []), ("Assistant", []));
        var (_, drama) = await JobAsync(scenario, other, "Drama", ("Director", []));
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);

        var (all, allBookings) = await ReadAsync(host, Monday, new BoardFilter());
        Assert.Equal(["Ada Example", "Bo Example", "Cy Example"], all.StaffMembers.Select(staff => staff.Name));
        Assert.True(all.Filter.IsEmpty);
        Assert.Equal(new HashSet<Guid> { algebra[0], algebra[1], drama[0] }, allBookings.ToStaff.Select(occurrence => occurrence.SlotId).Distinct().ToList().ToHashSet());

        var (found, foundBookings) = await ReadAsync(host, Monday, new BoardFilter("  bo EX "));
        Assert.Equal(["Bo Example"], found.StaffMembers.Select(staff => staff.Name));
        Assert.Equal((1, "bo EX"), (found.TotalStaffMembers, found.Filter.NameText));
        Assert.False(found.Filter.IsEmpty);
        Assert.Equal(new HashSet<Guid> { algebra[0], algebra[1], drama[0] }, foundBookings.ToStaff.Select(occurrence => occurrence.SlotId).Distinct().ToList().ToHashSet());

        var (reversed, _) = await ReadAsync(host, Monday, new BoardFilter(NameDescending: true));
        Assert.Equal(["Cy Example", "Bo Example", "Ada Example"], reversed.StaffMembers.Select(staff => staff.Name));
        var (nobody, nobodyBookings) = await ReadAsync(host, Monday, new BoardFilter("Zed"));
        Assert.Empty(nobody.StaffMembers);
        Assert.Equal(3, nobodyBookings.ToStaff.Select(occurrence => occurrence.SlotId).Distinct().ToList().Count);
    }

    [Fact]
    public async Task A_staff_members_unanswered_question_raises_the_slot_in_the_priority_list_and_marks_it_until_a_coordinator_replies()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Question", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead"]);
        var assignment = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        var adaRecord = await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct);
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);

        async Task<(CoordinationSlotRow Row, BoardOccurrence Bar, PriorityBoard Dock)> ReadAllAsync()
        {
            var snapshot = (await host.Get<CoordinationService>().ReadAsync(Ct)).Value!;
            var (_, bookings) = await ReadAsync(host, Monday, new BoardFilter());
            var dock = (await host.Get<CoordinationService>().ReadPrioritiesAsync(snapshot, new PriorityQuery(), Ct)).Value!;
            return (snapshot.All.Single(row => row.SlotId == slots[0]), bookings.OccurrencesOf(adaRecord, Monday).Single(), dock);
        }

        var (before, beforeBar, beforeDock) = await ReadAllAsync();
        Assert.Equal(PriorityKind.Covered, before.Kind);
        Assert.False(beforeBar.AwaitsReply);
        Assert.DoesNotContain(beforeDock.Items, item => item.Key == $"message-{assignment}");

        await using (var teaching = scenario.HostOf(this.database, ada))
        {
            Assert.True((await teaching.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, Staffora.Core.Entities.Comments.CommentAudience.Staff, "Is the room on the ground floor?", Ct)).IsSuccess);
        }

        var (asked, askedBar, askedDock) = await ReadAllAsync();
        Assert.Equal(PriorityKind.AwaitsReply, asked.Kind);
        Assert.Equal(["Ada Example wrote and waits for your reply."], asked.Reasons);
        Assert.True(askedBar.AwaitsReply);
        Assert.Contains(askedDock.Items, item => item.Kind == PriorityItemKind.StaffQuestions && item.Key == $"message-{assignment}" && item.Target == PriorityTarget.Job && item.TargetId == job);

        Assert.True((await host.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, Staffora.Core.Entities.Comments.CommentAudience.Staff, "Yes, room 4.", Ct)).IsSuccess);
        var (answered, answeredBar, answeredDock) = await ReadAllAsync();
        Assert.Equal(PriorityKind.Covered, answered.Kind);
        Assert.False(answeredBar.AwaitsReply);
        Assert.DoesNotContain(answeredDock.Items, item => item.Key == $"message-{assignment}");
    }

    [Fact]
    public async Task Only_a_coordinator_of_the_tenant_reads_the_board_and_another_tenants_board_shows_nothing_of_it()
    {
        var first = await AssignmentScenario.CreateAsync(this.database, "Board First", Ct);
        var second = await AssignmentScenario.CreateAsync(this.database, "Board Second", Ct);
        var ada = await first.AddStaffAsync(this.database, "Ada", Ct);
        await first.StartedJobAsync(this.database, "First algebra", PeriodStart, PeriodEnd, Ct);
        await second.AddStaffAsync(this.database, "Zed", Ct);

        await using (var staff = first.HostOf(this.database, ada))
        {
            Assert.Equal(CoordinationService.NoView, (await staff.Get<CoordinationService>().ReadStaffMembersAsync(Monday, new BoardFilter(), Ct)).Error);
        }

        await using (var console = ServiceHost.Create(this.database, first.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime(), AssignmentScenario.UseClock))
        {
            Assert.False((await console.Get<CoordinationService>().ReadStaffMembersAsync(Monday, new BoardFilter(), Ct)).IsSuccess);
        }

        await using var firstHost = first.HostOf(this.database, first.Coordinator);
        await using var secondHost = second.HostOf(this.database, second.Coordinator);
        var (mine, myBookings) = await ReadAsync(firstHost, Monday, new BoardFilter());
        var (theirs, theirBookings) = await ReadAsync(secondHost, Monday, new BoardFilter());
        Assert.Equal(["Ada Example"], mine.StaffMembers.Select(staff => staff.Name));
        Assert.Equal(["Zed Example"], theirs.StaffMembers.Select(staff => staff.Name));
        Assert.Single(myBookings.ToStaff.Select(occurrence => occurrence.SlotId).Distinct().ToList());
        Assert.Empty(theirBookings.ToStaff);

        var snapshot = (await firstHost.Get<CoordinationService>().ReadAsync(Ct)).Value!;
        Assert.Equal(CoordinationService.NotThisTenant, (await secondHost.Get<CoordinationService>().ReadBookingsAsync(mine, snapshot, Ct)).Error);
        var ownSnapshot = (await secondHost.Get<CoordinationService>().ReadAsync(Ct)).Value!;
        Assert.Equal(CoordinationService.NotThisTenant, (await secondHost.Get<CoordinationService>().ReadBookingsAsync(mine, ownSnapshot, Ct)).Error);
        Assert.Equal(CoordinationService.NotThisTenant, (await secondHost.Get<CoordinationService>().ReadBookingsAsync(theirs, snapshot, Ct)).Error);

        var quiet = (await firstHost.Get<CoordinationService>().ReadStaffMembersAsync(new DateOnly(2030, 1, 7), new BoardFilter(), Ct)).Value!;
        await using (var staff = first.HostOf(this.database, ada))
        {
            Assert.Equal(CoordinationService.NoView, (await staff.Get<CoordinationService>().ReadBookingsAsync(mine, snapshot, Ct)).Error);
            Assert.Equal(CoordinationService.NoView, (await staff.Get<CoordinationService>().ReadBookingsAsync(quiet, snapshot, Ct)).Error);
        }

        Assert.Empty((await secondHost.Get<CoordinationService>().ReadStaffMembersAsync(Monday, new BoardFilter("Ada"), Ct)).Value!.StaffMembers);

        var morgan = await first.StaffMembers.AddPersonAsync(this.database, "Morgan", Ct);
        await TwoTenantScenario.GrantAsync(this.database, first.StaffMembers.Onboarded.Tenant, morgan, Ct, RolePermission.ViewCoordination);
        await TwoTenantScenario.GrantAsync(this.database, first.StaffMembers.Onboarded.Tenant, ada, Ct, RolePermission.ViewCoordination, RolePermission.ManageResourceAbsence);
        await using (var viewer = first.HostOf(this.database, morgan))
        {
            var (seen, _) = await ReadAsync(viewer, Monday, new BoardFilter());
            Assert.False(seen.MayAddAbsence);
            Assert.All(seen.StaffMembers, staff => Assert.False(staff.IsReader));
        }

        await using (var teaching = first.HostOf(this.database, ada))
        {
            var (own, _) = await ReadAsync(teaching, Monday, new BoardFilter());
            Assert.True(own.MayAddAbsence);
            Assert.True(Assert.Single(own.StaffMembers).IsReader);
        }
    }

    private async Task SaveClosedOnHolidaysAsync(AssignmentScenario scenario, bool closed)
    {
        await using var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime());
        var settings = owner.Get<TenantSettingsService>();
        var loaded = await settings.LoadAsync(Ct);
        Assert.True(loaded.IsSuccess, loaded.Error);
        var saved = await settings.SaveAsync(TenantSettingDefinitions.ClosedOnPublicHolidays.Key, TenantSettingDefinitions.FormatSwitch(closed), loaded.Value!.Of(TenantSettingDefinitions.ClosedOnPublicHolidays).Version, Ct);
        Assert.True(saved.IsSuccess, saved.Error);
    }

    private async Task<(BoardStaffMembers StaffMembers, BoardBookings Bookings)> ReadAsync(ServiceHost host, DateOnly week, BoardFilter filter)
    {
        var board = host.Get<CoordinationService>();
        var staffMembers = await board.ReadStaffMembersAsync(week, filter, Ct);
        Assert.True(staffMembers.IsSuccess, staffMembers.Error);
        var snapshot = await host.Get<CoordinationService>().ReadAsync(Ct);
        Assert.True(snapshot.IsSuccess, snapshot.Error);
        var bookings = await board.ReadBookingsAsync(staffMembers.Value!, snapshot.Value!, Ct);
        Assert.True(bookings.IsSuccess, bookings.Error);
        return (staffMembers.Value!, bookings.Value!);
    }

    private async Task<(int StaffMembers, int Bookings)> CommandsOfAsync(AssignmentScenario scenario, int expected)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var board = host.Get<CoordinationService>();
        await ReadAsync(host, Monday, new BoardFilter());
        var snapshot = (await host.Get<CoordinationService>().ReadAsync(Ct)).Value!;
        host.ClearCommandLog();
        var staffMembers = await board.ReadStaffMembersAsync(Monday, new BoardFilter(), Ct);
        Assert.True(staffMembers.IsSuccess, staffMembers.Error);
        Assert.Equal((Math.Min(expected, BoardRules.PageSize), expected), (staffMembers.Value!.StaffMembers.Count, staffMembers.Value.TotalStaffMembers));
        var first = host.CommandLog.Count;
        host.ClearCommandLog();
        var bookings = await board.ReadBookingsAsync(staffMembers.Value, snapshot, Ct);
        Assert.True(bookings.IsSuccess, bookings.Error);
        Assert.NotEmpty(bookings.Value!.ToStaff);
        Assert.NotEmpty(bookings.Value.OccurrencesOf(staffMembers.Value.StaffMembers[0].ResourceId, Monday));
        return (first, host.CommandLog.Count);
    }

    private async Task<(Guid Job, Guid[] Slots)> JobAsync(AssignmentScenario scenario, Guid customer, string name, params (string Slot, Guid[] Requires)[] slots)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();
        var job = Made(await jobs.CreateJobAsync(new Job
        {
            Name = name,
            CustomerId = customer,
            OrganizationUnitId = scenario.StaffMembers.RootId,
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            TimeSlots = [AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0))],
        }, JobWorkflowAttribute.RegisterStep, Ct));
        var ids = new List<Guid>();
        foreach (var (slot, requires) in slots)
        {
            ids.Add(Made(await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = slot, Qualifications = [.. requires.Select(qualification => new JobSlotQualification { QualificationId = qualification })] }, JobSlotWorkflowAttribute.AddStep, Ct)));
        }

        Made(await AssignmentScenario.StepJobAsync(jobs, job, JobWorkflowAttribute.StartFromRegistrationStep, Ct));
        return (job, [.. ids]);
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private static void Made(Result done) => Assert.True(done.IsSuccess, done.Error);
}
