using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Scheduling;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.PatternAssignment)]
public sealed class PatternAssignmentDialogContext : StateEntityDialogContextBase<ResourcePatternAssignmentState, ResourcePatternAssignment>
{
    private bool fieldsLocked = true;
    private readonly PatternAssignmentService service;

    public PatternAssignmentDialogContext(PatternAssignmentService service, Guid? assignmentId) : base(assignmentId, ResourcePatternAssignmentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    public Guid? UserId { get; init; }

    public IReadOnlyList<WorkPattern> Patterns { get; private set; } = [];

    public string Staff { get; private set; } = string.Empty;

    public string? PatternNote { get; private set; }

    protected override Task<Result<DetachedEntityUpdater<ResourcePatternAssignment>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadResourcePatternAssignmentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<ResourcePatternAssignment> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateResourcePatternAssignmentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result> DeleteAsync(DetachedEntityUpdater<ResourcePatternAssignment> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateResourcePatternAssignmentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetResourcePatternAssignmentHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetResourcePatternAssignmentEditorOptionsAsync(EntityId, UserId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        (this.fieldsLocked, Patterns, Staff, PatternNote) = (options.Value.FieldsLocked, options.Value.Patterns, options.Value.Staff, options.Value.PatternNote);
        return Result.Success();
    }

    protected override bool ReadsEditorOptionsWithEveryLoad => true;

    protected override Task<Result<ResourcePatternAssignment>> NewEntityAsync(CancellationToken cancellationToken) =>
        UserId is { } user
            ? this.service.NewResourcePatternAssignmentAsync(user, cancellationToken)
            : Task.FromResult(Result<ResourcePatternAssignment>.Failure(PatternAssignmentService.StaffNotFound));

    protected override Task<Result<Guid>> CreateEntityAsync(ResourcePatternAssignment entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateResourcePatternAssignmentAsync(entity, transitionKey, cancellationToken);

    protected override bool IsLockedRecord(ResourcePatternAssignment entity) => this.fieldsLocked;
}
