using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Staffora.Data;

internal sealed class TenantHoldInterceptor : DbTransactionInterceptor
{
    public static TenantHoldInterceptor Instance { get; } = new();

    private TenantHoldInterceptor()
    {
    }

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        try
        {
            (eventData.Context as ApplicationDbContext)?.HoldOnTransactionStartAsync(connection, result, synchronously: true, CancellationToken.None).GetAwaiter().GetResult();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        try
        {
            if (eventData.Context is ApplicationDbContext context)
            {
                await context.HoldOnTransactionStartAsync(connection, result, synchronously: false, cancellationToken);
            }

            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }
}
