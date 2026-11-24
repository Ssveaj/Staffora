using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Staffora.Blazor.Pages.SuperAdmin;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Platform;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;

namespace Staffora.Test.Web.Pages;

public class SuperAdminOverviewPageTests : WebTestContext
{
    private readonly RuntimeExecutionContext runtime = new();

    public SuperAdminOverviewPageTests()
    {
        this.runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        this.runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);
        this.runtime.StartImpersonation(Guid.NewGuid(), Guid.NewGuid());
        this.runtime.MarkInitialized();
    }

    [Fact]
    public void The_page_name_is_the_one_h1_and_Impersonate_is_offered_once_in_the_act_as_a_user_card()
    {
        Host(new DateTimeOffset(2026, 11, 19, 15, 12, 0, TimeSpan.Zero), "Etc/UTC");
        var page = Render<SuperAdminOverview>();

        Assert.Equal("Platform overview", Assert.Single(page.FindAll("h1")).TextContent.Trim());
        Assert.Contains("Thursday 19 November · 15:12 UTC", page.Find(".home-head__eyebrow").TextContent, StringComparison.Ordinal);
        var impersonate = Assert.Single(page.FindAll("button"), button => button.TextContent.Contains("Impersonate", StringComparison.Ordinal));
        Assert.NotNull(impersonate.Closest(".act-card"));
        Assert.Empty(page.FindAll(".material-icons"));
    }

    [Theory]
    [InlineData(8, 0, "Etc/UTC", "Thursday 19 November · 08:00 UTC")]
    [InlineData(20, 0, "Etc/UTC", "Thursday 19 November · 20:00 UTC")]
    [InlineData(23, 30, "Europe/Stockholm", "Friday 20 November · 00:30")]
    public void The_head_shows_the_date_and_time_on_the_platforms_clock_and_no_greeting(int hour, int minute, string zone, string overline)
    {
        Host(new DateTimeOffset(2026, 11, 19, hour, minute, 0, TimeSpan.Zero), zone);

        var page = Render<SuperAdminOverview>();

        Assert.Equal("Platform overview", page.Find("h1").TextContent.Trim());
        Assert.Contains(overline, page.Find(".home-head__eyebrow").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Good ", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void While_impersonating_the_overview_is_refused_with_the_reason_and_reads_nothing()
    {
        Host(new DateTimeOffset(2026, 11, 19, 15, 12, 0, TimeSpan.Zero), "Etc/UTC");
        var page = Render<SuperAdminOverview>();

        page.WaitForAssertion(() => Assert.Contains(PlatformConsoleService.ConsoleOnly, page.Find(".empty-state[role=alert]").TextContent, StringComparison.Ordinal));
        Assert.Empty(page.FindAll(".hero-figures"));
        Assert.Empty(page.FindAll(".ring"));
        Assert.Empty(page.FindAll(".tenant-rows"));
        Assert.Empty(page.FindAll(".state-counts"));
    }

    private void Host(DateTimeOffset now, string zone)
    {
        var clock = new ManualClock(now);
        var platform = Options.Create(new PlatformOptions { TimeZone = zone });
        var factory = new UnreachableDbContextFactory("The platform overview");
        Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
        Services.AddSingleton<IApplicationDbContextFactory<ApplicationDbContext>>(factory);
        Services.AddSingleton<TimeProvider>(clock);
        Services.AddSingleton(platform);
        Services.AddScoped<PermissionService>();
        Services.AddScoped<ImpersonationAuthorizer>();
        Services.AddScoped<ImpersonationAuditWriter>();
        Services.AddScoped<UserSessionService>();

        Services.AddSingleton(new PlatformConsoleService(factory, this.runtime, users: null!, clock, queue: null!, retention: null!, platform, NullLogger<PlatformConsoleService>.Instance));
        Services.AddSingleton(new TenantService(factory, this.runtime, permissionService: null!, workflows: null!, history: null!, settingsEnsure: null!, NullLogger<TenantService>.Instance));
        Services.AddSingleton<AuthenticationStateProvider>(new SignedIn());
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner@example.com")], "Test"))));
    }
}
