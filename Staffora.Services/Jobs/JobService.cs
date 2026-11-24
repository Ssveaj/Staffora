using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Comments;
using Staffora.Services.CustomFields;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Jobs;

public sealed class JobService
{
    public static readonly IReadOnlyList<RolePermission> Readers =
    [
        RolePermission.JobsView,
        RolePermission.JobsCreate,
        RolePermission.JobsEdit,
        RolePermission.JobsDelete,
        RolePermission.JobsCancel,
        RolePermission.ViewCoordination,
        RolePermission.PerformCoordination,
    ];

    public static readonly IReadOnlyList<RolePermission> Steppers = [RolePermission.JobsCreate, RolePermission.JobsEdit, RolePermission.PerformCoordination, RolePermission.JobsDelete, RolePermission.JobsCancel];

    public const string NoView = "You do not have permission to view jobs.";

    public const string NoCreate = "You do not have permission to register jobs.";

    public const string NoChange = "You do not have permission to change jobs.";

    public const string NotFound = "Job not found.";

    public const string NameMissing = "A job needs a name.";

    public static readonly string TextTooLong = $"A job's name has at most {Job.NameMaxLength} characters and its description {Job.DescriptionMaxLength}.";

    public static readonly string PlaceTooLong = $"A job's place has at most {Job.PlaceNameMaxLength} characters in its name, {Job.StreetAddressMaxLength} in its street address, {Job.PostalCodeMaxLength} in its postal code, {Job.CityMaxLength} in its city and {Job.AccessNotesMaxLength} in its access notes.";

    public const string CreateByRegister = "A job is created by registering it.";

    public const string ChangeInOwnTenantOnly = "The platform console keeps no tenant's jobs: impersonate one of the tenant's users to change them.";

    public const string ReadFailed = "The jobs could not be read. Try again.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly TenantClock clock;
    private readonly JobSlotService slots;
    private readonly CustomFieldService attributes;
    private readonly CommentService comments;
    private readonly StaffConversationService conversations;
    private readonly ILogger<JobService> log;

    public JobService(
        IApplicationDbContextFactory<ApplicationDbContext> dbFactory,
        IRuntimeExecutionContext runtime,
        PermissionService permissionService,
        WorkflowService workflows,
        StateHistoryService history,
        TenantClock clock,
        JobSlotService slots,
        CustomFieldService attributes,
        CommentService comments,
        StaffConversationService conversations,
        ILogger<JobService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.clock = clock;
        this.slots = slots;
        this.attributes = attributes;
        this.comments = comments;
        this.conversations = conversations;
        this.log = log;
    }

    private static DetachedEntityUpdaterOptions<Job> UpdaterOptions => new DetachedEntityUpdaterOptions<Job>()
        .WithCollection(job => job.TimeSlots)
        .WithCollection(job => job.Attributes);

    public JobSlotService Slots => this.slots;

    public StaffConversationService Conversations => this.conversations;

    public async Task<Result<ListPage<JobListItem>>> QueryJobsAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<ListPage<JobListItem>>.Failure(NoView);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<ListPage<JobListItem>>.Failure(IRuntimeExecutionContext.NoTenant);

