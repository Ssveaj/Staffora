using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Comments;
using Staffora.Services.Workflow;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Architecture.DialogScanSamples;

public static class SampleParentWorkflowQueries
{
    public static IQueryable<SampleParent> WithWorkflowGraph(this IQueryable<SampleParent> query) => query;
}

public abstract class SampleStepService
{
    public WorkflowService Workflows { get; set; } = null!;

    public StateHistoryService History { get; set; } = null!;

    public Task<Result> UpdateSampleParentAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        Workflows.SaveAndTransitionAsync<ParentState, SampleParent>(updater, transitionKey, SampleParentWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);

    public Task<Result<StateHistoryPage>> GetSampleParentHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        History.GetHistoryAsync<SampleParent>(id, query, cancellationToken);

    public CommentService Comments { get; set; } = null!;

    public static readonly IReadOnlyList<CommentThreadAccess> CommentAccess = [new(CommentAudience.Internal, RolePermission.TenantView, RolePermission.TenantView)];

    public Task<Result<IReadOnlyList<CommentThread>>> GetSampleParentCommentsAsync(Guid id, CancellationToken cancellationToken) =>
        Comments.GetThreadsAsync<SampleParent>(id, CommentAccess, cancellationToken);

    public Task<Result<CommentThread>> AddSampleParentCommentAsync(Guid id, CommentAudience audience, string text, CancellationToken cancellationToken) =>
        Comments.AddCommentAsync<SampleParent>(id, CommentAccess.Single(access => access.Audience == audience), text, cancellationToken);
}

public sealed class MatchingSampleService : SampleStepService
{
    public Task<Result<Guid>> CreateSampleParentAsync(SampleParent parent, string transitionKey, CancellationToken cancellationToken) =>
        Workflows.CreateAndTransitionAsync<ParentState, SampleParent>(new SampleParent { Name = parent.Name }, transitionKey, SampleParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);

    public Task<Result<DetachedEntityUpdater<SampleParent>>> LoadSampleParentUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        _ = Enumerable.Empty<SampleParent>().AsQueryable().WithWorkflowGraph().FirstOrDefault(parent => parent.Id == id);
        return Task.FromResult(Result<DetachedEntityUpdater<SampleParent>>.Failure("sample"));
    }

    public Task<Result<Staffora.Core.Lists.ListPage<Guid>>> QuerySampleParentChildrenAsync(Guid id, Staffora.Core.Lists.ListQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<Staffora.Core.Lists.ListPage<Guid>>.Failure("sample"));
}

public sealed class MismatchedSampleService : SampleStepService
{
    public Task<Result<DetachedEntityUpdater<SampleParent>>> LoadSampleParentUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        _ = Enumerable.Empty<SampleParent>().AsQueryable().FirstOrDefault(parent => parent.Id == id);
        return Task.FromResult(Result<DetachedEntityUpdater<SampleParent>>.Failure("sample"));
    }
}

public sealed class SideQuerySampleService : SampleStepService
{
    public Task<Result<DetachedEntityUpdater<SampleParent>>> LoadSampleParentUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        var parents = Enumerable.Empty<SampleParent>().AsQueryable();
        _ = parents.WithWorkflowGraph().Any(parent => parent.Id == id);
        _ = parents.FirstOrDefault(parent => parent.Id == id);
        return Task.FromResult(Result<DetachedEntityUpdater<SampleParent>>.Failure("sample"));
    }
}

