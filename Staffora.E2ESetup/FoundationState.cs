using System.Text;
using System.Text.Json;

namespace Staffora.E2ESetup;

public sealed record FoundationState(DateTimeOffset PreparedAt, string Database, IReadOnlyList<FoundationPersona> Personas, FoundationTenantUser ImpersonationTarget, FoundationTenantUser TenantAdministrator, FoundationTenantUser TenantViewer, FoundationErrorLog ErrorLog)
{
    public FoundationStaffing? Staffing { get; init; }

    public FoundationPersona Persona(string key) =>
        Personas.FirstOrDefault(persona => persona.Key == key)
        ?? throw new InvalidOperationException($"The foundation state has no persona '{key}'.");
}

public sealed record FoundationStaffing(string Customer, string Qualification, string Service);

public sealed record FoundationPersona(string Key, string Actor, string Email, string Password)
{
    public string? Name { get; init; }

    public override string ToString() => $"{Actor} {Email}";
}

public static class FoundationStateFile
{
    public const string FileName = "foundation-state.json";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath => Path.Combine(FindRepositoryRoot(), ".e2e", FileName);

    public static void RemoveStale(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Directory.Exists(directory))
        {
            return;
        }

        File.Delete(path);
        foreach (var temporary in Directory.EnumerateFiles(directory, TemporaryPattern(path)))
        {
            File.Delete(temporary);
        }
    }

    public static async Task WriteAtomicallyAsync<T>(string path, T state, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    public static FoundationState? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<FoundationState>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
            var complete = state is { Database.Length: > 0, Personas.Count: > 0 }
                && state.Personas.All(persona => persona is { Key.Length: > 0, Email.Length: > 0, Password.Length: > 0 })
                && state.ImpersonationTarget is { Tenant.Length: > 0, Name.Length: > 0, Email.Length: > 0 }
                && state.TenantAdministrator is { Tenant.Length: > 0, Name.Length: > 0, Email.Length: > 0 }
                && state.TenantViewer is { Tenant.Length: > 0, Name.Length: > 0, Email.Length: > 0 }
                && state.ErrorLog is { Search.Length: > 0, Entries.Count: > 0 }
                && state.ErrorLog.Entries.All(entry => entry is { Key.Length: > 0, Level.Length: > 0, Message.Length: > 0 });
            return complete ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Staffora.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Staffora.slnx was not found above {AppContext.BaseDirectory}, so the foundation state has no place to go.");
    }

    private static string TemporaryPattern(string path) => $"{Path.GetFileName(path)}.*.tmp";
}
