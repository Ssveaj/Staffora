using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Staffora.Core.Models;

namespace Staffora.Services.Diagnostics;

public static class DeadlockRetry
{
    public const int DeadlockVictim = 1205;

    public static async Task<Result<T>> ReadAsync<T>(Func<CancellationToken, Task<Result<T>>> read, ILogger log, string what, string failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(log);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await read(cancellationToken);
            }
            catch (Exception exception) when (attempt == 1 && IsDeadlockVictim(exception) && !cancellationToken.IsCancellationRequested)
            {
                log.LogWarning(exception, "Reading {What} was chosen as a deadlock victim; reading it once more.", what);
            }
            catch (Exception exception)
            {
                ReadCancellation.ThrowIfReaderLeft(exception, log, what, cancellationToken);
                log.LogError(exception, "Reading {What} failed.", what);
                return Result<T>.Failure(failure);
            }
        }
    }

    public static bool IsDeadlockVictim(Exception? exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException { Number: DeadlockVictim })
            {
                return true;
            }
        }

        return false;
    }
}