[DialogRoute("samplematching")]
public sealed class MatchingSampleContext
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly MatchingSampleService service;

    public MatchingSampleContext(MatchingSampleService service, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetSampleParentHistoryAsync(entityId, query, cancellationToken);

    protected override Task<Result<Guid>> CreateEntityAsync(SampleParent entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateSampleParentAsync(entity, transitionKey, cancellationToken);

    protected override Task<Result<IReadOnlyList<CommentThread>>> LoadCommentThreadsAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.GetSampleParentCommentsAsync(entityId, cancellationToken);

    protected override Task<Result<CommentThread>> PostCommentAsync(Guid entityId, CommentAudience audience, string text, CancellationToken cancellationToken) =>
        this.service.AddSampleParentCommentAsync(entityId, audience, text, cancellationToken);
}

[DialogRoute("samplemismatched")]
public sealed class MismatchedSampleContext
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly MismatchedSampleService service;

    public MismatchedSampleContext(MismatchedSampleService service, Guid? id)
        : base(id, query => query)
    {
        this.service = service;
    }

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<StateHistoryPage>.Failure("sample"));
}

[DialogRoute("samplesidequery")]
public sealed class SideQuerySampleContext
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly SideQuerySampleService service;

    public SideQuerySampleContext(SideQuerySampleService service, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<StateHistoryPage>.Failure("sample"));
}

public abstract class InheritedSampleContextBase
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly MatchingSampleService service;

    public InheritedSampleContextBase(MatchingSampleService service, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected MatchingSampleService Service => this.service;

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetSampleParentHistoryAsync(entityId, query, cancellationToken);

    protected override Task<Result<IReadOnlyList<CommentThread>>> LoadCommentThreadsAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.GetSampleParentCommentsAsync(entityId, cancellationToken);

    protected override Task<Result<CommentThread>> PostCommentAsync(Guid entityId, CommentAudience audience, string text, CancellationToken cancellationToken) =>
        this.service.AddSampleParentCommentAsync(entityId, audience, text, cancellationToken);
}

[DialogRoute("sampleinherited")]
public sealed class InheritedSampleContext : InheritedSampleContextBase
{
    public InheritedSampleContext(MatchingSampleService service, Guid? id)
        : base(service, id)
    {
    }
}

[DialogRoute("samplehistorybypass")]
public sealed class HistoryBypassSampleContext
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly MatchingSampleService service;
    private readonly StateHistoryService history;

    public HistoryBypassSampleContext(MatchingSampleService service, StateHistoryService history, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
        this.history = history;
    }

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.history.GetHistoryAsync<SampleParent>(entityId, query, cancellationToken);
}

[DialogRoute("samplecommentreadbypass")]
public sealed class CommentReadBypassSampleContext
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly MatchingSampleService service;
    private readonly CommentService comments;

    public CommentReadBypassSampleContext(MatchingSampleService service, CommentService comments, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
        this.comments = comments;
    }

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetSampleParentHistoryAsync(entityId, query, cancellationToken);

    protected override Task<Result<IReadOnlyList<CommentThread>>> LoadCommentThreadsAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.comments.GetThreadsAsync<SampleParent>(entityId, SampleStepService.CommentAccess, cancellationToken);

    protected override Task<Result<CommentThread>> PostCommentAsync(Guid entityId, CommentAudience audience, string text, CancellationToken cancellationToken) =>
        this.service.AddSampleParentCommentAsync(entityId, audience, text, cancellationToken);
}

[DialogRoute("samplecommentpostbypass")]
public sealed class CommentPostBypassSampleContext
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly MatchingSampleService service;
    private readonly CommentService comments;

    public CommentPostBypassSampleContext(MatchingSampleService service, CommentService comments, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
        this.comments = comments;
    }

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetSampleParentHistoryAsync(entityId, query, cancellationToken);

    protected override Task<Result<IReadOnlyList<CommentThread>>> LoadCommentThreadsAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.GetSampleParentCommentsAsync(entityId, cancellationToken);

    protected override Task<Result<CommentThread>> PostCommentAsync(Guid entityId, CommentAudience audience, string text, CancellationToken cancellationToken) =>
        this.comments.AddCommentAsync<SampleParent>(entityId, new CommentThreadAccess(audience, RolePermission.TenantView, RolePermission.TenantView), text, cancellationToken);
}

public sealed class TwiceCommentSampleService
{
    public CommentService Comments { get; set; } = null!;

