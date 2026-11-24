using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Radzen;
using Staffora.Blazor.Helper;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Services.Authorization;
using Staffora.Services.Comments;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

public abstract class StateEntityDialogContext
{
    internal const string LoadFailed = "The record could not be opened because of an error. Try again in a moment.";

    internal const string CommentsFailed = "The comments could not be read. Close the record and open it again to try once more.";

    internal const string CommentPostFailed = "The comment could not be posted. Try again.";

    internal const string CannotBeChecked = WorkflowService.StepCannotBeChecked;

    internal const string NavigationHeldWhileBusy = "A step is still running. Wait until it has finished before you leave.";

    private static int contexts;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationToken lifetimeToken;
    private readonly Dictionary<string, CancellationTokenSource> viewLifetimes = new(StringComparer.Ordinal);
    private readonly HashSet<string> visitedViews = new(StringComparer.Ordinal);
    private readonly HashSet<RolePermission> grantedViewPermissions = [];
    private IReadOnlyList<StateEntityDialogView>? views;
    private string? activeViewKey;
    private bool settling;
    private (Guid? EntityId, int Version, Task<Result<IReadOnlyList<TransitionOffer>>> Answer)? offers;
    private Task? firstLoad;
    private WorkflowService? workflow;
    private DialogService? dialogs;
    private string? displayName;
    private string? oneTimeMessage;
    private Guid? entityId;
    private bool? declaresCommentThreads;
    private bool? declaresConversations;
    private CommentAudience? selectedAudience;
    private bool conversationsSelected;
    private readonly Dictionary<string, string> drafts = new(StringComparer.Ordinal);

    protected StateEntityDialogContext()
    {
        this.lifetimeToken = this.lifetime.Token;
        DomId = $"state-entity-dialog-views-{Interlocked.Increment(ref contexts)}";
    }

    public string DefaultTitle { get; init; } = "Record";

    public string CreateTitle { get; init; } = "New record";

    public string EntityIcon { get; init; } = "description";

    public string UnsavedChangesMessage { get; init; } = "You have changes that are not saved. Close the dialog and lose them?";

    public string? DisplayName
    {
        get => this.displayName;
        set
        {
            if (!string.Equals(this.displayName, value, StringComparison.Ordinal))
            {
                this.displayName = value;
                Changed?.Invoke();
            }
        }
    }

    public string Title =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
        : IsCreate ? CreateTitle
        : DefaultTitle;

    public bool FooterDisabled { get; set; }

    public RenderFragment? FooterActions { get; set; }

    public Func<ITransition, Task<bool>>? OnBeforeTransition { get; set; }

    public Func<ITransition, bool>? TransitionFilter { get; set; }

    public Func<ITransition, string?>? TransitionLabel { get; set; }

    public abstract Type EntityType { get; }

    public Guid? EntityId
    {
        get => this.entityId;
        private protected set
        {
            if (this.entityId is { } current && current != value)
            {
                throw new InvalidOperationException($"The dialog shows {EntityType.Name} {current}; a dialog never moves to another row.");
            }

            this.entityId = value;
        }
    }

    public bool IsCreate => EntityId is null;

    public bool Editing { get; private set; }

    public bool IsUnlocked => !LocksUntilEdit || Editing;

    internal bool LocksUntilEdit => OpensLocked && !IsCreate && StateHasSave;

    protected internal virtual bool OpensLocked => true;

    internal abstract bool StateHasSave { get; }

    internal abstract bool IsEditStep(ITransition transition);

    internal abstract bool RecordLocked { get; }

    internal void BeginEditing()
    {
        Editing = true;
        Changed?.Invoke();
    }

    internal async Task CancelEditingAsync()
    {
        if (HasChanges)
        {
            await ReloadEntityAsync();
        }

        Editing = false;
        Changed?.Invoke();
    }

    public StateEntityDialogLoadState LoadState { get; private protected set; } = StateEntityDialogLoadState.Loading;

    public string? CouldNotOpenReason { get; private protected set; }

    public abstract Enum? State { get; }

    public abstract bool HasChanges { get; }

    public int Version { get; private protected set; }

    public bool IsBusy { get; internal set; }

    internal bool IsHeldByMove { get; set; }

    internal bool IsSettling => this.settling;

