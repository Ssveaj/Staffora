using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace Staffora.Services.Diagnostics;

public static class ReadCancellation
{
    public static void ThrowIfReaderLeft(Exception exception, ILogger log, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(log);
        if (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (exception is OperationCanceledException)
        {
            ExceptionDispatchInfo.Throw(exception);
        }

        log.LogInformation("Reading {What} ended with {Failure} after its reader left; it is taken as a cancellation.", what, exception.GetType().Name);
        throw new OperationCanceledException($"Reading {what} was canceled: its reader left.", exception, cancellationToken);
    }
}
