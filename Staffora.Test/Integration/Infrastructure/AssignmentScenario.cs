using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Messaging;
using Staffora.Services.Customers;

namespace Staffora.Test.Integration.Infrastructure;

public sealed record AssignmentScenario(AbsenceScenario Absence, Guid Customer)
{
    public Guid TenantId => Absence.TenantId;

    public Guid Coordinator => Absence.Coordinator;

    public StaffScenario StaffMembers => Absence.StaffMembers;

    public static async Task<AssignmentScenario> CreateAsync(RealDatabase database, string name, CancellationToken cancellationToken)
    {
        var absence = await AbsenceScenario.CreateAsync(database, name, cancellationToken);
        await using var host = absence.HostOf(database, absence.Coordinator);
        var customer = await host.Get<CustomerService>().CreateCustomerAsync(new Customer { Name = "Brightpath Learning" }, CustomerWorkflowAttribute.ActivateStep, cancellationToken);
        Assert.True(customer.IsSuccess, customer.Error);
        return new AssignmentScenario(absence, customer.Value);
    }

    public static TimeProvider Clock { get; } = new AnchoredClock(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));

    public static void UseClock(IServiceCollection services) => services.AddSingleton(Clock);

    public ServiceHost HostOf(RealDatabase database, Guid userId) => ServiceHost.Create(database, StaffMembers.Onboarded.RuntimeOf(userId), UseClock);

    public Task<Guid> AddStaffAsync(RealDatabase database, string firstName, CancellationToken cancellationToken) =>
        Absence.AddStaffAsync(database, firstName, cancellationToken);

    public Task<Guid> AddCoordinatorAsync(RealDatabase database, string firstName, CancellationToken cancellationToken) =>
        Absence.AddCoordinatorAsync(database, firstName, cancellationToken);

    public ServiceHost HostOf(RealDatabase database, Guid userId, NoticeBus bus) =>
        ServiceHost.Create(database, StaffMembers.Onboarded.RuntimeOf(userId), services =>
        {
            UseClock(services);
            services.AddSingleton(bus);
        });

    public static Task<Guid> ResourceOfAsync(RealDatabase database, Guid userId, CancellationToken cancellationToken) =>
        ScheduleScenario.ResourceOfAsync(database, userId, cancellationToken);

    public static JobTimeSlot Occurrence(Weekday day, TimeOnly start, TimeOnly end) => new() { Weekday = day, Start = start, End = end };

    public async Task<(Guid Job, Guid[] Slots)> StartedJobAsync(RealDatabase database, string name, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken, string[]? slots = null, params JobTimeSlot[] occurrences)
    {
        await using var host = HostOf(database, Coordinator);
        var jobs = host.Get<JobService>();
        var draft = new Job
        {
            Name = name,
            CustomerId = Customer,
            OrganizationUnitId = StaffMembers.RootId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            TimeSlots = [.. occurrences.Length == 0 ? [Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0))] : occurrences],
        };
        var job = await jobs.CreateJobAsync(draft, JobWorkflowAttribute.RegisterStep, cancellationToken);
        Assert.True(job.IsSuccess, job.Error);
        var ids = new List<Guid>();
        foreach (var slot in slots ?? ["Lead"])
        {
            var added = await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job.Value, Name = slot }, JobSlotWorkflowAttribute.AddStep, cancellationToken);
            Assert.True(added.IsSuccess, added.Error);
            ids.Add(added.Value);
        }

        var started = await StepJobAsync(jobs, job.Value, JobWorkflowAttribute.StartFromRegistrationStep, cancellationToken);
        Assert.True(started.IsSuccess, started.Error);
        return (job.Value, [.. ids]);
    }

    public const string StepReason = "The reason the test gives.";

    public static async Task<Result> StepJobAsync(JobService jobs, Guid job, string step, CancellationToken cancellationToken, string? reason = null)
    {
        var updater = await jobs.LoadJobUpdaterAsync(job, cancellationToken);
        var asks = Staffora.Core.Workflow.WorkflowRegistry.Default.Get<JobState, Job>().TryGetTransition(step, out var transition) && transition.CommentPrompt is not null;
        return updater.IsSuccess ? await jobs.UpdateJobAndTransitionAsync(updater.Value!, step, reason ?? (asks ? StepReason : null), cancellationToken) : Result.Failure(updater.Error);
    }

    public static async Task<Result> StepSlotAsync(JobSlotService slots, Guid slot, string step, CancellationToken cancellationToken)
    {
        var updater = await slots.LoadJobSlotUpdaterAsync(slot, cancellationToken);
        return updater.IsSuccess ? await slots.UpdateJobSlotAndTransitionAsync(updater.Value!, step, null, cancellationToken) : Result.Failure(updater.Error);
    }

    public async Task<Result<Guid>> RequestAsync(RealDatabase database, Guid actor, Guid slot, Guid staffUser, CancellationToken cancellationToken)
    {
        await using var host = HostOf(database, actor);
        return await host.Get<SlotAssignmentService>().RequestStaffAsync(slot, await ResourceOfAsync(database, staffUser, cancellationToken), cancellationToken);
    }

    public async Task<Result<Guid>> AssignAsync(RealDatabase database, Guid actor, Guid slot, Guid staffUser, CancellationToken cancellationToken)
    {
        await using var host = HostOf(database, actor);
        return await host.Get<SlotAssignmentService>().AssignStaffAsync(slot, await ResourceOfAsync(database, staffUser, cancellationToken), cancellationToken);
    }

    public async Task<Result> StepAsync(RealDatabase database, Guid actor, Guid assignment, string step, CancellationToken cancellationToken, string? comment = null)
    {
        await using var host = HostOf(database, actor);
        return await StepAsync(host.Get<SlotAssignmentService>(), assignment, step, cancellationToken, comment);
    }

    public static async Task<Result> StepAsync(SlotAssignmentService service, Guid assignment, string step, CancellationToken cancellationToken, string? comment = null)
    {
        var updater = await service.LoadSlotAssignmentUpdaterAsync(assignment, cancellationToken);
        return updater.IsSuccess ? await service.UpdateSlotAssignmentAndTransitionAsync(updater.Value!, step, comment, cancellationToken) : Result.Failure(updater.Error);
    }

    public static async Task<SlotAssignment?> StoredAsync(RealDatabase database, Guid id, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        return await context.SlotAssignments.AsNoTracking().FirstOrDefaultAsync(assignment => assignment.Id == id, cancellationToken);
    }

    public static async Task<SlotAssignmentState[]> StatesAsync(RealDatabase database, CancellationToken cancellationToken, params Guid[] ids)
    {
        await using var context = database.CreateSystemContext();
        var states = await context.SlotAssignments.AsNoTracking().Where(assignment => ids.Contains(assignment.Id)).ToDictionaryAsync(assignment => assignment.Id, assignment => assignment.State, cancellationToken);
        return [.. ids.Select(id => states[id])];
    }

    public static async Task<List<string>> HistoryAsync(RealDatabase database, Guid id, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        return await context.StateHistoryEntries.AsNoTracking()
            .Where(entry => entry.EntityType == nameof(SlotAssignment) && entry.EntityId == id)
            .OrderBy(entry => entry.Created).ThenBy(entry => entry.Id)
            .Select(entry => entry.TransitionKey)
            .ToListAsync(cancellationToken);
    }

    public Task<List<(string Subject, string Text, string? Link)>> NoticesOfAsync(RealDatabase database, Guid userId, CancellationToken cancellationToken) =>
        Absence.NoticesOfAsync(database, userId, cancellationToken);

    public static async Task<List<string>> StaffThreadAsync(RealDatabase database, Guid assignment, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        return await context.Comments.AsNoTracking()
            .Where(comment => comment.CommentContainer!.EntityType == nameof(SlotAssignment) && comment.CommentContainer!.EntityId == assignment && comment.CommentContainer!.Audience == CommentAudience.Staff)
            .OrderBy(comment => comment.Created).ThenBy(comment => comment.Id)
            .Select(comment => comment.Text)
            .ToListAsync(cancellationToken);
    }
}
