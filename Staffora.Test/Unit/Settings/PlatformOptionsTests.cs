using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Staffora.Services.Settings;

namespace Staffora.Test.Unit.Settings;

public class PlatformOptionsTests
{
    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("W. Europe Standard Time")]
    [InlineData("")]
    public async Task A_host_whose_platform_time_zone_the_system_does_not_offer_does_not_start(string zone)
    {
        using var host = HostWith(zone);

        var refused = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Platform:TimeZone must be an IANA time zone id this system offers", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_whose_platform_time_zone_is_offered_starts()
    {
        using var host = HostWith("Europe/Stockholm");

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Europe/Stockholm", host.Services.GetRequiredService<IOptions<PlatformOptions>>().Value.TimeZone);
    }

    private static IHost HostWith(string zone)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production, DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:TimeZone"] = zone });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddTenantSettings(builder.Configuration);
        return builder.Build();
    }
}
