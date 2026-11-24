using Microsoft.Data.SqlClient;

namespace Staffora.E2ESetup;

public static class E2EDatabaseGuard
{
    public const string Marker = "e2e";

    public static readonly IReadOnlyList<string> EnvironmentMarkers = ["prod", "stage", "staging"];

    public static bool IsE2EDatabase(string? databaseName) => Refusal(databaseName) is null;

    public static string EnsureE2EDatabase(string? databaseName) =>
        Refusal(databaseName) is { } refusal ? throw new InvalidOperationException(refusal) : databaseName!;

    public static string EnsureE2EConnection(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("No connection string for the end-to-end database: set STAFFORA_E2E_CONNECTION to a database whose name contains \"e2e\".");
        }

        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(builder.AttachDBFilename) || builder.UserInstance)
        {
            throw new InvalidOperationException("Refusing to prepare a database for end-to-end tests from a connection string with AttachDbFilename or User Instance: the attached file can be any database, so name the database on a server instead.");
        }

        EnsureE2EDatabase(builder.InitialCatalog);
        return connectionString;
    }

    private static string? Refusal(string? databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName) || !databaseName.Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            return $"Refusing to prepare the database '{databaseName}' for end-to-end tests: its name must contain \"{Marker}\".";
        }

        var environment = EnvironmentMarkers.FirstOrDefault(marker => databaseName.Contains(marker, StringComparison.OrdinalIgnoreCase));
        return environment is null
            ? null
            : $"Refusing to prepare the database '{databaseName}' for end-to-end tests: its name contains \"{environment}\", which marks a shared environment.";
    }
}
