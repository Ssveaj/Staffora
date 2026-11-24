using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class PatternAssignmentLifecycleTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly Monday = new(2026, 11, 9);
    private static readonly TimeOnly Eight = new(8, 0);
    private static readonly TimeOnly Sixteen = new(16, 0);

    private readonly RealDatabase database;

    public PatternAssignmentLifecycleTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_overlapping_assignment_is_refused_and_stores_nothing()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Overlapping Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var fullTime = await scenario.PublishPatternAsync(this.database, "Full time", Ct, ScheduleScenario.Weekdays(Eight, Sixteen));
        var mornings = await scenario.PublishPatternAsync(this.database, "Mornings", Ct, ScheduleScenario.Weekdays(Eight, new TimeOnly(12, 0)));
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin));
        var assignments = host.Get<PatternAssignmentService>();

        var first = await ScheduleScenario.CreateAssignmentAsync(assignments, kim, fullTime, Monday.AddDays(-28), Monday.AddDays(27), Ct);
        Assert.True(first.IsSuccess, first.Error);
        Assert.Equal(ResourcePatternAssignmentState.Registration, (await ScheduleScenario.StoredAsync(this.database, first.Value, Ct))!.State);
        var activated = await ScheduleScenario.StepAsync(assignments, first.Value, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct);
        Assert.True(activated.IsSuccess, activated.Error);
        Assert.Equal([ResourcePatternAssignmentWorkflowAttribute.SaveStep, ResourcePatternAssignmentWorkflowAttribute.ActivateStep], await ScheduleScenario.HistoryAsync(this.database, first.Value, Ct));

        var second = await ScheduleScenario.CreateAssignmentAsync(assignments, kim, mornings, Monday, null, Ct);
        Assert.True(second.IsSuccess, second.Error);
        var refused = await ScheduleScenario.StepAsync(assignments, second.Value, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct);
        Assert.False(refused.IsSuccess);
        Assert.Contains("It overlaps the active assignment of Full time from 2026-10-12 to 2026-12-06", refused.Error, StringComparison.Ordinal);
        Assert.Equal(ResourcePatternAssignmentState.Registration, (await ScheduleScenario.StoredAsync(this.database, second.Value, Ct))!.State);
        Assert.Equal([ResourcePatternAssignmentWorkflowAttribute.SaveStep], await ScheduleScenario.HistoryAsync(this.database, second.Value, Ct));

        var robin = await scenario.AddStaffAsync(this.database, "Robin", Ct);
        var robins = await ScheduleScenario.CreateAssignmentAsync(assignments, robin, fullTime, Monday, null, Ct);
        Assert.True((await ScheduleScenario.StepAsync(assignments, robins.Value, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);

        var rows = (await host.Get<UserService>().PatternAssignments.QueryAssignmentsOfUserAsync(kim, new ListQuery(), Ct)).Value!.Rows;
        Assert.Equal([("Mornings", ResourcePatternAssignmentState.Registration, "2026-11-09 until further notice"), ("Full time", ResourcePatternAssignmentState.Active, "2026-10-12 to 2026-12-06")], rows.Select(row => (row.Pattern, row.State, row.PeriodLabel)));
    }

    [Fact]
    public async Task A_pattern_is_replaced_by_ending_the_current_assignment_and_activating_the_next_from_the_day_after()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Replacing Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var fullTime = await scenario.PublishPatternAsync(this.database, "Full time", Ct, ScheduleScenario.Weekdays(Eight, Sixteen));
        var mornings = await scenario.PublishPatternAsync(this.database, "Mornings", Ct, ScheduleScenario.Weekdays(Eight, new TimeOnly(12, 0)));
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin));
        var assignments = host.Get<PatternAssignmentService>();
        var current = await ScheduleScenario.CreateAssignmentAsync(assignments, kim, fullTime, Monday.AddDays(-70), null, Ct);
        Assert.True((await ScheduleScenario.StepAsync(assignments, current.Value, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);
        var next = await ScheduleScenario.CreateAssignmentAsync(assignments, kim, mornings, Monday.AddDays(14), null, Ct);

        var ended = await ScheduleScenario.StepAsync(assignments, current.Value, ResourcePatternAssignmentWorkflowAttribute.SaveActiveStep, Ct, edit => edit.EffectiveTo = Monday.AddDays(13));
        Assert.True(ended.IsSuccess, ended.Error);
        var taken = await ScheduleScenario.StepAsync(assignments, next.Value, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct);
        Assert.True(taken.IsSuccess, taken.Error);

        await using var coordinator = await CoordinatorHostAsync(scenario);
        var resource = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
        var answer = (await coordinator.Get<IStaffAvailability>().ReadAsync([resource], Monday.AddDays(7), Monday.AddDays(20), Ct)).Value!;
        Assert.Equal(
            [.. Enumerable.Range(7, 5).Select(day => (Monday.AddDays(day), Sixteen)), .. Enumerable.Range(14, 5).Select(day => (Monday.AddDays(day), new TimeOnly(12, 0)))],
            answer.StaffMembers[resource].Working.Select(segment => (segment.Date, segment.LocalEnd)));

        var later = await ScheduleScenario.StepAsync(assignments, current.Value, ResourcePatternAssignmentWorkflowAttribute.SaveActiveStep, Ct, edit => edit.EffectiveTo = Monday.AddDays(14));
        Assert.Contains("It overlaps the active assignment of Mornings from 2026-11-23 until further notice", later.Error, StringComparison.Ordinal);
        var locked = await ScheduleScenario.StepAsync(assignments, current.Value, ResourcePatternAssignmentWorkflowAttribute.SaveActiveStep, Ct, edit => edit.EffectiveFrom = Monday);
        Assert.False(locked.IsSuccess);
        var stored = (await ScheduleScenario.StoredAsync(this.database, current.Value, Ct))!;
        Assert.Equal((Monday.AddDays(-70), Monday.AddDays(13)), (stored.EffectiveFrom, stored.EffectiveTo));

        Assert.True((await ScheduleScenario.StepAsync(assignments, current.Value, ResourcePatternAssignmentWorkflowAttribute.DeactivateStep, Ct)).IsSuccess);
        answer = (await coordinator.Get<IStaffAvailability>().ReadAsync([resource], Monday.AddDays(7), Monday.AddDays(13), Ct)).Value!;
        Assert.Empty(answer.StaffMembers[resource].Working);
        Assert.True((await ScheduleScenario.StepAsync(assignments, current.Value, ResourcePatternAssignmentWorkflowAttribute.ReactivateStep, Ct)).IsSuccess);
        Assert.True((await ScheduleScenario.StepAsync(assignments, current.Value, ResourcePatternAssignmentWorkflowAttribute.DeactivateStep, Ct)).IsSuccess);
        Assert.True((await ScheduleScenario.StepAsync(assignments, next.Value, ResourcePatternAssignmentWorkflowAttribute.SaveActiveStep, Ct, edit => edit.EffectiveTo = null)).IsSuccess);
        var overlapping = await ScheduleScenario.CreateAssignmentAsync(assignments, kim, fullTime, Monday.AddDays(21), null, Ct);
        var refused = await ScheduleScenario.StepAsync(assignments, overlapping.Value, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct);
        Assert.Contains("It overlaps the active assignment of Mornings", refused.Error, StringComparison.Ordinal);

        Assert.True((await ScheduleScenario.StepAsync(assignments, overlapping.Value, ResourcePatternAssignmentWorkflowAttribute.DeleteStep, Ct)).IsSuccess);
        Assert.Null(await ScheduleScenario.StoredAsync(this.database, overlapping.Value, Ct));
        Assert.Equal([ResourcePatternAssignmentWorkflowAttribute.SaveStep, ResourcePatternAssignmentWorkflowAttribute.DeleteStep], await ScheduleScenario.HistoryAsync(this.database, overlapping.Value, Ct));
    }

    [Fact]
    public async Task Archiving_a_pattern_staff_members_have_keeps_their_assignments_and_gives_it_to_nobody_new()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Archiving Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var robin = await scenario.AddStaffAsync(this.database, "Robin", Ct);
        var sam = await scenario.AddStaffAsync(this.database, "Sam", Ct);
        var pattern = await scenario.PublishPatternAsync(this.database, "Old hours", Ct, ScheduleScenario.Weekdays(Eight, Sixteen));
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin));
        var assignments = host.Get<PatternAssignmentService>();
        var kims = await ScheduleScenario.CreateAssignmentAsync(assignments, kim, pattern, Monday, null, Ct);
        Assert.True((await ScheduleScenario.StepAsync(assignments, kims.Value, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);
        var robins = await ScheduleScenario.CreateAssignmentAsync(assignments, robin, pattern, Monday, null, Ct);
        var withdrawn = await ScheduleScenario.CreateAssignmentAsync(assignments, sam, pattern, Monday, null, Ct);
        Assert.True((await ScheduleScenario.StepAsync(assignments, withdrawn.Value, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);
        Assert.True((await ScheduleScenario.StepAsync(assignments, withdrawn.Value, ResourcePatternAssignmentWorkflowAttribute.DeactivateStep, Ct)).IsSuccess);

        var patterns = host.Get<WorkPatternService>();
        var archived = await patterns.UpdateWorkPatternAndTransitionAsync((await patterns.LoadWorkPatternUpdaterAsync(pattern, Ct)).Value!, WorkPatternWorkflowAttribute.ArchiveStep, null, Ct);
        Assert.True(archived.IsSuccess, archived.Error);

        var kept = (await ScheduleScenario.StoredAsync(this.database, kims.Value, Ct))!;
        Assert.Equal((ResourcePatternAssignmentState.Active, pattern), (kept.State, kept.WorkPatternId));
        await using var coordinator = await CoordinatorHostAsync(scenario);
        var resource = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
        var answer = (await coordinator.Get<IStaffAvailability>().ReadAsync([resource], Monday, Monday.AddDays(6), Ct)).Value!;
        Assert.Equal(5, answer.StaffMembers[resource].Working.Count);
        var options = (await assignments.GetResourcePatternAssignmentEditorOptionsAsync(kims.Value, null, Ct)).Value!;
        Assert.Equal(PatternAssignmentService.ArchivedPatternNote, options.PatternNote);
        Assert.Contains(options.Patterns, offered => offered.Id == pattern);
        Assert.Equal("Old hours (archived pattern)", (await host.Get<UserService>().PatternAssignments.QueryAssignmentsOfUserAsync(kim, new ListQuery(), Ct)).Value!.Rows.Single().PatternLabel);

        Assert.True((await ScheduleScenario.StepAsync(assignments, kims.Value, ResourcePatternAssignmentWorkflowAttribute.SaveActiveStep, Ct, edit => edit.EffectiveTo = Monday.AddDays(90))).IsSuccess);

        Assert.DoesNotContain((await assignments.GetResourcePatternAssignmentEditorOptionsAsync(null, robin, Ct)).Value!.Patterns, offered => offered.Id == pattern);
        var created = await ScheduleScenario.CreateAssignmentAsync(assignments, robin, pattern, Monday.AddDays(7), null, Ct);
        Assert.Contains(ResourcePatternAssignmentWorkflowAttribute.PatternPublishedReason, created.Error, StringComparison.Ordinal);
        var activation = await ScheduleScenario.StepAsync(assignments, robins.Value, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct);
        Assert.Contains(ResourcePatternAssignmentWorkflowAttribute.PatternPublishedReason, activation.Error, StringComparison.Ordinal);
        var reactivation = await ScheduleScenario.StepAsync(assignments, withdrawn.Value, ResourcePatternAssignmentWorkflowAttribute.ReactivateStep, Ct);
        Assert.Contains(ResourcePatternAssignmentWorkflowAttribute.PatternPublishedReason, reactivation.Error, StringComparison.Ordinal);
        Assert.Equal(ResourcePatternAssignmentState.Registration, (await ScheduleScenario.StoredAsync(this.database, robins.Value, Ct))!.State);
        Assert.Equal(ResourcePatternAssignmentState.Inactive, (await ScheduleScenario.StoredAsync(this.database, withdrawn.Value, Ct))!.State);
    }

    [Fact]
    public async Task A_cycle_starts_on_a_Monday_whoever_writes_it_and_a_given_pattern_is_not_deleted()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Anchoring Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var pattern = await scenario.PublishPatternAsync(this.database, "Weekly", Ct, ScheduleScenario.Weekdays(Eight, Sixteen));
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin));
        var assignments = host.Get<PatternAssignmentService>();

        var wednesday = await ScheduleScenario.CreateAssignmentAsync(assignments, kim, pattern, Monday, null, Ct, anchor: Monday.AddDays(2));
        Assert.Contains(ScheduleRules.AnchorNotMonday, wednesday.Error, StringComparison.Ordinal);
        Assert.Empty((await host.Get<UserService>().PatternAssignments.QueryAssignmentsOfUserAsync(kim, new ListQuery(), Ct)).Value!.Rows);

        var resource = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
        foreach (var (anchor, to) in new (DateOnly, DateOnly?)[] { (Monday.AddDays(2), null), (Monday, Monday.AddDays(-1)) })
        {
            await using var context = this.database.CreateSystemContext();
            context.ResourcePatternAssignments.Add(new ResourcePatternAssignment { TenantId = scenario.TenantId, ResourceId = resource, WorkPatternId = pattern, EffectiveFrom = Monday, EffectiveTo = to, CycleAnchor = anchor, CreatedById = scenario.Admin });
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
            Assert.Contains("CK_ResourcePatternAssignments_", exception.InnerException!.Message, StringComparison.Ordinal);
        }

        var patterns = host.Get<WorkPatternService>();
        var draft = await patterns.CreateWorkPatternAsync(new WorkPattern { Name = "Draft", CycleWeeks = 1 }, WorkPatternWorkflowAttribute.SaveStep, Ct);
        await using (var context = this.database.CreateSystemContext())
        {
            context.ResourcePatternAssignments.Add(new ResourcePatternAssignment { TenantId = scenario.TenantId, ResourceId = resource, WorkPatternId = draft.Value, EffectiveFrom = Monday, CycleAnchor = Monday, CreatedById = scenario.Admin });
            await context.SaveChangesAsync(Ct);
        }

        var deleted = await patterns.UpdateWorkPatternAndTransitionAsync((await patterns.LoadWorkPatternUpdaterAsync(draft.Value, Ct)).Value!, WorkPatternWorkflowAttribute.DeleteStep, null, Ct);
        Assert.Contains(PatternAssignmentsBlockPatternDelete.Reason, deleted.Error, StringComparison.Ordinal);
        Assert.Equal(WorkPatternState.Draft, (await patterns.LoadWorkPatternUpdaterAsync(draft.Value, Ct)).Value!.Entity.State);
    }

    [Fact]
    public async Task A_reader_of_staff_reads_assignments_and_moves_none_and_another_tenant_finds_nothing()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Reading Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var pattern = await scenario.PublishPatternAsync(this.database, "Weekly", Ct, ScheduleScenario.Weekdays(Eight, Sixteen));
        Guid id;
        await using (var admin = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin)))
        {
            id = (await ScheduleScenario.CreateAssignmentAsync(admin.Get<PatternAssignmentService>(), kim, pattern, Monday, null, Ct)).Value;
        }

        var reader = await scenario.StaffMembers.AddPersonAsync(this.database, "Reader", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, reader, Ct, RolePermission.ResourcesView, RolePermission.UsersView);
        await using (var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(reader)))
        {
            var assignments = host.Get<PatternAssignmentService>();
            Assert.Single((await assignments.QueryAssignmentsOfUserAsync(kim, new ListQuery(), Ct)).Value!.Rows);
            Assert.True((await assignments.LoadResourcePatternAssignmentUpdaterAsync(id, Ct)).IsSuccess);
            Assert.Equal(Staffora.Services.Workflow.StateHistoryService.OwnersOnly, (await assignments.GetResourcePatternAssignmentHistoryAsync(id, Staffora.Services.Workflow.StateHistoryQuery.Newest(3), Ct)).Error);
            Assert.True((await assignments.GetResourcePatternAssignmentEditorOptionsAsync(id, null, Ct)).Value!.FieldsLocked);
            Assert.Equal(PatternAssignmentService.NoAssign, (await assignments.NewResourcePatternAssignmentAsync(kim, Ct)).Error);
            Assert.Equal(PatternAssignmentService.NoAssign, (await ScheduleScenario.StepAsync(assignments, id, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).Error);
            Assert.False(await assignments.MayAssignToUserAsync(kim, Ct));
        }

        Assert.Equal(ResourcePatternAssignmentState.Registration, (await ScheduleScenario.StoredAsync(this.database, id, Ct))!.State);

        var plain = await scenario.StaffMembers.AddPersonAsync(this.database, "Plain", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, plain, Ct, RolePermission.UsersView);
        await using (var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(plain)))
        {
            Assert.Equal(PatternAssignmentService.NoView, (await host.Get<PatternAssignmentService>().LoadResourcePatternAssignmentUpdaterAsync(id, Ct)).Error);
            Assert.Equal(PatternAssignmentService.NoView, (await host.Get<PatternAssignmentService>().QueryAssignmentsOfUserAsync(kim, new ListQuery(), Ct)).Error);
        }

        var other = await ScheduleScenario.CreateAsync(this.database, "Other Reading Staffing", Ct);
        await using (var host = ServiceHost.Create(this.database, other.StaffMembers.Onboarded.RuntimeOf(other.Admin)))
        {
            var assignments = host.Get<PatternAssignmentService>();
            Assert.Equal(PatternAssignmentService.NotFound, (await assignments.LoadResourcePatternAssignmentUpdaterAsync(id, Ct)).Error);
            Assert.Equal(PatternAssignmentService.StaffNotFound, (await assignments.NewResourcePatternAssignmentAsync(kim, Ct)).Error);
            Assert.Empty((await assignments.QueryAssignmentsOfUserAsync(kim, new ListQuery(), Ct)).Value!.Rows);
            Assert.False(await assignments.MayAssignToUserAsync(kim, Ct));
            var otherStaff = await other.AddStaffAsync(this.database, "Lee", Ct);
            var foreignPattern = await ScheduleScenario.CreateAssignmentAsync(assignments, otherStaff, pattern, Monday, null, Ct);
            Assert.False(foreignPattern.IsSuccess);
            Assert.DoesNotContain("Weekly", foreignPattern.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_user_dialogs_Schedule_view_reads_through_the_assignment_service_by_its_readers_and_refuses_anyone_else()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Viewing Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var pattern = await scenario.PublishPatternAsync(this.database, "Weekly", Ct, ScheduleScenario.Weekdays(Eight, Sixteen));
        Guid id;
        await using (var admin = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin)))
        {
            id = (await ScheduleScenario.CreateAssignmentAsync(admin.Get<PatternAssignmentService>(), kim, pattern, Monday, null, Ct)).Value;
        }

        var reader = await scenario.StaffMembers.AddPersonAsync(this.database, "Reader", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, reader, Ct, RolePermission.ResourcesView, RolePermission.UsersView);
        var assigner = await scenario.StaffMembers.AddPersonAsync(this.database, "Assigner", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, assigner, Ct, RolePermission.AssignWorkPatterns, RolePermission.UsersView);
        var plain = await scenario.StaffMembers.AddPersonAsync(this.database, "Plain", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, plain, Ct, RolePermission.UsersView);

        await using (var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(reader)))
        {
            Assert.Equal([id], (await host.Get<UserService>().PatternAssignments.QueryAssignmentsOfUserAsync(kim, new ListQuery(), Ct)).Value!.Rows.Select(row => row.Id));
        }

        await using (var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(assigner)))
        {
            Assert.Equal([id], (await host.Get<UserService>().PatternAssignments.QueryAssignmentsOfUserAsync(kim, new ListQuery(), Ct)).Value!.Rows.Select(row => row.Id));
            Assert.True((await host.Get<PatternAssignmentService>().LoadResourcePatternAssignmentUpdaterAsync(id, Ct)).IsSuccess);
        }

        await using (var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(plain)))
        {
            Assert.Equal(PatternAssignmentService.NoView, (await host.Get<UserService>().PatternAssignments.QueryAssignmentsOfUserAsync(kim, new ListQuery(), Ct)).Error);
        }
    }

    [Fact]
    public async Task Reactivate_is_refused_over_another_active_assignment_and_for_a_staff_member_out_of_service()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Reactivating Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var fullTime = await scenario.PublishPatternAsync(this.database, "Full time", Ct, ScheduleScenario.Weekdays(Eight, Sixteen));
        var mornings = await scenario.PublishPatternAsync(this.database, "Mornings", Ct, ScheduleScenario.Weekdays(Eight, new TimeOnly(12, 0)));
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin));
        var assignments = host.Get<PatternAssignmentService>();

        var first = (await ScheduleScenario.CreateAssignmentAsync(assignments, kim, fullTime, Monday, null, Ct)).Value;
        Assert.True((await ScheduleScenario.StepAsync(assignments, first, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);
        Assert.True((await ScheduleScenario.StepAsync(assignments, first, ResourcePatternAssignmentWorkflowAttribute.DeactivateStep, Ct)).IsSuccess);
        var second = (await ScheduleScenario.CreateAssignmentAsync(assignments, kim, mornings, Monday.AddDays(7), null, Ct)).Value;
        Assert.True((await ScheduleScenario.StepAsync(assignments, second, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);

        var over = await ScheduleScenario.StepAsync(assignments, first, ResourcePatternAssignmentWorkflowAttribute.ReactivateStep, Ct);
        Assert.False(over.IsSuccess);
        Assert.Contains("It overlaps the active assignment of Mornings from 2026-11-16 until further notice", over.Error, StringComparison.Ordinal);
        Assert.Equal(ResourcePatternAssignmentState.Inactive, (await ScheduleScenario.StoredAsync(this.database, first, Ct))!.State);

        Assert.True((await ScheduleScenario.StepAsync(assignments, second, ResourcePatternAssignmentWorkflowAttribute.DeactivateStep, Ct)).IsSuccess);
        var third = (await ScheduleScenario.CreateAssignmentAsync(assignments, kim, mornings, Monday.AddDays(400), null, Ct)).Value;
        Assert.True((await ScheduleScenario.StepAsync(assignments, third, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);
        Assert.True(await assignments.MayAssignToUserAsync(kim, Ct));
        var holding = await StaffScenario.HoldingIdAsync(this.database, kim, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.StaffMembers.TeamId, Ct);
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holding)).IsSuccess);
        }

        var outOfService = await ScheduleScenario.StepAsync(assignments, first, ResourcePatternAssignmentWorkflowAttribute.ReactivateStep, Ct);
        Assert.False(outOfService.IsSuccess);
        Assert.Contains(ResourcePatternAssignmentWorkflowAttribute.StaffSetUpOpenReason, outOfService.Error, StringComparison.Ordinal);
        Assert.Equal(ResourcePatternAssignmentState.Inactive, (await ScheduleScenario.StoredAsync(this.database, first, Ct))!.State);
        var saved = await ScheduleScenario.StepAsync(assignments, third, ResourcePatternAssignmentWorkflowAttribute.SaveActiveStep, Ct, edit => edit.EffectiveTo = Monday.AddDays(500));
        Assert.Contains(ResourcePatternAssignmentWorkflowAttribute.StaffSetUpOpenReason, saved.Error, StringComparison.Ordinal);
        Assert.Null((await ScheduleScenario.StoredAsync(this.database, third, Ct))!.EffectiveTo);
        Assert.False(await assignments.MayAssignToUserAsync(kim, Ct));
    }

    [Fact]
    public async Task A_create_takes_only_the_save_and_an_end_before_the_start_is_refused_with_its_reason()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Refusing Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var pattern = await scenario.PublishPatternAsync(this.database, "Weekly", Ct, ScheduleScenario.Weekdays(Eight, Sixteen));
        await using var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin));
        var assignments = host.Get<PatternAssignmentService>();

        foreach (var step in new[] { ResourcePatternAssignmentWorkflowAttribute.ActivateStep, ResourcePatternAssignmentWorkflowAttribute.ReactivateStep, ResourcePatternAssignmentWorkflowAttribute.DeleteStep, "no-such-step" })
        {
            var fresh = (await assignments.NewResourcePatternAssignmentAsync(kim, Ct)).Value!;
            fresh.WorkPatternId = pattern;
            fresh.EffectiveFrom = Monday;
            fresh.CycleAnchor = Monday;
            Assert.Equal(PatternAssignmentService.CreateBySaveOnly, (await assignments.CreateResourcePatternAssignmentAsync(fresh, step, Ct)).Error);
        }

        Assert.Empty((await host.Get<UserService>().PatternAssignments.QueryAssignmentsOfUserAsync(kim, new ListQuery(), Ct)).Value!.Rows);

        var id = (await ScheduleScenario.CreateAssignmentAsync(assignments, kim, pattern, Monday, null, Ct)).Value;
        var prepared = await ScheduleScenario.StepAsync(assignments, id, ResourcePatternAssignmentWorkflowAttribute.SaveStep, Ct, edit => edit.EffectiveTo = Monday.AddDays(-1));
        Assert.Equal(ScheduleRules.EndsBeforeStart, prepared.Error);
        Assert.True((await ScheduleScenario.StepAsync(assignments, id, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, Ct)).IsSuccess);
        var active = await ScheduleScenario.StepAsync(assignments, id, ResourcePatternAssignmentWorkflowAttribute.SaveActiveStep, Ct, edit => edit.EffectiveTo = Monday.AddDays(-1));
        Assert.Equal(ScheduleRules.EndsBeforeStart, active.Error);
        Assert.Null((await ScheduleScenario.StoredAsync(this.database, id, Ct))!.EffectiveTo);
    }

    private async Task<ServiceHost> CoordinatorHostAsync(ScheduleScenario scenario)
    {
        var coordinator = await scenario.StaffMembers.AddPersonAsync(this.database, "Casey", Ct);
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(coordinator, scenario.StaffMembers.RootId, scenario.StaffMembers.Onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
        }

        return ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(coordinator));
    }
}
