using Radzen;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Comments;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.Job)]
public sealed class JobDialogContext : StateEntityDialogContextBase<JobState, Job>
{
    public const string InfoView = "info";

    public const string StaffingView = "staffing";

    public const string PlaceView = "place";

    public const string TimetableView = "timetable";

    public const string SlotsView = "slots";

    private readonly JobService service;

    private readonly JobSlotService slots;

    private bool fieldsLocked = true;
    private bool mayAddSlots;
    private bool staffingOpen;
    private IReadOnlyList<CustomerContact> contacts = [];

    public JobDialogContext(JobService service, Guid? jobId)
        : base(jobId, JobWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
        this.slots = this.service.Slots;
    }

    public IReadOnlyList<Customer> Customers { get; private set; } = [];

    public IReadOnlyList<OrganizationUnit> Units { get; private set; } = [];

    public IReadOnlyList<ServiceChoice> SlotServices { get; private set; } = [];

    private Guid? slotService;

    public IReadOnlyList<JobStaffEntry>? Staff { get; private set; }

    private Guid? staffingSlot;

    public Guid? StaffingSlot
    {
        get => Staff is not { Count: > 0 } staff ? null
            : staff.FirstOrDefault(entry => entry.SlotId == this.staffingSlot)?.SlotId ?? staff.FirstOrDefault(entry => entry.Open > 0)?.SlotId ?? staff[0].SlotId;
        set => this.staffingSlot = value;
    }

    public IReadOnlyList<CustomerContact> ContactsOf(Guid customerId) => [.. this.contacts.Where(contact => contact.CustomerId == customerId)];

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogView(InfoView, "Info") { Icon = "info" },
        new StateEntityDialogView(StaffingView, "Staffing") { Order = 1, Icon = "how_to_reg", Permission = RolePermission.ViewCoordination, When = () => this.staffingOpen && Staff is { Count: > 0 }, StoredOnly = true },
        new StateEntityDialogView(PlaceView, "Place") { Order = 1, Icon = "location_on", Area = JobWorkflowAttribute.AreaPlace, StoredOnly = true },
        new StateEntityDialogView(TimetableView, "Timetable") { Order = 1, Icon = "schedule", Area = JobWorkflowAttribute.AreaTimetable, StoredOnly = true },
        new StateEntityDialogListView<JobSlotRow>(SlotsView, "Slots", JobSlotList.Definition, (query, cancellationToken) => this.service.QueryJobSlotsAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 2,
            Icon = "person_search",
            Area = JobWorkflowAttribute.AreaSlots,
            AddLabel = "Add slot",
            Add = AddSlotAsync,
            CanAdd = () => this.mayAddSlots,
            AddChoice = new StateEntityDialogAddChoice(
                "Service",
                "slot-service",
                "No service",
                () => [.. SlotServices.Select(service => new ListOption(service.Id.ToString(), service.Name))],
                chosen => this.slotService = Guid.TryParse(chosen, out var id) ? id : null),
        },
    ];

    private async Task<bool> AddSlotAsync(DialogService dialogs, CancellationToken cancellationToken)
    {
        if (EntityId is not { } id)
        {
            return false;
        }

        var result = await JobSlotDialog.OpenCreateAsync(dialogs, this.slots, id, this.slotService);
        return result.Changed;
    }

    public Task<StateEntityDialogResult> OpenSlotAsync(DialogService dialogs, JobSlotRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return JobSlotDialog.OpenAsync(dialogs, this.slots, row.Id);
    }

    protected override Task<Result<DetachedEntityUpdater<Job>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadJobUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<Job> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateJobAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result> DeleteAsync(DetachedEntityUpdater<Job> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateJobAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetJobHistoryAsync(entityId, query, cancellationToken);

    protected override Task<Result<IReadOnlyList<CommentThread>>> LoadCommentThreadsAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.GetJobCommentsAsync(entityId, cancellationToken);

    protected override Task<Result<CommentThread>> PostCommentAsync(Guid entityId, CommentAudience audience, string text, CancellationToken cancellationToken) =>
        this.service.AddJobCommentAsync(entityId, audience, text, cancellationToken);

    protected override async Task<Result<IReadOnlyList<StaffConversationRow>?>> LoadConversationsAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var read = await this.service.Conversations.GetJobConversationsAsync(entityId, cancellationToken);
        return read.IsSuccess
            ? Result<IReadOnlyList<StaffConversationRow>?>.Success(read.Value)
            : read.Error == CommentService.NoAccess
                ? Result<IReadOnlyList<StaffConversationRow>?>.Success(null)
                : Result<IReadOnlyList<StaffConversationRow>?>.Failure(read.Error);
    }

    protected override Task<Result<IReadOnlyList<CommentThread>>> LoadConversationThreadAsync(Guid conversationId, CancellationToken cancellationToken) =>
        this.service.Conversations.GetSlotAssignmentCommentsAsync(conversationId, cancellationToken);

    protected override Task<Result<CommentThread>> PostConversationCommentAsync(Guid conversationId, string text, CancellationToken cancellationToken) =>
        this.service.Conversations.AddSlotAssignmentCommentAsync(conversationId, CommentAudience.Staff, text, cancellationToken);

    protected internal override string ConversationsLabel => "Staff";

    protected override Task<Result<IReadOnlyList<IEntityAttributeDefinition>>> LoadCustomFieldsAsync(CancellationToken cancellationToken) =>
        this.service.GetJobCustomFieldsAsync(EntityId, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetJobEditorOptionsAsync(EntityId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        (this.fieldsLocked, this.mayAddSlots, Customers, this.contacts, Units, Staff) =
            (options.Value.FieldsLocked, options.Value.MayAddSlots, options.Value.Customers, options.Value.Contacts, options.Value.Units, options.Value.Staff);
        SlotServices = options.Value.Services;
        this.staffingOpen = options.Value.StaffingOpen;
        if (this.slotService is { } chosen && SlotServices.All(service => service.Id != chosen))
        {
            this.slotService = null;
        }
        return Result.Success();
    }

    protected override bool ReadsEditorOptionsWithEveryLoad => true;

    protected override Task<Result<Job>> NewEntityAsync(CancellationToken cancellationToken) =>
        this.service.NewJobAsync(cancellationToken);

    protected override Task<Result<Guid>> CreateEntityAsync(Job entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateJobAsync(entity, transitionKey, cancellationToken);

    protected override bool IsLockedRecord(Job entity) => this.fieldsLocked;
}
