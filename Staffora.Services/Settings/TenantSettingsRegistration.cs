using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Settings;
using Staffora.Services.Caching;

namespace Staffora.Services.Settings;

public static class TenantSettingsRegistration
{
    public static IServiceCollection AddTenantSettings(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<TenantLookupCache>();
        services.AddOptions<PlatformOptions>()
            .Bind(configuration.GetSection(PlatformOptions.Section))
            .Validate(options => IanaTimeZones.IsKnown(options.TimeZone), $"{PlatformOptions.Section}:{nameof(PlatformOptions.TimeZone)} must be an IANA time zone id this system offers, such as Europe/Stockholm.")
            .ValidateOnStart();
        services.AddScoped<TenantSettingsService>();
        services.AddScoped<TenantClock>();
        services.AddSingleton<TenantSettingsEnsureService>();
        return services;
    }
}