    public async Task<Result<IReadOnlyList<CommentThread>>> GetSampleParentCommentsAsync(Guid id, CancellationToken cancellationToken)
    {
        await Comments.GetThreadsAsync<SampleParent>(id, SampleStepService.CommentAccess, cancellationToken);
        return await Comments.GetThreadsAsync<SampleParent>(id, SampleStepService.CommentAccess, cancellationToken);
    }
}

[DialogRoute("samplecommenttwice")]
public sealed class CommentTwiceSampleContext
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly MatchingSampleService service;
    private readonly TwiceCommentSampleService twice;

    public CommentTwiceSampleContext(MatchingSampleService service, TwiceCommentSampleService twice, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
        this.twice = twice;
    }

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetSampleParentHistoryAsync(entityId, query, cancellationToken);

    protected override Task<Result<IReadOnlyList<CommentThread>>> LoadCommentThreadsAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.twice.GetSampleParentCommentsAsync(entityId, cancellationToken);

    protected override Task<Result<CommentThread>> PostCommentAsync(Guid entityId, CommentAudience audience, string text, CancellationToken cancellationToken) =>
        this.service.AddSampleParentCommentAsync(entityId, audience, text, cancellationToken);
}

[DialogRoute("samplecommentreadonly")]
public sealed class CommentReadOnlySampleContext
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly MatchingSampleService service;

    public CommentReadOnlySampleContext(MatchingSampleService service, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetSampleParentHistoryAsync(entityId, query, cancellationToken);

    protected override Task<Result<IReadOnlyList<CommentThread>>> LoadCommentThreadsAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.GetSampleParentCommentsAsync(entityId, cancellationToken);
}

public sealed class LoadingSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override async Task OnInitializedAsync() => await Service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);
}

public sealed class QuietSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    private string title = string.Empty;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void OnInitialized() => this.title = Context.Title;

    public string Shown => this.title;
}

public sealed class RadzenSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>, IDisposable
{
    [Inject] public DialogService Dialogs { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public int Closed { get; private set; }

    protected override void OnInitialized() => Dialogs.OnClose += OnClose;

    private void OnClose(dynamic result) => Closed++;

    public void Dispose() => Dialogs.OnClose -= OnClose;
}

public abstract class LoadingSampleBodyBase : ComponentBase
{
    [Inject] public MatchingSampleService Service { get; set; } = null!;

    protected override async Task OnInitializedAsync() => await Service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);
}

public sealed class BaseClassSampleBody : LoadingSampleBodyBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;
}

public sealed class PublicHelperSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override Task OnInitializedAsync() => FetchAsync();

    public async Task FetchAsync() => await Service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);
}

public static class SampleLoads
{
    public static Task FetchAsync(MatchingSampleService service) => service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);

    public static Task FetchThroughAnExtensionAsync(this MatchingSampleService service) => service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);
}

public sealed class StaticHelperSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override Task OnInitializedAsync() => SampleLoads.FetchAsync(Service);
}

public sealed class ExtensionSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override Task OnInitializedAsync() => Service.FetchThroughAnExtensionAsync();
}

public interface ISampleParentSource
{
    Task<SampleParent?> ReadAsync(Guid id);
}

public sealed class InterfaceSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public ISampleParentSource Source { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override Task OnInitializedAsync() => Source.ReadAsync(Guid.NewGuid());
}

public sealed class ParametersSetSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override async Task OnParametersSetAsync() => await Service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);
}

public sealed class AfterRenderSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override async Task OnAfterRenderAsync(bool firstRender) => await Service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);
}

public sealed class UnusedGraphSampleService : SampleStepService
{
    private readonly IQueryable<SampleParent> parents = Enumerable.Empty<SampleParent>().AsQueryable();

    public Task<Result<DetachedEntityUpdater<SampleParent>>> LoadSampleParentUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        var withGraph = this.parents.WithWorkflowGraph();
        _ = this.parents.FirstOrDefault(parent => parent.Id == id);
        GC.KeepAlive(withGraph);
        return Task.FromResult(Result<DetachedEntityUpdater<SampleParent>>.Failure("sample"));
    }
}