    public string DialogToken => DialogRouteAttribute.TokenOf(GetType())
        ?? throw new InvalidOperationException($"{GetType().Name} declares no [DialogRoute]: every state-entity dialog context names its token with the attribute.");

    public DialogStackSegment? UrlSegment => EntityId is { } id ? new DialogStackSegment(DialogToken, id) : null;

    public CancellationToken Lifetime => this.lifetimeToken;

    public bool HasSaved { get; private protected set; }

    public StateEntityDialogResult DialogResult => new(HasSaved, LoadState == StateEntityDialogLoadState.Deleted, EntityId);

    public event Action? Changed;

    public event Action? Edited;

    internal event Action<StateEntityDialogContext>? CloseRequested;

    internal event Action<string>? StatusReported;

    private protected WorkflowService Workflows => this.workflow ?? throw new InvalidOperationException("The dialog's host attaches the workflow service before the context loads.");

    private protected TimeProvider Clock { get; private set; } = TimeProvider.System;

    private protected IServiceProvider? Services { get; private set; }

    private protected ILogger Log { get; private set; } = NullLogger.Instance;

    internal abstract object? EntityObject { get; }

    public void NotifyChanged() => Changed?.Invoke();

    public void NotifyEdited() => Edited?.Invoke();

    protected void ShowOnce(string message) => this.oneTimeMessage = message;

    internal string? TakeOneTimeMessage()
    {
        var message = this.oneTimeMessage;
        this.oneTimeMessage = null;
        return message;
    }

    internal void Attach(WorkflowService workflow, TimeProvider clock, IServiceProvider? services, ILogger log, DialogService? dialogs = null)
    {
        this.workflow = workflow;
        Clock = clock;
        Services = services;
        Log = log;
        this.dialogs = dialogs;
    }

    internal bool IsAttached => this.workflow is not null;

    internal Task Shown => this.shown.Task;

    internal void MarkShown() => this.shown.TrySetResult();

    internal Task EnsureLoadedAsync() => this.firstLoad ??= LoadAsync();

    internal bool HasStartedLoading => this.firstLoad is not null;

    internal Task ReloadEntityAsync() => LoadAsync();

    public async Task<bool> ReloadAsync()
    {
        if (LoadState != StateEntityDialogLoadState.Loaded || IsBusy || Lifetime.IsCancellationRequested)
        {
            return false;
        }

        switch (await SettleEditsAsync())
        {
            case EditsSettled.Kept:
                return false;
            case EditsSettled.None:
                if (Lifetime.IsCancellationRequested)
                {
                    return false;
                }

                ReloadRequests++;
                await ReloadEntityAsync();
                return true;
            default:
                return true;
        }
    }

    internal int ReloadRequests { get; private set; }

    protected virtual IEnumerable<StateEntityDialogView> DeclareViews() => [];

    internal IReadOnlyList<StateEntityDialogView> Views => this.views ??= ReadViews();

