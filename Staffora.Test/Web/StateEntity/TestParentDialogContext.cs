using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Comments;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Web.StateEntity;

[DialogRoute(TestParentDialogContext.Token)]
public sealed class TestParentDialogContext : StateEntityDialogContextBase<TestParentState, TestParent>
{
    public const string Token = "testparent";

    public const string InfoView = "info";

    public const string ChildrenView = "children";

    private readonly TestParentDialogService service;

    public TestParentDialogContext(TestParentDialogService service, Guid? parentId) : base(parentId, TestParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogView(InfoView, "Info") { Icon = "info" },
        new StateEntityDialogListView<TestChildRow>(ChildrenView, "Children", TestChildList.Definition, (query, cancellationToken) => this.service.QueryTestParentChildrenAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 1,
            Icon = "account_tree",
            Permission = TestParentDialogService.ChildrenPermission,
            Area = TestParentWorkflowAttribute.AreaRows,
        },
    ];

    protected override Task<Result<DetachedEntityUpdater<TestParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadTestParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<TestParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateTestParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetTestParentHistoryAsync(entityId, query, cancellationToken);

    protected override Task<Result<IReadOnlyList<CommentThread>>> LoadCommentThreadsAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.GetTestParentCommentsAsync(entityId, cancellationToken);

    protected override Task<Result<CommentThread>> PostCommentAsync(Guid entityId, CommentAudience audience, string text, CancellationToken cancellationToken) =>
        this.service.AddTestParentCommentAsync(entityId, audience, text, cancellationToken);

    protected override Task<Result<IReadOnlyList<IEntityAttributeDefinition>>> LoadCustomFieldsAsync(CancellationToken cancellationToken) =>
        this.service.GetTestParentCustomFieldsAsync(cancellationToken);

    protected override Task<Result<TestParent>> NewEntityAsync(CancellationToken cancellationToken) =>
        this.service.NewTestParentAsync(cancellationToken);

    protected override async Task<Result<Guid>> CreateEntityAsync(TestParent entity, string transitionKey, CancellationToken cancellationToken)
    {
        var created = await this.service.CreateTestParentAsync(entity, transitionKey, cancellationToken);

        if (created.IsSuccess && !string.IsNullOrWhiteSpace(created.Message))
        {
            ShowOnce(created.Message);
        }

        return created;
    }

    protected internal override bool OpensLocked => false;
}