[DialogRoute("sampleunusedgraph")]
public sealed class UnusedGraphSampleContext
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly UnusedGraphSampleService service;

    public UnusedGraphSampleContext(UnusedGraphSampleService service, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<StateHistoryPage>.Failure("sample"));
}

public static class SampleParentReads
{
    public static SampleParent? WithTheGraph(IQueryable<SampleParent> parents, Guid id) => parents.WithWorkflowGraph().FirstOrDefault(parent => parent.Id == id);

    public static SampleParent? WithoutTheGraph(IQueryable<SampleParent> parents, Guid id) => parents.FirstOrDefault(parent => parent.Id == id);
}

public sealed class HelperSampleService : SampleStepService
{
    public Task<Result<DetachedEntityUpdater<SampleParent>>> LoadSampleParentUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        _ = SampleParentReads.WithTheGraph(Enumerable.Empty<SampleParent>().AsQueryable(), id);
        return Task.FromResult(Result<DetachedEntityUpdater<SampleParent>>.Failure("sample"));
    }
}

[DialogRoute("samplehelper")]
public sealed class HelperSampleContext
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly HelperSampleService service;

    public HelperSampleContext(HelperSampleService service, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<StateHistoryPage>.Failure("sample"));
}

public sealed class UngraphedHelperSampleService : SampleStepService
{
    public Task<Result<DetachedEntityUpdater<SampleParent>>> LoadSampleParentUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        _ = SampleParentReads.WithoutTheGraph(Enumerable.Empty<SampleParent>().AsQueryable(), id);
        return Task.FromResult(Result<DetachedEntityUpdater<SampleParent>>.Failure("sample"));
    }
}

[DialogRoute("sampleungraphedhelper")]
public sealed class UngraphedHelperSampleContext
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly UngraphedHelperSampleService service;

    public UngraphedHelperSampleContext(UngraphedHelperSampleService service, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<StateHistoryPage>.Failure("sample"));
}

public sealed class UngraphedStepSampleService
{
    public WorkflowService Workflows { get; set; } = null!;

    public Task<Result<DetachedEntityUpdater<SampleParent>>> LoadSampleParentUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        _ = Enumerable.Empty<SampleParent>().AsQueryable().WithWorkflowGraph().FirstOrDefault(parent => parent.Id == id);
        return Task.FromResult(Result<DetachedEntityUpdater<SampleParent>>.Failure("sample"));
    }

    public Task<Result> UpdateSampleParentAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        Workflows.SaveAndTransitionAsync<ParentState, SampleParent>(updater, transitionKey, null, comment, cancellationToken);
}

[DialogRoute("sampleungraphedstep")]
public sealed class UngraphedStepSampleContext
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly UngraphedStepSampleService service;

    public UngraphedStepSampleContext(UngraphedStepSampleService service, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<StateHistoryPage>.Failure("sample"));
}

public sealed class PassThroughCreateSampleService : SampleStepService
{
    public Task<Result<DetachedEntityUpdater<SampleParent>>> LoadSampleParentUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        _ = Enumerable.Empty<SampleParent>().AsQueryable().WithWorkflowGraph().FirstOrDefault(parent => parent.Id == id);
        return Task.FromResult(Result<DetachedEntityUpdater<SampleParent>>.Failure("sample"));
    }

    public Task<Result<Guid>> CreateSampleParentAsync(SampleParent parent, string transitionKey, CancellationToken cancellationToken) =>
        Workflows.CreateAndTransitionAsync<ParentState, SampleParent>(parent, transitionKey, SampleParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
}

[DialogRoute("samplepassthrough")]
public sealed class PassThroughCreateSampleContext
    : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly PassThroughCreateSampleService service;

    public PassThroughCreateSampleContext(PassThroughCreateSampleService service, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<StateHistoryPage>.Failure("sample"));

    protected override Task<Result<Guid>> CreateEntityAsync(SampleParent entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateSampleParentAsync(entity, transitionKey, cancellationToken);
}

