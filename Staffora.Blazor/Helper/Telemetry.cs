using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Staffora.Services.Diagnostics;

namespace Staffora.Blazor.Helper;

public static class Telemetry
{
    public const string EndpointSetting = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public const string EntityFrameworkCoreMeter = "Microsoft.EntityFrameworkCore";

    public static readonly string[] BlazorSources = ["Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.Server.Circuits"];

    public static readonly string[] BlazorMeters = ["Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.Lifecycle", "Microsoft.AspNetCore.Components.Server.Circuits"];

    public static bool IsExported(IConfiguration configuration) => !string.IsNullOrWhiteSpace(configuration[EndpointSetting]);

    public static IServiceCollection AddTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddStafforaTelemetry();
        if (!IsExported(configuration))
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(StafforaTelemetry.Name))
            .WithTracing(tracing => tracing
                .AddSource(StafforaTelemetry.Name)
                .AddSource(BlazorSources)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(StafforaTelemetry.Name, EntityFrameworkCoreMeter)
                .AddMeter(BlazorMeters)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation())
            .WithLogging()
            .UseOtlpExporter();
        return services;
    }
}
