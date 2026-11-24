using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Scheduling;
using Staffora.Services.Tenancy;

namespace Staffora.Test.Integration.Infrastructure;

public sealed record ScheduleScenario(StaffScenario StaffMembers, Guid Admin)
{
    public Guid TenantId => StaffMembers.TenantId;

    public static async Task<ScheduleScenario> CreateAsync(RealDatabase database, string name, CancellationToken cancellationToken)
    {
        var staffMembers = await StaffScenario.CreateAsync(database, cancellationToken, TwoTenantScenario.UniqueName(name));
        var admin = await staffMembers.AddPersonAsync(database, "Alex", cancellationToken);
        await using var owner = ServiceHost.Create(database, staffMembers.Onboarded.OwnerRuntime());
        var given = await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(admin, staffMembers.RootId, staffMembers.Onboarded.Roles.Administrator, staffMembers.TenantId);
        Assert.True(given.IsSuccess, given.Error);
        return new ScheduleScenario(staffMembers, admin);
    }

    public async Task<Guid> AddStaffAsync(RealDatabase database, string firstName, CancellationToken cancellationToken)
    {
        var user = await StaffMembers.AddPersonAsync(database, firstName, cancellationToken);
        await using var owner = ServiceHost.Create(database, StaffMembers.Onboarded.OwnerRuntime());
        var given = await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(user, StaffMembers.TeamId, StaffMembers.Onboarded.Roles.Staff, TenantId);
        Assert.True(given.IsSuccess, given.Error);
        return user;
    }

    public async Task<Guid> PublishPatternAsync(RealDatabase database, string name, CancellationToken cancellationToken, params PatternSegment[] segments)
    {
        await using var host = ServiceHost.Create(database, StaffMembers.Onboarded.RuntimeOf(Admin));
        var patterns = host.Get<WorkPatternService>();
        var draft = new WorkPattern { Name = name, CycleWeeks = 1 };
        foreach (var segment in segments)
        {
            draft.Segments.Add(new WorkPatternSegment { DayIndex = segment.DayIndex, Start = segment.Start, End = segment.End });
        }

        var created = await patterns.CreateWorkPatternAsync(draft, WorkPatternWorkflowAttribute.SaveStep, cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        var published = await patterns.UpdateWorkPatternAndTransitionAsync((await patterns.LoadWorkPatternUpdaterAsync(created.Value, cancellationToken)).Value!, WorkPatternWorkflowAttribute.PublishStep, null, cancellationToken);
        Assert.True(published.IsSuccess, published.Error);
        return created.Value;
    }

    public static PatternSegment[] Weekdays(TimeOnly start, TimeOnly end) =>
        [.. Enumerable.Range(0, 5).Select(day => new PatternSegment(day, start, end))];

    public static async Task<Core.Models.Result<Guid>> CreateAssignmentAsync(PatternAssignmentService service, Guid staffUser, Guid patternId, DateOnly from, DateOnly? to, CancellationToken cancellationToken, DateOnly? anchor = null)
    {
        var fresh = await service.NewResourcePatternAssignmentAsync(staffUser, cancellationToken);
        Assert.True(fresh.IsSuccess, fresh.Error);
        var assignment = fresh.Value!;
        assignment.WorkPatternId = patternId;
        assignment.EffectiveFrom = from;
        assignment.EffectiveTo = to;
        assignment.CycleAnchor = anchor ?? ScheduleRules.MondayOf(from);
        return await service.CreateResourcePatternAssignmentAsync(assignment, ResourcePatternAssignmentWorkflowAttribute.SaveStep, cancellationToken);
    }

    public static async Task<Core.Models.Result> StepAsync(PatternAssignmentService service, Guid id, string step, CancellationToken cancellationToken, Action<ResourcePatternAssignment>? edit = null)
    {
        var updater = await service.LoadResourcePatternAssignmentUpdaterAsync(id, cancellationToken);
        Assert.True(updater.IsSuccess, updater.Error);
        edit?.Invoke(updater.Value!.Entity);
        return await service.UpdateResourcePatternAssignmentAndTransitionAsync(updater.Value!, step, null, cancellationToken);
    }

    public static async Task<ResourcePatternAssignment?> StoredAsync(RealDatabase database, Guid id, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        return await context.ResourcePatternAssignments.AsNoTracking().FirstOrDefaultAsync(assignment => assignment.Id == id, cancellationToken);
    }

    public static async Task<List<string>> HistoryAsync(RealDatabase database, Guid id, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        return await context.StateHistoryEntries.AsNoTracking()
            .Where(entry => entry.EntityType == nameof(ResourcePatternAssignment) && entry.EntityId == id)
            .OrderBy(entry => entry.Created).ThenBy(entry => entry.Id)
            .Select(entry => entry.TransitionKey)
            .ToListAsync(cancellationToken);
    }

    public static async Task<Guid> ResourceOfAsync(RealDatabase database, Guid userId, CancellationToken cancellationToken) =>
        (await StaffScenario.RecordOfAsync(database, userId, cancellationToken)).Id;
}
