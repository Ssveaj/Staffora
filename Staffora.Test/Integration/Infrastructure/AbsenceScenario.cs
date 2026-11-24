using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Messaging;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Coordination;
using Staffora.Services.Messaging;
using Staffora.Services.Scheduling;
using Staffora.Services.Tenancy;

namespace Staffora.Test.Integration.Infrastructure;

public sealed record AbsenceScenario(ScheduleScenario Schedule, Guid Coordinator, Guid Registrar, Guid Approver, Guid Viewer)
{
    public Guid TenantId => Schedule.TenantId;

    public StaffScenario StaffMembers => Schedule.StaffMembers;

    public static async Task<AbsenceScenario> CreateAsync(RealDatabase database, string name, CancellationToken cancellationToken)
    {
        var schedule = await ScheduleScenario.CreateAsync(database, name, cancellationToken);
        var staffMembers = schedule.StaffMembers;
        var coordinator = await staffMembers.AddPersonAsync(database, "Casey", cancellationToken);
        await using (var owner = ServiceHost.Create(database, staffMembers.Onboarded.OwnerRuntime()))
        {
            var given = await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(coordinator, staffMembers.RootId, staffMembers.Onboarded.Roles.Coordinator, staffMembers.TenantId);
            Assert.True(given.IsSuccess, given.Error);
        }

        var registrar = await staffMembers.AddPersonAsync(database, "Riley", cancellationToken);
        await TwoTenantScenario.GrantAsync(database, staffMembers.Onboarded.Tenant, registrar, cancellationToken, RolePermission.ManageResourceAbsence, RolePermission.ViewResourceAbsence, RolePermission.UsersView, RolePermission.ResourcesView);
        var approver = await staffMembers.AddPersonAsync(database, "Avery", cancellationToken);
        await TwoTenantScenario.GrantAsync(database, staffMembers.Onboarded.Tenant, approver, cancellationToken, RolePermission.ApproveResourceAbsence, RolePermission.ViewResourceAbsence);
        var viewer = await staffMembers.AddPersonAsync(database, "Vic", cancellationToken);
        await TwoTenantScenario.GrantAsync(database, staffMembers.Onboarded.Tenant, viewer, cancellationToken, RolePermission.ViewResourceAbsence, RolePermission.UsersView, RolePermission.ResourcesView);
        return new AbsenceScenario(schedule, coordinator, registrar, approver, viewer);
    }

    public Task<Guid> AddStaffAsync(RealDatabase database, string firstName, CancellationToken cancellationToken) =>
        Schedule.AddStaffAsync(database, firstName, cancellationToken);

    public async Task<Guid> AddCoordinatorAsync(RealDatabase database, string firstName, CancellationToken cancellationToken)
    {
        var user = await StaffMembers.AddPersonAsync(database, firstName, cancellationToken);
        await using var owner = ServiceHost.Create(database, StaffMembers.Onboarded.OwnerRuntime());
        var given = await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(user, StaffMembers.RootId, StaffMembers.Onboarded.Roles.Coordinator, StaffMembers.TenantId);
        Assert.True(given.IsSuccess, given.Error);
        return user;
    }

    public async Task<List<string>> StaffQuestionKeysAsync(RealDatabase database, Guid reader, CancellationToken cancellationToken)
    {
        await using var host = HostOf(database, reader);
        var workspace = host.Get<CoordinationService>();
        var snapshot = await workspace.ReadAsync(cancellationToken);
        Assert.True(snapshot.IsSuccess, snapshot.Error);
        var dock = await workspace.ReadPrioritiesAsync(snapshot.Value!, new PriorityQuery(), cancellationToken);
        Assert.True(dock.IsSuccess, dock.Error);
        return [.. dock.Value!.Items.Where(item => item.Kind == PriorityItemKind.StaffQuestions).Select(item => item.Key)];
    }

    public static async Task<int> UnreadAboutAsync(RealDatabase database, Guid userId, Guid entityId, CancellationToken cancellationToken, params IReadOnlyList<MessageType> types)
    {
        var keys = (types.Count == 0 ? SlotAssignmentWorkflowAttribute.StaffNoticeTypes.Concat(ResourceAbsenceWorkflowAttribute.StaffNoticeTypes) : types).Select(type => type.Key).ToList();
        await using var context = database.CreateSystemContext();
        return await context.MessageRecipients.AsNoTracking().CountAsync(copy => copy.UserId == userId && !copy.IsRead && copy.StoredMessage!.EntityId == entityId && keys.Contains(copy.StoredMessage.MessageType), cancellationToken);
    }

