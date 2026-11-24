using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Messaging;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Actions;
using Staffora.Core.Workflow.Conditions;
using Staffora.Test.Unit.Workflow.Samples;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Test.Unit.Workflow;

public class SlotAssignmentWorkflowTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static Workflow<SlotAssignmentState, SlotAssignment> Assignments => WorkflowRegistry.Default.Get<SlotAssignmentState, SlotAssignment>();

    private static Workflow<JobSlotState, JobSlot> Slots => WorkflowRegistry.Default.Get<JobSlotState, JobSlot>();

    [Fact]
    public void The_lifecycle_has_the_planned_states_and_each_step_its_gate_and_its_notice()
    {
        Assert.Equal(
            [("Create", 0), ("Requested", 1), ("Accepted", 2), ("Declined", 3), ("Assigned", 4), ("NotSelected", 5), ("Canceled", 6), ("StaffQuestion", 7), ("Expired", 8)],
            Enum.GetValues<SlotAssignmentState>().Select(state => (state.ToString(), (int)state)));
        Assert.Equal(
            [
                (SlotAssignmentWorkflowAttribute.RequestStep, SlotAssignmentState.Create, SlotAssignmentState.Requested, (RolePermission?)RolePermission.PerformCoordination, $"{MessageTypes.AssignmentRequested.Key}+{MessageTypes.AssignmentRequestedInRound.Key}"),
                (SlotAssignmentWorkflowAttribute.AssignStep, SlotAssignmentState.Create, SlotAssignmentState.Assigned, RolePermission.PerformCoordination, MessageTypes.AssignmentAssigned.Key),
                (SlotAssignmentWorkflowAttribute.AcceptStep, SlotAssignmentState.Requested, SlotAssignmentState.Accepted, RolePermission.RespondToAssignment, MessageTypes.AssignmentAccepted.Key),
                (SlotAssignmentWorkflowAttribute.AcceptQuestionStep, SlotAssignmentState.StaffQuestion, SlotAssignmentState.Accepted, RolePermission.RespondToAssignment, MessageTypes.AssignmentAccepted.Key),
                (SlotAssignmentWorkflowAttribute.DeclineStep, SlotAssignmentState.Requested, SlotAssignmentState.Declined, RolePermission.RespondToAssignment, MessageTypes.AssignmentDeclined.Key),
                (SlotAssignmentWorkflowAttribute.DeclineQuestionStep, SlotAssignmentState.StaffQuestion, SlotAssignmentState.Declined, RolePermission.RespondToAssignment, MessageTypes.AssignmentDeclined.Key),
                (SlotAssignmentWorkflowAttribute.AskCoordinatorStep, SlotAssignmentState.Requested, SlotAssignmentState.StaffQuestion, RolePermission.RespondToAssignment, MessageTypes.AssignmentQuestionFromStaff.Key),
                (SlotAssignmentWorkflowAttribute.AskCoordinatorAgainStep, SlotAssignmentState.StaffQuestion, SlotAssignmentState.StaffQuestion, RolePermission.RespondToAssignment, MessageTypes.AssignmentQuestionFromStaff.Key),
                (SlotAssignmentWorkflowAttribute.AnswerStaffStep, SlotAssignmentState.StaffQuestion, SlotAssignmentState.Requested, RolePermission.PerformCoordination, MessageTypes.AssignmentAnswerToStaff.Key),
                (SlotAssignmentWorkflowAttribute.QuestionHandledStep, SlotAssignmentState.StaffQuestion, SlotAssignmentState.Requested, RolePermission.PerformCoordination, ""),
                (SlotAssignmentWorkflowAttribute.AcceptedMessageHandledStep, SlotAssignmentState.Accepted, SlotAssignmentState.Accepted, RolePermission.PerformCoordination, ""),
                (SlotAssignmentWorkflowAttribute.AssignedMessageHandledStep, SlotAssignmentState.Assigned, SlotAssignmentState.Assigned, RolePermission.PerformCoordination, ""),
                (SlotAssignmentWorkflowAttribute.WithdrawStep, SlotAssignmentState.Accepted, SlotAssignmentState.Declined, RolePermission.RespondToAssignment, MessageTypes.AssignmentWithdrawn.Key),
                (SlotAssignmentWorkflowAttribute.PickStep, SlotAssignmentState.Accepted, SlotAssignmentState.Assigned, RolePermission.PerformCoordination, MessageTypes.AssignmentAssigned.Key),
                (SlotAssignmentWorkflowAttribute.ClaimStep, SlotAssignmentState.Accepted, SlotAssignmentState.Assigned, null, $"{MessageTypes.AssignmentAssigned.Key}+{MessageTypes.AssignmentTaken.Key}"),
                (SlotAssignmentWorkflowAttribute.CloseRequestStep, SlotAssignmentState.Requested, SlotAssignmentState.NotSelected, null, MessageTypes.AssignmentNotSelected.Key),
                (SlotAssignmentWorkflowAttribute.CloseQuestionStep, SlotAssignmentState.StaffQuestion, SlotAssignmentState.NotSelected, null, MessageTypes.AssignmentNotSelected.Key),
                (SlotAssignmentWorkflowAttribute.NotSelectedStep, SlotAssignmentState.Accepted, SlotAssignmentState.NotSelected, null, MessageTypes.AssignmentNotSelected.Key),
                (SlotAssignmentWorkflowAttribute.ExpireStep, SlotAssignmentState.Requested, SlotAssignmentState.Expired, null, MessageTypes.AssignmentExpired.Key),
                (SlotAssignmentWorkflowAttribute.ExpireQuestionStep, SlotAssignmentState.StaffQuestion, SlotAssignmentState.Expired, null, MessageTypes.AssignmentExpired.Key),
                (SlotAssignmentWorkflowAttribute.CancelRequestStep, SlotAssignmentState.Requested, SlotAssignmentState.Canceled, RolePermission.PerformCoordination, MessageTypes.AssignmentCanceled.Key),
                (SlotAssignmentWorkflowAttribute.CancelQuestionStep, SlotAssignmentState.StaffQuestion, SlotAssignmentState.Canceled, RolePermission.PerformCoordination, MessageTypes.AssignmentCanceled.Key),
                (SlotAssignmentWorkflowAttribute.CancelAcceptedStep, SlotAssignmentState.Accepted, SlotAssignmentState.Canceled, RolePermission.PerformCoordination, MessageTypes.AssignmentCanceled.Key),
                (SlotAssignmentWorkflowAttribute.CancelAssignedStep, SlotAssignmentState.Assigned, SlotAssignmentState.Canceled, RolePermission.PerformCoordination, MessageTypes.AssignmentCanceled.Key),
                (SlotAssignmentWorkflowAttribute.TakeOffRequestStep, SlotAssignmentState.Requested, SlotAssignmentState.Canceled, RolePermission.PerformCoordination, MessageTypes.AssignmentTakenOff.Key),
                (SlotAssignmentWorkflowAttribute.TakeOffQuestionStep, SlotAssignmentState.StaffQuestion, SlotAssignmentState.Canceled, RolePermission.PerformCoordination, MessageTypes.AssignmentTakenOff.Key),
                (SlotAssignmentWorkflowAttribute.TakeOffAcceptedStep, SlotAssignmentState.Accepted, SlotAssignmentState.Canceled, RolePermission.PerformCoordination, MessageTypes.AssignmentTakenOff.Key),
                (SlotAssignmentWorkflowAttribute.TakeOffStep, SlotAssignmentState.Assigned, SlotAssignmentState.Canceled, RolePermission.PerformCoordination, MessageTypes.AssignmentTakenOff.Key),
            ],
            Assignments.Transitions.Select(transition => (transition.Key, transition.From, transition.To, transition.RequiredPermission, string.Join("+", transition.Actions.OfType<NotifyAction<SlotAssignmentState, SlotAssignment>>().Select(notice => notice.Type.Key)))));

        Assert.Equal([SlotAssignmentWorkflowAttribute.ClaimStep, SlotAssignmentWorkflowAttribute.CloseRequestStep, SlotAssignmentWorkflowAttribute.CloseQuestionStep, SlotAssignmentWorkflowAttribute.NotSelectedStep, SlotAssignmentWorkflowAttribute.ExpireStep, SlotAssignmentWorkflowAttribute.ExpireQuestionStep], Assignments.Transitions.Where(transition => transition.IsSystemDriven).Select(transition => transition.Key));
        Assert.All(Assignments.Transitions, transition => Assert.True(transition.TakesNoEdits));
        Assert.All(Assignments.Transitions, transition => Assert.False(transition.IsSave));
        Assert.Equal(
            [
                (SlotAssignmentWorkflowAttribute.DeclineStep, CommentAudience.Staff), (SlotAssignmentWorkflowAttribute.DeclineQuestionStep, CommentAudience.Staff),
                (SlotAssignmentWorkflowAttribute.AskCoordinatorStep, CommentAudience.Staff), (SlotAssignmentWorkflowAttribute.AskCoordinatorAgainStep, CommentAudience.Staff),
                (SlotAssignmentWorkflowAttribute.AnswerStaffStep, CommentAudience.Staff), (SlotAssignmentWorkflowAttribute.WithdrawStep, CommentAudience.Staff),
                (SlotAssignmentWorkflowAttribute.TakeOffRequestStep, CommentAudience.Staff), (SlotAssignmentWorkflowAttribute.TakeOffQuestionStep, CommentAudience.Staff),
                (SlotAssignmentWorkflowAttribute.TakeOffAcceptedStep, CommentAudience.Staff), (SlotAssignmentWorkflowAttribute.TakeOffStep, CommentAudience.Staff),
            ],
            Assignments.Transitions.Where(transition => transition.CommentPrompt is not null).Select(transition => (transition.Key, transition.CommentPrompt!.Audience)));
        Assert.Equal(
            [SlotAssignmentWorkflowAttribute.CancelRequestStep, SlotAssignmentWorkflowAttribute.CancelQuestionStep, SlotAssignmentWorkflowAttribute.CancelAcceptedStep, SlotAssignmentWorkflowAttribute.CancelAssignedStep],
            Assignments.Transitions.Where(transition => transition.ConfirmationMessage is not null).Select(transition => transition.Key));

        Assert.Empty(((IWorkflow)Assignments).GetStateCascades(SlotAssignmentState.Assigned));
        var canceled = Assert.Single(((IWorkflow)Slots).GetStateCascades(JobSlotState.Canceled));
        Assert.Equal(("Assignments", (Enum)SlotAssignmentState.Canceled), (canceled.Navigation, canceled.Target));
        Assert.Equal([SlotAssignmentState.Declined, SlotAssignmentState.NotSelected, SlotAssignmentState.Expired], canceled.LeaveAlone.Cast<SlotAssignmentState>());
    }

    [Fact]
    public async Task Expired_is_reached_only_by_the_system_steps_from_requested_and_a_question_once_the_answer_by_time_passed()
    {
        var into = Assignments.Transitions.Where(transition => transition.To == SlotAssignmentState.Expired).ToList();
        Assert.Equal(
            [(SlotAssignmentWorkflowAttribute.ExpireStep, SlotAssignmentState.Requested), (SlotAssignmentWorkflowAttribute.ExpireQuestionStep, SlotAssignmentState.StaffQuestion)],
            into.Select(transition => (transition.Key, transition.From)));
        Assert.All(into, transition => Assert.True(transition.IsSystemDriven && transition.RequiredPermission is null && !transition.RequiresPlatformOwner, transition.Key));
        Assert.DoesNotContain(Assignments.Transitions, transition => transition.From == SlotAssignmentState.Expired);
        Assert.Equal(Staffora.Core.Annotations.StateCategory.Canceled, Staffora.Core.CoreExtensions.GetStateCategory(SlotAssignmentState.Expired));
        Assert.False(SlotAssignmentWorkflowAttribute.IsLive(SlotAssignmentState.Expired));
        Assert.All(Enum.GetValues<SlotAssignmentState>().Where(state => state is not (SlotAssignmentState.Requested or SlotAssignmentState.StaffQuestion)), state => Assert.Null(SlotAssignmentWorkflowAttribute.ExpireStepFor(state)));

        var (assignment, _) = Requested();
        var coordinator = Guid.NewGuid();
        assignment.Slot.Job.CoordinatorUserId = coordinator;
        var noon = Environments.Noon.UtcDateTime;
        foreach (var notYet in new DateTime?[] { null, noon.AddMinutes(1) })
        {
            assignment.RespondBy = notYet;
            Assert.Equal([SlotAssignmentWorkflowAttribute.RespondByPassedReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.ExpireStep, As(coordinator)));
        }

        assignment.RespondBy = noon;
        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.ExpireStep, As(coordinator)));
        assignment.State = SlotAssignmentState.StaffQuestion;
        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.ExpireQuestionStep, As(coordinator)));
        var expired = await Assignments.ExecuteTransitionAsync(assignment, SlotAssignmentWorkflowAttribute.ExpireQuestionStep, As(coordinator), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SlotAssignmentState.Expired, assignment.State);
        Assert.Equal([(MessageTypes.AssignmentExpired.Key, coordinator)], expired.OutgoingMessages.Select(message => (message.MessageType, Assert.Single(message.RecipientUserIds))));

        var fresh = New(assignment.Slot, Guid.NewGuid());
        fresh.RespondBy = noon;
        Assert.Equal([SlotAssignmentWorkflowAttribute.RespondByAheadReason], Assignments.GetBlockingReasons(fresh, SlotAssignmentWorkflowAttribute.RequestStep, As(coordinator)));
        Assert.Equal([SlotAssignmentWorkflowAttribute.AssignAsksNoAnswerReason], Assignments.GetBlockingReasons(fresh, SlotAssignmentWorkflowAttribute.AssignStep, As(coordinator)));
        fresh.RespondBy = noon.AddHours(48);
        Assert.Empty(Assignments.GetBlockingReasons(fresh, SlotAssignmentWorkflowAttribute.RequestStep, As(coordinator)));
        var round = new RequestRound { Id = Guid.NewGuid(), TenantId = Tenant, JobSlotId = fresh.JobSlotId, Slot = fresh.Slot, State = RequestRoundState.Open, RespondBy = noon.AddHours(24) };
        (fresh.Round, fresh.RequestRoundId) = (round, round.Id);
        Assert.Equal([SlotAssignmentWorkflowAttribute.RoundTimeReason], Assignments.GetBlockingReasons(fresh, SlotAssignmentWorkflowAttribute.RequestStep, As(coordinator)));
        fresh.RespondBy = round.RespondBy;
        Assert.Empty(Assignments.GetBlockingReasons(fresh, SlotAssignmentWorkflowAttribute.RequestStep, As(coordinator)));
    }

    [Fact]
    public async Task Take_off_for_absence_is_a_coordinators_step_from_every_live_state_offered_only_while_an_absence_blocks_a_remaining_time_and_it_tells_the_staff_member_why()
    {
        var takeOffs = Assignments.Transitions.Where(transition => SlotAssignmentWorkflowAttribute.IsTakeOff(transition.Key)).ToList();
        Assert.Equal(
            [(SlotAssignmentState.Requested, SlotAssignmentState.Canceled), (SlotAssignmentState.StaffQuestion, SlotAssignmentState.Canceled), (SlotAssignmentState.Accepted, SlotAssignmentState.Canceled), (SlotAssignmentState.Assigned, SlotAssignmentState.Canceled)],
            takeOffs.Select(transition => (transition.From, transition.To)));
        Assert.Equal(SlotAssignmentWorkflowAttribute.TakeOffSteps.Order(), takeOffs.Select(transition => transition.Key).Order());
        Assert.All(takeOffs, transition => Assert.True(
            transition is { RequiredPermission: RolePermission.PerformCoordination, IsSystemDriven: false, TakesNoEdits: true, NeverCascaded: true, Label: "Take off for absence", CommentPrompt: { Required: false, Audience: CommentAudience.Staff } },
            transition.Key));
        Assert.Equal([SlotAssignmentWorkflowAttribute.TakeOffRequestStep, SlotAssignmentWorkflowAttribute.TakeOffQuestionStep, SlotAssignmentWorkflowAttribute.TakeOffAcceptedStep, SlotAssignmentWorkflowAttribute.TakeOffStep], Assignments.Transitions.Where(transition => transition.NeverCascaded).Select(transition => transition.Key));
        Assert.All(Enum.GetValues<SlotAssignmentState>().Where(state => !SlotAssignmentWorkflowAttribute.IsLive(state)), state => Assert.DoesNotContain(takeOffs, transition => transition.From == state));
        Assert.False(SlotAssignmentWorkflowAttribute.IsTakeOff(SlotAssignmentWorkflowAttribute.CancelAssignedStep));

        var (assignment, staff) = Requested();
        var coordinator = Guid.NewGuid();
        var occurrence = new OccurrenceInstant(new DateOnly(2027, 2, 8), new TimeOnly(8, 0), new TimeOnly(10, 0), new DateTime(2027, 2, 8, 7, 0, 0, DateTimeKind.Utc), new DateTime(2027, 2, 8, 9, 0, 0, DateTimeKind.Utc));
        var sick = new ScheduledAbsence(Guid.NewGuid(), assignment.ResourceId, AbsenceKind.SickLeave, ResourceAbsenceState.Requested, new DateOnly(2027, 2, 8), null, new DateOnly(2027, 2, 9), null);
        var absent = new Guard(new StaffingVerdict(40, [new OccurrenceFinding(occurrence, "On sick leave (reported)", sick.AbsenceId)], []), sick);
        var booked = new Guard(new StaffingVerdict(40, [new OccurrenceFinding(occurrence, "Assigned to work a job", Guid.NewGuid())], []));

        foreach (var (state, key) in new[] { (SlotAssignmentState.Requested, SlotAssignmentWorkflowAttribute.TakeOffRequestStep), (SlotAssignmentState.StaffQuestion, SlotAssignmentWorkflowAttribute.TakeOffQuestionStep), (SlotAssignmentState.Accepted, SlotAssignmentWorkflowAttribute.TakeOffAcceptedStep), (SlotAssignmentState.Assigned, SlotAssignmentWorkflowAttribute.TakeOffStep) })
        {
            assignment.State = state;
            Assert.Empty(Assignments.GetBlockingReasons(assignment, key, As(coordinator, guard: absent)));
            Assert.Equal([SlotAssignmentWorkflowAttribute.AwayReason], Assignments.GetBlockingReasons(assignment, key, As(coordinator, guard: booked)));
            Assert.Equal([SlotAssignmentWorkflowAttribute.AwayReason], Assignments.GetBlockingReasons(assignment, key, As(coordinator)));
        }

        var unguarded = new WorkflowEnvironment(new FakeClock(Environments.Noon), NavigationLoadState.Complete, new Bare(new RuntimeExecutionContext(Tenant, coordinator)), (_, _) => null);
        Assert.Equal([$"{SlotAssignmentWorkflowAttribute.AwayReason} (cannot be checked: no availability guard)"], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.TakeOffStep, unguarded));
        assignment.Slot.Job.State = JobState.Completed;
        Assert.Contains(SlotAssignmentWorkflowAttribute.StaffingOpenReason, Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.TakeOffStep, As(coordinator, guard: absent)));
        assignment.Slot.Job.State = JobState.Coordinating;

        var outcome = await Assignments.ExecuteTransitionAsync(assignment, SlotAssignmentWorkflowAttribute.TakeOffStep, As(coordinator, guard: absent), "Get well soon.", TestContext.Current.CancellationToken);
        Assert.Equal(SlotAssignmentState.Canceled, assignment.State);
        var notice = Assert.Single(outcome.OutgoingMessages);
        Assert.Equal((MessageTypes.AssignmentTakenOff.Key, staff), (notice.MessageType, Assert.Single(notice.RecipientUserIds)));
        Assert.Equal("You are taken off Algebra", notice.Subject);
        Assert.Equal("Hello Ada, because of your sick leave from 2027-02-08 to 2027-02-09 you no longer work Algebra as Lead. The coordinator asks someone else to work it.\n\nGet well soon.", notice.Text);
        Assert.Equal("sick leave from 2027-02-08 to 2027-02-09", SlotAssignmentWorkflowAttribute.AbsenceText(sick));
    }

    [Fact]
    public async Task A_rounds_open_requests_expire_together_with_one_notice_for_the_round_and_its_answers_stay()
    {
        var (asked, _) = Requested();
        var slot = asked.Slot;
        var coordinator = Guid.NewGuid();
        var noon = Environments.Noon.UtcDateTime;
        var round = new RequestRound { Id = Guid.NewGuid(), TenantId = Tenant, JobSlotId = slot.Id, Slot = slot, State = RequestRoundState.Open, RespondBy = noon, CreatedById = coordinator };
        var question = Sibling(slot, SlotAssignmentState.StaffQuestion);
        var yes = Sibling(slot, SlotAssignmentState.Accepted);
        var no = Sibling(slot, SlotAssignmentState.Declined);
        foreach (var request in new[] { asked, question, yes, no })
        {
            (request.Round, request.RequestRoundId, request.RespondBy) = (round, round.Id, noon);
            round.Requests.Add(request);
        }

        var rounds = WorkflowRegistry.Default.Get<RequestRoundState, RequestRound>();
        var expire = rounds.Transitions.Single(transition => transition.Key == RequestRoundWorkflowAttribute.ExpireStep);
        Assert.Equal((RequestRoundState.Open, RequestRoundState.Expired, true, (RolePermission?)null), (expire.From, expire.To, expire.IsSystemDriven, expire.RequiredPermission));
        var cascade = Assert.Single(((IWorkflow)rounds).GetStateCascades(RequestRoundState.Expired));
        Assert.Equal(("Requests", (Enum)SlotAssignmentState.Expired), (cascade.Navigation, cascade.Target));

        round.RespondBy = noon.AddMinutes(1);
        Assert.Equal([SlotAssignmentWorkflowAttribute.RespondByPassedReason], rounds.GetBlockingReasons(round, RequestRoundWorkflowAttribute.ExpireStep, As(coordinator)));
        round.RespondBy = noon;

        var outcome = await rounds.ExecuteTransitionAsync(round, RequestRoundWorkflowAttribute.ExpireStep, As(coordinator), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RequestRoundState.Expired, round.State);
        Assert.Equal([SlotAssignmentState.Expired, SlotAssignmentState.Expired, SlotAssignmentState.Accepted, SlotAssignmentState.Declined], new[] { asked, question, yes, no }.Select(request => request.State));
        var notice = Assert.Single(outcome.OutgoingMessages);
        Assert.Equal((MessageTypes.RoundExpired.Key, coordinator), (notice.MessageType, Assert.Single(notice.RecipientUserIds)));
        Assert.Contains("Ada Example, Ada Example", notice.Text, StringComparison.Ordinal);
        Assert.Equal([SlotAssignmentWorkflowAttribute.ExpireStep, SlotAssignmentWorkflowAttribute.ExpireQuestionStep], outcome.Steps.Where(step => step.Entity is SlotAssignment).Select(step => step.TransitionKey).Order(StringComparer.Ordinal));

        Assert.Equal([RequestRoundWorkflowAttribute.UnansweredReason], rounds.GetBlockingReasons(new RequestRound { Id = Guid.NewGuid(), TenantId = Tenant, JobSlotId = slot.Id, Slot = slot, State = RequestRoundState.Open, RespondBy = noon, Requests = [yes] }, RequestRoundWorkflowAttribute.ExpireStep, As(coordinator)));
    }

    [Fact]
    public void Only_the_staff_member_asked_answers_and_every_step_acts_inside_the_assignments_own_tenant()
    {
        var (assignment, staff) = Requested();

        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AcceptStep, As(staff)));
        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.DeclineStep, As(staff)));
        Assert.Equal([SlotAssignmentWorkflowAttribute.OwnReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AcceptStep, As(Guid.NewGuid())));
        Assert.Equal([SlotAssignmentWorkflowAttribute.OwnReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.DeclineStep, As(Guid.NewGuid())));

        Assert.Contains(SlotAssignmentWorkflowAttribute.OwnReason, Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AcceptStep, Environments.Complete()));
        Assert.Contains(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AcceptStep, new WorkflowEnvironment(new FakeClock(Environments.Noon), NavigationLoadState.None, new Session(new RuntimeExecutionContext(Tenant, staff)))), reason => reason.StartsWith(SlotAssignmentWorkflowAttribute.OwnReason, StringComparison.Ordinal));

        Assert.Equal([JobWorkflowAttribute.OwnTenantReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AcceptStep, As(staff, Guid.NewGuid())));
        Assert.Equal([JobWorkflowAttribute.OwnTenantReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.CancelRequestStep, As(staff, Guid.NewGuid())));
    }

    [Fact]
    public void A_slot_takes_one_holder_a_staff_member_once_while_live_and_staffing_only_while_the_job_is_staffed_and_the_slot_active()
    {
        var (assignment, staff) = Requested();
        var slot = assignment.Slot;
        var coordinator = Guid.NewGuid();

        var holder = Sibling(slot, SlotAssignmentState.Assigned);
        var fresh = New(slot, Guid.NewGuid());
        Assert.Equal([SlotAssignmentWorkflowAttribute.OneHolderReason], Assignments.GetBlockingReasons(fresh, SlotAssignmentWorkflowAttribute.AssignStep, As(coordinator)));
        Assert.Equal([SlotAssignmentWorkflowAttribute.OneHolderReason], Assignments.GetBlockingReasons(fresh, SlotAssignmentWorkflowAttribute.RequestStep, As(coordinator)));
        Assert.Equal([SlotAssignmentWorkflowAttribute.OneHolderReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AcceptStep, As(staff)));
        assignment.State = SlotAssignmentState.Accepted;
        Assert.Equal([SlotAssignmentWorkflowAttribute.OneHolderReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.PickStep, As(coordinator)));

        holder.State = SlotAssignmentState.Canceled;
        slot.Assignments.Add(new SlotAssignment { Id = assignment.Id, TenantId = Tenant, JobSlotId = slot.Id, ResourceId = assignment.ResourceId, State = SlotAssignmentState.Assigned });
        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.PickStep, As(coordinator)));

        var again = New(slot, assignment.ResourceId, assignment.Resource);
        Assert.Contains(SlotAssignmentWorkflowAttribute.OneLiveReason, Assignments.GetBlockingReasons(again, SlotAssignmentWorkflowAttribute.RequestStep, As(coordinator)));
        assignment.State = SlotAssignmentState.Declined;
        slot.Assignments.Remove(slot.Assignments.Last());
        Assert.Empty(Assignments.GetBlockingReasons(again, SlotAssignmentWorkflowAttribute.RequestStep, As(coordinator)));

        slot.Job.State = JobState.Registration;
        Assert.Equal([SlotAssignmentWorkflowAttribute.StaffingOpenReason], Assignments.GetBlockingReasons(again, SlotAssignmentWorkflowAttribute.RequestStep, As(coordinator)));
        foreach (var open in new[] { JobState.Coordinating, JobState.Confirmed, JobState.InProgress })
        {
            slot.Job.State = open;
            Assert.Empty(Assignments.GetBlockingReasons(again, SlotAssignmentWorkflowAttribute.AssignStep, As(coordinator)));
        }

        slot.State = JobSlotState.Canceled;
        Assert.Equal([SlotAssignmentWorkflowAttribute.SlotActiveReason], Assignments.GetBlockingReasons(again, SlotAssignmentWorkflowAttribute.AssignStep, As(coordinator)));
        slot.State = JobSlotState.Active;
        again.Resource.State = ResourceState.Inactive;
        Assert.Equal([SlotAssignmentWorkflowAttribute.StaffInServiceReason], Assignments.GetBlockingReasons(again, SlotAssignmentWorkflowAttribute.AssignStep, As(coordinator)));
    }

    [Fact]
    public async Task Every_step_into_assigned_takes_an_open_slot_of_the_group_and_moves_no_other_assignment_itself()
    {
        var (assignment, _) = Requested();
        var coordinator = Guid.NewGuid();
        var slot = assignment.Slot;
        var job = slot.Job;
        var second = new JobSlot { Id = Guid.NewGuid(), TenantId = Tenant, JobId = job.Id, Job = job, Name = " lead", State = JobSlotState.Active, Created = slot.Created.AddMinutes(1) };
        job.Slots = [slot, second];
        assignment.State = SlotAssignmentState.Accepted;
        Sibling(slot, SlotAssignmentState.Assigned);
        var requested = Sibling(slot, SlotAssignmentState.Requested);

        var candidates = Assignments.GetAllCandidatesForTransition(assignment, SlotAssignmentWorkflowAttribute.PickStep, As(coordinator));
        Assert.Equal([assignment.Id], candidates.All.Select(candidate => candidate.Id));
        var outcome = await Assignments.ExecuteTransitionAsync(assignment, SlotAssignmentWorkflowAttribute.PickStep, As(coordinator), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((SlotAssignmentState.Assigned, second.Id), (assignment.State, assignment.JobSlotId));
        Assert.Same(second, assignment.Slot);
        Assert.Equal(SlotAssignmentState.Requested, requested.State);
        Assert.Equal([(assignment.Id, SlotAssignmentWorkflowAttribute.PickStep)], outcome.Steps.Select(step => (step.Entity.Id, step.TransitionKey)));
        Assert.Equal([(MessageTypes.AssignmentAssigned.Key, assignment.Id, assignment.Resource.UserId)], outcome.OutgoingMessages.Select(message => (message.MessageType, message.EntityId, Assert.Single(message.RecipientUserIds))));

        second.Assignments.Add(assignment);
        var fresh = New(slot, Guid.NewGuid());
        Assert.Equal([SlotAssignmentWorkflowAttribute.GroupOpenReason], Assignments.GetBlockingReasons(fresh, SlotAssignmentWorkflowAttribute.AssignStep, As(coordinator)));
        Assert.Equal([SlotAssignmentWorkflowAttribute.GroupOpenReason], Assignments.GetBlockingReasons(fresh, SlotAssignmentWorkflowAttribute.RequestStep, As(coordinator)));

        var third = new JobSlot { Id = Guid.NewGuid(), TenantId = Tenant, JobId = job.Id, Job = job, Name = "Lead", State = JobSlotState.Active, Created = slot.Created.AddMinutes(2) };
        job.Slots.Add(third);
        var straight = New(third, Guid.NewGuid());
        straight.Id = Guid.NewGuid();
        third.Assignments.Add(straight);
        await Assignments.ExecuteTransitionAsync(straight, SlotAssignmentWorkflowAttribute.AssignStep, As(coordinator), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((SlotAssignmentState.Assigned, third.Id), (straight.State, straight.JobSlotId));
    }

    [Theory]
    [InlineData(JobState.Completed)]
    [InlineData(JobState.CanceledByCustomer)]
    [InlineData(JobState.CanceledBySupplier)]
    public async Task Once_the_job_is_no_longer_staffed_no_answer_or_assignment_is_rewritten_and_the_clock_closes_open_requests_quietly(JobState over)
    {
        var (assignment, staff) = Requested();
        var coordinator = Guid.NewGuid();
        var slot = assignment.Slot;
        slot.Job.State = over;
        Assert.Equal([SlotAssignmentWorkflowAttribute.StaffingOpenReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.DeclineStep, As(staff)));
        Assert.Equal([SlotAssignmentWorkflowAttribute.StaffingOpenReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.CancelRequestStep, As(coordinator)));
        var accepted = Sibling(slot, SlotAssignmentState.Accepted);
        Assert.Equal([SlotAssignmentWorkflowAttribute.StaffingOpenReason], Assignments.GetBlockingReasons(accepted, SlotAssignmentWorkflowAttribute.WithdrawStep, As(accepted.Resource.UserId)));
        Assert.Equal([SlotAssignmentWorkflowAttribute.StaffingOpenReason], Assignments.GetBlockingReasons(accepted, SlotAssignmentWorkflowAttribute.CancelAcceptedStep, As(coordinator)));
        var holder = Sibling(slot, SlotAssignmentState.Assigned);
        Assert.Equal([SlotAssignmentWorkflowAttribute.StaffingOpenReason], Assignments.GetBlockingReasons(holder, SlotAssignmentWorkflowAttribute.CancelAssignedStep, As(coordinator)));

        var closed = await Assignments.ExecuteTransitionAsync(assignment, SlotAssignmentWorkflowAttribute.CloseRequestStep, As(coordinator), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SlotAssignmentState.NotSelected, assignment.State);
        Assert.Empty(closed.OutgoingMessages);
        Assert.Empty((await Assignments.ExecuteTransitionAsync(accepted, SlotAssignmentWorkflowAttribute.NotSelectedStep, As(coordinator), cancellationToken: TestContext.Current.CancellationToken)).OutgoingMessages);

        foreach (var open in new[] { JobState.Coordinating, JobState.Confirmed, JobState.InProgress })
        {
            slot.Job.State = open;
            Assert.Empty(Assignments.GetBlockingReasons(holder, SlotAssignmentWorkflowAttribute.CancelAssignedStep, As(coordinator)));
        }

        var other = Sibling(slot, SlotAssignmentState.Requested);
        Assert.Single((await Assignments.ExecuteTransitionAsync(other, SlotAssignmentWorkflowAttribute.CloseRequestStep, As(coordinator), cancellationToken: TestContext.Current.CancellationToken)).OutgoingMessages);
    }

    [Fact]
    public void A_request_an_assignment_a_yes_and_a_pick_need_the_staff_member_free_and_a_warning_never_refuses()
    {
        var (assignment, staff) = Requested();
        var coordinator = Guid.NewGuid();
        var fresh = New(assignment.Slot, Guid.NewGuid());
        var occurrence = new OccurrenceInstant(new DateOnly(2027, 2, 8), new TimeOnly(8, 0), new TimeOnly(10, 0), new DateTime(2027, 2, 8, 7, 0, 0, DateTimeKind.Utc), new DateTime(2027, 2, 8, 9, 0, 0, DateTimeKind.Utc));
        var absent = new Guard(new StaffingVerdict(40, [new OccurrenceFinding(occurrence, "On sick leave", Guid.NewGuid())], []));
        var warned = new Guard(new StaffingVerdict(40, [], [new OccurrenceFinding(occurrence, StaffingRules.OutsidePatternReason, null)]));
        const string blocked = "The staff member is not free for 1 of the job's 40 remaining occurrences. On sick leave: 1 occurrence, 2027-02-08.";

        Assert.Equal([blocked], Assignments.GetBlockingReasons(fresh, SlotAssignmentWorkflowAttribute.RequestStep, As(coordinator, guard: absent)));
        Assert.Equal([blocked], Assignments.GetBlockingReasons(fresh, SlotAssignmentWorkflowAttribute.AssignStep, As(coordinator, guard: absent)));
        Assert.Equal([blocked], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AcceptStep, As(staff, guard: absent)));
        assignment.State = SlotAssignmentState.Accepted;
        Assert.Equal([blocked], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.PickStep, As(coordinator, guard: absent)));

        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.WithdrawStep, As(staff, guard: absent)));
        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.CancelAcceptedStep, As(coordinator, guard: absent)));

        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.PickStep, As(coordinator, guard: warned)));
        Assert.Empty(Assignments.GetBlockingReasons(fresh, SlotAssignmentWorkflowAttribute.AssignStep, As(coordinator, guard: warned)));
        var unguarded = new WorkflowEnvironment(new FakeClock(Environments.Noon), NavigationLoadState.Complete, new Bare(new RuntimeExecutionContext(Tenant, coordinator)), (_, _) => null);
        Assert.Equal([$"{SlotAssignmentWorkflowAttribute.StaffFreeReason} (cannot be checked: no availability guard)"], Assignments.GetBlockingReasons(fresh, SlotAssignmentWorkflowAttribute.AssignStep, unguarded));
    }

    [Fact]
    public void A_question_is_the_staff_members_own_step_and_the_coordinators_answer_it_on_another_staff_members_request()
    {
        var (assignment, staff) = Requested();
        var coordinator = Guid.NewGuid();
        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, As(staff)));
        Assert.Equal([SlotAssignmentWorkflowAttribute.OwnQuestionReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, As(coordinator)));

        assignment.State = SlotAssignmentState.StaffQuestion;
        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AskCoordinatorAgainStep, As(staff)));
        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AcceptQuestionStep, As(staff)));
        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.DeclineQuestionStep, As(staff)));
        Assert.Equal([SlotAssignmentWorkflowAttribute.OwnReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AcceptQuestionStep, As(coordinator)));
        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AnswerStaffStep, As(coordinator)));
        Assert.Equal([SlotAssignmentWorkflowAttribute.NotOwnQuestionReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AnswerStaffStep, As(staff)));
        Assert.Equal(SlotAssignmentState.Requested, Assignments.GetTransition(SlotAssignmentWorkflowAttribute.AnswerStaffStep).To);
        Assert.Empty(Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.QuestionHandledStep, As(coordinator)));
        Assert.Equal([SlotAssignmentWorkflowAttribute.NotOwnQuestionReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.QuestionHandledStep, As(staff)));
        Assert.Equal(
            [SlotAssignmentWorkflowAttribute.AnswerStaffStep, SlotAssignmentWorkflowAttribute.QuestionHandledStep],
            Assignments.Transitions.Where(transition => transition.From == SlotAssignmentState.StaffQuestion && transition.RequiredPermission == RolePermission.PerformCoordination && transition.To != SlotAssignmentState.Canceled).Select(transition => transition.Key));
        Assert.Equal(
            [(SlotAssignmentWorkflowAttribute.AnswerStaffStep, true), (SlotAssignmentWorkflowAttribute.QuestionHandledStep, true)],
            new[] { SlotAssignmentWorkflowAttribute.AnswerStaffStep, SlotAssignmentWorkflowAttribute.QuestionHandledStep }.Select(key => (key, Assignments.GetTransition(key).Actions.OfType<MarkNoticesReadAction<SlotAssignmentState, SlotAssignment>>().Any(action => action.Types.SequenceEqual(SlotAssignmentWorkflowAttribute.StaffNoticeTypes)))));
        Assert.Null(Assignments.GetTransition(SlotAssignmentWorkflowAttribute.QuestionHandledStep).CommentPrompt);

        var holder = Sibling(assignment.Slot, SlotAssignmentState.Assigned);
        Assert.Equal([SlotAssignmentWorkflowAttribute.OneHolderReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AcceptQuestionStep, As(staff)));
        holder.State = SlotAssignmentState.Canceled;

        assignment.Slot.Job.State = JobState.Completed;
        Assert.Equal([SlotAssignmentWorkflowAttribute.StaffingOpenReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AskCoordinatorAgainStep, As(staff)));
        Assert.Equal([SlotAssignmentWorkflowAttribute.StaffingOpenReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AnswerStaffStep, As(coordinator)));

        Assert.True(SlotAssignmentWorkflowAttribute.IsAsked(SlotAssignmentState.StaffQuestion));
        Assert.True(SlotAssignmentWorkflowAttribute.IsLive(SlotAssignmentState.StaffQuestion));
        Assert.False(SlotAssignmentWorkflowAttribute.Holds(SlotAssignmentState.StaffQuestion));
        Assert.Equal(SlotAssignmentWorkflowAttribute.CloseQuestionStep, SlotAssignmentWorkflowAttribute.NotSelectedStepFor(SlotAssignmentState.StaffQuestion));
        Assert.True(SlotAssignmentWorkflowAttribute.IsAcceptStep(SlotAssignmentWorkflowAttribute.AcceptQuestionStep));
        Assert.Equal(SlotAssignmentWorkflowAttribute.CancelQuestionStep, Assert.Single(Assignments.Transitions, transition => transition.From == SlotAssignmentState.StaffQuestion && transition.To == SlotAssignmentState.Canceled && !transition.NeverCascaded).Key);
    }

    [Fact]
    public void A_staff_members_message_is_marked_handled_by_another_coordinator_only_while_it_waits_for_a_reply()
    {
        var (assignment, staff) = Requested();
        var coordinator = Guid.NewGuid();
        var wrote = new DateTime(2027, 2, 1, 8, 0, 0, DateTimeKind.Utc);
        foreach (var (state, key) in new[] { (SlotAssignmentState.Accepted, SlotAssignmentWorkflowAttribute.AcceptedMessageHandledStep), (SlotAssignmentState.Assigned, SlotAssignmentWorkflowAttribute.AssignedMessageHandledStep) })
        {
            assignment.State = state;
            (assignment.LastStaffCommentAt, assignment.LastCoordinatorCommentAt) = (null, null);
            Assert.Equal([SlotAssignmentWorkflowAttribute.AwaitsReplyReason], Assignments.GetBlockingReasons(assignment, key, As(coordinator)));

            assignment.LastStaffCommentAt = wrote;
            Assert.Empty(Assignments.GetBlockingReasons(assignment, key, As(coordinator)));
            Assert.Equal([SlotAssignmentWorkflowAttribute.NotOwnQuestionReason], Assignments.GetBlockingReasons(assignment, key, As(staff)));

            assignment.LastCoordinatorCommentAt = wrote.AddMinutes(1);
            Assert.Equal([SlotAssignmentWorkflowAttribute.AwaitsReplyReason], Assignments.GetBlockingReasons(assignment, key, As(coordinator)));

            var step = Assignments.GetTransition(key);
            Assert.Equal((state, "Mark as handled", "done_all", true, (TransitionCommentPrompt?)null), (step.To, step.Label, step.Icon, step.TakesNoEdits, step.CommentPrompt));
            Assert.Single(step.Actions.OfType<MarkNoticesReadAction<SlotAssignmentState, SlotAssignment>>());
            Assert.Empty(step.Actions.OfType<NotifyAction<SlotAssignmentState, SlotAssignment>>());
        }

        assignment.LastStaffCommentAt = wrote.AddMinutes(2);
        assignment.Slot.Job.State = JobState.Completed;
        Assert.Equal([SlotAssignmentWorkflowAttribute.StaffingOpenReason], Assignments.GetBlockingReasons(assignment, SlotAssignmentWorkflowAttribute.AssignedMessageHandledStep, As(coordinator)));
    }

    [Fact]
    public void A_cascade_through_a_reference_never_deletes_and_its_path_is_named_as_written()
    {
        Assert.Throws<ArgumentException>(() => new CascadeAction<SlotAssignmentState, SlotAssignment, SlotAssignmentState, SlotAssignment>(e => e.Slot.Assignments.Where(other => other.State == SlotAssignmentState.Requested), SlotAssignmentState.NotSelected));
        var path = new CascadeAction<SlotAssignmentState, SlotAssignment, SlotAssignmentState, SlotAssignment>(e => e.Slot.Assignments, SlotAssignmentState.NotSelected);
        Assert.Equal(("Slot.Assignments", true), (path.Navigation, path.ThroughReference));
        var own = new CascadeAction<JobSlotState, JobSlot, SlotAssignmentState, SlotAssignment>(e => e.Assignments, SlotAssignmentState.Canceled);
        Assert.Equal(("Assignments", false), (own.Navigation, own.ThroughReference));

        var registry = new WorkflowRegistry();
        var error = Assert.Throws<WorkflowDefinitionException>(() => registry.Get(typeof(SiblingDeleteEntity)));
        Assert.Contains("a cascade through a reference never deletes", error.Message, StringComparison.Ordinal);
    }

    private static (SlotAssignment Assignment, Guid Staff) Requested()
    {
        var job = new Job { Id = Guid.NewGuid(), TenantId = Tenant, Name = "Algebra", State = JobState.Coordinating, PeriodStart = new DateOnly(2027, 2, 1), PeriodEnd = new DateOnly(2027, 6, 11) };
        var slot = new JobSlot { Id = Guid.NewGuid(), TenantId = Tenant, JobId = job.Id, Job = job, Name = "Lead", State = JobSlotState.Active };
        var assignment = New(slot, Guid.NewGuid());
        assignment.Id = Guid.NewGuid();
        assignment.State = SlotAssignmentState.Requested;
        slot.Assignments.Add(assignment);
        return (assignment, assignment.Resource.UserId);
    }

    private static SlotAssignment New(JobSlot slot, Guid resourceId, Resource? resource = null)
    {
        var user = resource?.User ?? new ApplicationUser { Id = Guid.NewGuid(), TenantId = Tenant, FirstName = "Ada", LastName = "Example", State = UserState.Active };
        resource ??= new Resource { Id = resourceId, TenantId = Tenant, UserId = user.Id, User = user, State = ResourceState.Active };
        return new SlotAssignment { TenantId = Tenant, JobSlotId = slot.Id, Slot = slot, ResourceId = resource.Id, Resource = resource, State = SlotAssignmentState.Create };
    }

    private static SlotAssignment Sibling(JobSlot slot, SlotAssignmentState state)
    {
        var sibling = New(slot, Guid.NewGuid());
        sibling.Id = Guid.NewGuid();
        sibling.State = state;
        slot.Assignments.Add(sibling);
        return sibling;
    }

    private static WorkflowEnvironment As(Guid userId, Guid? tenantId = null, ISlotAssignmentGuard? guard = null) =>
        new(new FakeClock(Environments.Noon), NavigationLoadState.Complete, new Session(new RuntimeExecutionContext(tenantId ?? Tenant, userId), guard), (_, _) => null);

    private sealed class Session : IServiceProvider
    {
        private readonly IRuntimeExecutionContext runtime;
        private readonly ISlotAssignmentGuard? guard;

        public Session(IRuntimeExecutionContext runtime, ISlotAssignmentGuard? guard = null)
        {
            this.runtime = runtime;
            this.guard = guard;
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IRuntimeExecutionContext) ? this.runtime
            : serviceType == typeof(ISlotAssignmentGuard) ? this.guard ?? new Guard(StaffingVerdict.None)
            : null;
    }

    private sealed class Bare : IServiceProvider
    {
        private readonly IRuntimeExecutionContext runtime;

        public Bare(IRuntimeExecutionContext runtime)
        {
            this.runtime = runtime;
        }

        public object? GetService(Type serviceType) => serviceType == typeof(IRuntimeExecutionContext) ? this.runtime : null;
    }

    private sealed class Guard : ISlotAssignmentGuard
    {
        private readonly StaffingVerdict verdict;
        private readonly ScheduledAbsence? absence;

        public Guard(StaffingVerdict verdict, ScheduledAbsence? absence = null)
        {
            this.verdict = verdict;
            this.absence = absence;
        }

        public StaffingVerdict VerdictOf(SlotAssignment assignment) => this.verdict;

        public ScheduledAbsence? BlockingAbsenceOf(SlotAssignment assignment) => this.absence;
    }
}
