namespace Staffora.Blazor.Helper;

public static class StartupSeeding
{
    public const string SkipSetting = "STAFFORA_E2E_SKIP_STARTUP_SEED";

    public static bool IsRequested(IConfiguration configuration) =>
        configuration[SkipSetting]?.Trim() is { } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    public static bool IsSkipped(IConfiguration configuration, IHostEnvironment environment) =>
        IsRequested(configuration) && environment.IsDevelopment();
}
