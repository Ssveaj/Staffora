using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Radzen;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.JobSlot)]
public sealed class JobSlotDialogContext : StateEntityDialogContextBase<JobSlotState, JobSlot>
{
    public const string InfoView = "info";

    public const string StaffMembersView = "staff";

    public const string StaffingView = "staffing";

    public const string RoundsView = "rounds";

    private readonly JobSlotService service;

    private bool fieldsLocked = true;

    public JobSlotDialogContext(JobSlotService service, Guid? slotId)
        : base(slotId, JobSlotWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    public Guid? JobId { get; init; }

    public Guid? ServiceId { get; init; }
    public Guid? CopyOf { get; init; }

    public bool MayAddSlots { get; private set; }

    public IReadOnlyList<SlotQualificationChoice> Qualifications { get; private set; } = [];

    public SlotStaffingSummary? Staffing { get; private set; }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogView(InfoView, "Info") { Icon = "info" },
        new StateEntityDialogView(StaffingView, "Staffing")
        {
            Order = 1,
            Icon = "person_search",
            Permission = RolePermission.ViewCoordination,
            When = () => Staffing is not null,
        },
        new StateEntityDialogListView<RequestRoundRow>(RoundsView, "Request rounds", RequestRoundList.Definition, (query, cancellationToken) => this.service.Rounds.QueryGroupRoundsAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 3,
            Icon = "campaign",
            Permission = RolePermission.ViewCoordination,
            When = () => Staffing is not null,
        },
        new StateEntityDialogListView<SlotAssignmentRow>(StaffMembersView, "Staff", SlotAssignmentList.Definition, (query, cancellationToken) => this.service.Assignments.QuerySlotAssignmentsAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 2,
            Icon = "badge",

            AddLabel = "Copy slot",
            Add = CopySlotAsync,
            CanAdd = () => MayAddSlots,
        },
    ];

    private async Task<bool> CopySlotAsync(DialogService dialogs, CancellationToken cancellationToken)
    {
        if (EntityId is not { } id || Entity is not { } slot)
        {
            return false;
        }

        var result = await JobSlotDialog.OpenCopyAsync(dialogs, this.service, slot.JobId, id);
        return result.Changed;
    }

    protected override Task<Result<DetachedEntityUpdater<JobSlot>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadJobSlotUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<JobSlot> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateJobSlotAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result> DeleteAsync(DetachedEntityUpdater<JobSlot> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateJobSlotAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetJobSlotHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetJobSlotEditorOptionsAsync(EntityId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        (this.fieldsLocked, Qualifications, Staffing, MayAddSlots) = (options.Value.FieldsLocked, options.Value.Qualifications, options.Value.Staffing, options.Value.MayAddSlots);
        return Result.Success();
    }

    protected override bool ReadsEditorOptionsWithEveryLoad => true;

    protected override Task<Result<JobSlot>> NewEntityAsync(CancellationToken cancellationToken) =>
        JobId is not { } job
            ? Task.FromResult(Result<JobSlot>.Failure(JobService.NotFound))
            : ServiceId is { } made
                ? this.service.NewJobSlotFromServiceAsync(job, made, cancellationToken)
                : this.service.NewJobSlotAsync(job, CopyOf, cancellationToken);

    protected override Task<Result<Guid>> CreateEntityAsync(JobSlot entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateJobSlotAsync(entity, transitionKey, cancellationToken);

    protected override bool IsLockedRecord(JobSlot entity) => this.fieldsLocked;
}
