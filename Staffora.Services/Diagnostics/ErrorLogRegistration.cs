using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Staffora.Services.Diagnostics;

public static class ErrorLogRegistration
{
    public static IServiceCollection AddErrorLog(this IServiceCollection services)
    {
        services.TryAddSingleton<ErrorLogQueue>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ErrorLogLoggerProvider>());
        services.TryAddSingleton<ErrorLogWriter>();
        services.AddHostedService(provider => provider.GetRequiredService<ErrorLogWriter>());
        return services;
    }
}
