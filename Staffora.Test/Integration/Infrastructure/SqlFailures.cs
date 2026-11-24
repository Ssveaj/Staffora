using System.Reflection;
using Microsoft.Data.SqlClient;

namespace Staffora.Test.Integration.Infrastructure;

public static class SqlFailures
{
    public const string SevereError = "A severe error occurred on the current command.  The results, if any, should be discarded.";

    public const string DeadlockVictim = "Transaction was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.";

    public static SqlException Create(int number, string message)
    {
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        var create = typeof(SqlError).GetConstructors(Hidden).OrderBy(constructor => constructor.GetParameters().Length).First();
        var arguments = create.GetParameters().Select((parameter, index) =>
            index == 0 ? number
            : parameter.ParameterType == typeof(byte) ? (byte)(number == 0 ? 11 : 13)
            : parameter.ParameterType == typeof(string) ? message
            : parameter.ParameterType == typeof(int) ? 0
            : parameter.ParameterType == typeof(uint) ? 0u
            : (object?)null).ToArray();
        typeof(SqlErrorCollection).GetMethod("Add", Hidden)!.Invoke(errors, [create.Invoke(arguments)]);
        return (SqlException)typeof(SqlException).GetMethod("CreateException", BindingFlags.NonPublic | BindingFlags.Static, [typeof(SqlErrorCollection), typeof(string)])!.Invoke(null, [errors, "16.0.0"])!;
    }
}
