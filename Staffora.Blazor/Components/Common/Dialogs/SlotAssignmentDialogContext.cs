using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Comments;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.SlotAssignment)]
public sealed class SlotAssignmentDialogContext : StateEntityDialogContextBase<SlotAssignmentState, SlotAssignment>
{
    private readonly SlotAssignmentService service;

    private bool readByStaff;

    public SlotAssignmentDialogContext(SlotAssignmentService service, Guid? assignmentId)
        : base(assignmentId, SlotAssignmentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    protected override Task<Result<DetachedEntityUpdater<SlotAssignment>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSlotAssignmentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SlotAssignment> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSlotAssignmentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetSlotAssignmentHistoryAsync(entityId, query, cancellationToken);

    internal const string StaffThreadTitle = "Questions to the coordinator";

    protected override async Task<Result<IReadOnlyList<CommentThread>>> LoadCommentThreadsAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var read = await this.service.Conversations.GetSlotAssignmentCommentsAsync(entityId, cancellationToken);
        if (!read.IsSuccess || read.Value is not { } threads)
        {
            return read;
        }

        this.readByStaff = threads.Any(thread => thread.Turn?.AttentionText == CommentTurn.NewReply);
        return Result<IReadOnlyList<CommentThread>>.Success([.. threads.Select(AsReaderSees)]);
    }

    protected override async Task<Result<CommentThread>> PostCommentAsync(Guid entityId, CommentAudience audience, string text, CancellationToken cancellationToken)
    {
        var posted = await this.service.Conversations.AddSlotAssignmentCommentAsync(entityId, audience, text, cancellationToken);
        return posted.IsSuccess && posted.Value is { } thread ? Result<CommentThread>.Success(AsReaderSees(thread)) : posted;
    }

    private CommentThread AsReaderSees(CommentThread thread) =>
        this.readByStaff && thread.Audience == CommentAudience.Staff
            ? thread with { Title = StaffThreadTitle, Readers = "You and the coordinators of the job" }
            : thread;

    public IReadOnlyList<AssignmentJobField> JobFields { get; private set; } = [];

    public bool ForStaff { get; private set; }

    public string? Service { get; private set; }

    public IReadOnlyList<string> Qualifications { get; private set; } = [];

    public string? Status { get; private set; }

    public string? StatusHint { get; private set; }

    public string? AnswerBy { get; private set; }

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetSlotAssignmentEditorOptionsAsync(EntityId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        (JobFields, ForStaff, Service, Qualifications, Status, StatusHint, AnswerBy) = (options.Value.JobFields, options.Value.ForStaff, options.Value.Service, options.Value.Qualifications, options.Value.Status, options.Value.StatusHint, options.Value.AnswerBy);
        return Result.Success();
    }

    protected override bool ReadsEditorOptionsWithEveryLoad => true;

    protected override bool IsLockedRecord(SlotAssignment entity) => true;
}
