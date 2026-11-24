using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.RequestRound)]
public sealed class RequestRoundDialogContext : StateEntityDialogContextBase<RequestRoundState, RequestRound>
{
    public const string InfoView = "info";

    public const string RequestsView = "requests";

    private readonly RequestRoundService service;

    public RequestRoundDialogContext(RequestRoundService service, Guid? roundId)
        : base(roundId, RequestRoundWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    public RequestRoundSummary? Summary { get; private set; }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogView(InfoView, "Info") { Icon = "info" },
        new StateEntityDialogListView<SlotAssignmentRow>(RequestsView, "Requests", SlotAssignmentList.Definition, (query, cancellationToken) => this.service.QueryRoundRequestsAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 1,
            Icon = "group",
            Permission = RolePermission.ViewCoordination,
        },
    ];

    protected override Task<Result<DetachedEntityUpdater<RequestRound>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadRequestRoundUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<RequestRound> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateRequestRoundAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetRequestRoundHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var summary = await this.service.GetRequestRoundEditorOptionsAsync(EntityId, cancellationToken);
        if (!summary.IsSuccess || summary.Value is null)
        {
            return Result.Failure(summary.Error);
        }

        Summary = summary.Value;
        return Result.Success();
    }

    protected override bool ReadsEditorOptionsWithEveryLoad => true;

    protected override bool IsLockedRecord(RequestRound entity) => true;
}
