using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.Tenant)]
public sealed class TenantDialogContext
    : StateEntityDialogContextBase<TenantState, Tenant>
{
    private readonly TenantService service;

    public TenantDialogContext(TenantService service, Guid? tenantId)
        : base(tenantId, TenantWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected override Task<Result<DetachedEntityUpdater<Tenant>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadTenantUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<Tenant> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateTenantAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetTenantHistoryAsync(entityId, query, cancellationToken);

    protected override bool IsLockedRecord(Tenant entity) => entity.IsRootTenant;
}
