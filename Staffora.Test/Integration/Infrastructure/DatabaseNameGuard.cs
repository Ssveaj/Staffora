using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Staffora.Test.Integration.Infrastructure;

public static partial class DatabaseNameGuard
{
    public const string Marker = "test";

    public static readonly IReadOnlyList<string> EnvironmentMarkers = ["prod", "stage", "staging"];

    public static bool IsTestDatabase(string? databaseName) =>
        HasMarkerWord(databaseName) && EnvironmentMarkerIn(databaseName!) is null;

    public static string EnsureTestDatabase(string? databaseName)
    {
        if (!HasMarkerWord(databaseName))
        {
            throw new InvalidOperationException($"Refusing to use the database '{databaseName}' for real-database tests: its name must contain \"{Marker}\" as a word of its own.");
        }

        if (EnvironmentMarkerIn(databaseName!) is { } environment)
        {
            throw new InvalidOperationException($"Refusing to use the database '{databaseName}' for real-database tests: its name contains \"{environment}\", which marks a shared environment.");
        }

        return databaseName!;
    }

    public static string EnsureTestConnection(string connectionString)
    {
        EnsureTestDatabase(new SqlConnectionStringBuilder(connectionString).InitialCatalog);
        return connectionString;
    }

    public static IEnumerable<string> WordsOf(string databaseName) =>
        WordBoundary().Split(databaseName).Where(word => word.Length > 0);

    private static bool HasMarkerWord(string? databaseName) =>
        !string.IsNullOrWhiteSpace(databaseName)
        && WordsOf(databaseName).Any(word => word.Equals(Marker, StringComparison.OrdinalIgnoreCase));

    private static string? EnvironmentMarkerIn(string databaseName) =>
        EnvironmentMarkers.FirstOrDefault(marker => databaseName.Contains(marker, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"[_\-.\s]+|(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])")]
    private static partial Regex WordBoundary();
}
