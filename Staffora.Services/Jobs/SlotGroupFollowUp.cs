using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Jobs;

public sealed class SlotGroupFollowUp : IStepFollowUp
{
    private readonly WorkflowService workflows;

    public SlotGroupFollowUp(WorkflowService workflows)
    {
        this.workflows = workflows;
    }

    public Type EntityType => typeof(SlotAssignment);

    public async Task RunAsync(ApplicationDbContext context, IStateEntity entity, StepFollowUpContext taken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(taken);
        if (entity is not SlotAssignment assignment)
        {
            return;
        }

        if (taken.Outcome.Steps.Any(step => ReferenceEquals(step.Entity, assignment) && SlotAssignmentWorkflowAttribute.IsAcceptStep(step.TransitionKey))
            && SlotAssignmentWorkflowAttribute.TakesASlotAtOnce(assignment))
        {
            await this.workflows.RunSystemStepInTransactionAsync<SlotAssignmentState, SlotAssignment>(context, assignment, SlotAssignmentWorkflowAttribute.ClaimStep, cancellationToken, taken.AfterCommit, taken.CorrelationId);
        }
        else if (!EnteredAssigned(taken.Outcome, assignment))
        {
            return;
        }

        await SettleAsync(context, assignment, taken, cancellationToken);
    }

    private static bool EnteredAssigned(TransitionOutcome outcome, SlotAssignment assignment) =>
        outcome.Steps.Any(step => ReferenceEquals(step.Entity, assignment) && Equals(step.To, SlotAssignmentState.Assigned));

    private Task SettleAsync(ApplicationDbContext context, SlotAssignment assignment, StepFollowUpContext taken, CancellationToken cancellationToken) =>
        SettleGroupAsync(this.workflows, context, SlotAssignmentWorkflowAttribute.GroupSlotsOf(assignment), [assignment], taken, cancellationToken);

    internal static async Task SettleGroupAsync(WorkflowService workflows, ApplicationDbContext context, IReadOnlyList<JobSlot> group, IEnumerable<SlotAssignment> also, StepFollowUpContext taken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(group);
        var rows = group.SelectMany(slot => slot.Assignments).Concat(also).DistinctBy(row => row.Id).ToList();
        if (group.Count == 0 || rows.Count(row => row.State == SlotAssignmentState.Assigned) < group.Count)
        {
            return;
        }

        foreach (var other in rows.Where(row => row.State is SlotAssignmentState.Requested or SlotAssignmentState.StaffQuestion or SlotAssignmentState.Accepted).OrderBy(row => row.Created).ThenBy(row => row.Id).ToList())
        {
            var step = SlotAssignmentWorkflowAttribute.NotSelectedStepFor(other.State)!;
            await workflows.RunSystemStepInTransactionAsync<SlotAssignmentState, SlotAssignment>(context, other, step, cancellationToken, taken.AfterCommit, taken.CorrelationId);
        }

        var groupIds = group.Select(slot => slot.Id).ToList();
        var rounds = await context.RequestRounds.AsTracking()
            .Where(round => groupIds.Contains(round.JobSlotId) && round.State == RequestRoundState.Open)
            .OrderBy(round => round.Created).ThenBy(round => round.Id)
            .ToListAsync(cancellationToken);
        foreach (var round in rounds)
        {
            await workflows.RunSystemStepInTransactionAsync<RequestRoundState, RequestRound>(context, round, RequestRoundWorkflowAttribute.FilledStep, cancellationToken, taken.AfterCommit, taken.CorrelationId);
        }
    }
}

public sealed class SlotStepsSettleTheirGroups : IStepFollowUp
{
    private readonly WorkflowService workflows;

    public SlotStepsSettleTheirGroups(WorkflowService workflows)
    {
        this.workflows = workflows;
    }

    public Type EntityType => typeof(JobSlot);

    public async Task RunAsync(ApplicationDbContext context, IStateEntity entity, StepFollowUpContext taken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(taken);
        if (entity is not JobSlot slot || !taken.Outcome.Steps.Any(step => ReferenceEquals(step.Entity, slot) && step.TransitionKey is JobSlotWorkflowAttribute.CancelStep or JobSlotWorkflowAttribute.SaveStep))
        {
            return;
        }

        var live = await context.SlotAssignments.AsTracking()
            .WithWorkflowGraph()
            .Where(assignment => assignment.Slot.JobId == slot.JobId && assignment.Slot.State == JobSlotState.Active
                && (assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted))
            .OrderBy(assignment => assignment.Created).ThenBy(assignment => assignment.Id)
            .ToListAsync(cancellationToken);
        var settled = new HashSet<Guid>();
        foreach (var request in live)
        {
            var group = SlotAssignmentWorkflowAttribute.GroupSlotsOf(request);
            if (settled.Add(group[0].Id))
            {
                await SlotGroupFollowUp.SettleGroupAsync(this.workflows, context, group, [], taken, cancellationToken);
            }
        }
    }
}
