using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Execution;
using Staffora.Data;

namespace Staffora.Services.Diagnostics;

public sealed class ErrorLogWriter : BackgroundService
{
    public const int BatchSize = 100;

    private long droppedRecorded;
    private readonly ErrorLogQueue queue;
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly TimeProvider clock;
    private readonly ILogger<ErrorLogWriter> logger;

    public ErrorLogWriter(ErrorLogQueue queue, IApplicationDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider clock, ILogger<ErrorLogWriter> logger)
    {
        this.queue = queue;
        this.dbFactory = dbFactory;
        this.clock = clock;
        this.logger = logger;
    }

    public static string DroppedMessage(long dropped) => dropped == 1
        ? "1 error-log entry was dropped because the queue was full."
        : $"{dropped} error-log entries were dropped because the queue was full.";

    public static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var suppression = ErrorLogQueue.Suppress();
        try
        {
            while (await this.queue.Reader.WaitToReadAsync(stoppingToken))
            {
                await WriteAvailableAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        using var drain = new CancellationTokenSource(ShutdownDrainTimeout);
        try
        {
            await WriteAvailableAsync(drain.Token);
        }
        catch (OperationCanceledException)
        {
            this.logger.LogWarning("Stopped before the queued errors were stored; they are lost.");
        }
    }

    private async Task WriteAvailableAsync(CancellationToken cancellationToken)
    {
        var batch = new List<ErrorLogQueue.QueuedError>(BatchSize);
        while (true)
        {
            batch.Clear();
            while (batch.Count < BatchSize && this.queue.Reader.TryRead(out var queued))
            {
                batch.Add(queued);
            }

            if (batch.Count == 0)
            {
                return;
            }

            await WriteBatchAsync(batch, cancellationToken);
            this.queue.MarkProcessed(batch[^1].Sequence);
        }
    }

    private async Task WriteBatchAsync(IReadOnlyList<ErrorLogQueue.QueuedError> batch, CancellationToken cancellationToken)
    {
        try
        {
            var dropped = this.queue.Dropped - this.droppedRecorded;
            await using var context = this.dbFactory.CreateSystemDbContext();
            using var lease = context.BeginSystemScope("error log: store the errors the application logged");
            context.ErrorLogs.AddRange(batch.Select(queued => queued.Error));
            if (dropped > 0)
            {
                context.ErrorLogs.Add(new ErrorLog
                {
                    Occurred = this.clock.GetUtcNow().UtcDateTime,
                    Level = ErrorLogLevel.Warning,
                    Category = typeof(ErrorLogQueue).FullName!,
                    Message = DroppedMessage(dropped),
                });
            }

            await context.SaveChangesAsync(cancellationToken);
            this.droppedRecorded += dropped;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            this.logger.LogWarning(exception, "Could not store {Count} errors in the error log; they are lost. Dropped so far because the queue was full: {Dropped}.", batch.Count, this.queue.Dropped);
        }
    }
}
