using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Jobs;

public sealed class AssignmentsBlockSlotDelete : IDeleteBlocker
{
    public Type EntityType => typeof(JobSlot);

    public IReadOnlyList<Type> Dependents { get; } = [typeof(SlotAssignment)];

    public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        var assignments = await context.SlotAssignments.AsNoTracking().CountAsync(assignment => assignment.JobSlotId == entity.Id, cancellationToken);
        return assignments == 0 ? null : Reason(assignments);
    }

    public static string Reason(int assignments) =>
        $"{assignments} {(assignments == 1 ? "staff member was" : "staff members were")} asked for or assigned to this slot: it is kept for their record. Cancel the slot instead.";
}

public sealed class JobsBlockCustomerDelete : IDeleteBlocker
{
    public Type EntityType => typeof(Customer);

    public IReadOnlyList<Type> Dependents { get; } = [typeof(Job)];

    public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        var jobs = await context.Jobs.AsNoTracking().CountAsync(job => job.CustomerId == entity.Id, cancellationToken);
        return jobs == 0 ? null : Reason(jobs);
    }

    public static string Reason(int jobs) =>
        $"{JobsBlockWords.Count(jobs)} registered for this customer: it is kept for their record. Keep the customer inactive instead.";
}

public sealed class JobsBlockContactDelete : IDeleteBlocker
{
    public Type EntityType => typeof(CustomerContact);

    public IReadOnlyList<Type> Dependents { get; } = [typeof(Job)];

    public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        var fixedStates = JobsBlockWords.StatesFixing(nameof(Job.ContactId));
        var jobs = await context.Jobs.AsNoTracking().Where(job => job.ContactId == entity.Id)
            .GroupBy(job => fixedStates.Contains(job.State))
            .Select(group => new { Kept = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var total = jobs.Sum(group => group.Count);
        return total == 0 ? null : Reason(total, jobs.Where(group => group.Kept).Sum(group => group.Count));
    }

    public static string Reason(int jobs, int kept = 0) =>
        kept == 0
            ? $"{JobsBlockWords.Count(jobs)} agreed with this contact: choose another contact on them first, or keep the contact inactive."
            : $"{JobsBlockWords.Count(jobs)} agreed with this contact, {JobsBlockWords.Kept(jobs, kept)}: keep the contact inactive instead.";
}

public sealed class JobsBlockUnitDelete : IDeleteBlocker
{
    public Type EntityType => typeof(OrganizationUnit);

    public IReadOnlyList<Type> Dependents { get; } = [typeof(Job)];

    public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        var fixedStates = JobsBlockWords.StatesFixing(nameof(Job.OrganizationUnitId));
        var jobs = await context.Jobs.AsNoTracking().Where(job => job.OrganizationUnitId == entity.Id)
            .GroupBy(job => fixedStates.Contains(job.State))
            .Select(group => new { Kept = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var total = jobs.Sum(group => group.Count);
        return total == 0 ? null : Reason(total, jobs.Where(group => group.Kept).Sum(group => group.Count));
    }

    public static string Reason(int jobs, int kept = 0) =>
        kept == 0
            ? $"{JobsBlockWords.Count(jobs)} run by this unit: move them to another unit first, or archive the unit instead."
            : $"{JobsBlockWords.Count(jobs)} run by this unit, {JobsBlockWords.Kept(jobs, kept)}: archive the unit instead.";
}

internal static class JobsBlockWords
{
    public static string Count(int jobs) => jobs == 1 ? "1 job is" : $"{jobs} jobs are";

    public static string Kept(int jobs, int kept) =>
        kept < jobs ? $"{kept} of which {(kept == 1 ? "keeps it for its record" : "keep it for their record")}"
        : jobs == 1 ? "which keeps it for its record"
        : "which keep it for their record";

    public static JobState[] StatesFixing(string field)
    {
        var workflow = WorkflowRegistry.Default.Get<JobState, Job>();
        return [.. Enum.GetValues<JobState>().Where(state => !workflow.GetFieldConfig(state).GetFieldAccess(field).HasFlag(FieldAccessFlags.Editable))];
    }
}

public sealed class RoundsBlockSlotDelete : IDeleteBlocker
{
    public Type EntityType => typeof(JobSlot);

    public IReadOnlyList<Type> Dependents { get; } = [typeof(RequestRound)];

    public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        var rounds = await context.RequestRounds.AsNoTracking().CountAsync(round => round.JobSlotId == entity.Id, cancellationToken);
        return rounds == 0 ? null : $"{rounds} request {(rounds == 1 ? "round was" : "rounds were")} sent from this slot: it is kept for their record. Cancel the slot instead.";
    }
}
