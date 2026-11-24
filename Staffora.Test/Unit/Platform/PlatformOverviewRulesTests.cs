using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;

namespace Staffora.Test.Unit.Platform;

public class PlatformOverviewRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 15, 12, 0, DateTimeKind.Utc);

    [Fact]
    public void A_company_waiting_in_setup_past_three_days_is_overdue()
    {
        Assert.True(PlatformOverviewRules.IsSetupOverdue(Tenant(TenantState.Setup, created: Now.AddDays(-5)), Now));
        Assert.False(PlatformOverviewRules.IsSetupOverdue(Tenant(TenantState.Setup, created: Now.AddDays(-2)), Now));
        Assert.False(PlatformOverviewRules.IsSetupOverdue(Tenant(TenantState.Active, created: Now.AddDays(-5)), Now));

        Assert.False(PlatformOverviewRules.IsSetupOverdue(Tenant(TenantState.Setup, created: Now - PlatformOverviewRules.SetupWaitLimit), Now));
    }

    [Fact]
    public void Each_figure_takes_its_tone_from_its_count()
    {
        Assert.Equal(PlatformHealth.Healthy, PlatformOverviewRules.ErrorsTone(0));
        Assert.Equal(PlatformHealth.Attention, PlatformOverviewRules.ErrorsTone(1));
        Assert.Equal(PlatformHealth.Healthy, PlatformOverviewRules.MessagesTone(0));
        Assert.Equal(PlatformHealth.Problem, PlatformOverviewRules.MessagesTone(3));
        Assert.Equal(PlatformHealth.Healthy, PlatformOverviewRules.ActingTone(null));
        Assert.Equal(PlatformHealth.Attention, PlatformOverviewRules.ActingTone(new PlatformRecentImpersonation(Guid.NewGuid(), "Owner", "Mina Rahimi", "Clearshine", Now.AddMinutes(-4), null)));
    }

    [Theory]
    [InlineData(0.5, "just now")]
    [InlineData(60 * 5, "5 minutes ago")]
    [InlineData(60, "1 minute ago")]
    [InlineData(60 * 60 * 2, "2 hours ago")]
    [InlineData(60 * 60 * 24 * 6, "6 days ago")]
    [InlineData(60 * 60 * 24, "1 day ago")]
    public void Ago_says_how_long_ago_in_the_largest_whole_unit(double seconds, string expected)
    {
        Assert.Equal(expected, PlatformOverviewRules.Ago(Now.AddSeconds(-seconds), Now));
    }

    private static PlatformOverviewTenant Tenant(TenantState state, DateTime? created = null) =>
        new(Guid.NewGuid(), "Lindale School Authority", "School staffing", state, 15, created ?? Now.AddDays(-30));
}
