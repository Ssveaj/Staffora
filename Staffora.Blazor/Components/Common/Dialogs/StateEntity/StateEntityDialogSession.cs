using Radzen;
using Staffora.Blazor.Helper;

namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

public sealed class StateEntityDialogSession
{
    internal const string NavigationHeldWhileMoving = "Wait until the next record has loaded.";

    private readonly List<StateEntityDialogTarget> targets;
    private readonly List<StateEntityDialogTarget> removed = [];

    private StateEntityDialogContext? held;

    private StateEntityDialogContext? preloading;

    private TaskCompletionSource? givenUp;

    private (StateEntityDialogContext Context, Task Load)? leftRunning;

    private int dialogsAbove;

    private TaskCompletionSource? uncovered;

    public StateEntityDialogSession(IReadOnlyList<StateEntityDialogTarget> targets, int start = 0)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            throw new ArgumentException("A session shows at least one entity.", nameof(targets));
        }

        if (targets.Select(target => target.Context).Distinct().Count() != targets.Count)
        {
            throw new ArgumentException("Each target of a session has a context of its own; a context is one dialog over one entity.", nameof(targets));
        }

        if (targets.Count > 1 && targets.FirstOrDefault(target => target.Context.IsCreate) is { } created)
        {
            throw new ArgumentException($"A session of several records shows saved rows only, and a new {created.Context.EntityType.Name} has none: open it in a dialog of its own, where its first step creates it.", nameof(targets));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(start, targets.Count);
        this.targets = [.. targets];
        CurrentIndex = start;
    }

    public IReadOnlyList<StateEntityDialogTarget> Targets => this.targets;

    public int CurrentIndex { get; private set; }

    public StateEntityDialogTarget Current => this.targets[CurrentIndex];

    public bool IsSwapping { get; private set; }

    internal bool IsPreloading => this.preloading is not null;

    internal int Preloads { get; private set; }

    internal IEnumerable<StateEntityDialogContext> AllContexts => this.targets.Concat(this.removed).Select(target => target.Context);

    public StateEntityDialogResult Result =>
        new(AllContexts.Any(context => context.HasSaved), Current.Context.LoadState == StateEntityDialogLoadState.Deleted, Current.Context.EntityId);

    internal event Action? Changed;

    internal bool Holds(StateEntityDialogContext context) => ReferenceEquals(this.held, context);

    public Task<bool> PreviousAsync() => GoToAsync(CurrentIndex - 1);

    public Task<bool> NextAsync() => GoToAsync(CurrentIndex + 1);

    public async Task<bool> GoToAsync(int index)
    {
        if (index < 0 || index >= this.targets.Count || index == CurrentIndex || IsSwapping)
        {
            return false;
        }

        var shown = Current.Context;
        if (shown.IsBusy || !this.targets[index].Context.IsAttached || shown.Lifetime.IsCancellationRequested)
        {
            return false;
        }

        IsSwapping = true;
        Changed?.Invoke();
        try
        {
            if (await shown.SettleEditsAsync() == EditsSettled.Kept || shown.Lifetime.IsCancellationRequested)
            {
                return false;
            }

            return await SwapToAsync(index, shown, givesWay: true);
        }
        finally
        {
            IsSwapping = false;
            Changed?.Invoke();
        }
    }

    internal async Task<bool> RemoveDeletedAsync(StateEntityDialogContext deleted)
    {
        var index = this.targets.FindIndex(target => ReferenceEquals(target.Context, deleted));
        if (index < 0 || this.targets.Count == 1 || deleted.Lifetime.IsCancellationRequested)
        {
            return false;
        }

        if (index != CurrentIndex)
        {
            Remove(index);
            Changed?.Invoke();
            return true;
        }

        IsSwapping = true;
        Changed?.Invoke();
        try
        {
            var neighbour = index < this.targets.Count - 1 ? index + 1 : index - 1;
            if (!await SwapToAsync(neighbour, deleted, givesWay: false))
            {
                return true;
            }

            Remove(index);
            deleted.End();
            return true;
        }
        finally
        {
            IsSwapping = false;
            Changed?.Invoke();
        }
    }

    internal async Task<bool> MayCloseAsync(DialogService dialogService)
    {
        if (IsSwapping && this.preloading is null)
        {
            return false;
        }

        var preloading = this.preloading;

        var contexts = this.targets.Select(target => target.Context).ToList();
        if (contexts.Any(context => context.IsBusy && !Holds(context)))
        {
            return false;
        }

        var edited = contexts.Where(context => context.HasChanges).ToList();
        if (edited.Count > 0 && !await dialogService.ConfirmDiscardChangesAsync(edited))
        {
            return false;
        }

        preloading?.End();
        this.preloading?.End();
        return true;
    }

    internal void DialogOpenedAbove()
    {
        this.dialogsAbove++;
        if (this.givenUp is { } givenUp)
        {
            this.givenUp = null;
            ReleaseHold();
            givenUp.TrySetResult();
        }
    }

    internal void DialogClosedAbove()
    {
        if (this.dialogsAbove == 0)
        {
            return;
        }

        this.dialogsAbove--;
        if (this.dialogsAbove == 0 && this.uncovered is { } uncovered)
        {
            this.uncovered = null;
            uncovered.TrySetResult();
        }
    }

    private async Task<bool> SwapToAsync(int index, StateEntityDialogContext shown, bool givesWay)
    {
        var next = this.targets[index].Context;
        if (!shown.IsBusy)
        {
            shown.IsBusy = true;
            shown.IsHeldByMove = true;
            this.held = shown;
            shown.NotifyChanged();
        }

        Preloads++;
        this.preloading = next;
        var givenUp = givesWay ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        this.givenUp = givenUp;
        try
        {
            var load = LoadBeforeSwap(next);
            if (givenUp is not null && await Task.WhenAny(load, givenUp.Task) != load)
            {
                this.leftRunning = (next, load);
                return false;
            }

            await load;
            if (!givesWay && this.dialogsAbove > 0 && !shown.Lifetime.IsCancellationRequested && !next.Lifetime.IsCancellationRequested)
            {
                using var ended = CancellationTokenSource.CreateLinkedTokenSource(shown.Lifetime, next.Lifetime);
                this.uncovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await this.uncovered.Task.WaitAsync(ended.Token);
            }
        }
        catch (Exception) when (shown.Lifetime.IsCancellationRequested || next.Lifetime.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            this.givenUp = null;
            this.uncovered = null;
            this.preloading = null;
            ReleaseHold();
        }

        if (shown.Lifetime.IsCancellationRequested || next.Lifetime.IsCancellationRequested)
        {
            return false;
        }

        shown.MarkHandedOff();
        CurrentIndex = index;
        return true;
    }

    private Task LoadBeforeSwap(StateEntityDialogContext next)
    {
        if (this.leftRunning is { } running && ReferenceEquals(running.Context, next) && !running.Load.IsCompleted)
        {
            return running.Load;
        }

        this.leftRunning = null;
        return !next.HasStartedLoading ? next.EnsureLoadedAsync()
            : !next.HasChanges ? next.ReloadEntityAsync()
            : Task.CompletedTask;
    }

    private void ReleaseHold()
    {
        if (this.held is not { } held)
        {
            return;
        }

        this.held = null;
        held.IsBusy = false;
        held.IsHeldByMove = false;
        held.NotifyChanged();
    }

    private void Remove(int index)
    {
        var shown = Current;
        this.removed.Add(this.targets[index]);
        this.targets.RemoveAt(index);
        CurrentIndex = Math.Max(0, this.targets.IndexOf(shown));
    }
}
