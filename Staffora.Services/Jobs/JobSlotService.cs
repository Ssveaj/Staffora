using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Caching;
using Staffora.Services.Catalogue;
using Staffora.Services.Resources;
using Staffora.Services.Workflow;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Jobs;

public sealed class JobSlotService
{
    public const string NoEdit = "You do not have permission to change jobs' slots.";

    public const string NotFound = "Slot not found.";

    public const string NameMissing = "A slot needs a name.";

    public static readonly string TextTooLong = $"A slot's name has at most {JobSlot.NameMaxLength} characters and its description {JobSlot.DescriptionMaxLength}.";

    public const string CreateBySave = "Slots are added by their add step.";

    public static readonly string CountOutOfRange = $"Between 1 and {JobSlotRules.MaxAddCount} slots are added at once.";

    public const string QualificationNotActive = "Only an active qualification of the catalogue is required of a staff member.";

    public const string ServiceNotActive = "Only an active service of the catalogue makes a slot.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly SlotAssignmentService assignments;
    private readonly StaffingService staffing;
    private readonly ServiceCatalogueService catalogue;
    private readonly TenantLookupCache lookupCache;
    private readonly RequestRoundService rounds;
    private readonly ILogger<JobSlotService> log;

    public JobSlotService(
        IApplicationDbContextFactory<ApplicationDbContext> dbFactory,
        IRuntimeExecutionContext runtime,
        PermissionService permissionService,
        WorkflowService workflows,
        StateHistoryService history,
        SlotAssignmentService assignments,
        StaffingService staffing,
        ServiceCatalogueService catalogue,
        TenantLookupCache lookupCache,
        RequestRoundService rounds,
        ILogger<JobSlotService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.assignments = assignments;
        this.staffing = staffing;
        this.catalogue = catalogue;
        this.lookupCache = lookupCache;
        this.rounds = rounds;
        this.log = log;
    }

    private static DetachedEntityUpdaterOptions<JobSlot> UpdaterOptions => new DetachedEntityUpdaterOptions<JobSlot>()
        .WithCollection(slot => slot.Qualifications);

    public SlotAssignmentService Assignments => this.assignments;

    public StaffingService Staffing => this.staffing;

    public RequestRoundService Rounds => this.rounds;