public sealed class RenderSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder) => _ = Service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);
}

public sealed class ShouldRenderSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override bool ShouldRender()
    {
        _ = Service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);
        return true;
    }
}

public sealed class DelegateFieldSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    private readonly Func<Task> warmUp;

    public DelegateFieldSampleBody() => this.warmUp = () => Service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);

    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override Task OnInitializedAsync() => this.warmUp();
}

public abstract class HookedSampleBodyBase : ComponentBase
{
    protected override void OnInitialized() => Prepare();

    protected virtual void Prepare()
    {
    }
}

public sealed class VirtualDispatchSampleBody : HookedSampleBodyBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void Prepare() => _ = Service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);
}

public interface ISampleWarmUp
{
    Task WarmUpAsync();
}

public sealed class LoadingSampleWarmUp : ISampleWarmUp
{
    private readonly MatchingSampleService service;

    public LoadingSampleWarmUp(MatchingSampleService service)
    {
        this.service = service;
    }

    public Task WarmUpAsync() => this.service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);
}

public sealed class InterfaceImplementationSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override Task OnInitializedAsync()
    {
        ISampleWarmUp warmUp = new LoadingSampleWarmUp(Service);
        return warmUp.WarmUpAsync();
    }
}

public static class DeepSampleLoads
{
    public static Task One(MatchingSampleService service) => Two(service);

    public static Task Two(MatchingSampleService service) => Three(service);

    public static Task Three(MatchingSampleService service) => Four(service);

    public static Task Four(MatchingSampleService service) => Five(service);

    public static Task Five(MatchingSampleService service) => Six(service);

    public static Task Six(MatchingSampleService service) => Seven(service);

    public static Task Seven(MatchingSampleService service) => service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);
}

public sealed class DeepSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override Task OnInitializedAsync() => DeepSampleLoads.One(Service);
}

public static class SampleIncludeWorkflowQueries
{
    public static IQueryable<SampleParent> WithWorkflowGraph(IQueryable<SampleParent> query) => query.Include(parent => parent.Children);
}

public sealed class ChildDialogSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public DialogService Dialogs { get; set; } = null!;

    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public Func<IQueryable<SampleParent>, IQueryable<SampleParent>>? Graph { get; private set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder) =>
        builder.AddAttribute(0, "onclick", EventCallback.Factory.Create(this, () => OpenChildAsync(Guid.NewGuid())));

    private Task<StateEntityDialogResult> OpenChildAsync(Guid id)
    {
        Graph = SampleIncludeWorkflowQueries.WithWorkflowGraph;
        return Dialogs.OpenStateEntityDialogAsync<QuietSampleBody, MatchingSampleContext>(new MatchingSampleContext(Service, id));
    }
}

public sealed class ChildDialogLoadingSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public DialogService Dialogs { get; set; } = null!;

    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder) =>
        builder.AddAttribute(0, "onclick", EventCallback.Factory.Create(this, () => OpenChildAsync(Guid.NewGuid())));

    private async Task OpenChildAsync(Guid id)
    {
        await Dialogs.OpenStateEntityDialogAsync<QuietSampleBody, MatchingSampleContext>(new MatchingSampleContext(Service, id));
        await Service.LoadSampleParentUpdaterAsync(id, CancellationToken.None);
    }
}

public static class SneakyWorkflowQueries
{
    public static Task WithWorkflowGraph(MatchingSampleService service) => service.LoadSampleParentUpdaterAsync(Guid.NewGuid(), CancellationToken.None);
}

public sealed class SneakyGraphSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public MatchingSampleService Service { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override Task OnInitializedAsync() => SneakyWorkflowQueries.WithWorkflowGraph(Service);
}

public sealed class GraphCalledSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public IQueryable<SampleParent>? Shaped { get; private set; }

    protected override void OnInitialized() => Shaped = SampleIncludeWorkflowQueries.WithWorkflowGraph(Enumerable.Empty<SampleParent>().AsQueryable());
}

