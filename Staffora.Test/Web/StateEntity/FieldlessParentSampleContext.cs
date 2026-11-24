using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Unit.Architecture.DialogScanSamples;

[DialogRoute("samplefieldlessparent")]
public sealed class FieldlessParentSampleContext : StateEntityDialogContextBase<TestParentState, TestParent>
{
    private readonly TestParentDialogService service;

    public FieldlessParentSampleContext(TestParentDialogService service, Guid? parentId) : base(parentId, TestParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected override Task<Result<DetachedEntityUpdater<TestParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadTestParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<TestParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateTestParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetTestParentHistoryAsync(entityId, query, cancellationToken);
}
