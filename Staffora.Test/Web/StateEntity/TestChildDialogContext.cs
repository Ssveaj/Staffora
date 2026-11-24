using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Web.StateEntity;

[DialogRoute(TestChildDialogContext.Token)]
public sealed class TestChildDialogContext : StateEntityDialogContextBase<TestChildState, TestChild>
{
    public const string Token = "testchild";

    private readonly TestChildDialogService service;

    public TestChildDialogContext(TestChildDialogService service, Guid? childId) : base(childId, TestChildWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    public IReadOnlyList<TestParent> Parents { get; private set; } = [];

    protected override Task<Result<DetachedEntityUpdater<TestChild>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadTestChildUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<TestChild> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateTestChildAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetTestChildHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var parents = await this.service.GetTestChildEditorOptionsAsync(cancellationToken);
        if (!parents.IsSuccess || parents.Value is null)
        {
            return Result.Failure(parents.Error);
        }

        Parents = parents.Value;
        return Result.Success();
    }

    protected internal override bool OpensLocked => false;
}
