using System.Data.Common;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Staffora.Test.Integration.Infrastructure;

public sealed class DeadlockingReads : DbCommandInterceptor
{
    private int seen;
    private int thrown;
    private readonly string table;
    private readonly int failures;

    public DeadlockingReads(string table, int failures)
    {
        this.table = table;
        this.failures = failures;
    }

    public int Thrown => Volatile.Read(ref this.thrown);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains($"FROM [{this.table}]", StringComparison.Ordinal) && Interlocked.Increment(ref this.seen) <= this.failures)
        {
            Interlocked.Increment(ref this.thrown);
            throw Deadlock();
        }

        return ValueTask.FromResult(result);
    }

    public static SqlException Deadlock()
    {
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        var create = typeof(SqlError).GetConstructors(Hidden).OrderBy(constructor => constructor.GetParameters().Length).First();
        var arguments = create.GetParameters().Select((parameter, index) =>
            index == 0 ? 1205
            : parameter.ParameterType == typeof(byte) ? (byte)13
            : parameter.ParameterType == typeof(string) ? "Transaction was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction."
            : parameter.ParameterType == typeof(int) ? 0
            : parameter.ParameterType == typeof(uint) ? 0u
            : (object?)null).ToArray();
        typeof(SqlErrorCollection).GetMethod("Add", Hidden)!.Invoke(errors, [create.Invoke(arguments)]);
        return (SqlException)typeof(SqlException).GetMethod("CreateException", BindingFlags.NonPublic | BindingFlags.Static, [typeof(SqlErrorCollection), typeof(string)])!.Invoke(null, [errors, "16.0.0"])!;
    }
}