        var list = JobList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var jobs = context.Jobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.State != JobState.Deleted);
            if (list.Search is { Length: > 0 } search)
            {
                jobs = jobs.Where(job => job.Name.Contains(search) || job.Customer.Name.Contains(search));
            }

            if (list.Filter(JobList.Status) is { } status && ListOption.TryParseEnum<JobState>(status, out var state))
            {
                jobs = jobs.Where(job => job.State == state);
            }

            var total = await jobs.CountAsync(cancellationToken);
            var page = ListPage<JobListItem>.PageWithin(list, total);
            var order = new ListOrder<Job>(jobs);
            _ = list.Sort == JobList.SortName
                ? order.By(job => job.Name, list.Descending).By(job => job.PeriodStart)
                : order.By(job => job.PeriodStart, list.Descending).By(job => job.Name);
            var rows = await order.By(job => job.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(job => new
                {
                    job.Id,
                    job.Name,
                    Customer = job.Customer.Name,
                    job.Schedule,
                    job.PeriodStart,
                    job.PeriodEnd,
                    job.StartTime,
                    job.EndTime,
                    Unit = job.OrganizationUnit.Name,
                    Coordinator = job.Coordinator == null ? null : job.Coordinator.FirstName + " " + job.Coordinator.LastName,
                    job.State,
                })
                .ToListAsync(cancellationToken);
            var coverage = await JobCoverageReader.ReadJobsAsync(context, [.. rows.Select(row => row.Id)], cancellationToken);
            List<JobListItem> items =
            [
                .. rows.Select(row =>
                {
                    var summary = coverage.GetValueOrDefault(row.Id) ?? new JobCoverageSummary(0, JobCoverage.Uncovered);
                    return new JobListItem(row.Id, row.Name, row.Customer, row.PeriodStart, row.PeriodEnd, row.Unit, row.Coordinator?.Trim(), row.State, summary.ActiveSlots, summary.Coverage)
                    {
                        Times = new JobTimes(row.Schedule, row.PeriodStart, row.PeriodEnd, row.StartTime, row.EndTime, []),
                    };
                }),
            ];
            return Result<ListPage<JobListItem>>.Success(ListPage<JobListItem>.Ungrouped(items, total, page, list.PageSize));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the jobs of tenant {TenantId} failed.", tenantId);
            return Result<ListPage<JobListItem>>.Failure(ReadFailed);
        }
    }

    public async Task<Result<ListPage<JobSlotRow>>> QueryJobSlotsAsync(Guid jobId, ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<ListPage<JobSlotRow>>.Failure(NoView);

        var list = JobSlotList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var entries = await ReadSlotEntriesAsync(context, jobId, cancellationToken);
            var descriptions = await context.JobSlots.AsNoTracking()
                .Where(slot => slot.JobId == jobId && slot.State != JobSlotState.Deleted)
                .Select(slot => new { slot.Id, slot.Description })
                .ToDictionaryAsync(slot => slot.Id, slot => slot.Description, cancellationToken);
            var qualifications = (await context.JobSlotQualifications.AsNoTracking()
                    .Where(required => required.JobSlot.JobId == jobId)
                    .Select(required => new { required.JobSlotId, required.Qualification.Name })
                    .ToListAsync(cancellationToken))
                .ToLookup(required => required.JobSlotId, required => required.Name);
            var rows = entries.Select(entry =>
            {
                var first = entry.Members[0];
                var assigned = entry.Assigned.ToList();
                var held = SlotGroups.Held(entry.Members.Count, assigned.Count(assignment => assignment.InService), entry.Live.Count(assignment => assignment.State == SlotAssignmentState.Accepted && assignment.InService));
                return new JobSlotRow(
                    first.SlotId,
                    jobId,
                    first.Name,
                    descriptions.GetValueOrDefault(first.SlotId),
                    [.. qualifications[first.SlotId].Order(StringComparer.CurrentCulture)],
                    first.State,
                    first.State == JobSlotState.Active ? JobCoverageRules.OfGroup(entry.Members.Count, held) : JobCoverage.Uncovered,
                    assigned.FirstOrDefault()?.Name,
                    entry.Live.Count(assignment => SlotAssignmentWorkflowAttribute.IsAsked(assignment.State)),
                    entry.Live.Count(assignment => assignment.State == SlotAssignmentState.Accepted))
                {
                    HolderOutOfService = assigned.Any(assignment => !assignment.InService),
                    SlotCount = entry.Members.Count,
                    Held = held,
                    Holders = [.. assigned.Select(assignment => assignment.Name)],
                };
            });
            if (list.Search is { Length: > 0 } search)
            {
                rows = rows.Where(row => row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || (row.Description?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false));
            }

            if (list.Filter(JobSlotList.Status) is { } status && ListOption.TryParseEnum<JobSlotState>(status, out var state))
            {
                rows = rows.Where(row => row.State == state);
            }

            var all = (list.Descending ? rows.OrderByDescending(row => row.Name, StringComparer.CurrentCultureIgnoreCase) : rows.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)).ThenBy(row => row.Id).ToList();
            var page = ListPage<JobSlotRow>.PageWithin(list, all.Count);
            return Result<ListPage<JobSlotRow>>.Success(ListPage<JobSlotRow>.Ungrouped([.. all.Skip((page - 1) * list.PageSize).Take(list.PageSize)], all.Count, page, list.PageSize));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the slots of job {JobId} failed.", jobId);
            return Result<ListPage<JobSlotRow>>.Failure(ReadFailed);
        }
    }

    private static async Task<IReadOnlyList<SlotEntry>> ReadSlotEntriesAsync(ApplicationDbContext context, Guid jobId, CancellationToken cancellationToken)
    {
        var slots = await SlotGroupReader.ReadAsync(context, [jobId], cancellationToken);
        var inService = JobCoverageReader.StaffMembersInService(context);
        var live = (await context.SlotAssignments.AsNoTracking()
                .Where(assignment => assignment.Slot.JobId == jobId
                    && (assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted || assignment.State == SlotAssignmentState.Assigned))
                .OrderBy(assignment => assignment.Created)
                .ThenBy(assignment => assignment.Id)
                .Select(assignment => new { assignment.JobSlotId, assignment.State, Name = (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Trim(), InService = inService.Contains(assignment.ResourceId) })
                .ToListAsync(cancellationToken))
            .Select(assignment => new LiveAssignment(assignment.JobSlotId, assignment.State, assignment.Name, assignment.InService))
            .ToLookup(assignment => assignment.JobSlotId);
        return [.. SlotGroupReader.Entries(slots).Select(members => new SlotEntry(members, [.. members.SelectMany(member => live[member.SlotId])]))];
    }

    private static async Task<IReadOnlyList<JobStaffEntry>> ReadJobStaffAsync(ApplicationDbContext context, Guid tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var entries = (await ReadSlotEntriesAsync(context, jobId, cancellationToken)).Where(entry => entry.Members[0].State == JobSlotState.Active).ToList();
        var openAgain = await OpenAgainReader.ReadAsync(context, tenantId, [.. entries.Select(entry => (IReadOnlyCollection<Guid>)[.. entry.Members.Select(member => member.SlotId)])], cancellationToken);
        return [.. entries.Select(entry =>
        {
            var assigned = entry.Assigned.Select(assignment => assignment.InService ? assignment.Name : $"{assignment.Name} (out of service)").ToList();
            var open = Math.Max(0, entry.Members.Count - assigned.Count);
            return new JobStaffEntry(
                entry.Members[0].SlotId,
                entry.Members[0].Name,
                assigned,
                open,
                [.. entry.Live.Where(assignment => SlotAssignmentWorkflowAttribute.IsAsked(assignment.State)).Select(assignment => assignment.Name)],
                [.. entry.Live.Where(assignment => assignment.State == SlotAssignmentState.Accepted).Select(assignment => assignment.Name)])
            {
                OpenAgain = openAgain.CountFor([.. entry.Members.Select(member => member.SlotId)], open),
            };
        })];
    }

    private sealed record LiveAssignment(Guid JobSlotId, SlotAssignmentState State, string Name, bool InService);

    private sealed record SlotEntry(IReadOnlyList<SlotGroupInput> Members, IReadOnlyList<LiveAssignment> Live)
    {
        public IEnumerable<LiveAssignment> Assigned => Live.Where(assignment => assignment.State == SlotAssignmentState.Assigned);
    }

    public async Task<Result<DetachedEntityUpdater<Job>>> LoadJobUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<DetachedEntityUpdater<Job>>.Failure(NoView);

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var job = await context.Jobs
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return job is null
                ? Result<DetachedEntityUpdater<Job>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<Job>>.Success(new DetachedEntityUpdater<Job>(job, context, UpdaterOptions));
        }, this.log, $"the job {id}", ReadFailed, cancellationToken);
    }

    public async Task<Result<Job>> NewJobAsync(CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.JobsCreate, cancellationToken))
            return Result<Job>.Failure(NoCreate);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<Job>.Failure(ChangeInOwnTenantOnly);

        if (session.TenantId is not { } tenantId)
            return Result<Job>.Failure(IRuntimeExecutionContext.NoTenant);

        var today = await this.clock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<Job>.Failure(today.Error);

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var root = await context.OrganizationUnits.AsNoTracking().FirstOrDefaultAsync(unit => unit.UnitType == OrganizationUnitType.Company, cancellationToken);
        var job = new Job { TenantId = tenantId, State = JobState.Create, Schedule = JobSchedule.Single, PeriodStart = today.Value, PeriodEnd = today.Value };
        if (root is not null)
        {
            job.OrganizationUnitId = root.Id;
            job.OrganizationUnit = root;
        }

        return Result<Job>.Success(job);
    }

    public async Task<Result<Guid>> CreateJobAsync(Job job, string transitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.JobsCreate, cancellationToken))
            return Result<Guid>.Failure(NoCreate);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<Guid>.Failure(ChangeInOwnTenantOnly);

        if (session.TenantId is not { } tenantId)
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

        if (transitionKey != JobWorkflowAttribute.RegisterStep)
            return Result<Guid>.Failure(CreateByRegister);

        var row = new Job
        {
            TenantId = tenantId,
            State = JobState.Create,
            Name = job.Name?.Trim() ?? string.Empty,
            Description = Optional(job.Description),
            CustomerId = job.CustomerId,
            ContactId = job.ContactId,
            OrganizationUnitId = job.OrganizationUnitId,
            Schedule = job.Schedule,
            PeriodStart = job.PeriodStart,
            PeriodEnd = job.PeriodEnd,
            StartTime = job.StartTime,
            EndTime = job.EndTime,
            PlaceName = Optional(job.PlaceName),
            StreetAddress = Optional(job.StreetAddress),
            PostalCode = Optional(job.PostalCode),
            City = Optional(job.City),
            AccessNotes = Optional(job.AccessNotes),
        };
        foreach (var timeSlot in job.TimeSlots)
        {
            row.TimeSlots.Add(new JobTimeSlot { TenantId = tenantId, Weekday = timeSlot.Weekday, Start = timeSlot.Start, End = timeSlot.End });
        }

        JobRules.Normalize(row);

        if ((DescribeInvalidText(row) ?? DescribeInvalidPlace(row) ?? DescribeInvalidSchedule(row)) is { } reason)
            return Result<Guid>.Failure(reason);

        try
        {
            var values = CustomFieldValues.Normalized(job.Attributes).Where(value => !AttributeValues.IsEmpty(value)).ToList();
            await using (var check = this.dbFactory.CreateDbContext(this.runtime))
            {
                if (await CustomFieldValues.DescribeInvalidAsync<JobAttributeDefinition, JobAttribute>(check, tenantId, values, cancellationToken: cancellationToken) is { } invalid)
                    return Result<Guid>.Failure(invalid);
            }

            foreach (var value in values)
            {
                var copy = new JobAttribute { EntityAttributeDefinitionId = value.EntityAttributeDefinitionId };
                AttributeValues.CopyValues(value, copy);
                row.Attributes.Add(copy);
            }

            return await this.workflows.CreateAndTransitionAsync<JobState, Job>(row, transitionKey, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Registering a job in tenant {TenantId} failed.", tenantId);
            return Result<Guid>.Failure("An error occurred while registering the job.");
        }
    }

    public async Task<Result> UpdateJobAndTransitionAsync(DetachedEntityUpdater<Job> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (!await this.permissionService.HasAnyPermissionAsync(Steppers, cancellationToken))
            return Result.Failure(NoChange);

        if (this.runtime.Current.HasPlatformReach())
            return Result.Failure(ChangeInOwnTenantOnly);

        var id = (Guid)updater.OriginalValue(nameof(Job.Id))!;
        try
        {
            var workflow = WorkflowRegistry.Default.Get<JobState, Job>();
            if (workflow.IsDeleteStep(transitionKey))
            {
                return await this.workflows.TransitionEntityAsync<JobState, Job>(id, transitionKey, JobWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
            }

            if (!(workflow.TryGetTransition(transitionKey, out var step) && step.TakesNoEdits))
            {
                var job = updater.Entity;
                Tidy(job, updater);
                JobRules.Normalize(job);
                if (updater.HasChanges() && (DescribeInvalidText(job) ?? DescribeInvalidPlace(job) ?? DescribeInvalidSchedule(job) ?? await DescribeInvalidCustomFieldsAsync(job, id, cancellationToken)) is { } reason)
                    return Result.Failure(reason);
            }

            return await this.workflows.SaveAndTransitionAsync<JobState, Job>(updater, transitionKey, JobWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Saving the job {JobId} failed.", id);
            return Result.Failure("An error occurred while saving the job.");
        }
    }

    public async Task<Result<IReadOnlyList<IEntityAttributeDefinition>>> GetJobCustomFieldsAsync(Guid? jobId, CancellationToken cancellationToken)
    {
        if (jobId is null ? !await this.permissionService.HasPermissionAsync(RolePermission.JobsCreate, cancellationToken) : !await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<IReadOnlyList<IEntityAttributeDefinition>>.Failure(jobId is null ? NoCreate : NoView);

        Guid? tenantId = this.runtime.TenantId;
        if (jobId is { } id)
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            tenantId = await context.Jobs.AsNoTracking().Where(job => job.Id == id).Select(job => (Guid?)job.TenantId).FirstOrDefaultAsync(cancellationToken);
            if (tenantId is null)
                return Result<IReadOnlyList<IEntityAttributeDefinition>>.Failure(NotFound);
        }

        var fields = await this.attributes.GetDefinitionsAsync<JobAttributeDefinition>(tenantId);
        return fields.IsSuccess && fields.Value is { } read
            ? Result<IReadOnlyList<IEntityAttributeDefinition>>.Success([.. read])
            : Result<IReadOnlyList<IEntityAttributeDefinition>>.Failure(fields.Error);
    }

    public async Task<Result<StateHistoryPage>> GetJobHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken)
            ? await this.history.GetHistoryAsync<Job>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(NoView);

    public static readonly CommentThreadAccess InternalThread = new(CommentAudience.Internal, RolePermission.ViewCoordination, RolePermission.PerformCoordination);

    public static readonly CommentThreadAccess CustomerThread = new(CommentAudience.Customer, RolePermission.ViewCoordination, RolePermission.PerformCoordination);

    public const string CustomerThreadClosed = "The job is completed or canceled, so its customer thread takes no new messages.";

    public async Task<Result<IReadOnlyList<CommentThread>>> GetJobCommentsAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<IReadOnlyList<CommentThread>>.Failure(NoView);

        CustomerThreadFacts? facts;
        try
        {
            facts = await CustomerThreadFactsAsync(id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the threads of the job {JobId} failed.", id);
            return Result<IReadOnlyList<CommentThread>>.Failure(ReadFailed);
        }

        var threads = await this.comments.GetThreadsAsync<Job>(id, facts is null ? [InternalThread] : [InternalThread, CustomerAccess(facts)], cancellationToken);
        return threads.IsSuccess && threads.Value is { } read
            ? Result<IReadOnlyList<CommentThread>>.Success([.. read.Select(thread => thread.Audience == CommentAudience.Customer ? thread.JudgedBySide() : thread)])
            : threads;
    }

    public async Task<Result<CommentThread>> AddJobCommentAsync(Guid id, CommentAudience audience, string text, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<CommentThread>.Failure(NoView);

        if (audience is not (CommentAudience.Internal or CommentAudience.Customer))
            return Result<CommentThread>.Failure(CommentService.NoAccess);

        var access = audience == CommentAudience.Internal ? InternalThread : null;

        if (access is null)
        {
            CustomerThreadFacts? facts;
            try
            {
                facts = await CustomerThreadFactsAsync(id, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                this.log.LogError(exception, "Reading the customer thread of the job {JobId} failed.", id);
                return Result<CommentThread>.Failure(ReadFailed);
            }

            if (facts is null)
                return Result<CommentThread>.Failure(CommentService.NoAccess);

            if (!string.IsNullOrWhiteSpace(text) && JobThreadSync.WriteToPortal(new ToPortalComment(Guid.NewGuid(), text.Trim())) is null)
                return Result<CommentThread>.Failure(JobThreadSync.TooLongToSend);

            access = CustomerPostAccess(facts);
        }

        var posted = await this.comments.AddCommentAsync<Job>(id, access, text, cancellationToken);
        return posted.IsSuccess && posted.Value is { Audience: CommentAudience.Customer } thread
            ? Result<CommentThread>.Success(thread.JudgedBySide(), posted.Message)
            : posted;
    }

    private static CommentThreadAccess CustomerPostAccess(CustomerThreadFacts facts) =>
        CustomerAccess(facts) with
        {
            Effects = new CommentPostEffects(null, (context, comment, _) =>
            {
                var payload = JobThreadSync.WriteToPortal(new ToPortalComment(comment.CrossTenantId!.Value, comment.Text))
                    ?? throw new InvalidOperationException("A customer thread's message no longer fits its payload.");
                IntegrationOutbox.Enqueue(context, JobThreadSync.ToPortalKind, facts.PortalTenantId, facts.Correlation, payload);
                return Task.FromResult(CommentPostOutcome.None);
            })
            {
                Crosses = true,
            },
        };

    private static CommentThreadAccess CustomerAccess(CustomerThreadFacts facts) =>
        CustomerThread with { ClosedReason = JobThreadSync.OpenOnJob(facts.State) ? null : CustomerThreadClosed };

    private async Task<CustomerThreadFacts?> CustomerThreadFactsAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var read = await context.Jobs.AsNoTracking()
            .Where(job => job.Id == id)
            .Select(job => new { job.State, job.CrossTenantId, Portal = job.Customer.LinkedTenantId })
            .FirstOrDefaultAsync(cancellationToken);
        return read is { CrossTenantId: { } correlation, Portal: { } portal } ? new CustomerThreadFacts(read.State, correlation, portal) : null;
    }

    private sealed record CustomerThreadFacts(JobState State, Guid Correlation, Guid PortalTenantId);

    public async Task<Result<JobEditorOptions>> GetJobEditorOptionsAsync(Guid? id, CancellationToken cancellationToken)
    {
        var console = this.runtime.Current.HasPlatformReach();
        var creates = await this.permissionService.HasPermissionAsync(RolePermission.JobsCreate, cancellationToken) && !console;
        var edits = await this.permissionService.HasPermissionAsync(RolePermission.JobsEdit, cancellationToken) && !console;
        if (id is null ? !creates : !edits && !await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<JobEditorOptions>.Failure(id is null ? NoCreate : NoView);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var stored = id is { } jobId
                ? await context.Jobs.AsNoTracking().Where(job => job.Id == jobId).Select(job => new { job.TenantId, job.CustomerId, job.ContactId, job.OrganizationUnitId, job.State }).FirstOrDefaultAsync(cancellationToken)
                : null;
            if (id is not null && stored is null)
                return Result<JobEditorOptions>.Failure(NotFound);

            Guid? storedCustomer = stored?.CustomerId;
            Guid? storedContact = stored?.ContactId;
            Guid? storedUnit = stored?.OrganizationUnitId;
            var customers = await context.Customers.AsNoTracking()
                .Where(customer => customer.State == CustomerState.Active || customer.Id == storedCustomer)
                .OrderBy(customer => customer.Name)
                .ToListAsync(cancellationToken);
            var customerIds = customers.Select(customer => customer.Id).ToList();
            var contacts = await context.CustomerContacts.AsNoTracking()
                .Where(contact => (contact.State == CustomerContactState.Active && customerIds.Contains(contact.CustomerId)) || contact.Id == storedContact)
                .OrderBy(contact => contact.LastName).ThenBy(contact => contact.FirstName)
                .ToListAsync(cancellationToken);
            var units = await context.OrganizationUnits.AsNoTracking()
                .Where(unit => unit.State == OrganizationUnitState.Active || unit.UnitType == OrganizationUnitType.Company || unit.Id == storedUnit)
                .OrderBy(unit => unit.UnitType == OrganizationUnitType.Company ? 0 : 1).ThenBy(unit => unit.Name)
                .ToListAsync(cancellationToken);
            var staff = id is { } job && stored is { } found ? await ReadJobStaffAsync(context, found.TenantId, job, cancellationToken) : null;
            return Result<JobEditorOptions>.Success(new JobEditorOptions(
                FieldsLocked: id is null ? !creates : !edits,
                MayAddSlots: edits,
                customers,
                contacts,
                units)
            {
                Services = edits && id is not null ? await this.Slots.ServiceChoicesAsync(cancellationToken) : [],
                Staff = staff,
                StaffingOpen = stored is { } read && JobRules.StaffingOpen(read.State),
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the editor options of job {JobId} failed.", id);
            return Result<JobEditorOptions>.Failure(ReadFailed);
        }
    }

    private static void Tidy(Job job, DetachedEntityUpdater<Job> updater)
    {
        if (!string.Equals(job.Name, updater.OriginalValue(nameof(Job.Name)) as string, StringComparison.Ordinal))
        {
            job.Name = job.Name?.Trim() ?? string.Empty;
        }

        if (!string.Equals(job.Description, updater.OriginalValue(nameof(Job.Description)) as string, StringComparison.Ordinal))
        {
            job.Description = Optional(job.Description);
        }

        if (!string.Equals(job.PlaceName, updater.OriginalValue(nameof(Job.PlaceName)) as string, StringComparison.Ordinal))
        {
            job.PlaceName = Optional(job.PlaceName);
        }

        if (!string.Equals(job.StreetAddress, updater.OriginalValue(nameof(Job.StreetAddress)) as string, StringComparison.Ordinal))
        {
            job.StreetAddress = Optional(job.StreetAddress);
        }

        if (!string.Equals(job.PostalCode, updater.OriginalValue(nameof(Job.PostalCode)) as string, StringComparison.Ordinal))
        {
            job.PostalCode = Optional(job.PostalCode);
        }

        if (!string.Equals(job.City, updater.OriginalValue(nameof(Job.City)) as string, StringComparison.Ordinal))
        {
            job.City = Optional(job.City);
        }

        if (!string.Equals(job.AccessNotes, updater.OriginalValue(nameof(Job.AccessNotes)) as string, StringComparison.Ordinal))
        {
            job.AccessNotes = Optional(job.AccessNotes);
        }
    }

    private async Task<string?> DescribeInvalidCustomFieldsAsync(Job job, Guid id, CancellationToken cancellationToken)
    {
        foreach (var emptied in CustomFieldValues.Normalized(job.Attributes).Where(value => AttributeValues.IsEmpty(value)).ToList())
        {
            job.Attributes.Remove(emptied);
        }

        var edited = CustomFieldValues.Normalized(job.Attributes);
        if (edited.Count == 0 || this.runtime.TenantId is not { } tenantId)
            return null;

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var stored = await context.JobAttributes.AsNoTracking().Where(value => value.EntityId == id).ToListAsync(cancellationToken);
        return await CustomFieldValues.DescribeInvalidAsync<JobAttributeDefinition, JobAttribute>(context, tenantId, edited, stored, cancellationToken);
    }

    private static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? DescribeInvalidText(Job job) =>
        string.IsNullOrWhiteSpace(job.Name) ? NameMissing
        : job.Name.Length > Job.NameMaxLength || job.Description?.Length > Job.DescriptionMaxLength ? TextTooLong
        : null;

    private static string? DescribeInvalidPlace(Job job) =>
        job.PlaceName?.Length > Job.PlaceNameMaxLength
        || job.StreetAddress?.Length > Job.StreetAddressMaxLength
        || job.PostalCode?.Length > Job.PostalCodeMaxLength
        || job.City?.Length > Job.CityMaxLength
        || job.AccessNotes?.Length > Job.AccessNotesMaxLength
            ? PlaceTooLong
            : null;

    private static string? DescribeInvalidSchedule(Job job) =>
        JobRules.Problems(job) is { Count: > 0 } problems ? string.Join(" ", problems) : null;
}

public sealed record JobEditorOptions(
    bool FieldsLocked,
    bool MayAddSlots,
    IReadOnlyList<Customer> Customers,
    IReadOnlyList<CustomerContact> Contacts,
    IReadOnlyList<OrganizationUnit> Units)
{
    public IReadOnlyList<ServiceChoice> Services { get; init; } = [];

    public IReadOnlyList<JobStaffEntry>? Staff { get; init; }

    public bool StaffingOpen { get; init; }
}
