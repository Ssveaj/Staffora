using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Staffora.Data;

public sealed class DatabaseCommandLog : DbCommandInterceptor
{
    public const string Category = "Staffora.Data.DatabaseCommands";

    public static readonly TimeSpan DefaultSlowCommandThreshold = TimeSpan.FromSeconds(1);

    public static readonly EventId CommandFailedEvent = new(31001, "DatabaseCommandFailed");
    public static readonly EventId SlowCommandEvent = new(31002, "DatabaseCommandSlow");

    public DatabaseCommandLog(TimeSpan slowCommandThreshold)
    {
        SlowCommandThreshold = slowCommandThreshold;
    }

    public static DatabaseCommandLog Instance { get; } = new(DefaultSlowCommandThreshold);

    public TimeSpan SlowCommandThreshold { get; }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => LogFailure(command, eventData);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        LogFailure(command, eventData);
        return Task.CompletedTask;
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        LogIfSlow(command, eventData);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        LogIfSlow(command, eventData);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        LogIfSlow(command, eventData);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData);
        return ValueTask.FromResult(result);
    }

    private static void LogFailure(DbCommand command, CommandErrorEventData eventData) =>
        LoggerOf(eventData.Context)?.LogError(
            CommandFailedEvent,
            eventData.Exception,
            "A database command failed after {ElapsedMilliseconds} ms: {CommandText} Parameters: {ParameterNames}.",
            (long)eventData.Duration.TotalMilliseconds,
            command.CommandText,
            ParameterNames(command));

    private void LogIfSlow(DbCommand command, CommandExecutedEventData eventData)
    {
        if (eventData.Duration <= SlowCommandThreshold)
        {
            return;
        }

        LoggerOf(eventData.Context)?.LogWarning(
            SlowCommandEvent,
            "A database command took {ElapsedMilliseconds} ms, more than {ThresholdMilliseconds} ms: {CommandText} Parameters: {ParameterNames}.",
            (long)eventData.Duration.TotalMilliseconds,
            (long)SlowCommandThreshold.TotalMilliseconds,
            command.CommandText,
            ParameterNames(command));
    }

    private static string ParameterNames(DbCommand command) =>
        command.Parameters.Count == 0 ? "none" : string.Join(", ", command.Parameters.Cast<DbParameter>().Select(parameter => parameter.ParameterName));

    private static ILogger? LoggerOf(DbContext? context) =>
        context?.GetInfrastructure().GetService<ILoggerFactory>()?.CreateLogger(Category);
}
