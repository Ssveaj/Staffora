using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Jobs;

public static class JobClockRegistration
{
    public static IServiceCollection AddJobClock(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<JobClockOptions>()
            .Bind(configuration.GetSection(JobClockOptions.Section))
            .Validate(options => options.Interval >= TimeSpan.FromSeconds(1) && options.Interval <= JobClockOptions.MaxInterval, $"{JobClockOptions.Section}:{nameof(JobClockOptions.Interval)} must be from a second to {JobClockOptions.MaxInterval}.")
            .ValidateOnStart();
        services.AddStafforaTelemetry();
        services.TryAddSingleton<JobClock>();
        services.AddHostedService<JobClockService>();
        return services;
    }
}
