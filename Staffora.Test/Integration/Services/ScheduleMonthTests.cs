using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Scheduling;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class ScheduleMonthTests : IClassFixture<RealDatabase>
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly RealDatabase database;

    public ScheduleMonthTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_month_holds_every_day_in_three_layers_with_its_notes_and_hours_and_never_a_request()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Month Layers", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", new DateOnly(2027, 1, 4), Ct);

        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", new DateOnly(2027, 2, 1), new DateOnly(2027, 3, 10), Ct, null,
            AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)),
            AssignmentScenario.Occurrence(Weekday.Wednesday, new TimeOnly(10, 0), new TimeOnly(11, 0)));
        var (_, physics) = await scenario.StartedJobAsync(this.database, "Physics", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), Ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(10, 0), new TimeOnly(11, 0)));
        var (_, chemistry) = await scenario.StartedJobAsync(this.database, "Chemistry", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), Ct, null, AssignmentScenario.Occurrence(Weekday.Thursday, new TimeOnly(12, 0), new TimeOnly(13, 0)));
        var assigned = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, algebra[0], ada, Ct));
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, physics[0], ada, Ct));
        var yes = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, chemistry[0], ada, Ct));
        Assert.True((await scenario.StepAsync(this.database, ada, yes, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);

        await using (var host = scenario.HostOf(this.database, ada))
        {
            Made(await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), ada, AbsenceKind.SickLeave, new DateOnly(2027, 3, 3), new DateOnly(2027, 3, 3), Ct, start: new TimeOnly(13, 0)));
            Made(await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), ada, AbsenceKind.PlannedLeave, new DateOnly(2027, 3, 15), new DateOnly(2027, 3, 17), Ct));
        }

        var month = await MonthAsync(scenario, ada, null, new DateOnly(2027, 3, 20));

        Assert.Equal(new DateOnly(2027, 3, 1), month.First);
        Assert.Equal(Enumerable.Range(0, 31).Select(offset => new DateOnly(2027, 3, 1).AddDays(offset)), month.Days.Select(day => day.Date));
        Assert.Equal([new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 8), new DateOnly(2027, 3, 15), new DateOnly(2027, 3, 22), new DateOnly(2027, 3, 29)], month.Weeks.Select(week => week.Monday));
        Assert.Equal(3, month.Weeks[^1].Days.Count);
        Assert.All(month.Weeks, week => Assert.Null(week.PatternWeek));

        var days = month.Days.ToDictionary(day => day.Date);
        Assert.Equal("08:00–16:00", days[new DateOnly(2027, 3, 1)].HoursLabel);
        Assert.Empty(days[new DateOnly(2027, 3, 6)].Working);
        Assert.Equal([(assigned, "08:00–09:30", "Brightpath Learning")], days[new DateOnly(2027, 3, 1)].Occurrences.Select(occurrence => (occurrence.AssignmentId, occurrence.TimesLabel, occurrence.Customer)));
        Assert.All(month.Days.SelectMany(day => day.Occurrences), occurrence => Assert.Equal(SlotAssignmentState.Assigned, occurrence.State));
        Assert.Equal(["Algebra"], month.Days.SelectMany(day => day.Occurrences).Select(occurrence => occurrence.Job).Distinct());
        Assert.Empty(days[new DateOnly(2027, 3, 15)].Occurrences);

        Assert.Equal([("From 13:00", ResourceAbsenceState.Requested)], days[new DateOnly(2027, 3, 3)].Absences.Select(absence => (absence.TimesLabel, absence.State)));
        Assert.Equal(["All day", "All day", "All day"], month.Days.Where(day => day.Date.Day is >= 15 and <= 17).SelectMany(day => day.Absences).Select(absence => absence.TimesLabel));

        Assert.Equal(["Sick leave starts"], days[new DateOnly(2027, 3, 3)].Notes);
        Assert.Equal(["Last time on Algebra"], days[new DateOnly(2027, 3, 10)].Notes);
        Assert.Equal(["Planned leave starts, until 17 Mar"], days[new DateOnly(2027, 3, 15)].Notes);
        Assert.Equal(3, month.Days.Count(day => day.Notes.Count > 0));

        var hours = month.Hours;
        Assert.Equal(("184 h", "5 h", "27 h", "152 h"), (hours.WorkingLabel, hours.AssignmentLabel, hours.AbsenceLabel, hours.OpenLabel));

        Assert.Equal(("Weekdays of Ada", new DateOnly(2027, 1, 4), (DateOnly?)null, true), (month.Pattern!.Name, month.Pattern.From, month.Pattern.To, month.Pattern.Confirmed));
        Assert.Null(month.NextPattern);
        Assert.Null(month.PreviousPattern);

        var current = await MonthAsync(scenario, ada, null, null);
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 9), true), (current.First, current.Today, current.IsCurrent));
    }

    [Fact]
    public async Task A_month_names_the_pattern_its_rotation_the_next_and_the_one_before_and_the_days_they_change()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Month Patterns", Ct);
        var ada = await scenario.Absence.AddStaffAsync(this.database, "Ada", Ct);

        var day = await PatternAsync(scenario, "Day cleaning", 1, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(16, 0)));
        var split = await PatternAsync(scenario, "Split rotation", 2,
            [.. Enumerable.Range(0, 5).Select(index => new PatternSegment(index, new TimeOnly(6, 0), new TimeOnly(10, 0))),
             .. Enumerable.Range(7, 4).Select(index => new PatternSegment(index, new TimeOnly(6, 0), new TimeOnly(10, 0))),
             new PatternSegment(12, new TimeOnly(8, 0), new TimeOnly(11, 30))]);
        var winter = await PatternAsync(scenario, "Winter evenings", 1, ScheduleScenario.Weekdays(new TimeOnly(14, 0), new TimeOnly(21, 30)));
        await GiveAsync(scenario, ada, day, new DateOnly(2026, 3, 2), new DateOnly(2026, 8, 30), activate: true);
        await GiveAsync(scenario, ada, split, new DateOnly(2026, 8, 31), new DateOnly(2027, 3, 21), activate: true);
        await GiveAsync(scenario, ada, winter, new DateOnly(2027, 3, 22), null, activate: false);

        var month = await MonthAsync(scenario, ada, null, new DateOnly(2027, 3, 1));

        Assert.Equal(("Split rotation", new DateOnly(2027, 3, 21), 2), (month.Pattern!.Name, month.Pattern.To, month.Pattern.CycleWeeks));
        Assert.Equal(("Winter evenings", false), (month.NextPattern!.Name, month.NextPattern.Confirmed));
        Assert.Equal(("Day cleaning", new DateOnly(2026, 8, 30)), (month.PreviousPattern!.Name, month.PreviousPattern.To));
        Assert.Equal((1200, 1170), (month.Pattern.CycleRows[0].Minutes, month.Pattern.CycleRows[1].Minutes));

        Assert.Equal(new int?[] { 1, 2, 1, null, null }, month.Weeks.Select(week => week.PatternWeek));
        var days = month.Days.ToDictionary(entry => entry.Date);
        Assert.Equal("06:00–10:00", days[new DateOnly(2027, 3, 5)].HoursLabel);
        Assert.Empty(days[new DateOnly(2027, 3, 12)].Working);
        Assert.Equal("08:00–11:30", days[new DateOnly(2027, 3, 13)].HoursLabel);
        Assert.Empty(days[new DateOnly(2027, 3, 22)].Working);

        Assert.Equal(["Pattern week 1 starts"], days[new DateOnly(2027, 3, 1)].Notes);
        Assert.Equal(["Pattern week 2 starts"], days[new DateOnly(2027, 3, 8)].Notes);
        Assert.Equal(["Pattern week 1 starts"], days[new DateOnly(2027, 3, 15)].Notes);
        Assert.Equal(["Work pattern Split rotation ends"], days[new DateOnly(2027, 3, 21)].Notes);
        Assert.Equal(["Work pattern Winter evenings starts, not confirmed yet"], days[new DateOnly(2027, 3, 22)].Notes);

        var august = await MonthAsync(scenario, ada, null, new DateOnly(2026, 8, 31));
        Assert.Equal(["Work pattern Day cleaning ends"], august.Days.Single(entry => entry.Date == new DateOnly(2026, 8, 30)).Notes);
        Assert.Equal(["Work pattern Split rotation starts"], august.Days.Single(entry => entry.Date == new DateOnly(2026, 8, 31)).Notes);
    }

    [Fact]
    public async Task A_coordinator_reads_a_staff_members_month_a_staff_member_is_refused_anothers_and_another_tenants_is_not_found()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Month Readers", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", new DateOnly(2027, 1, 4), Ct);
        var ben = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ben", new DateOnly(2027, 1, 4), Ct);
        var (adaRecord, benRecord) = (await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), await AssignmentScenario.ResourceOfAsync(this.database, ben, Ct));
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), Ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)));
        var assigned = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, algebra[0], ada, Ct));

        var read = await MonthAsync(scenario, scenario.Coordinator, adaRecord, new DateOnly(2027, 3, 1));
        Assert.Equal((adaRecord, ada), (read.ResourceId, read.UserId));
        Assert.Contains(read.Days.SelectMany(day => day.Occurrences), occurrence => occurrence.AssignmentId == assigned);
        var own = await MonthAsync(scenario, ada, null, new DateOnly(2027, 3, 1));
        Assert.Equal(read.Days.SelectMany(day => day.Occurrences).Select(occurrence => occurrence.AssignmentId), own.Days.SelectMany(day => day.Occurrences).Select(occurrence => occurrence.AssignmentId));
        Assert.Equal(read.Hours, own.Hours);

        await using (var host = scenario.HostOf(this.database, scenario.Coordinator))
        {
            Assert.Equal(StaffAvailabilityService.NoOwnSchedule, (await host.Get<StaffAvailabilityService>().ReadMonthAsync(null, null, Ct)).Error);
        }

        await using (var host = scenario.HostOf(this.database, ben))
        {
            var availability = host.Get<StaffAvailabilityService>();
            Assert.True((await availability.ReadMonthAsync(benRecord, null, Ct)).IsSuccess);
            Assert.Equal(StaffAvailabilityService.NoScheduleView, (await availability.ReadMonthAsync(adaRecord, null, Ct)).Error);
            Assert.Equal(StaffAvailabilityService.NoScheduleView, (await availability.ReadMonthAsync(Guid.NewGuid(), null, Ct)).Error);
        }

        var other = await AssignmentScenario.CreateAsync(this.database, "Month Readers Other", Ct);
        var carl = await other.Absence.AddWorkingStaffAsync(this.database, "Carl", new DateOnly(2027, 1, 4), Ct);
        var carlRecord = await AssignmentScenario.ResourceOfAsync(this.database, carl, Ct);
        await using (var host = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var availability = host.Get<StaffAvailabilityService>();
            Assert.Equal(StaffAvailabilityService.StaffNotFound, (await availability.ReadMonthAsync(carlRecord, null, Ct)).Error);
            Assert.Equal(StaffAvailabilityService.StaffNotFound, (await availability.ReadMonthAsync(Guid.NewGuid(), null, Ct)).Error);
            Assert.Equal(StaffAvailabilityService.RangeOutOfCalendar, (await availability.ReadMonthAsync(adaRecord, DateOnly.MinValue, Ct)).Error);
            Assert.Equal(StaffAvailabilityService.RangeOutOfCalendar, (await availability.ReadMonthAsync(adaRecord, DateOnly.MaxValue, Ct)).Error);
        }
    }

    private async Task<OwnScheduleMonth> MonthAsync(AssignmentScenario scenario, Guid reader, Guid? resourceId, DateOnly? day)
    {
        await using var host = scenario.HostOf(this.database, reader);
        var month = await host.Get<StaffAvailabilityService>().ReadMonthAsync(resourceId, day, Ct);
        Assert.True(month.IsSuccess, month.Error);
        return month.Value!;
    }

    private async Task<Guid> PatternAsync(AssignmentScenario scenario, string name, int cycleWeeks, PatternSegment[] segments)
    {
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Absence.Schedule.Admin));
        var patterns = host.Get<WorkPatternService>();
        var draft = new WorkPattern { Name = name, CycleWeeks = cycleWeeks };
        foreach (var segment in segments)
        {
            draft.Segments.Add(new WorkPatternSegment { DayIndex = segment.DayIndex, Start = segment.Start, End = segment.End });
        }

        var created = await patterns.CreateWorkPatternAsync(draft, WorkPatternWorkflowAttribute.SaveStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        var published = await patterns.UpdateWorkPatternAndTransitionAsync((await patterns.LoadWorkPatternUpdaterAsync(created.Value, Ct)).Value!, WorkPatternWorkflowAttribute.PublishStep, null, Ct);
        Assert.True(published.IsSuccess, published.Error);
        return created.Value;
    }

    private async Task GiveAsync(AssignmentScenario scenario, Guid staffUser, Guid pattern, DateOnly from, DateOnly? to, bool activate)
    {
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Absence.Schedule.Admin));
        var assignments = host.Get<PatternAssignmentService>();
        var id = Made(await ScheduleScenario.CreateAssignmentAsync(assignments, staffUser, pattern, from, to, Ct));
        if (activate)
        {
            var activated = await ScheduleScenario.StepAsync(assignments, id, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct);
            Assert.True(activated.IsSuccess, activated.Error);
        }
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }
}