[DialogRoute("sampleview")]
public sealed class ViewSampleContext : InheritedSampleContextBase
{
    public ViewSampleContext(MatchingSampleService service, Guid? id)
        : base(service, id)
    {
    }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogView("info", "Info"),
        new StateEntityDialogListView<Guid>("children", "Children", Staffora.Test.Web.StateEntity.TestChildList.Definition, (query, cancellationToken) => Service.QuerySampleParentChildrenAsync(EntityId!.Value, query, cancellationToken)),
    ];
}

[DialogRoute("sampleviewbypass")]
public sealed class ViewBypassSampleContext : InheritedSampleContextBase
{
    private readonly WorkflowService workflows;

    public ViewBypassSampleContext(MatchingSampleService service, WorkflowService workflows, Guid? id)
        : base(service, id)
    {
        this.workflows = workflows;
    }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogListView<Guid>("children", "Children", Staffora.Test.Web.StateEntity.TestChildList.Definition, async (query, cancellationToken) =>
        {
            await this.workflows.GetAvailableTransitionsAsync<ParentState, SampleParent>(EntityId!.Value, SampleParentWorkflowQueries.WithWorkflowGraph, cancellationToken);
            await Service.LoadSampleParentUpdaterAsync(EntityId!.Value, cancellationToken);
            return await Service.QuerySampleParentChildrenAsync(EntityId!.Value, query, cancellationToken);
        }),
    ];
}

public static class LoadingWorkflowQueries
{
    public static IServiceProvider? Services { get; set; }

    public static IQueryable<SampleParent> WithWorkflowGraph(IQueryable<SampleParent> query)
    {
        _ = Services!.GetRequiredService<Staffora.Services.Tenancy.TenantService>().FetchTenantContentAsync().GetAwaiter().GetResult();
        return query;
    }
}

public sealed class LoadingGraphGroupSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public IQueryable<SampleParent>? Shaped { get; private set; }

    protected override void OnInitialized()
    {
        Func<IQueryable<SampleParent>, IQueryable<SampleParent>> graph = LoadingWorkflowQueries.WithWorkflowGraph;
        Shaped = graph(Enumerable.Empty<SampleParent>().AsQueryable());
    }
}

public static class ViewLoadHelper
{
    public static Task AskStepsFirstAsync(WorkflowService workflows, Guid id, CancellationToken cancellationToken) =>
        workflows.GetAvailableTransitionsAsync<ParentState, SampleParent>(id, SampleParentWorkflowQueries.WithWorkflowGraph, cancellationToken);
}

[DialogRoute("sampleviewhelper")]
public sealed class ViewHelperSampleContext : InheritedSampleContextBase
{
    private readonly WorkflowService workflows;

    public ViewHelperSampleContext(MatchingSampleService service, WorkflowService workflows, Guid? id)
        : base(service, id)
    {
        this.workflows = workflows;
    }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogListView<Guid>("children", "Children", Staffora.Test.Web.StateEntity.TestChildList.Definition, async (query, cancellationToken) =>
        {
            await ViewLoadHelper.AskStepsFirstAsync(this.workflows, EntityId!.Value, cancellationToken);
            return await Service.QuerySampleParentChildrenAsync(EntityId!.Value, query, cancellationToken);
        }),
    ];
}

[DialogRoute("sampleviewtwice")]
public sealed class ViewTwiceSampleContext : InheritedSampleContextBase
{
    public ViewTwiceSampleContext(MatchingSampleService service, Guid? id)
        : base(service, id)
    {
    }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogListView<Guid>("children", "Children", Staffora.Test.Web.StateEntity.TestChildList.Definition, async (query, cancellationToken) =>
        {
            _ = await Service.QuerySampleParentChildrenAsync(EntityId!.Value, query, cancellationToken);
            return await Service.QuerySampleParentChildrenAsync(EntityId!.Value, query, cancellationToken);
        }),
    ];
}
