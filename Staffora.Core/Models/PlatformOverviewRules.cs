using System.Globalization;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Core.Models;

public enum PlatformHealth
{
    Healthy = 0,

    Attention = 1,

    Problem = 2,
}

public static class PlatformOverviewRules
{
    public static readonly TimeSpan SetupWaitLimit = TimeSpan.FromDays(3);

    public static bool IsSetupOverdue(PlatformOverviewTenant tenant, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return tenant.State == TenantState.Setup && now - tenant.Created > SetupWaitLimit;
    }

    public static PlatformHealth ErrorsTone(int errorsLastDay) => errorsLastDay > 0 ? PlatformHealth.Attention : PlatformHealth.Healthy;

    public static PlatformHealth MessagesTone(int deadLetters) => deadLetters > 0 ? PlatformHealth.Problem : PlatformHealth.Healthy;

    public static PlatformHealth ActingTone(PlatformRecentImpersonation? open) => open is null ? PlatformHealth.Healthy : PlatformHealth.Attention;

    public static string Ago(DateTime then, DateTime now)
    {
        var span = now - then;
        return span switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{Count((int)span.TotalMinutes, "minute", "minutes")} ago",
            { TotalDays: < 1 } => $"{Count((int)span.TotalHours, "hour", "hours")} ago",
            _ => $"{Count((int)span.TotalDays, "day", "days")} ago",
        };
    }

    public static string Count(int count, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? singular : plural)}");
}
