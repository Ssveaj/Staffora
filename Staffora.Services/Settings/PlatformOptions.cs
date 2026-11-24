using Staffora.Core.Settings;

namespace Staffora.Services.Settings;

public sealed class PlatformOptions
{
    public const string Section = "Platform";

    public string TimeZone { get; set; } = IanaTimeZones.Utc;

    public TenantSettingDefaults Defaults => new(TimeZone);
}