    public async Task<Result<DetachedEntityUpdater<JobSlot>>> LoadJobSlotUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(JobService.Readers, cancellationToken))
            return Result<DetachedEntityUpdater<JobSlot>>.Failure(JobService.NoView);

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var slot = await context.JobSlots
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return slot is null
                ? Result<DetachedEntityUpdater<JobSlot>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<JobSlot>>.Success(new DetachedEntityUpdater<JobSlot>(slot, context, UpdaterOptions));
        }, this.log, $"the slot {id}", JobService.ReadFailed, cancellationToken);
    }

    public Task<Result<JobSlot>> NewJobSlotAsync(Guid jobId, CancellationToken cancellationToken) =>
        NewJobSlotAsync(jobId, copyOf: null, cancellationToken);

    public async Task<Result<JobSlot>> NewJobSlotAsync(Guid jobId, Guid? copyOf, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.JobsEdit, cancellationToken))
            return Result<JobSlot>.Failure(NoEdit);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<JobSlot>.Failure(JobService.ChangeInOwnTenantOnly);

        if (session.TenantId is not { } tenantId)
            return Result<JobSlot>.Failure(IRuntimeExecutionContext.NoTenant);

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var job = await context.Jobs.AsNoTracking().FirstOrDefaultAsync(row => row.Id == jobId, cancellationToken);
        if (job is null)
            return Result<JobSlot>.Failure(JobService.NotFound);

        var slot = new JobSlot { TenantId = tenantId, State = JobSlotState.Create, JobId = job.Id, Job = job, Count = 1 };
        if (copyOf is { } sourceId)
        {
            var source = await context.JobSlots.AsNoTracking()
                .Where(row => row.Id == sourceId && row.JobId == job.Id)
                .Select(row => new { row.Name, row.Description, row.ServiceId, Required = row.Qualifications.Select(required => new { required.QualificationId, required.Level }).ToList() })
                .FirstOrDefaultAsync(cancellationToken);
            if (source is null)
                return Result<JobSlot>.Failure(NotFound);

            slot.Name = source.Name;
            slot.Description = source.Description;
            slot.ServiceId = source.ServiceId;
            slot.Qualifications = [.. source.Required.Select(required => new JobSlotQualification { TenantId = tenantId, QualificationId = required.QualificationId, Level = required.Level })];
        }

        return Result<JobSlot>.Success(slot);
    }

    public async Task<Result<JobSlot>> NewJobSlotFromServiceAsync(Guid jobId, Guid serviceId, CancellationToken cancellationToken)
    {
        var slot = await NewJobSlotAsync(jobId, cancellationToken);
        if (!slot.IsSuccess || slot.Value is not { } blank)
            return slot;

        var services = await this.catalogue.GetCatalogueForJobsAsync(cancellationToken);
        if (!services.IsSuccess || services.Value is null)
            return Result<JobSlot>.Failure(services.Error);

        if (services.Value.FirstOrDefault(service => service.Id == serviceId) is not { } chosen)
            return Result<JobSlot>.Failure(ServiceNotActive);

        var qualifications = await this.lookupCache.GetAsync(QualificationService.Catalogue, blank.TenantId, cancellationToken);
        var active = qualifications.Where(qualification => qualification.State == QualificationState.Active).Select(qualification => qualification.Id).ToHashSet();
        var made = ServiceSlotRules.Build(blank.TenantId, blank.JobId, chosen.Template, active);
        made.Job = blank.Job;

        made.State = JobSlotState.Create;
        made.Count = chosen.DefaultSlotCount;
        made.Service = new Service { Id = chosen.Id, TenantId = blank.TenantId, Name = chosen.Name, Description = chosen.Description, State = chosen.State };
        return Result<JobSlot>.Success(made);
    }

    internal async Task<IReadOnlyList<ServiceChoice>> ServiceChoicesAsync(CancellationToken cancellationToken)
    {
        var services = await this.catalogue.GetCatalogueForJobsAsync(cancellationToken);
        return services.IsSuccess && services.Value is { } active
            ? [.. active.Select(service => new ServiceChoice(service.Id, service.Name, service.DefaultSlotCount))]
            : [];
    }

    public async Task<Result<Guid>> CreateJobSlotAsync(JobSlot slot, string transitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.JobsEdit, cancellationToken))
            return Result<Guid>.Failure(NoEdit);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<Guid>.Failure(JobService.ChangeInOwnTenantOnly);

        if (session.TenantId is not { } tenantId)
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

        if (transitionKey != JobSlotWorkflowAttribute.AddStep)
            return Result<Guid>.Failure(CreateBySave);

        if (!JobSlotRules.CountInRange(slot.Count))
            return Result<Guid>.Failure(CountOutOfRange);

        var qualifications = slot.Qualifications.Select(required => required.QualificationId).Distinct().ToList();

        var requirements = slot.Qualifications.GroupBy(required => required.QualificationId).Select(group => new SlotRequirement(group.Key, group.Min(each => each.Level))).ToList();
        var rows = Enumerable.Range(0, slot.Count).Select(_ => new JobSlot
        {
            TenantId = tenantId,
            State = JobSlotState.Create,
            JobId = slot.JobId,
            Name = slot.Name?.Trim() ?? string.Empty,
            Description = Optional(slot.Description),
            ServiceId = slot.ServiceId,
            Qualifications = [.. requirements.Select(requirement => new JobSlotQualification { TenantId = tenantId, QualificationId = requirement.QualificationId, Level = requirement.Level })],
        }).ToList();

        if (DescribeInvalidText(rows[0]) is { } textReason)
            return Result<Guid>.Failure(textReason);

        try
        {
            if (!await AllActiveAsync(qualifications, cancellationToken))
                return Result<Guid>.Failure(QualificationNotActive);

            if (rows[0].ServiceId is { } serviceId && !await ServiceActiveAsync(serviceId, cancellationToken))
                return Result<Guid>.Failure(ServiceNotActive);

            var added = await this.workflows.CreateManyAndTransitionAsync<JobSlotState, JobSlot>(rows, transitionKey, JobSlotWorkflowQueries.WithWorkflowGraph, cancellationToken);
            return added.IsSuccess && added.Value is { Count: > 0 } ids ? Result<Guid>.Success(ids[0], added.Message) : Result<Guid>.Failure(added.Error);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Adding a slot to job {JobId} failed.", slot.JobId);
            return Result<Guid>.Failure("An error occurred while adding the slot.");
        }
    }

    public async Task<Result> UpdateJobSlotAndTransitionAsync(DetachedEntityUpdater<JobSlot> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.JobsEdit, cancellationToken))
            return Result.Failure(NoEdit);

        if (this.runtime.Current.HasPlatformReach())
            return Result.Failure(JobService.ChangeInOwnTenantOnly);

        var id = (Guid)updater.OriginalValue(nameof(JobSlot.Id))!;
        try
        {
            var workflow = WorkflowRegistry.Default.Get<JobSlotState, JobSlot>();
            if (workflow.IsDeleteStep(transitionKey))
            {
                return await this.workflows.TransitionEntityAsync<JobSlotState, JobSlot>(id, transitionKey, JobSlotWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
            }

            if (!(workflow.TryGetTransition(transitionKey, out var step) && step.TakesNoEdits))
            {
                var slot = updater.Entity;
                Tidy(slot, updater);
                if (updater.HasChanges())
                {
                    if (DescribeInvalidText(slot) is { } textReason)
                        return Result.Failure(textReason);

                    if (!await AllActiveAsync(await AddedQualificationsAsync(id, slot, cancellationToken), cancellationToken))
                        return Result.Failure(QualificationNotActive);
                }
            }

            return await this.workflows.SaveAndTransitionAsync<JobSlotState, JobSlot>(updater, transitionKey, JobSlotWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Saving the slot {SlotId} failed.", id);
            return Result.Failure("An error occurred while saving the slot.");
        }
    }

    public async Task<Result<StateHistoryPage>> GetJobSlotHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await this.permissionService.HasAnyPermissionAsync(JobService.Readers, cancellationToken)
            ? await this.history.GetHistoryAsync<JobSlot>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(JobService.NoView);

    public async Task<Result<JobSlotEditorOptions>> GetJobSlotEditorOptionsAsync(Guid? id, CancellationToken cancellationToken)
    {
        var edits = await this.permissionService.HasPermissionAsync(RolePermission.JobsEdit, cancellationToken) && !this.runtime.Current.HasPlatformReach();
        if (id is null ? !edits : !edits && !await this.permissionService.HasAnyPermissionAsync(JobService.Readers, cancellationToken))
            return Result<JobSlotEditorOptions>.Failure(id is null ? NoEdit : JobService.NoView);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            Guid? slotId = id;
            var named = await context.JobSlotQualifications.AsNoTracking()
                .Where(required => required.JobSlotId == slotId)
                .Select(required => required.QualificationId)
                .ToListAsync(cancellationToken);
            var qualifications = await context.Qualifications.AsNoTracking()
                .Where(qualification => qualification.State == QualificationState.Active || named.Contains(qualification.Id))
                .OrderBy(qualification => qualification.Name)
                .Select(qualification => new SlotQualificationChoice(qualification.Id, qualification.Name, qualification.State == QualificationState.Archived))
                .ToListAsync(cancellationToken);
            var mayStaff = false;
            var mayAddSlots = false;
            if (id is { } stored)
            {
                var slot = await context.JobSlots.AsNoTracking().Where(row => row.Id == stored)
                    .Select(row => new { row.State, JobState = row.Job.State })
                    .FirstOrDefaultAsync(cancellationToken);
                mayStaff = slot is not null && await MayStaffAsync(context, stored, slot.State, slot.JobState, cancellationToken);

                mayAddSlots = edits && slot is not null && JobRules.SlotsOpen(slot.JobState);
            }

            var summary = id is { } staffed ? await this.staffing.SummaryOfSlotAsync(context, staffed, mayStaff, cancellationToken) : null;
            return Result<JobSlotEditorOptions>.Success(new JobSlotEditorOptions(FieldsLocked: !edits, qualifications, mayStaff, summary) { MayAddSlots = mayAddSlots });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the editor options of slot {SlotId} failed.", id);
            return Result<JobSlotEditorOptions>.Failure(JobService.ReadFailed);
        }
    }

    private async Task<bool> MayStaffAsync(ApplicationDbContext context, Guid slotId, JobSlotState state, JobState jobState, CancellationToken cancellationToken) =>
        state == JobSlotState.Active
        && JobRules.StaffingOpen(jobState)
        && await this.permissionService.HasPermissionAsync(RolePermission.PerformCoordination, cancellationToken)
        && !this.runtime.Current.HasPlatformReach()
        && !(await StaffingService.SlotGroupStateAsync(context, slotId, cancellationToken)).Full;

    public async Task<Result<SlotStaffingRead>> ReadSlotStaffingAsync(Guid slotId, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return Result<SlotStaffingRead>.Failure(StaffingService.NoView);

        if (this.runtime.Current.HasPlatformReach())
            return Result<SlotStaffingRead>.Failure(NotFound);

        var candidates = await this.staffing.ReadRoundCandidatesAsync(slotId, cancellationToken);
        if (!candidates.IsSuccess || candidates.Value is not { } found)
            return Result<SlotStaffingRead>.Failure(candidates.Error);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var slot = await context.JobSlots.AsNoTracking().Where(row => row.Id == slotId)
                .Select(row => new { row.State, JobState = row.Job.State })
                .FirstOrDefaultAsync(cancellationToken);
            if (slot is null)
                return Result<SlotStaffingRead>.Failure(NotFound);

            var window = await this.assignments.DefaultResponseWindowAsync(cancellationToken);
            if (!window.IsSuccess || window.Value is not { } respondWithin)
                return Result<SlotStaffingRead>.Failure(window.Error);

            var mayStaff = await MayStaffAsync(context, slotId, slot.State, slot.JobState, cancellationToken);
            var summary = await this.staffing.SummaryOfSlotAsync(context, slotId, mayStaff, cancellationToken);
            return summary is null
                ? Result<SlotStaffingRead>.Failure(NotFound)
                : Result<SlotStaffingRead>.Success(new SlotStaffingRead(summary, found.Candidates, found.OpenSlots) { RespondWithin = respondWithin });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the staffing of slot {SlotId} failed.", slotId);
            return Result<SlotStaffingRead>.Failure(StaffingService.ReadFailed);
        }
    }

    private async Task<IReadOnlyList<Guid>> AddedQualificationsAsync(Guid id, JobSlot slot, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var stored = await context.JobSlotQualifications.AsNoTracking().Where(required => required.JobSlotId == id).Select(required => required.QualificationId).ToListAsync(cancellationToken);
        return [.. slot.Qualifications.Select(required => required.QualificationId).Except(stored)];
    }

    private async Task<bool> AllActiveAsync(IEnumerable<Guid> qualificationIds, CancellationToken cancellationToken)
    {
        var wanted = qualificationIds.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return true;
        }

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var active = await context.Qualifications.AsNoTracking().CountAsync(qualification => wanted.Contains(qualification.Id) && qualification.State == QualificationState.Active, cancellationToken);
        return active == wanted.Count;
    }

    private async Task<bool> ServiceActiveAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Services.AsNoTracking().AnyAsync(service => service.Id == serviceId && service.State == ServiceState.Active, cancellationToken);
    }

    private static void Tidy(JobSlot slot, DetachedEntityUpdater<JobSlot> updater)
    {
        if (!string.Equals(slot.Name, updater.OriginalValue(nameof(JobSlot.Name)) as string, StringComparison.Ordinal))
        {
            slot.Name = slot.Name?.Trim() ?? string.Empty;
        }

        if (!string.Equals(slot.Description, updater.OriginalValue(nameof(JobSlot.Description)) as string, StringComparison.Ordinal))
        {
            slot.Description = Optional(slot.Description);
        }
    }

    private static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? DescribeInvalidText(JobSlot slot) =>
        string.IsNullOrWhiteSpace(slot.Name) ? NameMissing
        : slot.Name.Length > JobSlot.NameMaxLength || slot.Description?.Length > JobSlot.DescriptionMaxLength ? TextTooLong
        : null;
}

public sealed record SlotStaffingRead(SlotStaffingSummary Summary, IReadOnlyList<StaffingCandidateRow> Candidates, int OpenSlots)
{
    public ResponseWindow RespondWithin { get; init; } = ResponseWindow.Default;
}

public sealed record SlotQualificationChoice(Guid Id, string Name, bool Archived);

public sealed record JobSlotEditorOptions(bool FieldsLocked, IReadOnlyList<SlotQualificationChoice> Qualifications, bool MayStaff = false, SlotStaffingSummary? Staffing = null)
{
    public bool MayAddSlots { get; init; }
}
