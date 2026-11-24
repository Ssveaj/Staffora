using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Staffora.Blazor.Helper;
using Staffora.Services.Diagnostics;

namespace Staffora.Test.Unit.Diagnostics;

public class TelemetryRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_an_endpoint_no_provider_or_exporter_is_registered_and_only_the_instruments_are(string? endpoint)
    {
        var services = Services(endpoint);

        Assert.False(Telemetry.IsExported(Configuration(endpoint)));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(TracerProvider) || descriptor.ServiceType == typeof(MeterProvider) || descriptor.ServiceType == typeof(LoggerProvider));
        Assert.DoesNotContain(services, descriptor => IsOpenTelemetry(descriptor.ServiceType) || IsOpenTelemetry(descriptor.ImplementationType));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(StafforaTelemetry));
    }

    [Fact]
    public void With_an_endpoint_traces_metrics_and_logs_are_exported_over_OTLP_as_the_Staffora_service()
    {
        var services = Services("http://localhost:4317");

        Assert.True(Telemetry.IsExported(Configuration("http://localhost:4317")));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(TracerProvider));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(MeterProvider));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(LoggerProvider));
        Assert.Contains(services, descriptor => Names(descriptor).Any(name => name.Contains("Otlp", StringComparison.Ordinal)));

        using var provider = services.BuildServiceProvider();
        var tracer = provider.GetRequiredService<TracerProvider>();
        Assert.Equal(StafforaTelemetry.Name, tracer.GetResource().Attributes.Single(attribute => attribute.Key == "service.name").Value);
        Assert.True(StafforaTelemetry.Source.HasListeners());
    }

    private static ServiceCollection Services(string? endpoint)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Configuration(endpoint));
        services.AddLogging();
        services.AddTelemetry(Configuration(endpoint));
        return services;
    }

    private static IConfiguration Configuration(string? endpoint) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [Telemetry.EndpointSetting] = endpoint }).Build();

    private static IEnumerable<string> Names(ServiceDescriptor descriptor) =>
        new[] { descriptor.ServiceType, descriptor.ImplementationType }.Concat(descriptor.ServiceType.GenericTypeArguments).OfType<Type>().Select(type => type.FullName ?? type.Name);

    private static bool IsOpenTelemetry(Type? type) => type?.Namespace?.StartsWith("OpenTelemetry", StringComparison.Ordinal) == true;
}
