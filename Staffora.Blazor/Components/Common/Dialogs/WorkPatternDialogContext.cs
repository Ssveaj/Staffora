using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Scheduling;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.WorkPattern)]
public sealed class WorkPatternDialogContext : StateEntityDialogContextBase<WorkPatternState, WorkPattern>
{
    public const string InfoView = "info";

    public const string StaffView = "staff";

    private bool fieldsLocked = true;
    private bool mayCopy;
    private readonly WorkPatternService service;

    private readonly PatternAssignmentService assignments;

    public WorkPatternDialogContext(WorkPatternService service, Guid? patternId) : base(patternId, WorkPatternWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
        this.assignments = service.Assignments;
    }

    public bool ReadsStaff { get; private set; }

    public bool CanAssignStaff { get; private set; }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogView(InfoView, "Info") { Icon = "calendar_month" },
        new StateEntityDialogListView<PatternStaffRow>(StaffView, "Staff", WorkPatternStaffList.Definition, (query, cancellationToken) => this.service.Assignments.QueryAssignmentsOfPatternAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 1,
            Icon = "groups",
            When = () => ReadsStaff,
            AddLabel = "Assign to staff",
            Add = AssignToStaffAsync,
            CanAdd = () => CanAssignStaff,
        },
    ];

    private Task<bool> AssignToStaffAsync(DialogService dialogs, CancellationToken cancellationToken) =>
        EntityId is { } id ? AssignPatternToStaffDialog.OpenAsync(dialogs, id, DisplayName) : Task.FromResult(false);

    public Task<StateEntityDialogResult> OpenAssignmentAsync(DialogService dialogs, PatternStaffRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return PatternAssignmentDialog.OpenAsync(dialogs, this.assignments, row.Id);
    }

    public Guid? CopyOf { get; init; }

    public DialogService? Dialogs { get; init; }

    protected override Task<Result<DetachedEntityUpdater<WorkPattern>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadWorkPatternUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<WorkPattern> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateWorkPatternAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result> DeleteAsync(DetachedEntityUpdater<WorkPattern> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateWorkPatternAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetWorkPatternHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetWorkPatternEditorOptionsAsync(EntityId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        (this.fieldsLocked, this.mayCopy) = (options.Value.FieldsLocked, options.Value.MayCopy);
        (ReadsStaff, CanAssignStaff) = (options.Value.ReadsStaff, options.Value.MayAssignStaff);

        FooterActions = this.mayCopy && EntityId is not null && Dialogs is not null ? CopyAction : null;
        NotifyChanged();
        return Result.Success();
    }

    protected override bool ReadsEditorOptionsWithEveryLoad => true;

    protected override Task<Result<WorkPattern>> NewEntityAsync(CancellationToken cancellationToken) =>
        this.service.NewWorkPatternAsync(CopyOf, cancellationToken);

    protected override Task<Result<Guid>> CreateEntityAsync(WorkPattern entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateWorkPatternAsync(entity, transitionKey, cancellationToken);

    protected override bool IsLockedRecord(WorkPattern entity) => this.fieldsLocked;

    private void CopyAction(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        builder.OpenComponent<IconTextButton>(0);
        builder.AddComponentParameter(1, nameof(IconTextButton.Text), "Copy to new draft");
        builder.AddComponentParameter(2, nameof(IconTextButton.Icon), "content_copy");
        builder.AddComponentParameter(3, nameof(IconTextButton.ButtonStyle), ButtonStyle.Light);
        builder.AddComponentParameter(4, nameof(IconTextButton.Click), EventCallback.Factory.Create<MouseEventArgs>(this, CopyAsync));
        builder.AddAttribute(5, "data-copy-pattern", true);
        builder.CloseComponent();
    }

    private async Task CopyAsync()
    {
        if (IsBusy || EntityId is not { } id || Dialogs is null)
        {
            return;
        }

        if (HasChanges && !await ReloadAsync())
        {
            return;
        }

        await WorkPatternDialog.OpenCopyAsync(Dialogs, this.service, id);
    }
}
