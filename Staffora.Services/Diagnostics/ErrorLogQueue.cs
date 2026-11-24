using System.Threading.Channels;
using Staffora.Core.Entities.Platform;

namespace Staffora.Services.Diagnostics;

public sealed class ErrorLogQueue
{
    public const int Capacity = 1_000;

    private static readonly AsyncLocal<bool> suppressed = new();

    private readonly Channel<QueuedError> channel = Channel.CreateBounded<QueuedError>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly object progressGate = new();
    private readonly List<(long Sequence, TaskCompletionSource Done)> flushWaiters = [];
    private long enqueued;
    private long processed;
    private long dropped;

    public static bool IsSuppressed => suppressed.Value;

    public static IDisposable Suppress()
    {
        var before = suppressed.Value;
        suppressed.Value = true;
        return new Restore(before);
    }

    public long Enqueued
    {
        get
        {
            lock (this.progressGate)
            {
                return this.enqueued;
            }
        }
    }

    public long Dropped => Interlocked.Read(ref this.dropped);

    internal ChannelReader<QueuedError> Reader => this.channel.Reader;

    public bool TryEnqueue(ErrorLog error)
    {
        ArgumentNullException.ThrowIfNull(error);
        bool written;
        lock (this.progressGate)
        {
            written = this.channel.Writer.TryWrite(new QueuedError(this.enqueued + 1, error));
            if (written)
            {
                this.enqueued++;
            }
        }

        if (!written)
        {
            Interlocked.Increment(ref this.dropped);
        }

        return written;
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource done;
        lock (this.progressGate)
        {
            if (this.processed >= this.enqueued)
            {
                return Task.CompletedTask;
            }

            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.flushWaiters.Add((this.enqueued, done));
        }

        return done.Task.WaitAsync(cancellationToken);
    }

    internal void MarkProcessed(long sequence)
    {
        List<TaskCompletionSource> released;
        lock (this.progressGate)
        {
            this.processed = Math.Max(this.processed, sequence);
            released = [.. this.flushWaiters.Where(waiter => waiter.Sequence <= this.processed).Select(waiter => waiter.Done)];
            this.flushWaiters.RemoveAll(waiter => waiter.Sequence <= this.processed);
        }

        foreach (var done in released)
        {
            done.TrySetResult();
        }
    }

    internal sealed record QueuedError(long Sequence, ErrorLog Error);

    private sealed class Restore : IDisposable
    {
        private readonly bool before;

        public Restore(bool before)
        {
            this.before = before;
        }

        public void Dispose() => suppressed.Value = this.before;
    }
}
