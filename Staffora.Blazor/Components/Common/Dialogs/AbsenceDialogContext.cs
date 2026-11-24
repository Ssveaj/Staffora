using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Comments;
using Staffora.Services.Scheduling;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.Absence)]
public sealed class AbsenceDialogContext : StateEntityDialogContextBase<ResourceAbsenceState, ResourceAbsence>
{
    public const string InfoView = "info";

    public const string ImpactView = "impact";

    private bool fieldsLocked = true;
    private readonly ResourceAbsenceService service;

    public AbsenceDialogContext(ResourceAbsenceService service, Guid? absenceId) : base(absenceId, ResourceAbsenceWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    public Guid? UserId { get; init; }

    public string Staff { get; private set; } = string.Empty;

    public string? Note { get; private set; }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogView(InfoView, "Info") { Icon = "info" },
        new StateEntityDialogView(ImpactView, "Affected jobs")
        {
            Order = 1,
            Icon = "event_busy",
            Permission = RolePermission.ViewCoordination,
            StoredOnly = true,
        },
    ];

    protected override Task<Result<DetachedEntityUpdater<ResourceAbsence>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadResourceAbsenceUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<ResourceAbsence> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateResourceAbsenceAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetResourceAbsenceHistoryAsync(entityId, query, cancellationToken);

    protected override Task<Result<IReadOnlyList<CommentThread>>> LoadCommentThreadsAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.GetResourceAbsenceCommentsAsync(entityId, cancellationToken);

    protected override Task<Result<CommentThread>> PostCommentAsync(Guid entityId, CommentAudience audience, string text, CancellationToken cancellationToken) =>
        this.service.AddResourceAbsenceCommentAsync(entityId, audience, text, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetResourceAbsenceEditorOptionsAsync(EntityId, UserId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        (this.fieldsLocked, Staff, Note) = (options.Value.FieldsLocked, options.Value.Staff, options.Value.Note);
        return Result.Success();
    }

    protected override bool ReadsEditorOptionsWithEveryLoad => true;

    protected override Task<Result<ResourceAbsence>> NewEntityAsync(CancellationToken cancellationToken) =>
        UserId is { } user
            ? this.service.NewResourceAbsenceAsync(user, cancellationToken)
            : Task.FromResult(Result<ResourceAbsence>.Failure(ResourceAbsenceService.StaffNotFound));

    protected override Task<Result<Guid>> CreateEntityAsync(ResourceAbsence entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateResourceAbsenceAsync(entity, transitionKey, cancellationToken);

    protected override bool IsLockedRecord(ResourceAbsence entity) => this.fieldsLocked;
}
