using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Home;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class WorkPatternCoverageTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly Today = new(2026, 11, 18);
    private static readonly TimeOnly Eight = new(8, 0);
    private static readonly TimeOnly Sixteen = new(16, 0);

    private readonly RealDatabase database;

    public WorkPatternCoverageTests(RealDatabase database)
    {
        this.database = database;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void OnToday(IServiceCollection services) =>
        services.AddSingleton<TimeProvider>(new FakeClock(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero)));

    [Fact]
    public async Task The_staff_list_says_who_works_by_a_pattern_and_filters_and_sorts_by_it_in_the_database()
    {
        var (scenario, staff) = await CoveredScenarioAsync("Pattern Column Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin), OnToday);
        var resources = host.Get<ResourceService>();

        var rows = (await resources.QueryStaffMembersAsync(new ListQuery { PageSize = 100 }, Ct)).Value!.Rows.Where(row => staff.ContainsKey(row.Name)).ToDictionary(row => row.Name, row => row.WorkPatternLabel);
        Assert.Equal("Full time · since 2026-11-02", rows["Kim Example"]);
        Assert.Equal("Full time · ends 2026-11-28", rows["Robin Example"]);
        Assert.Equal("Full time · ends 2026-11-28", rows["Noa Example"]);
        Assert.Equal("From 2026-12-01: Mornings", rows["Sam Example"]);
        Assert.Equal(StaffPatternCover.NoneLabel, rows["Lee Example"]);
        Assert.Equal(StaffPatternCover.NoneLabel, rows["Ida Example"]);
        Assert.Equal(StaffPatternCover.NoneLabel, rows["Out Example"]);

        Assert.Equal(["Ida Example", "Lee Example"], await NamesAsync(resources, StaffList.PatternNone, staff));
        Assert.Equal(["Robin Example"], await NamesAsync(resources, StaffList.PatternEnding, staff));
        Assert.Equal(["Kim Example", "Noa Example", "Robin Example"], await NamesAsync(resources, StaffList.PatternCurrent, staff));

        var sorted = (await resources.QueryStaffMembersAsync(new ListQuery { Sort = StaffList.SortPattern, PageSize = 100 }, Ct)).Value!.Rows.Select(row => row.Name).Where(staff.ContainsKey).ToList();
        Assert.Equal(["Ida Example", "Lee Example", "Out Example", "Kim Example", "Noa Example", "Robin Example", "Sam Example"], sorted);

        Assert.Equal(StaffList.PatternNone, StaffList.Definition.Read(key => key == StaffList.Pattern ? "NONE" : null).Filter(StaffList.Pattern));
    }

    [Fact]
    public async Task The_pattern_list_counts_its_staff_today_and_upcoming_and_its_staff_view_lists_its_assignments()
    {
        var (scenario, staff) = await CoveredScenarioAsync("Pattern Counts Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin), OnToday);
        var patterns = (await host.Get<WorkPatternService>().QueryWorkPatternsAsync(new ListQuery(), Ct)).Value!.Rows.ToDictionary(row => row.Name);

        Assert.Equal((3, 0, "3 today"), (patterns["Full time"].StaffToday, patterns["Full time"].StaffUpcoming, patterns["Full time"].StaffLabel));
        Assert.Equal((0, 2, "2 upcoming"), (patterns["Mornings"].StaffToday, patterns["Mornings"].StaffUpcoming, patterns["Mornings"].StaffLabel));

        var assignments = host.Get<WorkPatternService>().Assignments;
        var fullTime = patterns["Full time"].Id;
        var all = (await assignments.QueryAssignmentsOfPatternAsync(fullTime, new ListQuery { PageSize = 100 }, Ct)).Value!.Rows;
        Assert.Equal(
            [("Ida Example", ResourcePatternAssignmentState.Registration, "Current"), ("Kim Example", ResourcePatternAssignmentState.Active, "Current"), ("Noa Example", ResourcePatternAssignmentState.Active, "Current"), ("Out Example", ResourcePatternAssignmentState.Inactive, "Current"), ("Robin Example", ResourcePatternAssignmentState.Active, "Current")],
            all.Select(row => (row.Staff, row.State, row.WhenLabel)).OrderBy(row => row.Staff, StringComparer.Ordinal));
        var upcoming = (await assignments.QueryAssignmentsOfPatternAsync(patterns["Mornings"].Id, new ListQuery().WithFilter(WorkPatternStaffList.When, WorkPatternStaffList.WhenUpcoming), Ct)).Value!.Rows;
        Assert.Equal(["Noa Example", "Sam Example"], upcoming.Select(row => row.Staff).Order(StringComparer.Ordinal));
        var active = (await assignments.QueryAssignmentsOfPatternAsync(fullTime, new ListQuery { Search = "Robin" }.WithFilter(WorkPatternStaffList.Status, "active"), Ct)).Value!.Rows;
        Assert.Equal([("Robin Example", "2026-10-01 to 2026-11-28")], active.Select(row => (row.Staff, row.PeriodLabel)));

        var reader = await scenario.StaffMembers.AddPersonAsync(this.database, "Reader", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, reader, Ct, RolePermission.ResourcesView);
        await using (var readerHost = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(reader), OnToday))
        {
            Assert.Equal(5, (await readerHost.Get<PatternAssignmentService>().QueryAssignmentsOfPatternAsync(fullTime, new ListQuery(), Ct)).Value!.TotalCount);
        }

        var nobody = await scenario.StaffMembers.AddPersonAsync(this.database, "Nobody", Ct);
        await using (var nobodyHost = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(nobody), OnToday))
        {
            Assert.Equal(PatternAssignmentService.NoView, (await nobodyHost.Get<PatternAssignmentService>().QueryAssignmentsOfPatternAsync(fullTime, new ListQuery(), Ct)).Error);
        }

        var other = await ScheduleScenario.CreateAsync(this.database, "Other Pattern Counts Staffing", Ct);
        await using (var otherHost = ServiceHost.Create(this.database, other.StaffMembers.Onboarded.RuntimeOf(other.Admin), OnToday))
        {
            Assert.Equal(0, (await otherHost.Get<PatternAssignmentService>().QueryAssignmentsOfPatternAsync(fullTime, new ListQuery(), Ct)).Value!.TotalCount);
        }
    }

    [Fact]
    public async Task The_administrators_home_counts_the_staff_without_a_pattern_as_the_staff_lists_filter_finds_them()
    {
        var (scenario, _) = await CoveredScenarioAsync("Pattern Home Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime(), OnToday);

        var home = (await host.Get<TenantHomeService>().ReadAdministrationAsync(Ct)).Value!;
        var none = (await host.Get<ResourceService>().QueryStaffMembersAsync(new ListQuery().WithFilter(StaffList.Pattern, StaffList.PatternNone), Ct)).Value!.TotalCount;
        Assert.Equal(2, home.People!.WithoutWorkPattern);
        Assert.Equal(none, home.People.WithoutWorkPattern);
    }

    [Fact]
    public async Task Assign_to_staff_creates_and_activates_one_assignment_each_with_its_own_history_in_one_change()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Bulk Assign Staffing", Ct);
        var sam = await scenario.AddStaffAsync(this.database, "Sam", Ct);
        var lee = await scenario.AddStaffAsync(this.database, "Lee", Ct);
        var evenings = await scenario.PublishPatternAsync(this.database, "Evenings", Ct, ScheduleScenario.Weekdays(new TimeOnly(16, 0), new TimeOnly(20, 0)));
        var resources = new[] { await ScheduleScenario.ResourceOfAsync(this.database, sam, Ct), await ScheduleScenario.ResourceOfAsync(this.database, lee, Ct) };
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin), OnToday);
        var assignments = host.Get<PatternAssignmentService>();

        var choices = (await assignments.GetStaffChoicesForPatternAsync(evenings, Ct)).Value!;
        Assert.True(choices.Published);
        Assert.Equal(Today, choices.Today);
        Assert.Contains(choices.Staff, choice => choice.Label == "Sam Example (No work pattern)");

        var given = await assignments.AssignPatternToStaffAsync(evenings, resources, Today.AddDays(5), Today.AddDays(100), Ct);
        Assert.True(given.IsSuccess, given.Error);
        Assert.Equal(2, given.Value!.Created.Count);
        Assert.Empty(given.Value.Refused);

        await using var context = this.database.CreateSystemContext();
        var stored = await context.ResourcePatternAssignments.AsNoTracking().Where(row => given.Value.Created.Contains(row.Id)).ToListAsync(Ct);
        Assert.Equal(resources.Order(), stored.Select(row => row.ResourceId).Order());
        Assert.All(stored, row => Assert.Equal((ResourcePatternAssignmentState.Active, Today.AddDays(5), Today.AddDays(100), ScheduleRules.MondayOf(Today.AddDays(5)), scenario.TenantId), (row.State, row.EffectiveFrom, row.EffectiveTo, row.CycleAnchor, row.TenantId)));
        foreach (var id in given.Value.Created)
        {
            Assert.Equal([ResourcePatternAssignmentWorkflowAttribute.SaveStep, ResourcePatternAssignmentWorkflowAttribute.ActivateStep], await ScheduleScenario.HistoryAsync(this.database, id, Ct));
        }

        var correlations = await context.StateHistoryEntries.AsNoTracking()
            .Where(entry => entry.EntityType == nameof(ResourcePatternAssignment) && given.Value.Created.Contains(entry.EntityId))
            .Select(entry => entry.CorrelationId)
            .Distinct()
            .ToListAsync(Ct);
        Assert.Single(correlations);

        var listed = (await host.Get<WorkPatternService>().QueryWorkPatternsAsync(new ListQuery(), Ct)).Value!.Rows.Single(row => row.Id == evenings);
        Assert.Equal((0, 2), (listed.StaffToday, listed.StaffUpcoming));
        var samRow = (await host.Get<ResourceService>().QueryStaffMembersAsync(new ListQuery { Search = "Sam" }, Ct)).Value!.Rows.Single();
        Assert.Equal("From 2026-11-23: Evenings", samRow.WorkPatternLabel);
    }

    [Fact]
    public async Task Assign_to_staff_is_refused_all_or_nothing_naming_each_staff_member_refused_and_why()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Bulk Refusal Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var sam = await scenario.AddStaffAsync(this.database, "Sam", Ct);
        var outOfService = await scenario.AddStaffAsync(this.database, "Out", Ct);
        var fullTime = await scenario.PublishPatternAsync(this.database, "Full time", Ct, ScheduleScenario.Weekdays(Eight, Sixteen));
        var evenings = await scenario.PublishPatternAsync(this.database, "Evenings", Ct, ScheduleScenario.Weekdays(new TimeOnly(16, 0), new TimeOnly(20, 0)));
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin), OnToday);
        var assignments = host.Get<PatternAssignmentService>();
        await ActiveAsync(assignments, kim, fullTime, Today.AddDays(-16), null);
        await TakeStaffRoleAsync(scenario, outOfService);
        var chosen = new[] { kim, sam, outOfService };
        var resources = new List<Guid>();
        foreach (var user in chosen)
        {
            resources.Add(await ScheduleScenario.ResourceOfAsync(this.database, user, Ct));
        }

        var refused = await assignments.AssignPatternToStaffAsync(evenings, resources, Today, null, Ct);
        Assert.False(refused.IsSuccess);
        Assert.StartsWith(PatternAssignmentService.NothingAssigned, refused.Error, StringComparison.Ordinal);
        Assert.Equal(["Kim Example", "Out Example"], refused.Value!.Refused.Select(refusal => refusal.Staff));
        Assert.Contains("It overlaps the active assignment of Full time from 2026-11-02 until further notice", refused.Value.Refused[0].Reason, StringComparison.Ordinal);
        Assert.Contains(ResourcePatternAssignmentWorkflowAttribute.StaffSetUpOpenReason, refused.Value.Refused[1].Reason, StringComparison.Ordinal);
        Assert.Equal(0, await CountOfAsync(evenings));

        Assert.Equal(PatternAssignmentService.StaffOutOfRange, (await assignments.AssignPatternToStaffAsync(evenings, [], Today, null, Ct)).Error);
        Assert.Equal(PatternAssignmentService.StaffOutOfRange, (await assignments.AssignPatternToStaffAsync(evenings, [.. Enumerable.Range(0, PatternAssignmentService.MaxStaffPerAssign + 1).Select(_ => Guid.NewGuid())], Today, null, Ct)).Error);
        Assert.Equal(ScheduleRules.EndsBeforeStart, (await assignments.AssignPatternToStaffAsync(evenings, [resources[1]], Today, Today.AddDays(-1), Ct)).Error);
        var draft = await DraftAsync(scenario, "Nights");
        Assert.Contains(ResourcePatternAssignmentWorkflowAttribute.PatternPublishedReason, (await assignments.AssignPatternToStaffAsync(draft, [resources[1]], Today, null, Ct)).Error, StringComparison.Ordinal);
        Assert.Equal(0, await CountOfAsync(draft));

        Assert.True((await assignments.AssignPatternToStaffAsync(evenings, [resources[1]], Today, null, Ct)).IsSuccess);
        Assert.Equal(1, await CountOfAsync(evenings));
    }

    [Fact]
    public async Task Assign_to_staff_needs_AssignWorkPatterns_and_never_reaches_another_tenant()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Bulk Permission Staffing", Ct);
        var sam = await scenario.AddStaffAsync(this.database, "Sam", Ct);
        var evenings = await scenario.PublishPatternAsync(this.database, "Evenings", Ct, ScheduleScenario.Weekdays(new TimeOnly(16, 0), new TimeOnly(20, 0)));
        var samResource = await ScheduleScenario.ResourceOfAsync(this.database, sam, Ct);

        var reader = await scenario.StaffMembers.AddPersonAsync(this.database, "Reader", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, reader, Ct, RolePermission.ResourcesView, RolePermission.ManageWorkPatterns);
        await using (var readerHost = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(reader), OnToday))
        {
            var readerAssignments = readerHost.Get<PatternAssignmentService>();
            Assert.Equal(PatternAssignmentService.NoAssign, (await readerAssignments.AssignPatternToStaffAsync(evenings, [samResource], Today, null, Ct)).Error);
            Assert.Equal(PatternAssignmentService.NoAssign, (await readerAssignments.GetStaffChoicesForPatternAsync(evenings, Ct)).Error);
            var options = (await readerHost.Get<WorkPatternService>().GetWorkPatternEditorOptionsAsync(evenings, Ct)).Value!;
            Assert.Equal((true, false), (options.ReadsStaff, options.MayAssignStaff));
        }

        Assert.Equal(0, await CountOfAsync(evenings));

        var other = await ScheduleScenario.CreateAsync(this.database, "Other Bulk Permission Staffing", Ct);
        var theirs = await other.PublishPatternAsync(this.database, "Their evenings", Ct, ScheduleScenario.Weekdays(new TimeOnly(16, 0), new TimeOnly(20, 0)));
        await using (var otherHost = ServiceHost.Create(this.database, other.StaffMembers.Onboarded.RuntimeOf(other.Admin), OnToday))
        {
            var otherAssignments = otherHost.Get<PatternAssignmentService>();
            Assert.Equal(PatternAssignmentService.PatternNotFound, (await otherAssignments.AssignPatternToStaffAsync(evenings, [samResource], Today, null, Ct)).Error);
            Assert.Equal(PatternAssignmentService.PatternNotFound, (await otherAssignments.GetStaffChoicesForPatternAsync(evenings, Ct)).Error);
            var crossed = await otherAssignments.AssignPatternToStaffAsync(theirs, [samResource], Today, null, Ct);
            Assert.False(crossed.IsSuccess);
            Assert.Equal([PatternAssignmentService.StaffNotFound], crossed.Value!.Refused.Select(refusal => refusal.Reason));
            Assert.DoesNotContain("Sam", crossed.Error, StringComparison.Ordinal);
            Assert.DoesNotContain((await otherAssignments.GetStaffChoicesForPatternAsync(theirs, Ct)).Value!.Staff, choice => choice.ResourceId == samResource);
        }

        Assert.Equal(0, await CountOfAsync(evenings));
        Assert.Equal(0, await CountOfAsync(theirs));

        await using (var console = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime(), OnToday))
        {
            Assert.False((await console.Get<PatternAssignmentService>().AssignPatternToStaffAsync(evenings, [samResource], Today, null, Ct)).IsSuccess);
        }

        Assert.Equal(0, await CountOfAsync(evenings));
    }

    private async Task<(ScheduleScenario Scenario, Dictionary<string, Guid> Staff)> CoveredScenarioAsync(string name)
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, name, Ct);
        var staff = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var first in new[] { "Kim", "Robin", "Noa", "Sam", "Lee", "Ida", "Out" })
        {
            staff[$"{first} Example"] = await scenario.AddStaffAsync(this.database, first, Ct);
        }

        var fullTime = await scenario.PublishPatternAsync(this.database, "Full time", Ct, ScheduleScenario.Weekdays(Eight, Sixteen));
        var mornings = await scenario.PublishPatternAsync(this.database, "Mornings", Ct, ScheduleScenario.Weekdays(Eight, new TimeOnly(12, 0)));
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin), OnToday);
        var assignments = host.Get<PatternAssignmentService>();
        await ActiveAsync(assignments, staff["Kim Example"], fullTime, Today.AddDays(-16), null);
        await ActiveAsync(assignments, staff["Robin Example"], fullTime, new DateOnly(2026, 10, 1), Today.AddDays(10));
        await ActiveAsync(assignments, staff["Noa Example"], fullTime, new DateOnly(2026, 10, 1), Today.AddDays(10));
        await ActiveAsync(assignments, staff["Noa Example"], mornings, Today.AddDays(11), null);
        await ActiveAsync(assignments, staff["Sam Example"], mornings, Today.AddDays(13), null);
        Assert.True((await ScheduleScenario.CreateAssignmentAsync(assignments, staff["Ida Example"], fullTime, Today.AddDays(-2), null, Ct)).IsSuccess);
        var withdrawn = await ActiveAsync(assignments, staff["Out Example"], fullTime, Today.AddDays(-30), null);
        Assert.True((await ScheduleScenario.StepAsync(assignments, withdrawn, ResourcePatternAssignmentWorkflowAttribute.DeactivateStep, Ct)).IsSuccess);
        await TakeStaffRoleAsync(scenario, staff["Out Example"]);
        return (scenario, staff);
    }

    private async Task<Guid> ActiveAsync(PatternAssignmentService assignments, Guid user, Guid patternId, DateOnly from, DateOnly? to)
    {
        var created = await ScheduleScenario.CreateAssignmentAsync(assignments, user, patternId, from, to, Ct);
        Assert.True(created.IsSuccess, created.Error);
        var activated = await ScheduleScenario.StepAsync(assignments, created.Value, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct);
        Assert.True(activated.IsSuccess, activated.Error);
        return created.Value;
    }

    private async Task TakeStaffRoleAsync(ScheduleScenario scenario, Guid user)
    {
        var holding = await StaffScenario.HoldingIdAsync(this.database, user, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.StaffMembers.TeamId, Ct);
        await using var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime());
        Assert.True((await owner.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holding)).IsSuccess);
        Assert.Equal(ResourceState.Inactive, (await StaffScenario.RecordOfAsync(this.database, user, Ct)).State);
    }

    private async Task<Guid> DraftAsync(ScheduleScenario scenario, string name)
    {
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin));
        var draft = new WorkPattern { Name = name, CycleWeeks = 1 };
        draft.Segments.Add(new WorkPatternSegment { DayIndex = 0, Start = Eight, End = Sixteen });
        var created = await host.Get<WorkPatternService>().CreateWorkPatternAsync(draft, WorkPatternWorkflowAttribute.SaveStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task<int> CountOfAsync(Guid patternId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.ResourcePatternAssignments.AsNoTracking().CountAsync(assignment => assignment.WorkPatternId == patternId, Ct);
    }

    private static async Task<List<string>> NamesAsync(ResourceService resources, string filter, Dictionary<string, Guid> staff) =>
        [.. (await resources.QueryStaffMembersAsync(new ListQuery { PageSize = 100 }.WithFilter(StaffList.Pattern, filter), Ct)).Value!.Rows.Select(row => row.Name).Where(staff.ContainsKey).Order(StringComparer.Ordinal)];
}
