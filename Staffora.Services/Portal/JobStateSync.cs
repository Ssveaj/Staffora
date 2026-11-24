using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Integration;
using Staffora.Data;

namespace Staffora.Services.Portal;

public sealed class JobStateSync : IJobStateSync
{
    public async Task QueueStateAsync(DbContext store, Job job, JobState from, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var context = ContextOf(store);
        if (job.CrossTenantId is not { } correlation || !OrderSync.IsCustomerVisible(job.State))
        {
            return;
        }

        var portal = await PortalOfAsync(context, job, cancellationToken);
        var staffMembers = OrderSync.SendsStaffMembers(job.State) ? await StaffNamesAsync(context, job.Id, cancellationToken) : null;
        var payload = OrderSync.WriteJobState(new JobStatePayload(job.State, OrderSync.WasConfirmed(from), staffMembers));
        IntegrationOutbox.Enqueue(context, OrderSync.JobStateKind, portal, correlation, payload);
    }

    public async Task QueueStaffMembersAsync(DbContext store, SlotAssignment assignment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var context = ContextOf(store);

        if (assignment.Slot?.Job is not { CrossTenantId: { } correlation } job || job.State is not (JobState.Confirmed or JobState.InProgress))
        {
            return;
        }

        var portal = await PortalOfAsync(context, job, cancellationToken);
        var staffMembers = await StaffNamesAsync(context, job.Id, cancellationToken);
        var payload = OrderSync.WriteJobState(new JobStatePayload(job.State, Confirmed: true, staffMembers));
        IntegrationOutbox.Enqueue(context, OrderSync.JobStateKind, portal, correlation, payload);
    }

    public async Task QueueAnswerAsync(DbContext store, Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var context = ContextOf(store);
        if (job.CrossTenantId is not { } correlation || !OrderSync.AnswersCancellation(job.State))
        {
            return;
        }

        var jobId = job.Id;
        var confirmed = job.State == JobState.Completed
            || await context.StateHistoryEntries.AsNoTracking().AnyAsync(entry => entry.EntityType == nameof(Job) && entry.EntityId == jobId && entry.ToState == (int)JobState.Confirmed, cancellationToken);
        var portal = await PortalOfAsync(context, job, cancellationToken);
        var staffMembers = OrderSync.SendsStaffMembers(job.State) ? await StaffNamesAsync(context, job.Id, cancellationToken) : null;
        var payload = OrderSync.WriteJobState(new JobStatePayload(job.State, confirmed, staffMembers));
        IntegrationOutbox.Enqueue(context, OrderSync.JobStateKind, portal, correlation, payload);
    }

    private static ApplicationDbContext ContextOf(DbContext store) =>
        store as ApplicationDbContext ?? throw new InvalidOperationException("A job's portal is told on the application's data context.");

    private static async Task<Guid> PortalOfAsync(ApplicationDbContext context, Job job, CancellationToken cancellationToken)
    {
        var customerId = job.CustomerId;
        return await context.Customers.AsNoTracking()
            .Where(customer => customer.Id == customerId)
            .Select(customer => customer.LinkedTenantId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("A job a customer's portal sent names a customer without a portal, so its portal cannot be told.");
    }

    private static async Task<IReadOnlyList<string>> StaffNamesAsync(ApplicationDbContext context, Guid jobId, CancellationToken cancellationToken)
    {
        var stored = await context.SlotAssignments.AsNoTracking()
            .Where(assignment => assignment.Slot.JobId == jobId)
            .Select(assignment => new { assignment.Id, assignment.State, assignment.Resource.User.FirstName, assignment.Resource.User.LastName })
            .ToListAsync(cancellationToken);
        var inMemory = context.ChangeTracker.Entries<SlotAssignment>().ToDictionary(entry => entry.Entity.Id, entry => entry.Entity.State);
        return OrderSync.StaffNames(stored
            .Where(assignment => (inMemory.TryGetValue(assignment.Id, out var state) ? state : assignment.State) == SlotAssignmentState.Assigned)
            .Select(assignment => ((string?)assignment.FirstName, (string?)assignment.LastName)));
    }
}
