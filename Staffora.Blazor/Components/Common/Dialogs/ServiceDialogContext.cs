using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Catalogue;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.Service)]
public sealed class ServiceDialogContext : StateEntityDialogContextBase<ServiceState, Service>
{
    private bool fieldsLocked = true;
    private readonly ServiceCatalogueService service;

    public ServiceDialogContext(ServiceCatalogueService service, Guid? serviceId) : base(serviceId, ServiceWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    public IReadOnlyList<SlotQualificationChoice> Qualifications { get; private set; } = [];

    protected override Task<Result<DetachedEntityUpdater<Service>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadServiceUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<Service> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateServiceAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetServiceHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetServiceEditorOptionsAsync(EntityId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        (this.fieldsLocked, Qualifications) = (options.Value.FieldsLocked, options.Value.Qualifications);
        return Result.Success();
    }

    protected override Task<Result<Service>> NewEntityAsync(CancellationToken cancellationToken) =>
        this.service.NewServiceAsync(cancellationToken);

    protected override Task<Result<Guid>> CreateEntityAsync(Service entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateServiceAsync(entity, transitionKey, cancellationToken);

    protected override bool IsLockedRecord(Service entity) => this.fieldsLocked;
}
