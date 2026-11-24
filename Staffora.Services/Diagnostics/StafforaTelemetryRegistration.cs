using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Staffora.Services.Diagnostics;

public static class StafforaTelemetryRegistration
{
    public static IServiceCollection AddStafforaTelemetry(this IServiceCollection services)
    {
        services.AddMetrics();
        services.TryAddSingleton<StafforaTelemetry>();
        return services;
    }
}
