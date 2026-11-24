using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Platform;
using Staffora.Data.Configurations.Platform;

namespace Staffora.Services.Diagnostics;

public sealed class ErrorLogLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private static readonly string[] excludedCategories = ["Staffora.Services.Diagnostics.", "Microsoft.EntityFrameworkCore."];

    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
    private readonly ErrorLogQueue queue;
    private readonly TimeProvider clock;

    public ErrorLogLoggerProvider(ErrorLogQueue queue, TimeProvider clock)
    {
        this.queue = queue;
        this.clock = clock;
    }

    public ILogger CreateLogger(string categoryName) => new ErrorLogLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => this.scopes = scopeProvider;

    public void Dispose()
    {
    }

    public static bool IsExcluded(string category) => excludedCategories.Any(prefix => category.StartsWith(prefix, StringComparison.Ordinal));

    private void Write<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        string? correlationId = null;
        var followsTrace = false;
        string? requestPath = null;
        Guid? tenantId = null, userId = null, impersonatorId = null;
        this.scopes.ForEachScope(
            (scope, _) =>
            {
                if (scope is LogScope traced)
                {
                    followsTrace = traced.FollowsTrace;
                }

                if (scope is LogScope { Identity: { } identity } known)
                {
                    correlationId = known.CorrelationId;
                    requestPath = known.RequestPath ?? requestPath;
                    (tenantId, userId, impersonatorId) = (identity.TenantId, identity.UserId, identity.ImpersonatorId);
                    return;
                }

                if (scope is not IEnumerable<KeyValuePair<string, object?>> values)
                {
                    return;
                }

                foreach (var (key, value) in values)
                {
                    switch (key)
                    {
                        case LogScope.CorrelationIdKey when value is not null:
                            correlationId = value.ToString();
                            break;
                        case LogScope.RequestPathKey when value is not null:
                            requestPath = value.ToString();
                            break;
                        case LogScope.TenantIdKey when value is Guid tenant:
                            tenantId = tenant;
                            break;
                        case LogScope.UserIdKey when value is Guid user:
                            userId = user;
                            break;
                        case LogScope.ImpersonatorIdKey when value is Guid impersonator:
                            impersonatorId = impersonator;
                            break;
                    }
                }
            },
            (object?)null);

        var secretValues = SecretValues(state);
        var row = new ErrorLog
        {
            Occurred = this.clock.GetUtcNow().UtcDateTime,
            Level = level == LogLevel.Critical ? ErrorLogLevel.Critical : ErrorLogLevel.Error,
            Category = Cap(category, ErrorLogConfiguration.CategoryMaxLength)!,
            EventId = eventId.Id,
            EventName = Cap(eventId.Name, ErrorLogConfiguration.EventNameMaxLength),
            Message = Clean(formatter(state, exception), secretValues, ErrorLogConfiguration.MessageMaxLength) ?? string.Empty,
            ExceptionType = Cap(exception?.GetType().FullName, ErrorLogConfiguration.ExceptionTypeMaxLength),
            ExceptionDetail = Clean(exception?.ToString(), secretValues, ErrorLogConfiguration.ExceptionDetailMaxLength),
            CorrelationId = Cap((followsTrace ? CurrentTraceId() : null) ?? correlationId ?? CurrentTraceId(), ErrorLogConfiguration.CorrelationIdMaxLength),
            RequestPath = Cap(requestPath, ErrorLogConfiguration.RequestPathMaxLength),
            TenantId = tenantId,
            UserId = userId,
            ImpersonatorId = impersonatorId,
        };
        this.queue.TryEnqueue(row);
    }

    private static List<string> SecretValues<TState>(TState state) =>
        state is IEnumerable<KeyValuePair<string, object?>> values
            ? [.. values.Where(value => SecretScrubber.IsSecretName(value.Key)).Select(value => value.Value?.ToString()).OfType<string>().Where(value => value.Length > 0)]
            : [];

    private static string? Clean(string? text, List<string> secretValues, int maxLength)
    {
        var scanned = Cap(text, maxLength + SecretScrubber.ScanHeadroom);
        if (scanned is not null)
        {
            foreach (var secret in secretValues)
            {
                scanned = scanned.Replace(secret, SecretScrubber.Redacted, StringComparison.Ordinal);
            }
        }

        return SecretScrubber.Scrub(scanned, maxLength);
    }

    private static string? CurrentTraceId() =>
        Activity.Current is { } activity && activity.TraceId != default ? activity.TraceId.ToHexString() : null;

    private static string? Cap(string? text, int maxLength) => SecretScrubber.Cap(text, maxLength);

    private sealed class ErrorLogLogger : ILogger
    {
        private readonly bool excluded;
        private readonly ErrorLogLoggerProvider provider;
        private readonly string category;

        public ErrorLogLogger(ErrorLogLoggerProvider provider, string category)
        {
            this.provider = provider;
            this.category = category;
            this.excluded = IsExcluded(category);
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => this.provider.scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error && logLevel != LogLevel.None && !this.excluded;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || ErrorLogQueue.IsSuppressed)
            {
                return;
            }

            try
            {
                this.provider.Write(this.category, logLevel, eventId, state, exception, formatter);
            }
            catch
            {
            }
        }
    }
}
