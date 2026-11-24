using Radzen;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Resources;
using Staffora.Services.Services;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.Qualification)]
public sealed class QualificationDialogContext : StateEntityDialogContextBase<QualificationState, Qualification>
{
    public const string InfoView = "info";

    public const string StaffView = "staff";

    private bool fieldsLocked = true;
    private UserService? users;
    private readonly QualificationService service;

    public QualificationDialogContext(QualificationService service, Guid? qualificationId) : base(qualificationId, QualificationWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogView(InfoView, "Info") { Icon = "workspace_premium" },
        new StateEntityDialogListView<QualificationHolderRow>(StaffView, "Staff", QualificationHolderList.Definition, (query, cancellationToken) => this.service.QueryQualificationHoldersAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 1,
            Icon = "groups",
            Permission = RolePermission.ResourcesView,
        },
    ];

    public async Task<StateEntityDialogResult> OpenHolderAsync(DialogService dialogs, QualificationHolderRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (this.users is not { } users)
        {
            return new StateEntityDialogResult(false, false, null);
        }

        await UserDialog.OpenAsync(dialogs, users, row.UserId);
        return new StateEntityDialogResult(true, false, null);
    }

    protected override Task<Result<DetachedEntityUpdater<Qualification>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadQualificationUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<Qualification> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateQualificationAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetQualificationHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetQualificationEditorOptionsAsync(EntityId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        this.users ??= Services?.GetService(typeof(UserService)) as UserService;
        this.fieldsLocked = options.Value.FieldsLocked;
        return Result.Success();
    }

    protected override Task<Result<Qualification>> NewEntityAsync(CancellationToken cancellationToken) =>
        this.service.NewQualificationAsync(cancellationToken);

    protected override Task<Result<Guid>> CreateEntityAsync(Qualification entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateQualificationAsync(entity, transitionKey, cancellationToken);

    protected override bool IsLockedRecord(Qualification entity) => this.fieldsLocked;
}
