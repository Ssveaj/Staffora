using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;

namespace Staffora.Services.Coordination;

internal sealed class TenantChangeCollector
{
    private readonly Dictionary<Guid, (HashSet<Guid> Jobs, HashSet<Guid> StaffMembers)> byTenant = [];

    private readonly Dictionary<Guid, HashSet<Guid>> users = [];
    private readonly Dictionary<Guid, HashSet<Guid>> patterns = [];
    private readonly Dictionary<Guid, HashSet<Guid>> customers = [];
    private readonly Guid? fallbackTenant;

    private TenantChangeCollector(Guid? fallbackTenant) => this.fallbackTenant = fallbackTenant;

    public static TenantChangeCollector Attach(DbContext context, Guid? fallbackTenant)
    {
        ArgumentNullException.ThrowIfNull(context);
        var collector = new TenantChangeCollector(fallbackTenant);
        context.SavingChanges += (sender, _) => collector.Collect((DbContext)sender!);
        return collector;
    }

    public IReadOnlyList<TenantChange> Changes =>
        [.. this.byTenant.Select(tenant => new TenantChange(tenant.Key, tenant.Value.Jobs, tenant.Value.StaffMembers)).Where(change => !change.IsEmpty)];

    public async Task NameAffectedAsync(DbContext context, ILogger log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(log);
        foreach (var (tenant, users) in this.users.Where(entry => entry.Value.Count > 0).ToList())
        {
            var ids = users.ToList();
            await ResolveAsync(log, tenant, "the staff records of the users", async () => Ids(tenant).StaffMembers.UnionWith(await context.Set<Resource>().AsNoTracking()
                .Where(resource => resource.TenantId == tenant && ids.Contains(resource.UserId))
                .Select(resource => resource.Id)
                .ToListAsync(cancellationToken)), cancellationToken);
        }

        foreach (var (tenant, patterns) in this.patterns.Where(entry => entry.Value.Count > 0).ToList())
        {
            var ids = patterns.ToList();
            await ResolveAsync(log, tenant, "the staff members of the work patterns", async () => Ids(tenant).StaffMembers.UnionWith(await context.Set<ResourcePatternAssignment>().AsNoTracking()
                .Where(assignment => assignment.TenantId == tenant && ids.Contains(assignment.WorkPatternId) && assignment.State == ResourcePatternAssignmentState.Active)
                .Select(assignment => assignment.ResourceId)
                .Distinct()
                .ToListAsync(cancellationToken)), cancellationToken);
        }

        foreach (var (tenant, customers) in this.customers.Where(entry => entry.Value.Count > 0).ToList())
        {
            var ids = customers.ToList();
            await ResolveAsync(log, tenant, "the jobs of the customers", async () => Ids(tenant).Jobs.UnionWith(await context.Set<Job>().AsNoTracking()
                .Where(job => job.TenantId == tenant && ids.Contains(job.CustomerId))
                .Select(job => job.Id)
                .ToListAsync(cancellationToken)), cancellationToken);
        }

        await NameJobStaffMembersAsync(context, log, cancellationToken);
    }

    public async Task NameJobStaffMembersAsync(DbContext context, ILogger log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(log);
        foreach (var (tenant, ids) in this.byTenant.Where(entry => entry.Value.Jobs.Count > 0).ToList())
        {
            var jobs = ids.Jobs.ToList();
            try
            {
                var staffMembers = await context.Set<SlotAssignment>().AsNoTracking()
                    .Where(assignment => assignment.TenantId == tenant
                        && jobs.Contains(assignment.Slot.JobId)
                        && (assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted || assignment.State == SlotAssignmentState.Assigned))
                    .Select(assignment => assignment.ResourceId)
                    .Distinct()
                    .ToListAsync(cancellationToken);
                ids.StaffMembers.UnionWith(staffMembers.Where(staff => staff != Guid.Empty));
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                log.LogWarning(exception, "The staff members of the jobs a write touched in tenant {TenantId} could not be read; their own pages read again when they next load.", tenant);
            }
        }
    }

    public void PublishTo(TenantChangeBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        foreach (var change in Changes)
        {
            bus.Publish(change);
        }
    }

    private void Collect(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            switch (entry.Entity)
            {
                case Job job:
                    Add(job, job.Id, null);
                    break;
                case JobTimeSlot occurrence:
                    Add(occurrence, occurrence.JobId, null);
                    break;
                case JobSlot slot:
                    Add(slot, slot.JobId, null);
                    break;
                case SlotAssignment assignment:
                    Add(assignment, JobOf(context, assignment), assignment.ResourceId);
                    break;
                case Resource staff:
                    Add(staff, null, staff.Id);
                    break;
                case ResourceAbsence absence:
                    Add(absence, null, absence.ResourceId);
                    break;
                case ResourcePatternAssignment pattern:
                    Add(pattern, null, pattern.ResourceId);
                    break;
                case ResourceQualification held:
                    Add(held, null, held.ResourceId);
                    break;
                case WorkPattern workPattern:
                    Pend(this.patterns, workPattern, workPattern.Id);
                    break;
                case WorkPatternSegment segment:
                    Pend(this.patterns, segment, segment.WorkPatternId);
                    break;
                case Customer customer:
                    Pend(this.customers, customer, customer.Id);
                    break;
                case ApplicationUser user:
                    Pend(this.users, user, user.Id);
                    break;
            }
        }
    }

    private void Pend(Dictionary<Guid, HashSet<Guid>> pending, ITenantEntity row, Guid id)
    {
        var tenant = row.TenantId != Guid.Empty ? row.TenantId : this.fallbackTenant ?? Guid.Empty;
        if (tenant == Guid.Empty || id == Guid.Empty)
        {
            return;
        }

        if (!pending.TryGetValue(tenant, out var ids))
        {
            pending[tenant] = ids = [];
        }

        ids.Add(id);
    }

    private (HashSet<Guid> Jobs, HashSet<Guid> StaffMembers) Ids(Guid tenant)
    {
        if (!this.byTenant.TryGetValue(tenant, out var ids))
        {
            this.byTenant[tenant] = ids = ([], []);
        }

        return ids;
    }

    private static async Task ResolveAsync(ILogger log, Guid tenant, string what, Func<Task> read, CancellationToken cancellationToken)
    {
        try
        {
            await read();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            log.LogWarning(exception, "Reading {What} a write touched in tenant {TenantId} failed; open pages read again when they next load.", what, tenant);
        }
    }

    private static Guid? JobOf(DbContext context, SlotAssignment assignment)
    {
        if (assignment.Slot is { } slot && slot.Id == assignment.JobSlotId)
        {
            return slot.JobId;
        }

        return context.ChangeTracker.Entries<JobSlot>().Select(entry => entry.Entity).FirstOrDefault(tracked => tracked.Id == assignment.JobSlotId)?.JobId;
    }

    private void Add(ITenantEntity row, Guid? jobId, Guid? staffId)
    {
        var tenant = row.TenantId != Guid.Empty ? row.TenantId : this.fallbackTenant ?? Guid.Empty;
        if (tenant == Guid.Empty)
        {
            return;
        }

        if (!this.byTenant.TryGetValue(tenant, out var ids))
        {
            this.byTenant[tenant] = ids = ([], []);
        }

        if (jobId is { } job && job != Guid.Empty)
        {
            ids.Jobs.Add(job);
        }

        if (staffId is { } staff && staff != Guid.Empty)
        {
            ids.StaffMembers.Add(staff);
        }
    }
}
