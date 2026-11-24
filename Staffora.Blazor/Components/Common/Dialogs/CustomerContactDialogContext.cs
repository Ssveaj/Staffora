using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Customers;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.CustomerContact)]
public sealed class CustomerContactDialogContext : StateEntityDialogContextBase<CustomerContactState, CustomerContact>
{
    private bool fieldsLocked = true;
    private readonly CustomerContactService service;

    public CustomerContactDialogContext(CustomerContactService service, Guid? contactId) : base(contactId, CustomerContactWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    public Guid? CustomerId { get; init; }

    protected override Task<Result<DetachedEntityUpdater<CustomerContact>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadCustomerContactUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<CustomerContact> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateCustomerContactAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result> DeleteAsync(DetachedEntityUpdater<CustomerContact> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateCustomerContactAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetCustomerContactHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetCustomerContactEditorOptionsAsync(EntityId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        this.fieldsLocked = options.Value.FieldsLocked;
        return Result.Success();
    }

    protected override Task<Result<CustomerContact>> NewEntityAsync(CancellationToken cancellationToken) =>
        CustomerId is { } customer
            ? this.service.NewCustomerContactAsync(customer, cancellationToken)
            : Task.FromResult(Result<CustomerContact>.Failure(CustomerService.NotFound));

    protected override Task<Result<Guid>> CreateEntityAsync(CustomerContact entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateCustomerContactAsync(entity, transitionKey, cancellationToken);

    protected override bool IsLockedRecord(CustomerContact entity) => this.fieldsLocked;
}
