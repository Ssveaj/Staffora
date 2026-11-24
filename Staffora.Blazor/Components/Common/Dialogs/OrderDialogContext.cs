using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Comments;
using Staffora.Services.Portal;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.Order)]
public sealed class OrderDialogContext : StateEntityDialogContextBase<OrderState, Order>
{
    private bool fieldsLocked = true;
    private readonly OrderService service;

    public OrderDialogContext(OrderService service, Guid? requestId) : base(requestId, OrderWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    public string? Supplier { get; private set; }

    public string? Orderer { get; private set; }

    public IReadOnlyList<OfferedServiceChoice> OfferedServices { get; private set; } = [];

    protected override Task<Result<IReadOnlyList<CommentThread>>> LoadCommentThreadsAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.GetOrderCommentsAsync(entityId, cancellationToken);

    protected override Task<Result<CommentThread>> PostCommentAsync(Guid entityId, CommentAudience audience, string text, CancellationToken cancellationToken) =>
        this.service.AddOrderCommentAsync(entityId, audience, text, cancellationToken);

    protected override Task<Result<DetachedEntityUpdater<Order>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadOrderUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<Order> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateOrderAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetOrderHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetOrderEditorOptionsAsync(EntityId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        this.fieldsLocked = options.Value.FieldsLocked;
        Supplier = options.Value.Supplier;
        Orderer = options.Value.Orderer;
        OfferedServices = options.Value.Services;
        return Result.Success();
    }

    protected override bool ReadsEditorOptionsWithEveryLoad => true;

    protected override Task<Result<Order>> NewEntityAsync(CancellationToken cancellationToken) =>
        this.service.NewOrderAsync(cancellationToken);

    protected override Task<Result<Guid>> CreateEntityAsync(Order entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateOrderAsync(entity, transitionKey, cancellationToken);

    protected override bool IsLockedRecord(Order entity) => this.fieldsLocked;
}
