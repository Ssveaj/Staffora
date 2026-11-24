using Staffora.Core.Models;

namespace Staffora.Blazor.Components.Coordination;

public enum TwoStepLoadState
{
    Loading = 0,

    FirstShown = 1,

    Ready = 2,

    Failed = 3,

    SecondFailed = 4,
}

public sealed class TwoStepLoad<TFirst, TSecond> : IDisposable
    where TFirst : class
    where TSecond : class
{
    private int generation;
    private CancellationTokenSource? running;

    public event Action? Changed;

    public TwoStepLoadState State { get; private set; }

    public TFirst? First { get; private set; }

    public TSecond? Second { get; private set; }

    public string? Error { get; private set; }

    public string? SecondError { get; private set; }

    public int Loads => this.generation;

    public bool IsRunning { get; private set; }

    public async Task<bool> LoadAsync(Func<CancellationToken, Task<Result<TFirst>>> first, Func<TFirst, CancellationToken, Task<Result<TSecond>>> second, Func<TFirst, TSecond, bool>? stillFits = null)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var generation = ++this.generation;
        this.running?.Cancel();
        this.running?.Dispose();
        var running = this.running = new CancellationTokenSource();
        var token = running.Token;
        IsRunning = true;
        try
        {
            var read = await first(token);
            if (generation != this.generation)
            {
                return false;
            }

            if (!read.IsSuccess || read.Value is null)
            {
                (First, Second, Error, SecondError, State) = (null, null, read.Error ?? "The board could not be read.", null, TwoStepLoadState.Failed);
                IsRunning = false;
                Changed?.Invoke();
                return true;
            }

            var kept = Second is { } earlier && stillFits is not null && stillFits(read.Value, earlier) ? earlier : null;
            (First, Second, Error, SecondError, State) = (read.Value, kept, null, null, TwoStepLoadState.FirstShown);
            Changed?.Invoke();

            var then = await second(read.Value, token);
            if (generation != this.generation)
            {
                return false;
            }

            if (then.IsSuccess && then.Value is not null)
            {
                (Second, SecondError, State) = (then.Value, null, TwoStepLoadState.Ready);
            }
            else
            {
                (Second, SecondError, State) = (null, then.Error ?? "The bookings could not be read.", TwoStepLoadState.SecondFailed);
            }

            IsRunning = false;
            Changed?.Invoke();
            return true;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (generation == this.generation)
            {
                IsRunning = false;
            }
        }
    }

    public void Dispose()
    {
        this.generation++;
        this.running?.Cancel();
        this.running?.Dispose();
        this.running = null;
        IsRunning = false;
    }
}
