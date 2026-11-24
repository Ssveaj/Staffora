using Microsoft.Data.SqlClient;
using Staffora.E2ESetup;

namespace Staffora.Test.E2E;

public static class E2EDatabase
{
    public const string Variable = "STAFFORA_E2E_CONNECTION";

    public const string CiVariable = "CI";

    public static string ConnectionString
    {
        get
        {
            var connection = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(connection))
            {
                var message = $"{Variable} is not set, so the end-to-end database tests cannot run (see Staffora.Test/E2E/README.md).";
                if (MustFailWhenUnset(Environment.GetEnvironmentVariable(CiVariable)))
                {
                    Assert.Fail($"{message} In CI that is a failure, not a skip.");
                }

                Assert.Skip(message);
            }

            return E2EDatabaseGuard.EnsureE2EConnection(connection);
        }
    }

    public static bool MustFailWhenUnset(string? ciValue) =>
        !string.IsNullOrWhiteSpace(ciValue)
        && !ciValue.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)
        && ciValue.Trim() != "0";

    public static string NewStatePath() =>
        Path.Combine(AppContext.BaseDirectory, "e2e-state", Guid.NewGuid().ToString("N"), FoundationStateFile.FileName);

    public static async Task<FoundationState> PrepareAsync(string statePath, TextWriter? log = null)
    {
        var result = await new FoundationPrep(TimeProvider.System, log ?? TextWriter.Null).RunAsync(ConnectionString, statePath, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    public static async Task<int> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    public static void Remove(string statePath)
    {
        var folder = Path.GetDirectoryName(statePath)!;
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class E2EDatabaseCollection
{
    public const string Name = "End-to-end database";
}
