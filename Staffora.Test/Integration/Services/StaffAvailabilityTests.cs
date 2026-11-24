using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class StaffAvailabilityTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly Monday = new(2026, 11, 9);

    private readonly RealDatabase database;

    public StaffAvailabilityTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_bulk_read_takes_the_same_six_queries_for_three_staff_members_and_for_fifteen()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Counting Staffing", Ct);
        var pattern = await scenario.PublishPatternAsync(this.database, "Weekly", Ct, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(16, 0)));
        var other = await scenario.PublishPatternAsync(this.database, "Mornings", Ct, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(12, 0)));
        var resources = new List<Guid>();
        var users = new List<Guid>();
        await using (var admin = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin)))
        {
            var assignments = admin.Get<PatternAssignmentService>();
            for (var staff = 0; staff < 15; staff++)
            {
                var user = await scenario.AddStaffAsync(this.database, $"Staff{staff}", Ct);
                var id = (await ScheduleScenario.CreateAssignmentAsync(assignments, user, staff % 2 == 0 ? pattern : other, Monday, null, Ct)).Value;
                Assert.True((await ScheduleScenario.StepAsync(assignments, id, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);
                resources.Add(await ScheduleScenario.ResourceOfAsync(this.database, user, Ct));
                users.Add(user);
            }
        }

        await using var host = await CoordinatorHostAsync(scenario);

        for (var staff = 0; staff < 15; staff += 3)
        {
            await using var reporter = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(users[staff]));
            Assert.True((await AbsenceScenario.ReportAsync(reporter.Get<ResourceAbsenceService>(), users[staff], AbsenceKind.SickLeave, Monday.AddDays(staff), Monday.AddDays(staff), Ct)).IsSuccess);
        }

        var availability = host.Get<IStaffAvailability>();

        Assert.True((await availability.ReadAsync([resources[0]], Monday, Monday, Ct)).IsSuccess);

        host.ClearCommandLog();
        var few = await availability.ReadAsync(resources.Take(3).ToList(), Monday, Monday.AddDays(139), Ct);
        var fewCommands = host.CommandLog.Count;
        host.ClearCommandLog();
        var many = await availability.ReadAsync(resources, Monday, Monday.AddDays(139), Ct);
        var manyCommands = host.CommandLog.Count;

        Assert.Equal(6, fewCommands);
        Assert.Equal(fewCommands, manyCommands);
        Assert.Equal(3, few.Value!.StaffMembers.Count);
        Assert.Equal(15, many.Value!.StaffMembers.Count);
        Assert.All(many.Value.StaffMembers.Values, staff => Assert.Equal(100, staff.Working.Count));
        Assert.Equal([1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0], resources.Select(resource => many.Value.StaffMembers[resource].Blocks.Count));
    }

    [Fact]
    public async Task A_read_of_2500_ids_sends_them_as_one_parameter_in_the_same_six_queries()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Many Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var pattern = await scenario.PublishPatternAsync(this.database, "Weekly", Ct, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(16, 0)));
        await using (var admin = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin)))
        {
            var assignments = admin.Get<PatternAssignmentService>();
            var id = (await ScheduleScenario.CreateAssignmentAsync(assignments, kim, pattern, Monday, null, Ct)).Value;
            Assert.True((await ScheduleScenario.StepAsync(assignments, id, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);
        }

        var resource = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
        await using var host = await CoordinatorHostAsync(scenario);
        Assert.True((await host.Get<IStaffAvailability>().ReadAsync([resource], Monday, Monday, Ct)).IsSuccess);
        var ids = Enumerable.Range(0, 2_499).Select(_ => Guid.NewGuid()).Append(resource).ToList();
        host.ClearCommandLog();

        var answer = await host.Get<IStaffAvailability>().ReadAsync(ids, Monday, Monday.AddDays(6), Ct);

        Assert.True(answer.IsSuccess, answer.Error);
        Assert.Equal([resource], answer.Value!.StaffMembers.Keys);
        Assert.Equal(5, answer.Value.StaffMembers[resource].Working.Count);
        Assert.Equal(6, host.CommandLog.Count);
        var records = host.CommandLog[0];
        Assert.Contains("OPENJSON", records, StringComparison.Ordinal);
        Assert.DoesNotContain(ids[1].ToString(), records, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_staff_member_who_is_not_in_service_has_no_working_time_and_is_blocked_over_the_range()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Leaving Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var robin = await scenario.AddStaffAsync(this.database, "Robin", Ct);
        var pattern = await scenario.PublishPatternAsync(this.database, "Weekly", Ct, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(16, 0)));
        await using (var admin = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin)))
        {
            var assignments = admin.Get<PatternAssignmentService>();
            foreach (var user in new[] { kim, robin })
            {
                var id = (await ScheduleScenario.CreateAssignmentAsync(assignments, user, pattern, Monday, null, Ct)).Value;
                Assert.True((await ScheduleScenario.StepAsync(assignments, id, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);
            }
        }

        var holding = await StaffScenario.HoldingIdAsync(this.database, kim, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.StaffMembers.TeamId, Ct);
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            var taken = await owner.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holding);
            Assert.True(taken.IsSuccess, taken.Error);
        }

        Assert.Equal(Staffora.Core.Entities.Resources.ResourceState.Inactive, (await StaffScenario.RecordOfAsync(this.database, kim, Ct)).State);
        var (kimId, robinId) = (await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct), await ScheduleScenario.ResourceOfAsync(this.database, robin, Ct));
        await using var host = await CoordinatorHostAsync(scenario);
        var answer = (await host.Get<IStaffAvailability>().ReadAsync([kimId, robinId], Monday, Monday.AddDays(6), Ct)).Value!;

        var stockholm = TimeZoneInfo.FindSystemTimeZoneById(ServiceHost.PlatformTimeZone);
        Assert.Empty(answer.StaffMembers[kimId].Working);
        var block = Assert.Single(answer.StaffMembers[kimId].Blocks);
        Assert.Equal(AbsenceRules.NotInServiceReason, block.Reason);
        Assert.Equal((TenantClock.ToUtc(Monday, TimeOnly.MinValue, stockholm), TenantClock.ToUtc(Monday.AddDays(7), TimeOnly.MinValue, stockholm)), (block.StartUtc, block.EndUtc));
        Assert.Equal(5, answer.StaffMembers[robinId].Working.Count);
        Assert.Empty(answer.StaffMembers[robinId].Blocks);
    }

    [Fact]
    public async Task Only_active_assignments_give_hours_and_the_tenants_zone_converts_them_across_the_spring_change()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Springing Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var robin = await scenario.AddStaffAsync(this.database, "Robin", Ct);
        var sam = await scenario.AddStaffAsync(this.database, "Sam", Ct);

        var pattern = await scenario.PublishPatternAsync(this.database, "Weekend nights", Ct, new PatternSegment(5, new TimeOnly(8, 0), new TimeOnly(16, 0)), new PatternSegment(6, new TimeOnly(1, 0), new TimeOnly(4, 0)));
        var spring = new DateOnly(2026, 3, 23);
        await using (var admin = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin)))
        {
            var assignments = admin.Get<PatternAssignmentService>();
            var active = (await ScheduleScenario.CreateAssignmentAsync(assignments, kim, pattern, spring, null, Ct)).Value;
            Assert.True((await ScheduleScenario.StepAsync(assignments, active, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);
            Assert.True((await ScheduleScenario.CreateAssignmentAsync(assignments, robin, pattern, spring, null, Ct)).IsSuccess);
            var withdrawn = (await ScheduleScenario.CreateAssignmentAsync(assignments, sam, pattern, spring, null, Ct)).Value;
            Assert.True((await ScheduleScenario.StepAsync(assignments, withdrawn, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);
            Assert.True((await ScheduleScenario.StepAsync(assignments, withdrawn, ResourcePatternAssignmentWorkflowAttribute.DeactivateStep, Ct)).IsSuccess);
        }

        var (kimId, robinId, samId) = (await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct), await ScheduleScenario.ResourceOfAsync(this.database, robin, Ct), await ScheduleScenario.ResourceOfAsync(this.database, sam, Ct));
        await using var host = await CoordinatorHostAsync(scenario);
        var answer = (await host.Get<IStaffAvailability>().ReadAsync([kimId, robinId, samId], new DateOnly(2026, 3, 28), new DateOnly(2026, 3, 29), Ct)).Value!;

        Assert.Equal(ServiceHost.PlatformTimeZone, answer.TimeZoneId);
        Assert.Equal(
            [(DateTime.SpecifyKind(new DateTime(2026, 3, 28, 7, 0, 0), DateTimeKind.Utc), DateTime.SpecifyKind(new DateTime(2026, 3, 28, 15, 0, 0), DateTimeKind.Utc)), (DateTime.SpecifyKind(new DateTime(2026, 3, 29, 0, 0, 0), DateTimeKind.Utc), DateTime.SpecifyKind(new DateTime(2026, 3, 29, 2, 0, 0), DateTimeKind.Utc))],
            answer.StaffMembers[kimId].Working.Select(segment => (segment.StartUtc, segment.EndUtc)));
        Assert.Empty(answer.StaffMembers[robinId].Working);
        Assert.Empty(answer.StaffMembers[samId].Working);
    }

    [Fact]
    public async Task The_bulk_read_is_for_coordinators_one_schedule_for_readers_of_staff_and_the_staff_member_alone()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Asking Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var robin = await scenario.AddStaffAsync(this.database, "Robin", Ct);
        var pattern = await scenario.PublishPatternAsync(this.database, "Weekly", Ct, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(16, 0)));
        await using (var admin = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin)))
        {
            var assignments = admin.Get<PatternAssignmentService>();
            var id = (await ScheduleScenario.CreateAssignmentAsync(assignments, kim, pattern, Monday, null, Ct)).Value;
            Assert.True((await ScheduleScenario.StepAsync(assignments, id, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);

            var kimId = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
            Assert.Equal(StaffAvailabilityService.NoViewCoordination, (await admin.Get<IStaffAvailability>().ReadAsync([kimId], Monday, Monday, Ct)).Error);
            var schedule = (await admin.Get<StaffAvailabilityService>().ReadScheduleOfUserAsync(kim, Monday.AddDays(2), 2, Ct)).Value!;
            Assert.Equal([Monday, Monday.AddDays(7)], schedule.Weeks.Select(week => week.Monday));
            Assert.Equal("40 h", schedule.Weeks[0].WorkedLabel);

            await using var coordinator = await CoordinatorHostAsync(scenario);
            Assert.Equal(StaffAvailabilityService.RangeReversed, (await coordinator.Get<IStaffAvailability>().ReadAsync([kimId], Monday, Monday.AddDays(-1), Ct)).Error);
            Assert.Equal(StaffAvailabilityService.RangeTooLong, (await coordinator.Get<IStaffAvailability>().ReadAsync([kimId], Monday, Monday.AddDays(StaffAvailabilityService.MaxDays), Ct)).Error);
        }

        await using (var staff = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(kim)))
        {
            Assert.Contains(RolePermission.ViewOwnSchedule, RolePresets.Staff.Permissions);
            var own = await staff.Get<StaffAvailabilityService>().ReadScheduleOfUserAsync(kim, Monday, 1, Ct);
            Assert.True(own.IsSuccess, own.Error);
            Assert.Equal("08:00–16:00", own.Value!.Weeks[0].Days[0].HoursLabel);
            Assert.Equal(StaffAvailabilityService.NoScheduleView, (await staff.Get<StaffAvailabilityService>().ReadScheduleOfUserAsync(robin, Monday, 1, Ct)).Error);
            Assert.Equal(StaffAvailabilityService.NoViewCoordination, (await staff.Get<IStaffAvailability>().ReadAsync([Guid.NewGuid()], Monday, Monday, Ct)).Error);
        }

        var plain = await scenario.StaffMembers.AddPersonAsync(this.database, "Plain", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, plain, Ct, RolePermission.UsersView);
        await using (var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(plain)))
        {
            Assert.Equal(StaffAvailabilityService.NoScheduleView, (await host.Get<StaffAvailabilityService>().ReadScheduleOfUserAsync(kim, Monday, 1, Ct)).Error);
            Assert.Equal(StaffAvailabilityService.NoScheduleView, (await host.Get<StaffAvailabilityService>().ReadScheduleOfUserAsync(plain, Monday, 1, Ct)).Error);
        }

        await using (var staff = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(kim)))
        {
            foreach (var weeks in new[] { 0, -1, StaffAvailabilityService.MaxDays / 7 + 1, int.MaxValue, int.MinValue })
            {
                Assert.Equal(StaffAvailabilityService.WeeksOutOfRange, (await staff.Get<StaffAvailabilityService>().ReadScheduleOfUserAsync(kim, Monday, weeks, Ct)).Error);
            }

            Assert.Equal(StaffAvailabilityService.RangeOutOfCalendar, (await staff.Get<StaffAvailabilityService>().ReadScheduleOfUserAsync(kim, DateOnly.MaxValue, 1, Ct)).Error);
        }
    }

    [Fact]
    public async Task Another_tenants_coordinator_reads_none_of_the_tenants_staff_members()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Isolated Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var pattern = await scenario.PublishPatternAsync(this.database, "Weekly", Ct, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(16, 0)));
        await using (var admin = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin)))
        {
            var assignments = admin.Get<PatternAssignmentService>();
            var id = (await ScheduleScenario.CreateAssignmentAsync(assignments, kim, pattern, Monday, null, Ct)).Value;
            Assert.True((await ScheduleScenario.StepAsync(assignments, id, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);
        }

        var kimId = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
        var other = await ScheduleScenario.CreateAsync(this.database, "Other Isolated Staffing", Ct);
        await using var host = await CoordinatorHostAsync(other);
        var answer = await host.Get<IStaffAvailability>().ReadAsync([kimId], Monday, Monday.AddDays(6), Ct);

        Assert.True(answer.IsSuccess, answer.Error);
        Assert.Empty(answer.Value!.StaffMembers);
        Assert.Equal(StaffAvailabilityService.StaffNotFound, (await host.Get<StaffAvailabilityService>().ReadScheduleOfUserAsync(kim, Monday, 1, Ct)).Error);
        Assert.Null((await host.Get<UserService>().GetApplicationUserEditorOptionsAsync(kim, Ct)).Value?.Schedule);
    }

    [Fact]
    public async Task Assigned_and_accepted_occurrences_block_an_open_request_warns_in_the_tenants_zone_in_the_same_six_queries_for_one_staff_member_and_for_four()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Booked Staffing", Ct);
        var from = new DateOnly(2027, 3, 22);
        var users = new List<Guid>();
        foreach (var name in new[] { "Ada", "Bo", "Cy", "Dee" })
        {
            users.Add(await scenario.Absence.AddWorkingStaffAsync(this.database, name, from, Ct));
        }

        var (_, slots) = await scenario.StartedJobAsync(this.database, "Spring job", from, new DateOnly(2027, 4, 2), Ct, ["Main", "Second", "Third"]);
        var assigned = (await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], users[0], Ct)).Value;
        var accepted = (await scenario.RequestAsync(this.database, scenario.Coordinator, slots[1], users[1], Ct)).Value;
        Assert.True((await scenario.StepAsync(this.database, users[1], accepted, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        var requested = (await scenario.RequestAsync(this.database, scenario.Coordinator, slots[2], users[2], Ct)).Value;
        var declined = (await scenario.RequestAsync(this.database, scenario.Coordinator, slots[2], users[3], Ct)).Value;
        Assert.True((await scenario.StepAsync(this.database, users[3], declined, SlotAssignmentWorkflowAttribute.DeclineStep, Ct, "No")).IsSuccess);
        var resources = new List<Guid>();
        foreach (var user in users)
        {
            resources.Add(await ScheduleScenario.ResourceOfAsync(this.database, user, Ct));
        }

        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var availability = host.Get<IStaffAvailability>();
        Assert.True((await availability.ReadAsync([resources[0]], from, from, Ct)).IsSuccess);
        host.ClearCommandLog();
        var one = await availability.ReadAsync([resources[0]], from, new DateOnly(2027, 4, 4), Ct);
        var oneCommands = host.CommandLog.Count;
        host.ClearCommandLog();
        var all = (await availability.ReadAsync(resources, from, new DateOnly(2027, 4, 4), Ct)).Value!;
        Assert.Equal((6, 6), (oneCommands, host.CommandLog.Count));

        (DateTime, DateTime)[] occurrences =
        [
            (DateTime.SpecifyKind(new DateTime(2027, 3, 22, 7, 0, 0), DateTimeKind.Utc), DateTime.SpecifyKind(new DateTime(2027, 3, 22, 9, 0, 0), DateTimeKind.Utc)),
            (DateTime.SpecifyKind(new DateTime(2027, 3, 29, 6, 0, 0), DateTimeKind.Utc), DateTime.SpecifyKind(new DateTime(2027, 3, 29, 8, 0, 0), DateTimeKind.Utc)),
        ];
        Assert.Equal(occurrences, one.Value!.StaffMembers[resources[0]].Blocks.Select(block => (block.StartUtc, block.EndUtc)));
        Assert.All(all.StaffMembers[resources[0]].Blocks, block => Assert.Equal((AvailabilityRules.AssignedReason, (Guid?)assigned), (block.Reason, block.SourceId)));
        Assert.Equal(occurrences, all.StaffMembers[resources[1]].Blocks.Select(block => (block.StartUtc, block.EndUtc)));
        Assert.All(all.StaffMembers[resources[1]].Blocks, block => Assert.Equal((AvailabilityRules.AcceptedReason, (Guid?)accepted), (block.Reason, block.SourceId)));
        Assert.Empty(all.StaffMembers[resources[2]].Blocks);
        Assert.Equal(occurrences, all.StaffMembers[resources[2]].Warnings.Select(warning => (warning.StartUtc, warning.EndUtc)));
        Assert.All(all.StaffMembers[resources[2]].Warnings, warning => Assert.Equal((AvailabilityRules.RequestedReason, (Guid?)requested), (warning.Reason, warning.SourceId)));
        Assert.Equal((0, 0), (all.StaffMembers[resources[3]].Blocks.Count, all.StaffMembers[resources[3]].Warnings.Count));

        Assert.True((await AssignmentScenario.StepSlotAsync(host.Get<Staffora.Services.Jobs.JobSlotService>(), slots[0], JobSlotWorkflowAttribute.CancelStep, Ct)).IsSuccess);
        Assert.Empty((await availability.ReadAsync([resources[0]], from, new DateOnly(2027, 4, 4), Ct)).Value!.StaffMembers[resources[0]].Blocks);
    }

    private async Task<ServiceHost> CoordinatorHostAsync(ScheduleScenario scenario)
    {
        var coordinator = await scenario.StaffMembers.AddPersonAsync(this.database, "Casey", Ct);
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(coordinator, scenario.StaffMembers.RootId, scenario.StaffMembers.Onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
        }

        Assert.Contains(RolePermission.ViewCoordination, RolePresets.Coordinator.Permissions);
        return ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(coordinator));
    }
}
