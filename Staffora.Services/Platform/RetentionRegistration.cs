using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Platform;

public static class RetentionRegistration
{
    public static IServiceCollection AddRetention(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection(RetentionOptions.Section))
            .Validate(options => options.Interval >= TimeSpan.FromMinutes(1) && options.Interval <= RetentionOptions.MaxInterval, $"{RetentionOptions.Section}:{nameof(RetentionOptions.Interval)} must be from a minute to {RetentionOptions.MaxInterval}.")
            .Validate(options => options.FirstPassDelay >= TimeSpan.Zero && options.FirstPassDelay <= RetentionOptions.MaxInterval, $"{RetentionOptions.Section}:{nameof(RetentionOptions.FirstPassDelay)} must be from zero to {RetentionOptions.MaxInterval}.")
            .Validate(options => options.ErrorLogDays >= 1 && options.ReadNoticeDays >= 1 && options.ProcessedOutboxDays >= 1 && options.PendingDeletionGraceDays >= 1, $"Every period of {RetentionOptions.Section} is at least one day.")
            .Validate(options => options.BatchSize is >= 1 and <= RetentionOptions.MaxBatchSize, $"{RetentionOptions.Section}:{nameof(RetentionOptions.BatchSize)} must be from 1 to {RetentionOptions.MaxBatchSize}.")
            .ValidateOnStart();
        services.AddStafforaTelemetry();
        services.TryAddSingleton<Retention>();
        services.AddHostedService<RetentionService>();
        return services;
    }
}
