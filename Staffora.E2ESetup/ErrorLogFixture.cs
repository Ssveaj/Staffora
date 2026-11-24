using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Execution;
using Staffora.Data;

namespace Staffora.E2ESetup;

public sealed record FoundationErrorLogEntry(string Key, string Level, string Message, int DaysAgo);

public sealed record FoundationErrorLog(string Search, IReadOnlyList<FoundationErrorLogEntry> Entries);

public static class ErrorLogFixture
{
    public const string Search = "timetable";

    private static readonly FoundationErrorLogEntry[] entries =
    [
        new("criticalToday", nameof(ErrorLogLevel.Critical), "End-to-end error log: the timetable import crashed", 0),
        new("errorToday", nameof(ErrorLogLevel.Error), "End-to-end error log: the timetable export failed", 0),
        new("errorLongAgo", nameof(ErrorLogLevel.Error), "End-to-end error log: the timetable sync failed", 20),
        new("unmatchedErrorToday", nameof(ErrorLogLevel.Error), "End-to-end error log: a notification could not be sent", 0),
    ];

    public static async Task<FoundationErrorLog> SeedAsync(IServiceProvider services, TimeProvider clock, CancellationToken cancellationToken)
    {
        var contexts = services.GetRequiredService<IApplicationDbContextFactory<ApplicationDbContext>>();
        var now = clock.GetUtcNow().UtcDateTime;
        await using var context = contexts.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("end-to-end error-log rows for the smoke journey's filters");
        foreach (var entry in entries)
        {
            context.ErrorLogs.Add(new ErrorLog
            {
                Occurred = now.AddDays(-entry.DaysAgo).AddHours(-1),
                Level = Enum.Parse<ErrorLogLevel>(entry.Level),
                Category = typeof(ErrorLogFixture).FullName!,
                Message = entry.Message,
            });
        }

        await context.SaveChangesAsync(cancellationToken);
        return new FoundationErrorLog(Search, entries);
    }
}
