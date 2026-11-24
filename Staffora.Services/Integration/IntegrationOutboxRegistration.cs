using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Integration;

public static class IntegrationOutboxRegistration
{
    public static IServiceCollection AddIntegrationOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<IntegrationOutboxOptions>()
            .Bind(configuration.GetSection(IntegrationOutboxOptions.Section))
            .Validate(options => options.Interval >= TimeSpan.FromSeconds(1) && options.Interval <= IntegrationOutboxOptions.MaxInterval, $"{IntegrationOutboxOptions.Section}:{nameof(IntegrationOutboxOptions.Interval)} must be from a second to {IntegrationOutboxOptions.MaxInterval}.")
            .Validate(options => options.BatchSize is >= 1 and <= IntegrationOutboxOptions.MaxBatchSize, $"{IntegrationOutboxOptions.Section}:{nameof(IntegrationOutboxOptions.BatchSize)} must be from 1 to {IntegrationOutboxOptions.MaxBatchSize}.")
            .ValidateOnStart();
        services.AddStafforaTelemetry();
        services.TryAddSingleton<IntegrationHandlerRegistry>();
        services.TryAddSingleton<IntegrationOutboxDispatcher>();
        services.AddHostedService<IntegrationOutboxService>();
        return services;
    }

    public static IServiceCollection AddIntegrationHandler<THandler>(this IServiceCollection services, string kind)
        where THandler : class, IIntegrationHandler
    {
        services.TryAddScoped<THandler>();
        services.AddSingleton(new IntegrationHandlerRegistration(kind, typeof(THandler)));
        return services;
    }
}
