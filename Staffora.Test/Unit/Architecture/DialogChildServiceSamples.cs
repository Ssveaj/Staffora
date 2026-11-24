using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Architecture.DialogScanSamples;

public sealed class ChildServiceSampleService : SampleStepService
{
    public StaffingService Staffing { get; set; } = null!;

    public Task<Result<DetachedEntityUpdater<SampleParent>>> LoadSampleParentUpdaterAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Result<DetachedEntityUpdater<SampleParent>>.Failure("sample"));
}

public abstract class ChildServiceSampleContextBase : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly ChildServiceSampleService service;

    protected ChildServiceSampleContextBase(ChildServiceSampleService service, Guid? id)
        : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected ChildServiceSampleService Service => this.service;

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetSampleParentHistoryAsync(entityId, query, cancellationToken);
}

public sealed class ChildServiceViewSampleContext : ChildServiceSampleContextBase
{
    public ChildServiceViewSampleContext(ChildServiceSampleService service, Guid? id)
        : base(service, id)
    {
    }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogListView<StaffingCandidateRow>("candidates", "Candidates", StaffingCandidateList.Definition, (query, cancellationToken) => Service.Staffing.QuerySlotCandidatesAsync(EntityId!.Value, query, cancellationToken)),
    ];
}

public sealed class ChildServiceTwiceSampleContext : ChildServiceSampleContextBase
{
    public ChildServiceTwiceSampleContext(ChildServiceSampleService service, Guid? id)
        : base(service, id)
    {
    }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogListView<StaffingCandidateRow>("candidates", "Candidates", StaffingCandidateList.Definition, async (query, cancellationToken) =>
        {
            _ = await Service.Staffing.QuerySlotCandidatesAsync(EntityId!.Value, query, cancellationToken);
            return await Service.Staffing.QuerySlotCandidatesAsync(EntityId!.Value, query, cancellationToken);
        }),
    ];
}

public sealed class ChildServiceOwnPermissionSampleContext : ChildServiceSampleContextBase
{
    public ChildServiceOwnPermissionSampleContext(ChildServiceSampleService service, Guid? id)
        : base(service, id)
    {
    }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogListView<StaffingCandidateRow>("candidates", "Candidates", StaffingCandidateList.Definition, (query, cancellationToken) => Service.Staffing.QuerySlotCandidatesAsync(EntityId!.Value, query, cancellationToken))
        {
            Permission = RolePermission.ViewCoordination,
        },
    ];
}

public sealed class ChildServiceOtherPermissionSampleContext : ChildServiceSampleContextBase
{
    public ChildServiceOtherPermissionSampleContext(ChildServiceSampleService service, Guid? id)
        : base(service, id)
    {
    }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogListView<StaffingCandidateRow>("candidates", "Candidates", StaffingCandidateList.Definition, (query, cancellationToken) => Service.Staffing.QuerySlotCandidatesAsync(EntityId!.Value, query, cancellationToken))
        {
            Permission = RolePermission.RolesView,
        },
    ];
}

public sealed class UncheckedChildServiceSampleContext : ChildServiceSampleContextBase
{
    public UncheckedChildServiceSampleContext(ChildServiceSampleService service, Guid? id)
        : base(service, id)
    {
    }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogListView<StaffingCandidateRow>("candidates", "Candidates", StaffingCandidateList.Definition, async (query, cancellationToken) =>
        {
            await Service.Workflows.GetAvailableTransitionsAsync<ParentState, SampleParent>(EntityId!.Value, SampleParentWorkflowQueries.WithWorkflowGraph, cancellationToken);
            return await Service.Staffing.QuerySlotCandidatesAsync(EntityId!.Value, query, cancellationToken);
        }),
    ];
}