    private List<StateEntityDialogView> ReadViews()
    {
        var views = DeclareViews().ToList();
        foreach (var view in views)
        {
            if (!StateEntityDialogView.IsValidKey(view.Key))
            {
                throw new InvalidOperationException($"The {EntityType.Name} dialog declares the view key '{view.Key}': a key is lowercase letters, digits and hyphens, starting with a letter.");
            }
        }

        if (views.GroupBy(view => view.Key, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice)
        {
            throw new InvalidOperationException($"The {EntityType.Name} dialog declares the view '{twice.Key}' twice.");
        }

        return [.. views.Select((view, index) => (view, index)).OrderBy(pair => pair.view.Order).ThenBy(pair => pair.index).Select(pair => pair.view)];
    }

    internal IReadOnlyList<StateEntityDialogView> OfferedViews =>
        LoadState != StateEntityDialogLoadState.Loaded
            ? []
            : [.. Views.Where(view =>
                (view.Permission is not { } permission || this.grantedViewPermissions.Contains(permission))
                && (view.Area is not { } area || AreaAccessOf(area).HasFlag(FieldAccessFlags.Visible))
                && (view.When is not { } when || when())
                && (!(view.ReadsStoredRows || view.StoredOnly) || !IsCreate))];

    internal bool HasViewTabs => OfferedViews.Count > 1;

    internal StateEntityDialogView? ActiveView
    {
        get
        {
            var offered = OfferedViews;
            return offered.FirstOrDefault(view => view.Key == this.activeViewKey) ?? offered.FirstOrDefault();
        }
    }

    internal string? ActiveViewKey => ActiveView?.Key;

    internal StateEntityDialogView? ViewNamed(string key) => Views.FirstOrDefault(view => view.Key == key);

    internal void OpenOnView(string key) => this.activeViewKey = key;

    internal bool IsViewMounted(string key) =>
        OfferedViews.Any(view => view.Key == key) && (this.visitedViews.Contains(key) || ActiveViewKey == key);

    internal string ViewTabId(string key) => $"{DomId}-tab-{key}";

    internal string ViewPanelId(string key) => $"{DomId}-panel-{key}";

    internal string DomId { get; }

    internal CancellationToken ViewLifetime(string key)
    {
        if (this.lifetimeToken.IsCancellationRequested)
        {
            return this.lifetimeToken;
        }

        if (!this.viewLifetimes.TryGetValue(key, out var source))
        {
            source = CancellationTokenSource.CreateLinkedTokenSource(this.lifetimeToken);
            this.viewLifetimes[key] = source;
        }

        return source.Token;
    }

    internal async Task<bool> SelectViewAsync(string key)
    {
        var target = OfferedViews.FirstOrDefault(view => view.Key == key);
        var current = ActiveView;
        if (target is null || IsBusy || this.settling || Lifetime.IsCancellationRequested)
        {
            return false;
        }

        if (current?.Key == key)
        {
            return true;
        }

        if (target.ReadsStoredRows && await SettleEditsAsync() == EditsSettled.Kept)
        {
            return false;
        }

        if (current is not null)
        {
            this.visitedViews.Add(current.Key);
            EndViewLoad(current.Key);
        }

        this.activeViewKey = key;
        this.visitedViews.Add(key);
        Changed?.Invoke();
        return true;
    }

    private void ForgetViewsNoLongerOffered()
    {
        var offered = OfferedViews;
        foreach (var key in this.visitedViews.Where(key => !offered.Any(view => view.Key == key)).ToList())
        {
            this.visitedViews.Remove(key);
            EndViewLoad(key);
        }

        if (this.activeViewKey is { } active && !offered.Any(view => view.Key == active))
        {
            this.activeViewKey = null;
        }
    }

    private void EndViewLoad(string key)
    {
        if (this.viewLifetimes.Remove(key, out var source))
        {
            source.Cancel();
            source.Dispose();
        }
    }

    private protected async Task LoadViewAccessAsync(CancellationToken cancellationToken)
    {
        var permissions = Views.Select(view => view.Permission).OfType<RolePermission>().Distinct().ToList();
        if (permissions.Count == 0 || Services?.GetService(typeof(PermissionService)) is not PermissionService service)
        {
            return;
        }

        foreach (var permission in permissions)
        {
            if (await service.HasPermissionAsync(permission, cancellationToken))
            {
                this.grantedViewPermissions.Add(permission);
            }
        }
    }

    internal bool MayReadHistory { get; private set; }

    private protected async Task LoadHistoryAccessAsync(CancellationToken cancellationToken)
    {
        if (Services?.GetService(typeof(StateHistoryService)) is StateHistoryService history)
        {
            MayReadHistory = await history.MayReadHistoryAsync(cancellationToken);
        }
    }

    internal async Task<EditsSettled> SettleEditsAsync()
    {
        if (LoadState != StateEntityDialogLoadState.Loaded || !HasChanges)
        {
            return EditsSettled.None;
        }

        if (IsBusy || this.settling || Lifetime.IsCancellationRequested)
        {
            return EditsSettled.Kept;
        }

        this.settling = true;
        Changed?.Invoke();
        try
        {
            var (save, reasons) = await FindSaveStepAsync();
            if (Lifetime.IsCancellationRequested)
            {
                return EditsSettled.Kept;
            }

            string? refusal = null;
            if (save is not null)
            {
                var attempt = await RunLeaveStepAsync(save);
                if (attempt.Ran)
                {
                    LeaveStepEnded?.Invoke(save, attempt);
                    return EditsSettled.Saved;
                }

                if (attempt.Error is not { } error || Lifetime.IsCancellationRequested)
                {
                    return EditsSettled.Kept;
                }

                refusal = error;
            }

            var discard = this.dialogs is not null && await this.dialogs.ConfirmDiscardUnsavableChangesAsync(refusal is null ? reasons : [refusal], refused: refusal is not null);
            if (Lifetime.IsCancellationRequested)
            {
                return EditsSettled.Kept;
            }

            if (!discard)
            {
                if (refusal is not null)
                {
                    LeaveStepEnded?.Invoke(save!, TransitionAttempt.Refused(refusal));
                }

                return EditsSettled.Kept;
            }

            await ReloadEntityAsync();
            return EditsSettled.Discarded;
        }
        finally
        {
            this.settling = false;
            Changed?.Invoke();
        }
    }

    private async Task<(ITransition? Save, IReadOnlyList<string> Reasons)> FindSaveStepAsync()
    {
        if (IsCreate || State is not { } state || EntityWorkflow.GetSaveStep(state) is not { } step)
        {
            return (null, []);
        }

        var answer = await GetTransitionOffersAsync();
        if (!answer.IsSuccess || answer.Value is not { } offers)
        {
            return (null, string.IsNullOrWhiteSpace(answer.Error) ? [] : [answer.Error]);
        }

        return JudgedOffers(offers).FirstOrDefault(offer => offer.Transition.Key == step.Key) switch
        {
            { CanRun: true } offer => (offer.Transition, []),
            { } blocked => (null, blocked.BlockingReasons),

            null => (null, []),
        };
    }

    private async Task<TransitionAttempt> RunLeaveStepAsync(ITransition transition)
    {
        IsBusy = true;
        LeaveStepStarted?.Invoke(transition);
        Changed?.Invoke();
        try
        {
            return await RunTransitionAsync(transition, null);
        }
        catch (Exception) when (Lifetime.IsCancellationRequested)
        {
            return TransitionAttempt.GivenUp;
        }
        catch (Exception exception)
        {
            Log.LogError(exception, "The save {TransitionKey} before leaving failed in the {EntityType} dialog for {EntityId}.", transition.Key, EntityType.Name, EntityId);
            return TransitionAttempt.Refused(TransitionButton.Unexpected);
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    internal event Action<ITransition>? LeaveStepStarted;

    internal event Action<ITransition, TransitionAttempt>? LeaveStepEnded;

    private bool handedOff;

    internal void MarkHandedOff() => this.handedOff = true;

    internal bool TakeHandOff()
    {
        var handedOff = this.handedOff;
        this.handedOff = false;
        return handedOff;
    }

    internal void ReportStatus(string status) => StatusReported?.Invoke(status);

    internal abstract IReadOnlyList<ITransition> CandidateTransitions();

    internal Task<Result<IReadOnlyList<TransitionOffer>>> GetTransitionOffersAsync()
    {
        if (this.offers is { } asked && asked.EntityId == EntityId && asked.Version == Version)
        {
            return asked.Answer;
        }

        OfferRequests++;
        var answer = AskTransitionOffersAsync();
        this.offers = (EntityId, Version, answer);
        return answer;
    }

    internal int OfferRequests { get; private set; }

    private protected abstract Task<Result<IReadOnlyList<TransitionOffer>>> AskTransitionOffersAsync();

    internal abstract IWorkflow EntityWorkflow { get; }

    internal Task<Result<StateHistoryPage>> GetHistoryAsync(StateHistoryQuery query)
    {
        if (EntityId is not { } id)
        {
            return Task.FromResult(Result<StateHistoryPage>.Success(StateHistoryPage.Empty));
        }

        if (!MayReadHistory)
        {
            return Task.FromResult(Result<StateHistoryPage>.Failure(StateHistoryService.OwnersOnly));
        }

        HistoryRequests++;
        return AskHistoryAsync(id, query);
    }

    internal int HistoryRequests { get; private set; }

    private protected abstract Task<Result<StateHistoryPage>> AskHistoryAsync(Guid entityId, StateHistoryQuery query);

    internal abstract IReadOnlyList<TransitionOffer> JudgedOffers(IReadOnlyList<TransitionOffer> answers);

    internal abstract FieldAccessFlags FieldAccessOf(string field);

    internal abstract FieldAccessFlags AreaAccessOf(string area);

    internal abstract FieldAccessFlags RowsAreaAccessOf(string area);

    internal abstract CustomFieldRequirement RequirementOf(Type definitionType);

    internal IReadOnlyList<IEntityAttributeDefinition>? CustomFields { get; private protected set; }

    internal abstract Task<TransitionAttempt> RunTransitionAsync(ITransition transition, string? comment);

    internal string LabelOf(ITransition transition) =>
        TransitionLabel?.Invoke(transition) ?? transition.Label ?? Helper.EnumDisplayHelper.GetDisplayName(transition.To);

    internal void End()
    {
        this.oneTimeMessage = null;
        this.shown.TrySetResult();
        if (!this.lifetime.IsCancellationRequested)
        {
            this.lifetime.Cancel();
            foreach (var view in this.viewLifetimes.Values)
            {
                view.Dispose();
            }

            this.viewLifetimes.Clear();
            this.lifetime.Dispose();
        }
    }

    private protected async Task LoadAsync()
    {
        string? reason;

        var threads = EntityId is { } id && DeclaresCommentThreads ? ReadCommentThreadsAsync(id) : null;
        var conversations = EntityId is { } owner && DeclaresConversations ? ReadConversationsAsync(owner) : null;
        try
        {
            reason = await LoadEntityCoreAsync(Lifetime);
            if (threads is not null)
            {
                await threads;
            }

            if (conversations is not null)
            {
                await conversations;
            }
        }
        catch (Exception) when (Lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Log.LogError(exception, "The {EntityType} dialog for {EntityId} could not load its entity.", EntityType.Name, EntityId);
            reason = LoadFailed;
        }

        if (Lifetime.IsCancellationRequested)
        {
            return;
        }

        if (reason is null)
        {
            LoadState = StateEntityDialogLoadState.Loaded;
            CouldNotOpenReason = null;

            Editing = false;
            Version++;
            ForgetViewsNoLongerOffered();
        }
        else
        {
            Log.LogInformation("The {EntityType} dialog for {EntityId} could not open: {Reason}", EntityType.Name, EntityId, reason);
            LoadState = StateEntityDialogLoadState.CouldNotOpen;
            CouldNotOpenReason = reason;
        }

        Changed?.Invoke();
    }

    protected virtual Task<Result<IReadOnlyList<CommentThread>>> LoadCommentThreadsAsync(Guid entityId, CancellationToken cancellationToken) =>
        Task.FromResult(Result<IReadOnlyList<CommentThread>>.Success([]));

    protected virtual Task<Result<CommentThread>> PostCommentAsync(Guid entityId, CommentAudience audience, string text, CancellationToken cancellationToken) =>
        Task.FromResult(Result<CommentThread>.Failure("Comments are not written here."));

    internal bool DeclaresCommentThreads => this.declaresCommentThreads ??=
        GetType().GetMethod(nameof(LoadCommentThreadsAsync), BindingFlags.Instance | BindingFlags.NonPublic)?.DeclaringType != typeof(StateEntityDialogContext);

    internal IReadOnlyList<CommentThread> CommentThreads { get; private set; } = [];

    internal string? CommentThreadsError { get; private set; }

    internal bool HasSideColumn => LoadState == StateEntityDialogLoadState.Loaded && !IsCreate && (CommentThreads.Count > 0 || CommentThreadsError is not null || HasConversationsTab);

    internal CommentThread? SelectedCommentThread =>
        ConversationsShown ? null : CommentThreads.FirstOrDefault(thread => thread.Audience == this.selectedAudience) ?? CommentThreads.FirstOrDefault();

    internal void SelectCommentThread(CommentAudience audience)
    {
        if ((this.selectedAudience != audience || this.conversationsSelected) && CommentThreads.Any(thread => thread.Audience == audience))
        {
            this.selectedAudience = audience;
            this.conversationsSelected = false;
            Changed?.Invoke();
        }
    }

    internal string? DraftOf(string key) => this.drafts.GetValueOrDefault(key);

    internal void KeepDraft(string key, string? draft)
    {
        if (string.IsNullOrEmpty(draft))
        {
            this.drafts.Remove(key);
        }
        else
        {
            this.drafts[key] = draft;
        }
    }

    internal static string ThreadDraftKey(CommentAudience audience) => $"thread:{audience}";

    internal static string ConversationDraftKey(Guid conversationId) => $"conversation:{conversationId:N}";

    protected virtual Task<Result<IReadOnlyList<StaffConversationRow>?>> LoadConversationsAsync(Guid entityId, CancellationToken cancellationToken) =>
        Task.FromResult(Result<IReadOnlyList<StaffConversationRow>?>.Success(null));

    protected virtual Task<Result<IReadOnlyList<CommentThread>>> LoadConversationThreadAsync(Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult(Result<IReadOnlyList<CommentThread>>.Failure("Conversations are not read here."));

    protected virtual Task<Result<CommentThread>> PostConversationCommentAsync(Guid conversationId, string text, CancellationToken cancellationToken) =>
        Task.FromResult(Result<CommentThread>.Failure("Comments are not written here."));

    protected internal virtual string ConversationsLabel => "Conversations";

    internal bool DeclaresConversations => this.declaresConversations ??=
        GetType().GetMethod(nameof(LoadConversationsAsync), BindingFlags.Instance | BindingFlags.NonPublic)?.DeclaringType != typeof(StateEntityDialogContext);

    internal IReadOnlyList<StaffConversationRow>? Conversations { get; private set; }

    internal string? ConversationsError { get; private set; }

    internal bool HasConversationsTab => Conversations is not null || ConversationsError is not null;

    internal bool ConversationsShown => HasConversationsTab && (this.conversationsSelected || (CommentThreads.Count == 0 && CommentThreadsError is null));

    internal void SelectConversations()
    {
        if (HasConversationsTab && !this.conversationsSelected)
        {
            this.conversationsSelected = true;
            Changed?.Invoke();
        }
    }

    internal Guid? OpenConversationId { get; private set; }

    internal CommentThread? OpenConversationThread { get; private set; }

    internal string? OpenConversationError { get; private set; }

    internal async Task OpenConversationAsync(Guid conversationId)
    {
        OpenConversationId = conversationId;
        (OpenConversationThread, OpenConversationError) = (null, null);
        Changed?.Invoke();
        try
        {
            var read = await LoadConversationThreadAsync(conversationId, Lifetime);
            if (OpenConversationId != conversationId)
            {
                return;
            }

            (OpenConversationThread, OpenConversationError) = read.IsSuccess && read.Value is [var thread, ..]
                ? (thread, (string?)null)
                : ((CommentThread?)null, string.IsNullOrWhiteSpace(read.Error) ? CommentsFailed : read.Error);
        }
        catch (Exception) when (Lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Log.LogError(exception, "The {EntityType} dialog for {EntityId} could not read the conversation {ConversationId}.", EntityType.Name, EntityId, conversationId);
            (OpenConversationThread, OpenConversationError) = (null, CommentsFailed);
        }

        Changed?.Invoke();
    }

    internal void CloseConversation()
    {
        if (OpenConversationId is not null)
        {
            (OpenConversationId, OpenConversationThread, OpenConversationError) = (null, null, null);
            Changed?.Invoke();
        }
    }

    internal async Task<Result> SendConversationCommentAsync(string text)
    {
        if (OpenConversationId is not { } conversationId || EntityId is not { } id || Lifetime.IsCancellationRequested)
        {
            return Result.Failure(CommentPostFailed);
        }

        Result<CommentThread> posted;
        try
        {
            posted = await PostConversationCommentAsync(conversationId, text, Lifetime);
        }
        catch (Exception) when (Lifetime.IsCancellationRequested)
        {
            return Result.Failure(CommentPostFailed);
        }
        catch (Exception exception)
        {
            Log.LogError(exception, "Posting in the conversation {ConversationId} of the {EntityType} dialog for {EntityId} failed.", conversationId, EntityType.Name, id);
            return Result.Failure(CommentPostFailed);
        }

        if (!posted.IsSuccess || posted.Value is not { } thread)
        {
            return Result.Failure(string.IsNullOrWhiteSpace(posted.Error) ? CommentPostFailed : posted.Error);
        }

        if (OpenConversationId == conversationId)
        {
            OpenConversationThread = thread;
        }

        try
        {
            await ReadConversationsAsync(id);
        }
        catch (Exception) when (Lifetime.IsCancellationRequested)
        {
            return Result.Success(message: posted.Message);
        }

        Changed?.Invoke();
        return Result.Success(message: posted.Message);
    }

    public async Task RefreshConversationsAsync()
    {
        if (!DeclaresConversations || EntityId is not { } id || LoadState != StateEntityDialogLoadState.Loaded || Lifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await ReadConversationsAsync(id);
            if (OpenConversationId is { } open)
            {
                var read = await LoadConversationThreadAsync(open, Lifetime);
                if (OpenConversationId == open && read.IsSuccess && read.Value is [var thread, ..])
                {
                    OpenConversationThread = thread;
                }
            }
        }
        catch (Exception) when (Lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Log.LogError(exception, "The {EntityType} dialog for {EntityId} could not read its open conversation again.", EntityType.Name, id);
        }

        Changed?.Invoke();
    }

    private async Task ReadConversationsAsync(Guid entityId)
    {
        try
        {
            var read = await LoadConversationsAsync(entityId, Lifetime);
            (Conversations, ConversationsError) = read.IsSuccess
                ? (read.Value, (string?)null)
                : ((IReadOnlyList<StaffConversationRow>?)null, string.IsNullOrWhiteSpace(read.Error) ? CommentsFailed : read.Error);
        }
        catch (Exception exception) when (Lifetime.IsCancellationRequested)
        {
            throw exception as OperationCanceledException ?? new OperationCanceledException(Lifetime);
        }
        catch (Exception exception)
        {
            Log.LogError(exception, "The {EntityType} dialog for {EntityId} could not read its conversations.", EntityType.Name, entityId);
            (Conversations, ConversationsError) = (null, CommentsFailed);
        }
    }

    internal int CommentThreadRequests { get; private set; }

    private async Task ReadCommentThreadsAsync(Guid entityId)
    {
        CommentThreadRequests++;
        try
        {
            var read = await LoadCommentThreadsAsync(entityId, Lifetime);
            if (read.IsSuccess && read.Value is { } threads)
            {
                (CommentThreads, CommentThreadsError) = (threads, null);
            }
            else
            {
                (CommentThreads, CommentThreadsError) = ([], string.IsNullOrWhiteSpace(read.Error) ? CommentsFailed : read.Error);
            }
        }
        catch (Exception exception) when (Lifetime.IsCancellationRequested)
        {
            throw exception as OperationCanceledException ?? new OperationCanceledException(Lifetime);
        }
        catch (Exception exception)
        {
            Log.LogError(exception, "The {EntityType} dialog for {EntityId} could not read its comment threads.", EntityType.Name, entityId);
            (CommentThreads, CommentThreadsError) = ([], CommentsFailed);
        }
    }

    internal async Task<Result> SendCommentAsync(CommentAudience audience, string text)
    {
        if (EntityId is not { } id || Lifetime.IsCancellationRequested)
        {
            return Result.Failure(CommentPostFailed);
        }

        Result<CommentThread> posted;
        try
        {
            posted = await PostCommentAsync(id, audience, text, Lifetime);
        }
        catch (Exception) when (Lifetime.IsCancellationRequested)
        {
            return Result.Failure(CommentPostFailed);
        }
        catch (Exception exception)
        {
            Log.LogError(exception, "Posting a comment in the {EntityType} dialog for {EntityId} failed.", EntityType.Name, id);
            return Result.Failure(CommentPostFailed);
        }

        if (!posted.IsSuccess || posted.Value is not { } thread)
        {
            return Result.Failure(string.IsNullOrWhiteSpace(posted.Error) ? CommentPostFailed : posted.Error);
        }

        CommentThreads = [.. CommentThreads.Select(shown => shown.Audience == audience ? thread : shown)];
        Changed?.Invoke();
        return Result.Success(message: posted.Message);
    }

    private protected abstract Task<string?> LoadEntityCoreAsync(CancellationToken cancellationToken);

    private protected void CloseAsDeleted()
    {
        LoadState = StateEntityDialogLoadState.Deleted;
        Version++;
        Changed?.Invoke();
        CloseRequested?.Invoke(this);
    }
}

internal enum EditsSettled
{
    None = 0,

    Saved = 1,

    Discarded = 2,

    Kept = 3,
}
