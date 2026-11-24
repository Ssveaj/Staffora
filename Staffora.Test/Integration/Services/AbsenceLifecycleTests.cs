using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Comments;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class AbsenceLifecycleTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly Monday = new(2026, 11, 9);
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById(ServiceHost.PlatformTimeZone);

    private readonly RealDatabase database;

    public AbsenceLifecycleTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stored_absence_takes_no_edits_with_its_register_or_report_step_whose_gate_is_not_its_saves()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Authority Absence", Ct);
        var kim = await scenario.AddWorkingStaffAsync(this.database, "Kim", Monday, Ct);
        await using var registrar = scenario.HostOf(this.database, scenario.Registrar);
        var service = registrar.Get<ResourceAbsenceService>();
        var id = (await AbsenceScenario.RegisterAsync(service, kim, AbsenceKind.PlannedLeave, Monday, Monday, Ct)).Value;

        foreach (var step in new[] { ResourceAbsenceWorkflowAttribute.RegisterStep, ResourceAbsenceWorkflowAttribute.ReportStep })
        {
            Assert.False((await AbsenceScenario.StepAsync(service, id, step, Ct, absence => absence.LastDay = Monday.AddDays(3))).IsSuccess);
            var updater = (await service.LoadResourceAbsenceUpdaterAsync(id, Ct)).Value!;
            updater.Entity.LastDay = Monday.AddDays(3);
            Assert.False((await registrar.Get<WorkflowService>().SaveAndTransitionAsync<ResourceAbsenceState, ResourceAbsence>(updater, step, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).IsSuccess);
        }

        var stored = (await AbsenceScenario.StoredAsync(this.database, id, Ct))!;
        Assert.Equal((ResourceAbsenceState.Requested, Monday), (stored.State, stored.LastDay));
    }

    [Fact]
    public async Task An_approved_absence_and_a_reported_sick_leave_block_the_staff_member_and_a_requested_planned_leave_does_not()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Blocking Absence", Ct);
        var kim = await scenario.AddWorkingStaffAsync(this.database, "Kim", Monday, Ct);
        var resource = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);
        var absences = staff.Get<ResourceAbsenceService>();

        var leave = await AbsenceScenario.ReportAsync(absences, kim, AbsenceKind.PlannedLeave, Monday, Monday.AddDays(1), Ct);
        Assert.True(leave.IsSuccess, leave.Error);
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, leave.Value, Ct))!.State);
        Assert.Empty((await WeekOfAsync(coordinator, resource)).Blocks);

        Assert.True((await AbsenceScenario.StepAsync(coordinator.Get<ResourceAbsenceService>(), leave.Value, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).IsSuccess);
        var approved = Assert.Single((await WeekOfAsync(coordinator, resource)).Blocks);
        Assert.Equal((Utc(Monday, 0), Utc(Monday.AddDays(2), 0), "On planned leave", (Guid?)leave.Value), (approved.StartUtc, approved.EndUtc, approved.Reason, approved.SourceId));

        var sick = await AbsenceScenario.ReportAsync(absences, kim, AbsenceKind.SickLeave, Monday.AddDays(3), Monday.AddDays(3), Ct, start: new TimeOnly(13, 0));
        Assert.True(sick.IsSuccess, sick.Error);
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, sick.Value, Ct))!.State);
        var week = await WeekOfAsync(coordinator, resource);
        Assert.Equal(
            [(Utc(Monday, 0), Utc(Monday.AddDays(2), 0), leave.Value), (Utc(Monday.AddDays(3), 13), Utc(Monday.AddDays(4), 0), sick.Value)],
            week.Blocks.Select(block => (block.StartUtc, block.EndUtc, block.SourceId!.Value)));
        Assert.Equal("On sick leave (reported)", week.Blocks[1].Reason);

        Assert.Equal(5, week.Working.Count);

        var rejected = (await AbsenceScenario.ReportAsync(absences, kim, AbsenceKind.PlannedLeave, Monday.AddDays(4), Monday.AddDays(4), Ct)).Value;
        Assert.True((await AbsenceScenario.StepAsync(coordinator.Get<ResourceAbsenceService>(), rejected, ResourceAbsenceWorkflowAttribute.RejectStep, Ct, comment: "Too short notice.")).IsSuccess);
        Assert.True((await AbsenceScenario.StepAsync(absences, leave.Value, ResourceAbsenceWorkflowAttribute.CancelApprovedStep, Ct)).IsSuccess);
        Assert.Equal([sick.Value], (await WeekOfAsync(coordinator, resource)).Blocks.Select(block => block.SourceId!.Value));

        Assert.True((await AbsenceScenario.StepAsync(coordinator.Get<ResourceAbsenceService>(), sick.Value, ResourceAbsenceWorkflowAttribute.RejectStep, Ct, comment: "Reported by mistake.")).IsSuccess);
        Assert.Empty((await WeekOfAsync(coordinator, resource)).Blocks);
    }

    [Fact]
    public async Task A_coordinators_sick_leave_registration_blocks_the_staff_member_at_once_with_a_history_row_and_a_notice_to_the_staff_member()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Registering Absence", Ct);
        var kim = await scenario.AddWorkingStaffAsync(this.database, "Kim", Monday, Ct);
        var resource = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);
        Assert.Contains(RolePermission.ApproveResourceAbsence, RolePresets.Coordinator.Permissions);

        var sick = await AbsenceScenario.RegisterAsync(coordinator.Get<ResourceAbsenceService>(), kim, AbsenceKind.SickLeave, Monday, Monday.AddDays(1), Ct);
        Assert.True(sick.IsSuccess, sick.Error);

        var stored = (await AbsenceScenario.StoredAsync(this.database, sick.Value, Ct))!;
        Assert.Equal((ResourceAbsenceState.Approved, AbsenceKind.SickLeave, scenario.Coordinator), (stored.State, stored.Kind, stored.CreatedById));
        Assert.Equal([ResourceAbsenceWorkflowAttribute.HandleSickStep], await AbsenceScenario.HistoryAsync(this.database, sick.Value, Ct));
        var block = Assert.Single((await WeekOfAsync(coordinator, resource)).Blocks);
        Assert.Equal((Utc(Monday, 0), Utc(Monday.AddDays(2), 0), "On sick leave"), (block.StartUtc, block.EndUtc, block.Reason));

        var notice = Assert.Single(await scenario.NoticesOfAsync(this.database, kim, Ct));
        Assert.Equal("Absence registered for you", notice.Subject);
        Assert.Equal("Hello Kim, a coordinator registered sick leave for you from 2026-11-09 to 2026-11-10. It is handled: you are not staffed for that time.", notice.Text);
        Assert.Equal($"?dlg=absence-{sick.Value}", notice.Link);
        await using (var context = this.database.CreateSystemContext())
        {
            var history = await context.StateHistoryEntries.AsNoTracking().SingleAsync(entry => entry.EntityId == sick.Value, Ct);
            Assert.Equal(history.Id, (await context.OutgoingMessages.AsNoTracking().SingleAsync(message => message.EntityId == sick.Value, Ct)).HistoryEntryId);
        }

        Assert.Empty(await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct));
    }

    [Fact]
    public async Task A_sick_leave_is_marked_handled_and_a_planned_leave_approved_each_by_its_own_step_only()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Handled Sick Leave", Ct);
        var kim = await scenario.AddWorkingStaffAsync(this.database, "Kim", Monday, Ct);
        var resource = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);
        var absences = coordinator.Get<ResourceAbsenceService>();
        var sick = (await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), kim, AbsenceKind.SickLeave, Monday, Monday, Ct)).Value;
        var leave = (await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), kim, AbsenceKind.PlannedLeave, Monday.AddDays(2), Monday.AddDays(2), Ct)).Value;

        Assert.Contains(ResourceAbsenceWorkflowAttribute.PlannedLeaveOnlyReason, (await AbsenceScenario.StepAsync(absences, sick, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).Error, StringComparison.Ordinal);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.SickLeaveOnlyReason, (await AbsenceScenario.StepAsync(absences, leave, ResourceAbsenceWorkflowAttribute.HandleSickStep, Ct)).Error, StringComparison.Ordinal);

        Assert.True((await AbsenceScenario.StepAsync(absences, sick, ResourceAbsenceWorkflowAttribute.HandleSickStep, Ct)).IsSuccess);
        Assert.Equal(ResourceAbsenceState.Approved, (await AbsenceScenario.StoredAsync(this.database, sick, Ct))!.State);
        Assert.Equal([ResourceAbsenceWorkflowAttribute.ReportStep, ResourceAbsenceWorkflowAttribute.HandleSickStep], await AbsenceScenario.HistoryAsync(this.database, sick, Ct));
        Assert.True((await AbsenceScenario.StepAsync(absences, leave, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).IsSuccess);
        Assert.Equal(2, (await WeekOfAsync(coordinator, resource)).Blocks.Count);
    }

    [Fact]
    public async Task A_coordinator_without_approval_right_registers_it_in_Requested_and_the_approvers_are_told()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Requesting Absence", Ct);
        var kim = await scenario.AddWorkingStaffAsync(this.database, "Kim", Monday, Ct);
        var resource = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
        await using var registrar = scenario.HostOf(this.database, scenario.Registrar);
        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);

        var sick = await AbsenceScenario.RegisterAsync(registrar.Get<ResourceAbsenceService>(), kim, AbsenceKind.SickLeave, Monday.AddDays(2), Monday.AddDays(2), Ct);
        Assert.True(sick.IsSuccess, sick.Error);
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, sick.Value, Ct))!.State);
        Assert.Equal([ResourceAbsenceWorkflowAttribute.RegisterStep], await AbsenceScenario.HistoryAsync(this.database, sick.Value, Ct));
        Assert.Equal([sick.Value], (await WeekOfAsync(coordinator, resource)).Blocks.Select(block => block.SourceId!.Value));

        var leave = await AbsenceScenario.RegisterAsync(registrar.Get<ResourceAbsenceService>(), kim, AbsenceKind.PlannedLeave, Monday.AddDays(4), Monday.AddDays(4), Ct);
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, leave.Value, Ct))!.State);
        Assert.Equal([sick.Value], (await WeekOfAsync(coordinator, resource)).Blocks.Select(block => block.SourceId!.Value));

        Assert.Equal(
            [
                ("Absence registered for you", "Hello Kim, a coordinator registered planned leave for you on 2026-11-13. It waits for an approver's decision."),
                ("Absence registered for you", "Hello Kim, a coordinator registered sick leave for you on 2026-11-11. It keeps you from being staffed for that time already, and a coordinator marks it handled."),
            ],
            (await scenario.NoticesOfAsync(this.database, kim, Ct)).Select(notice => (notice.Subject, notice.Text)));
        foreach (var approver in new[] { scenario.Coordinator, scenario.Approver })
        {
            Assert.Equal(
                ["Leave requested by Kim Example", "Sick leave reported for Kim Example"],
                (await scenario.NoticesOfAsync(this.database, approver, Ct)).Select(notice => notice.Subject));
        }

        Assert.Empty(await scenario.NoticesOfAsync(this.database, scenario.Registrar, Ct));
    }

    [Fact]
    public async Task The_staff_member_and_the_coordinators_receive_their_notices()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Telling Absence", Ct);
        var kim = await scenario.AddWorkingStaffAsync(this.database, "Kim", Monday, Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);
        await using var approver = scenario.HostOf(this.database, scenario.Approver);
        var own = staff.Get<ResourceAbsenceService>();

        var leave = (await AbsenceScenario.ReportAsync(own, kim, AbsenceKind.PlannedLeave, Monday.AddDays(14), Monday.AddDays(18), Ct)).Value;
        var sick = (await AbsenceScenario.ReportAsync(own, kim, AbsenceKind.SickLeave, Monday, Monday, Ct, start: new TimeOnly(10, 0), end: new TimeOnly(14, 30))).Value;
        var told = await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct);
        Assert.Equal(
            [
                ("Sick leave reported for Kim Example", "Kim Example is reported sick on 2026-11-09 from 10:00 until 14:30. They are not staffed for that time, and an approver confirms or rejects the report.", $"?dlg=absence-{sick}"),
                ("Leave requested by Kim Example", "Kim Example asks for planned leave from 2026-11-23 to 2026-11-27. It blocks them from staffing only once an approver approves it.", $"?dlg=absence-{leave}"),
            ],
            told);
        Assert.Equal(told, await scenario.NoticesOfAsync(this.database, scenario.Coordinator, Ct));
        Assert.Empty(await scenario.NoticesOfAsync(this.database, scenario.Registrar, Ct));
        Assert.Empty(await scenario.NoticesOfAsync(this.database, scenario.Viewer, Ct));
        Assert.Empty(await scenario.NoticesOfAsync(this.database, kim, Ct));

        Assert.True((await AbsenceScenario.StepAsync(coordinator.Get<ResourceAbsenceService>(), leave, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).IsSuccess);
        Assert.True((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), sick, ResourceAbsenceWorkflowAttribute.RejectStep, Ct, comment: "No doctor's note for 4711.")).IsSuccess);

        var other = (await AbsenceScenario.ReportAsync(own, kim, AbsenceKind.PlannedLeave, Monday.AddDays(30), Monday.AddDays(30), Ct)).Value;
        Assert.Contains(ResourceAbsenceWorkflowAttribute.OwnCancelReason, (await AbsenceScenario.StepAsync(coordinator.Get<ResourceAbsenceService>(), other, ResourceAbsenceWorkflowAttribute.CancelStep, Ct)).Error, StringComparison.Ordinal);
        var mine = (await AbsenceScenario.ReportAsync(own, kim, AbsenceKind.PlannedLeave, Monday.AddDays(40), Monday.AddDays(40), Ct)).Value;
        Assert.True((await AbsenceScenario.StepAsync(own, mine, ResourceAbsenceWorkflowAttribute.CancelStep, Ct)).IsSuccess);

        Assert.Equal(
            [
                ("Your sick leave was rejected", "Hello Kim, an approver rejected your sick leave on 2026-11-09 from 10:00 until 14:30. It does not keep you from being staffed. The reason is written on the absence."),
                ("Your planned leave is approved", "Hello Kim, an approver approved your planned leave from 2026-11-23 to 2026-11-27. You are not staffed for that time."),
            ],
            (await scenario.NoticesOfAsync(this.database, kim, Ct)).Select(notice => (notice.Subject, notice.Text)));
        Assert.Equal(
            ("An absence was canceled", "Kim Example canceled their planned leave on 2026-12-19. It no longer keeps them from being staffed."),
            (await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct)).Select(notice => (notice.Subject, notice.Text)).First());
    }

    [Fact]
    public async Task A_staff_member_reports_edits_and_cancels_their_own_absence_only_by_every_path()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Owning Absence", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var robin = await scenario.AddStaffAsync(this.database, "Robin", Ct);
        Assert.Contains(RolePermission.ReportOwnAbsence, RolePresets.Staff.Permissions);
        await using var kims = scenario.HostOf(this.database, kim);
        await using var robins = scenario.HostOf(this.database, robin);
        var service = kims.Get<ResourceAbsenceService>();
        var robinsAbsence = (await AbsenceScenario.ReportAsync(robins.Get<ResourceAbsenceService>(), robin, AbsenceKind.PlannedLeave, Monday, Monday, Ct)).Value;

        var own = await AbsenceScenario.ReportAsync(service, kim, AbsenceKind.PlannedLeave, Monday, Monday, Ct);
        Assert.True(own.IsSuccess, own.Error);
        Assert.True((await AbsenceScenario.StepAsync(service, own.Value, ResourceAbsenceWorkflowAttribute.SaveStep, Ct, edit => edit.LastDay = Monday.AddDays(2))).IsSuccess);
        Assert.Equal(Monday.AddDays(2), (await AbsenceScenario.StoredAsync(this.database, own.Value, Ct))!.LastDay);
        Assert.True(await service.MayAddAbsenceForUserAsync(kim, Ct));
        Assert.Equal([own.Value], (await service.QueryAbsencesAsync(new ListQuery(), Ct)).Value!.Rows.Select(row => row.Id));

        Assert.False(await service.MayAddAbsenceForUserAsync(robin, Ct));
        Assert.Equal(ResourceAbsenceService.NoReport, (await service.NewResourceAbsenceAsync(robin, Ct)).Error);
        var crafted = (await service.NewResourceAbsenceAsync(kim, Ct)).Value!;
        crafted.ResourceId = await ScheduleScenario.ResourceOfAsync(this.database, robin, Ct);
        crafted.Resource = null!;
        var forRobin = await service.CreateResourceAbsenceAsync(crafted, ResourceAbsenceWorkflowAttribute.ReportStep, Ct);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.OwnReason, forRobin.Error, StringComparison.Ordinal);
        Assert.Equal(ResourceAbsenceService.NoReport, (await service.CreateResourceAbsenceAsync(crafted, ResourceAbsenceWorkflowAttribute.RegisterStep, Ct)).Error);
        Assert.Equal(ResourceAbsenceService.CreateByReportOrRegister, (await service.CreateResourceAbsenceAsync(crafted, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).Error);

        Assert.Equal(ResourceAbsenceService.NotFound, (await service.LoadResourceAbsenceUpdaterAsync(robinsAbsence, Ct)).Error);
        Assert.Equal(ResourceAbsenceService.NotFound, (await service.GetResourceAbsenceHistoryAsync(robinsAbsence, StateHistoryQuery.Newest(3), Ct)).Error);
        Assert.Equal(ResourceAbsenceService.NotFound, (await service.GetResourceAbsenceEditorOptionsAsync(robinsAbsence, null, Ct)).Error);

        var workflows = kims.Get<WorkflowService>();
        var cancel = await workflows.TransitionEntityAsync<ResourceAbsenceState, ResourceAbsence>(robinsAbsence, ResourceAbsenceWorkflowAttribute.CancelStep, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.MayChangeReason, cancel.Error, StringComparison.Ordinal);
        var save = await workflows.TransitionEntityAsync<ResourceAbsenceState, ResourceAbsence>(robinsAbsence, ResourceAbsenceWorkflowAttribute.SaveStep, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.MayChangeReason, save.Error, StringComparison.Ordinal);
        var approve = await workflows.TransitionEntityAsync<ResourceAbsenceState, ResourceAbsence>(own.Value, ResourceAbsenceWorkflowAttribute.ApproveStep, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.False(approve.IsSuccess);
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, robinsAbsence, Ct))!.State);

        Assert.True((await AbsenceScenario.StepAsync(service, own.Value, ResourceAbsenceWorkflowAttribute.CancelStep, Ct)).IsSuccess);
        Assert.Equal(ResourceAbsenceState.Canceled, (await AbsenceScenario.StoredAsync(this.database, own.Value, Ct))!.State);
        Assert.Equal([ResourceAbsenceWorkflowAttribute.ReportStep, ResourceAbsenceWorkflowAttribute.SaveStep, ResourceAbsenceWorkflowAttribute.CancelStep], await AbsenceScenario.HistoryAsync(this.database, own.Value, Ct));
    }

    [Fact]
    public async Task Edits_and_cancellations_take_the_staff_member_or_a_manager_and_decisions_an_approver_who_is_not_the_staff_member()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Deciding Absence", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        await using var registrar = scenario.HostOf(this.database, scenario.Registrar);
        await using var approver = scenario.HostOf(this.database, scenario.Approver);
        await using var viewer = scenario.HostOf(this.database, scenario.Viewer);
        var first = (await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), kim, AbsenceKind.PlannedLeave, Monday, Monday, Ct)).Value;
        var second = (await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), kim, AbsenceKind.PlannedLeave, Monday.AddDays(7), Monday.AddDays(7), Ct)).Value;

        Assert.True((await viewer.Get<ResourceAbsenceService>().LoadResourceAbsenceUpdaterAsync(first, Ct)).IsSuccess);
        Assert.True((await viewer.Get<ResourceAbsenceService>().GetResourceAbsenceEditorOptionsAsync(first, null, Ct)).Value!.FieldsLocked);
        Assert.Equal(ResourceAbsenceService.NoChange, (await AbsenceScenario.StepAsync(viewer.Get<ResourceAbsenceService>(), first, ResourceAbsenceWorkflowAttribute.CancelStep, Ct)).Error);
        var viewerCancel = await viewer.Get<WorkflowService>().TransitionEntityAsync<ResourceAbsenceState, ResourceAbsence>(first, ResourceAbsenceWorkflowAttribute.CancelStep, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.MayChangeReason, viewerCancel.Error, StringComparison.Ordinal);

        Assert.Contains(ResourceAbsenceWorkflowAttribute.MayChangeReason, (await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), first, ResourceAbsenceWorkflowAttribute.SaveStep, Ct, edit => edit.LastDay = Monday.AddDays(1))).Error, StringComparison.Ordinal);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.MayChangeReason, (await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), first, ResourceAbsenceWorkflowAttribute.CancelStep, Ct)).Error, StringComparison.Ordinal);
        Assert.True((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), first, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).IsSuccess);

        var refused = await AbsenceScenario.StepAsync(registrar.Get<ResourceAbsenceService>(), second, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct);
        Assert.Contains("Approve Staff Absence", refused.Error, StringComparison.Ordinal);
        Assert.True((await AbsenceScenario.StepAsync(registrar.Get<ResourceAbsenceService>(), second, ResourceAbsenceWorkflowAttribute.SaveStep, Ct, edit => edit.EndTime = new TimeOnly(12, 0))).IsSuccess);
        Assert.Equal(new TimeOnly(12, 0), (await AbsenceScenario.StoredAsync(this.database, second, Ct))!.EndTime);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.OwnCancelReason, (await AbsenceScenario.StepAsync(registrar.Get<ResourceAbsenceService>(), first, ResourceAbsenceWorkflowAttribute.CancelApprovedStep, Ct)).Error, StringComparison.Ordinal);
        Assert.True((await AbsenceScenario.StepAsync(staff.Get<ResourceAbsenceService>(), first, ResourceAbsenceWorkflowAttribute.CancelApprovedStep, Ct)).IsSuccess);

        var changed = await AbsenceScenario.StepAsync(staff.Get<ResourceAbsenceService>(), second, ResourceAbsenceWorkflowAttribute.SaveStep, Ct, edit => edit.Kind = AbsenceKind.SickLeave);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.KindUnchangedReason, changed.Error, StringComparison.Ordinal);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.KindUnchangedReason, (await AbsenceScenario.StepAsync(registrar.Get<ResourceAbsenceService>(), second, ResourceAbsenceWorkflowAttribute.SaveStep, Ct, edit => edit.Kind = AbsenceKind.SickLeave)).Error, StringComparison.Ordinal);
        Assert.Equal(AbsenceKind.PlannedLeave, (await AbsenceScenario.StoredAsync(this.database, second, Ct))!.Kind);

        Assert.False((await AbsenceScenario.StepAsync(registrar.Get<ResourceAbsenceService>(), first, ResourceAbsenceWorkflowAttribute.SaveStep, Ct)).IsSuccess);

        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(scenario.Coordinator, scenario.StaffMembers.TeamId, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        }

        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);
        var service = coordinator.Get<ResourceAbsenceService>();
        Assert.Contains(ResourceAbsenceWorkflowAttribute.OnBehalfReason, (await AbsenceScenario.RegisterAsync(service, scenario.Coordinator, AbsenceKind.SickLeave, Monday, Monday, Ct)).Error, StringComparison.Ordinal);
        var theirs = await AbsenceScenario.ReportAsync(service, scenario.Coordinator, AbsenceKind.PlannedLeave, Monday, Monday, Ct);
        Assert.True(theirs.IsSuccess, theirs.Error);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.NotOwnDecisionReason, (await AbsenceScenario.StepAsync(service, theirs.Value, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).Error, StringComparison.Ordinal);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.NotOwnDecisionReason, (await AbsenceScenario.StepAsync(service, theirs.Value, ResourceAbsenceWorkflowAttribute.RejectStep, Ct, comment: "Mine.")).Error, StringComparison.Ordinal);
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, theirs.Value, Ct))!.State);
        Assert.True((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), theirs.Value, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).IsSuccess);

        var kimsRecord = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
        ResourceAbsence NewAbsence() => new() { TenantId = scenario.TenantId, ResourceId = kimsRecord, Kind = AbsenceKind.SickLeave, FirstDay = Monday, LastDay = Monday };
        var unmanaged = await approver.Get<WorkflowService>().CreateAndTransitionAsync<ResourceAbsenceState, ResourceAbsence>(NewAbsence(), ResourceAbsenceWorkflowAttribute.HandleSickStep, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.NewByManagerReason, unmanaged.Error, StringComparison.Ordinal);

        var before = await AbsencesOfAsync(kim);
        var rejectedNew = await approver.Get<WorkflowService>().CreateAndTransitionAsync<ResourceAbsenceState, ResourceAbsence>(NewAbsence(), ResourceAbsenceWorkflowAttribute.RejectStep, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, comment: "Made up.", cancellationToken: Ct);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.StoredReason, rejectedNew.Error, StringComparison.Ordinal);
        var savedNew = await registrar.Get<WorkflowService>().CreateAndTransitionAsync<ResourceAbsenceState, ResourceAbsence>(NewAbsence(), ResourceAbsenceWorkflowAttribute.SaveStep, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.StoredReason, savedNew.Error, StringComparison.Ordinal);
        var canceledNew = await registrar.Get<WorkflowService>().CreateAndTransitionAsync<ResourceAbsenceState, ResourceAbsence>(NewAbsence(), ResourceAbsenceWorkflowAttribute.CancelStep, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.MayChangeReason, canceledNew.Error, StringComparison.Ordinal);
        Assert.Equal(before, await AbsencesOfAsync(kim));
        Assert.DoesNotContain(await scenario.NoticesOfAsync(this.database, kim, Ct), notice => notice.Subject.Contains("rejected", StringComparison.Ordinal));

        var again = await AbsenceScenario.StepAsync(service, second, ResourceAbsenceWorkflowAttribute.RegisterStep, Ct);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.IsNewReason, again.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_staff_member_and_the_approvers_alone_read_the_reason_of_a_rejection_by_any_path()
    {
        const string Reason = "Already three staff members away that week, see 4711.";
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Reasoning Absence", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        await using var approver = scenario.HostOf(this.database, scenario.Approver);
        await using var viewer = scenario.HostOf(this.database, scenario.Viewer);
        await using var registrar = scenario.HostOf(this.database, scenario.Registrar);
        var own = staff.Get<ResourceAbsenceService>();
        var id = (await AbsenceScenario.ReportAsync(own, kim, AbsenceKind.PlannedLeave, Monday, Monday.AddDays(4), Ct)).Value;

        Assert.False((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.RejectStep, Ct)).IsSuccess);
        Assert.True((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.RejectStep, Ct, comment: Reason)).IsSuccess);

        foreach (var reader in new[] { approver.Get<ResourceAbsenceService>(), own })
        {
            var thread = Assert.Single((await reader.GetResourceAbsenceCommentsAsync(id, Ct)).Value!);
            Assert.Equal((CommentAudience.Staff, Reason, false), (thread.Audience, thread.Comments.Single().Text, thread.CanPost));
        }

        Assert.Null((await own.GetResourceAbsenceEditorOptionsAsync(id, null, Ct)).Value!.Note);

        Assert.DoesNotContain(await scenario.NoticesOfAsync(this.database, kim, Ct), notice => notice.Text.Contains("4711", StringComparison.Ordinal) || notice.Subject.Contains("4711", StringComparison.Ordinal));
        Assert.Equal(StateHistoryService.OwnersOnly, (await own.GetResourceAbsenceHistoryAsync(id, StateHistoryQuery.Newest(10), Ct)).Error);
        Assert.DoesNotContain("4711", System.Text.Json.JsonSerializer.Serialize((await own.QueryAbsencesAsync(new ListQuery(), Ct)).Value!), StringComparison.Ordinal);

        foreach (var reader in new[] { viewer, registrar })
        {
            Assert.Empty((await reader.Get<ResourceAbsenceService>().GetResourceAbsenceCommentsAsync(id, Ct)).Value!);
            Assert.Empty((await reader.Get<CommentService>().GetThreadsAsync<ResourceAbsence>(id, [ResourceAbsenceService.ApproverThread], Ct)).Value!);
            Assert.Equal(ResourceAbsenceService.ReasonWithApprovers, (await reader.Get<ResourceAbsenceService>().GetResourceAbsenceEditorOptionsAsync(id, null, Ct)).Value!.Note);
        }

        await using var context = this.database.CreateSystemContext();
        Assert.Equal([CommentAudience.Staff], await context.CommentContainers.AsNoTracking().Where(container => container.EntityId == id).Select(container => container.Audience).ToListAsync(Ct));
    }

    [Fact]
    public async Task Another_tenants_coordinator_finds_none_of_the_absences()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Isolated Absence", Ct);
        var kim = await scenario.AddWorkingStaffAsync(this.database, "Kim", Monday, Ct);
        await using (var staff = scenario.HostOf(this.database, kim))
        {
            Assert.True((await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), kim, AbsenceKind.SickLeave, Monday, Monday, Ct)).IsSuccess);
        }

        var id = await SomeAbsenceOfAsync(kim);
        var other = await AbsenceScenario.CreateAsync(this.database, "Other Isolated Absence", Ct);
        await using var foreign = other.HostOf(this.database, other.Coordinator);
        var service = foreign.Get<ResourceAbsenceService>();
        Assert.Equal(ResourceAbsenceService.NotFound, (await service.LoadResourceAbsenceUpdaterAsync(id, Ct)).Error);
        Assert.Equal(ResourceAbsenceService.NotFound, (await service.GetResourceAbsenceCommentsAsync(id, Ct)).Error);
        Assert.Empty((await service.QueryAbsencesAsync(new ListQuery(), Ct)).Value!.Rows);
        Assert.Empty((await service.QueryAbsencesOfUserAsync(kim, new ListQuery(), Ct)).Value!.Rows);
        Assert.Equal(ResourceAbsenceService.StaffNotFound, (await service.NewResourceAbsenceAsync(kim, Ct)).Error);
        Assert.False(await service.MayAddAbsenceForUserAsync(kim, Ct));
        Assert.Empty((await foreign.Get<IStaffAvailability>().ReadAsync([await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct)], Monday, Monday, Ct)).Value!.StaffMembers);
        Assert.DoesNotContain((await service.GetRegistrableStaffMembersAsync(Ct)).Value!, choice => choice.UserId == kim);
    }

    [Fact]
    public async Task The_user_dialogs_Absence_view_reads_through_the_absence_service_by_its_readers_and_refuses_anyone_else()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Viewing Absence", Ct);
        var kim = await scenario.AddWorkingStaffAsync(this.database, "Kim", Monday, Ct);
        await using (var staff = scenario.HostOf(this.database, kim))
        {
            Assert.True((await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), kim, AbsenceKind.SickLeave, Monday, Monday, Ct)).IsSuccess);
        }

        var id = await SomeAbsenceOfAsync(kim);
        var manager = await scenario.StaffMembers.AddPersonAsync(this.database, "Morgan", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, manager, Ct, RolePermission.ManageResourceAbsence, RolePermission.UsersView);
        var plain = await scenario.StaffMembers.AddPersonAsync(this.database, "Parker", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, plain, Ct, RolePermission.UsersView, RolePermission.ResourcesView);

        await using (var viewer = scenario.HostOf(this.database, scenario.Viewer))
        {
            Assert.Equal([id], (await viewer.Get<UserService>().Absences.QueryAbsencesOfUserAsync(kim, new ListQuery(), Ct)).Value!.Rows.Select(row => row.Id));
        }

        await using (var managing = scenario.HostOf(this.database, manager))
        {
            Assert.Equal([id], (await managing.Get<UserService>().Absences.QueryAbsencesOfUserAsync(kim, new ListQuery(), Ct)).Value!.Rows.Select(row => row.Id));
            Assert.True((await managing.Get<ResourceAbsenceService>().LoadResourceAbsenceUpdaterAsync(id, Ct)).IsSuccess);
        }

        await using (var reader = scenario.HostOf(this.database, plain))
        {
            Assert.Equal(ResourceAbsenceService.NoView, (await reader.Get<UserService>().Absences.QueryAbsencesOfUserAsync(kim, new ListQuery(), Ct)).Error);
        }
    }

    [Fact]
    public async Task The_period_is_refused_with_its_reason_whoever_writes_it()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Dating Absence", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        var service = staff.Get<ResourceAbsenceService>();

        Assert.Equal(AbsenceRules.EndsBeforeStart, (await AbsenceScenario.ReportAsync(service, kim, AbsenceKind.PlannedLeave, Monday, Monday.AddDays(-1), Ct)).Error);
        Assert.Equal(AbsenceRules.TimesOutOfOrder, (await AbsenceScenario.ReportAsync(service, kim, AbsenceKind.SickLeave, Monday, Monday, Ct, start: new TimeOnly(14, 0), end: new TimeOnly(9, 0))).Error);
        Assert.Equal(AbsenceRules.EndsAtMidnight, (await AbsenceScenario.ReportAsync(service, kim, AbsenceKind.SickLeave, Monday, Monday.AddDays(1), Ct, end: TimeOnly.MinValue)).Error);
        var id = (await AbsenceScenario.ReportAsync(service, kim, AbsenceKind.PlannedLeave, Monday, Monday, Ct)).Value;
        Assert.Equal(AbsenceRules.EndsBeforeStart, (await AbsenceScenario.StepAsync(service, id, ResourceAbsenceWorkflowAttribute.SaveStep, Ct, edit => edit.FirstDay = Monday.AddDays(1))).Error);
        Assert.Equal(Monday, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.FirstDay);

        var resource = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
        foreach (var (first, start, last, end) in new (DateOnly, TimeOnly?, DateOnly, TimeOnly?)[] { (Monday, null, Monday.AddDays(-1), null), (Monday, new TimeOnly(14, 0), Monday, new TimeOnly(9, 0)), (Monday, null, Monday, TimeOnly.MinValue) })
        {
            await using var context = this.database.CreateSystemContext();
            context.ResourceAbsences.Add(new ResourceAbsence { TenantId = scenario.TenantId, ResourceId = resource, FirstDay = first, StartTime = start, LastDay = last, EndTime = end, CreatedById = kim });
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
            Assert.Contains("CK_ResourceAbsences_", exception.InnerException!.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Approving_and_canceling_at_once_let_exactly_one_through()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Racing Absence", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        for (var round = 0; round < 8; round++)
        {
            await using var staff = scenario.HostOf(this.database, kim);
            await using var approver = scenario.HostOf(this.database, scenario.Approver);
            var id = (await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), kim, AbsenceKind.PlannedLeave, Monday.AddDays(round * 7), Monday.AddDays(round * 7), Ct)).Value;
            var cancel = (await staff.Get<ResourceAbsenceService>().LoadResourceAbsenceUpdaterAsync(id, Ct)).Value!;
            var approve = (await approver.Get<ResourceAbsenceService>().LoadResourceAbsenceUpdaterAsync(id, Ct)).Value!;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = Task.Run(async () => { await start.Task; return await staff.Get<ResourceAbsenceService>().UpdateResourceAbsenceAndTransitionAsync(cancel, ResourceAbsenceWorkflowAttribute.CancelStep, null, Ct); }, Ct);
            var approved = Task.Run(async () => { await start.Task; return await approver.Get<ResourceAbsenceService>().UpdateResourceAbsenceAndTransitionAsync(approve, ResourceAbsenceWorkflowAttribute.ApproveStep, null, Ct); }, Ct);
            start.SetResult();
            var results = await Task.WhenAll(canceled, approved);

            Assert.Single(results, result => result.IsSuccess);
            var state = (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State;
            Assert.Equal(results[0].IsSuccess ? ResourceAbsenceState.Canceled : ResourceAbsenceState.Approved, state);
            Assert.Single(await AbsenceScenario.HistoryAsync(this.database, id, Ct), key => key != ResourceAbsenceWorkflowAttribute.ReportStep);
        }
    }

    [Fact]
    public async Task A_decision_or_a_cancellation_takes_no_edits_whoever_decides_by_every_path()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Uneditable Decision", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        await using var approver = scenario.HostOf(this.database, scenario.Approver);
        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);
        var id = (await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), kim, AbsenceKind.PlannedLeave, Monday, Monday, Ct)).Value;

        static void Edit(ResourceAbsence absence) => (absence.Kind, absence.FirstDay, absence.LastDay) = (AbsenceKind.SickLeave, Monday.AddDays(-30), Monday.AddDays(30));
        foreach (var host in new[] { approver, coordinator })
        {
            foreach (var (step, comment) in new (string, string?)[] { (ResourceAbsenceWorkflowAttribute.ApproveStep, null), (ResourceAbsenceWorkflowAttribute.RejectStep, "Not that week."), (ResourceAbsenceWorkflowAttribute.CancelStep, null) })
            {
                Assert.Equal(TransitionRefusals.PendingEdits, (await AbsenceScenario.StepAsync(host.Get<ResourceAbsenceService>(), id, step, Ct, Edit, comment)).Error);
                var updater = (await host.Get<ResourceAbsenceService>().LoadResourceAbsenceUpdaterAsync(id, Ct)).Value!;
                Edit(updater.Entity);
                var past = await host.Get<WorkflowService>().SaveAndTransitionAsync<ResourceAbsenceState, ResourceAbsence>(updater, step, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, comment, Ct);
                Assert.Equal(TransitionRefusals.PendingEdits, past.Error);
            }
        }

        var stored = (await AbsenceScenario.StoredAsync(this.database, id, Ct))!;
        Assert.Equal((ResourceAbsenceState.Requested, AbsenceKind.PlannedLeave, Monday, Monday), (stored.State, stored.Kind, stored.FirstDay, stored.LastDay));
        Assert.Equal([ResourceAbsenceWorkflowAttribute.ReportStep], await AbsenceScenario.HistoryAsync(this.database, id, Ct));
        Assert.Empty(await scenario.NoticesOfAsync(this.database, kim, Ct));

        Assert.True((await AbsenceScenario.StepAsync(coordinator.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).IsSuccess);
        Assert.Equal(TransitionRefusals.PendingEdits, (await AbsenceScenario.StepAsync(coordinator.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.CancelApprovedStep, Ct, Edit)).Error);
        stored = (await AbsenceScenario.StoredAsync(this.database, id, Ct))!;
        Assert.Equal((ResourceAbsenceState.Approved, AbsenceKind.PlannedLeave, Monday, Monday), (stored.State, stored.Kind, stored.FirstDay, stored.LastDay));
    }

    [Fact]
    public async Task A_decision_is_refused_on_an_absence_saved_after_the_decider_opened_it()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Stale Decision", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        await using var approver = scenario.HostOf(this.database, scenario.Approver);
        var decider = approver.Get<ResourceAbsenceService>();
        var id = (await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), kim, AbsenceKind.PlannedLeave, Monday, Monday, Ct)).Value;

        var opened = (await decider.LoadResourceAbsenceUpdaterAsync(id, Ct)).Value!;
        Assert.True((await AbsenceScenario.StepAsync(staff.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.SaveStep, Ct, edit => edit.LastDay = Monday.AddDays(4))).IsSuccess);

        Assert.Equal(TransitionRefusals.ChangedSinceOpened(nameof(ResourceAbsence), "Approve"), (await decider.UpdateResourceAbsenceAndTransitionAsync(opened, ResourceAbsenceWorkflowAttribute.ApproveStep, null, Ct)).Error);
        Assert.Equal(TransitionRefusals.ChangedSinceOpened(nameof(ResourceAbsence), "Reject"), (await decider.UpdateResourceAbsenceAndTransitionAsync(opened, ResourceAbsenceWorkflowAttribute.RejectStep, "One day is fine, five are not.", Ct)).Error);
        var stored = (await AbsenceScenario.StoredAsync(this.database, id, Ct))!;
        Assert.Equal((ResourceAbsenceState.Requested, Monday.AddDays(4), false), (stored.State, stored.LastDay, stored.IsInTransition));

        Assert.True((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).IsSuccess);
        stored = (await AbsenceScenario.StoredAsync(this.database, id, Ct))!;
        Assert.Equal((ResourceAbsenceState.Approved, Monday.AddDays(4)), (stored.State, stored.LastDay));
        Assert.Equal([ResourceAbsenceWorkflowAttribute.ReportStep, ResourceAbsenceWorkflowAttribute.SaveStep, ResourceAbsenceWorkflowAttribute.ApproveStep], await AbsenceScenario.HistoryAsync(this.database, id, Ct));
    }

    [Fact]
    public async Task A_save_and_an_approval_at_once_never_approve_a_period_the_approver_did_not_see()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Racing Decision", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var (saves, approvals) = (0, 0);
        for (var round = 0; round < 10; round++)
        {
            await using var staff = scenario.HostOf(this.database, kim);
            await using var approver = scenario.HostOf(this.database, scenario.Approver);
            var day = Monday.AddDays(round * 7);
            var id = (await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), kim, AbsenceKind.PlannedLeave, day, day, Ct)).Value;
            var seen = (await approver.Get<ResourceAbsenceService>().LoadResourceAbsenceUpdaterAsync(id, Ct)).Value!;
            var edit = (await staff.Get<ResourceAbsenceService>().LoadResourceAbsenceUpdaterAsync(id, Ct)).Value!;
            edit.Entity.LastDay = day.AddDays(2);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var saved = Task.Run(async () => { await start.Task; return await staff.Get<ResourceAbsenceService>().UpdateResourceAbsenceAndTransitionAsync(edit, ResourceAbsenceWorkflowAttribute.SaveStep, null, Ct); }, Ct);
            var approved = Task.Run(async () => { await start.Task; return await approver.Get<ResourceAbsenceService>().UpdateResourceAbsenceAndTransitionAsync(seen, ResourceAbsenceWorkflowAttribute.ApproveStep, null, Ct); }, Ct);
            start.SetResult();
            var results = await Task.WhenAll(saved, approved);

            Assert.Single(results, result => result.IsSuccess);
            var stored = (await AbsenceScenario.StoredAsync(this.database, id, Ct))!;
            Assert.False(stored.IsInTransition);
            if (results[1].IsSuccess)
            {
                approvals++;
                Assert.Equal((ResourceAbsenceState.Approved, day), (stored.State, stored.LastDay));
            }
            else
            {
                saves++;
                Assert.Equal((ResourceAbsenceState.Requested, day.AddDays(2)), (stored.State, stored.LastDay));
            }
        }

        Assert.Equal(10, saves + approvals);
    }

    [Fact]
    public async Task A_new_or_approved_absence_needs_a_staff_member_in_service_and_a_rejection_does_not()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Serving Absence", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var robin = await scenario.AddStaffAsync(this.database, "Robin", Ct);
        var sam = await scenario.AddStaffAsync(this.database, "Sam", Ct);

        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, kim, Ct, RolePermission.ReportOwnAbsence);
        var kims = await ThreeRequestedAsync(kim);
        var robins = await ThreeRequestedAsync(robin);

        var holding = await StaffScenario.HoldingIdAsync(this.database, kim, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.StaffMembers.TeamId, Ct);
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holding)).IsSuccess);
        }

        Assert.Equal(ResourceState.Inactive, (await StaffScenario.RecordOfAsync(this.database, kim, Ct)).State);
        await using (var staff = scenario.HostOf(this.database, kim))
        {
            Assert.False(await staff.Get<ResourceAbsenceService>().MayAddAbsenceForUserAsync(kim, Ct));
            Assert.Contains(ResourceAbsenceWorkflowAttribute.StaffInServiceReason, (await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), kim, AbsenceKind.SickLeave, Monday, Monday, Ct)).Error, StringComparison.Ordinal);
        }

        await AssertOutOfServiceAsync(kim, kims);

        await UserEdits.SetStateInTableAsync(this.database, robin, UserState.Inactive, Ct);
        Assert.Equal(ResourceState.Active, (await StaffScenario.RecordOfAsync(this.database, robin, Ct)).State);
        await AssertOutOfServiceAsync(robin, robins);

        async Task<(Guid Approve, Guid Reject, Guid Cancel)> ThreeRequestedAsync(Guid user)
        {
            await using var staff = scenario.HostOf(this.database, user);
            var ids = new List<Guid>();
            foreach (var week in new[] { 0, 1, 2 })
            {
                var reported = await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), user, AbsenceKind.PlannedLeave, Monday.AddDays(week * 7), Monday.AddDays(week * 7), Ct);
                Assert.True(reported.IsSuccess, reported.Error);
                ids.Add(reported.Value);
            }

            return (ids[0], ids[1], ids[2]);
        }

        async Task AssertOutOfServiceAsync(Guid user, (Guid Approve, Guid Reject, Guid Cancel) absences)
        {
            await using var registrar = scenario.HostOf(this.database, scenario.Registrar);
            await using var approver = scenario.HostOf(this.database, scenario.Approver);
            var registrars = registrar.Get<ResourceAbsenceService>();

            Assert.Contains(ResourceAbsenceWorkflowAttribute.StaffInServiceReason, (await AbsenceScenario.RegisterAsync(registrars, user, AbsenceKind.SickLeave, Monday, Monday, Ct)).Error, StringComparison.Ordinal);
            Assert.Contains(ResourceAbsenceWorkflowAttribute.StaffInServiceReason, (await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), absences.Approve, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).Error, StringComparison.Ordinal);
            Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, absences.Approve, Ct))!.State);
            Assert.False(await registrars.MayAddAbsenceForUserAsync(user, Ct));
            var choices = (await registrars.GetRegistrableStaffMembersAsync(Ct)).Value!;
            Assert.DoesNotContain(choices, choice => choice.UserId == user);
            Assert.Contains(choices, choice => choice.UserId == sam);
            Assert.True(await registrars.MayAddAbsenceForUserAsync(sam, Ct));

            Assert.True((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), absences.Reject, ResourceAbsenceWorkflowAttribute.RejectStep, Ct, comment: "Out of service.")).IsSuccess);
            Assert.Contains(ResourceAbsenceWorkflowAttribute.OwnCancelReason, (await AbsenceScenario.StepAsync(registrars, absences.Cancel, ResourceAbsenceWorkflowAttribute.CancelStep, Ct)).Error, StringComparison.Ordinal);
            Assert.Equal(ResourceAbsenceState.Rejected, (await AbsenceScenario.StoredAsync(this.database, absences.Reject, Ct))!.State);
            Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, absences.Cancel, Ct))!.State);
        }
    }

    [Fact]
    public async Task A_kind_the_app_does_not_know_is_refused_whoever_writes_it()
    {
        const AbsenceKind Unknown = (AbsenceKind)7;
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Kinding Absence", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        var service = staff.Get<ResourceAbsenceService>();

        Assert.Equal(AbsenceRules.UnknownKind, (await AbsenceScenario.ReportAsync(service, kim, Unknown, Monday, Monday, Ct)).Error);
        var resource = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
        var past = await staff.Get<WorkflowService>().CreateAndTransitionAsync<ResourceAbsenceState, ResourceAbsence>(new ResourceAbsence { TenantId = scenario.TenantId, ResourceId = resource, Kind = Unknown, FirstDay = Monday, LastDay = Monday }, ResourceAbsenceWorkflowAttribute.ReportStep, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.KindKnownReason, past.Error, StringComparison.Ordinal);
        Assert.Empty(await AbsencesOfAsync(kim));

        var id = (await AbsenceScenario.ReportAsync(service, kim, AbsenceKind.PlannedLeave, Monday, Monday, Ct)).Value;
        Assert.Equal(AbsenceRules.UnknownKind, (await AbsenceScenario.StepAsync(service, id, ResourceAbsenceWorkflowAttribute.SaveStep, Ct, edit => edit.Kind = Unknown)).Error);
        Assert.Equal(AbsenceKind.PlannedLeave, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.Kind);

        await using var context = this.database.CreateSystemContext();
        context.ResourceAbsences.Add(new ResourceAbsence { TenantId = scenario.TenantId, ResourceId = resource, Kind = Unknown, FirstDay = Monday, LastDay = Monday, CreatedById = kim });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        Assert.Contains("CK_ResourceAbsences_KnownKind", exception.InnerException!.Message, StringComparison.Ordinal);
    }

    private async Task<List<Guid>> AbsencesOfAsync(Guid userId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.ResourceAbsences.AsNoTracking().Where(absence => absence.Resource.UserId == userId).OrderBy(absence => absence.Id).Select(absence => absence.Id).ToListAsync(Ct);
    }

    private static async Task<Staffora.Core.Scheduling.StaffAvailability> WeekOfAsync(ServiceHost coordinator, Guid resource)
    {
        var answer = await coordinator.Get<IStaffAvailability>().ReadAsync([resource], Monday, Monday.AddDays(6), TestContext.Current.CancellationToken);
        Assert.True(answer.IsSuccess, answer.Error);
        return answer.Value!.StaffMembers[resource];
    }

    private static DateTime Utc(DateOnly date, int hour) => TenantClock.ToUtc(date, new TimeOnly(hour, 0), Stockholm);

    private async Task<Guid> SomeAbsenceOfAsync(Guid userId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.ResourceAbsences.AsNoTracking().Where(absence => absence.Resource.UserId == userId).Select(absence => absence.Id).SingleAsync(Ct);
    }
}