    public static async Task<(Guid ActorId, DateTime At)> LastStepByAsync(RealDatabase database, Guid entityId, string transitionKey, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        var entry = await context.StateHistoryEntries.AsNoTracking()
            .Where(row => row.EntityId == entityId && row.TransitionKey == transitionKey)
            .OrderByDescending(row => row.Created).ThenByDescending(row => row.Id)
            .FirstAsync(cancellationToken);
        return (entry.ActorId, entry.Created);
    }

    public async Task<Guid> AddWorkingStaffAsync(RealDatabase database, string firstName, DateOnly from, CancellationToken cancellationToken)
    {
        var user = await AddStaffAsync(database, firstName, cancellationToken);
        var pattern = await Schedule.PublishPatternAsync(database, $"Weekdays of {firstName}", cancellationToken, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(16, 0)));
        await using var admin = ServiceHost.Create(database, StaffMembers.Onboarded.RuntimeOf(Schedule.Admin));
        var assignments = admin.Get<PatternAssignmentService>();
        var id = (await ScheduleScenario.CreateAssignmentAsync(assignments, user, pattern, from, null, cancellationToken)).Value;
        var activated = await ScheduleScenario.StepAsync(assignments, id, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, cancellationToken);
        Assert.True(activated.IsSuccess, activated.Error);
        return user;
    }

    public ServiceHost HostOf(RealDatabase database, Guid userId) => ServiceHost.Create(database, StaffMembers.Onboarded.RuntimeOf(userId));

    public static Task<Result<Guid>> ReportAsync(ResourceAbsenceService service, Guid staffUser, AbsenceKind kind, DateOnly first, DateOnly last, CancellationToken cancellationToken, TimeOnly? start = null, TimeOnly? end = null) =>
        CreateAsync(service, staffUser, ResourceAbsenceWorkflowAttribute.ReportStep, kind, first, last, start, end, cancellationToken);

    public static Task<Result<Guid>> RegisterAsync(ResourceAbsenceService service, Guid staffUser, AbsenceKind kind, DateOnly first, DateOnly last, CancellationToken cancellationToken, TimeOnly? start = null, TimeOnly? end = null) =>
        CreateAsync(service, staffUser, ResourceAbsenceWorkflowAttribute.RegisterStep, kind, first, last, start, end, cancellationToken);

    private static async Task<Result<Guid>> CreateAsync(ResourceAbsenceService service, Guid staffUser, string step, AbsenceKind kind, DateOnly first, DateOnly last, TimeOnly? start, TimeOnly? end, CancellationToken cancellationToken)
    {
        var fresh = await service.NewResourceAbsenceAsync(staffUser, cancellationToken);
        if (!fresh.IsSuccess)
        {
            return Result<Guid>.Failure(fresh.Error);
        }

        var absence = fresh.Value!;
        (absence.Kind, absence.FirstDay, absence.LastDay, absence.StartTime, absence.EndTime) = (kind, first, last, start, end);
        return await service.CreateResourceAbsenceAsync(absence, step, cancellationToken);
    }

    public static async Task<Result> StepAsync(ResourceAbsenceService service, Guid id, string step, CancellationToken cancellationToken, Action<ResourceAbsence>? edit = null, string? comment = null)
    {
        var updater = await service.LoadResourceAbsenceUpdaterAsync(id, cancellationToken);
        if (!updater.IsSuccess)
        {
            return Result.Failure(updater.Error);
        }

        edit?.Invoke(updater.Value!.Entity);
        return await service.UpdateResourceAbsenceAndTransitionAsync(updater.Value!, step, comment, cancellationToken);
    }

    public static async Task<ResourceAbsence?> StoredAsync(RealDatabase database, Guid id, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        return await context.ResourceAbsences.AsNoTracking().FirstOrDefaultAsync(absence => absence.Id == id, cancellationToken);
    }

    public static async Task<List<string>> HistoryAsync(RealDatabase database, Guid id, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        return await context.StateHistoryEntries.AsNoTracking()
            .Where(entry => entry.EntityType == nameof(ResourceAbsence) && entry.EntityId == id)
            .OrderBy(entry => entry.Created).ThenBy(entry => entry.Id)
            .Select(entry => entry.TransitionKey)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<(string Subject, string Text, string? Link)>> NoticesOfAsync(RealDatabase database, Guid userId, CancellationToken cancellationToken)
    {
        await using var host = HostOf(database, userId);
        var page = await host.Get<NoticeService>().GetNoticesAsync(NoticeFilter.All, cancellationToken);
        Assert.True(page.IsSuccess, page.Error);
        return [.. page.Value!.Notices.Select(notice => (notice.Subject, notice.Text, notice.Link))];
    }
}
