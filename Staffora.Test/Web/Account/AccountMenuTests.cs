using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Blazor.Components.Account;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;

namespace Staffora.Test.Web.Account;

public class AccountMenuTests : WebTestContext
{
    private readonly RuntimeExecutionContext runtime = new();

    public AccountMenuTests()
    {
        Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
        Services.AddSingleton<IApplicationDbContextFactory<ApplicationDbContext>>(new UnreachableDbContextFactory("The account menu"));
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<PermissionService>();
        Services.AddScoped<ImpersonationAuthorizer>();
        Services.AddScoped<ImpersonationAuditWriter>();
        Services.AddScoped<UserSessionService>();
        Services.AddSingleton<AuthenticationStateProvider>(new SignedIn());
        Services.AddSingleton<AntiforgeryStateProvider>(new FixedAntiforgery());

        ComponentFactories.AddStub<NotificationBell>();
    }

    [Fact]
    public void The_menu_hosts_the_bell_for_a_signed_in_session()
    {
        this.runtime.SetUser(Guid.NewGuid());

        Assert.Single(Render<AccountMenu>().FindComponents<Stub<NotificationBell>>());
    }

    [Fact]
    public void A_platform_owner_on_the_console_gets_no_bell_and_one_impersonating_gets_the_persons()
    {
        this.runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        this.runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);
        Assert.Empty(Render<AccountMenu>().FindComponents<Stub<NotificationBell>>());

        this.runtime.StartImpersonation(Guid.NewGuid(), Guid.NewGuid());
        Assert.Single(Render<AccountMenu>().FindComponents<Stub<NotificationBell>>());
    }

    [Fact]
    public void A_session_without_a_user_gets_no_bell()
    {
        Assert.Empty(Render<AccountMenu>().FindComponents<Stub<NotificationBell>>());
    }

    [Fact]
    public void Logging_out_is_a_form_post_to_the_logout_endpoint_with_an_antiforgery_token()
    {
        this.runtime.SetUser(Guid.NewGuid());

        var menu = Render<AccountMenu>();

        var logout = menu.Find("form[action='Account/Logout']");
        Assert.Equal("post", logout.GetAttribute("method"));
        Assert.Equal(FixedAntiforgery.Token, logout.QuerySelector($"input[name='{FixedAntiforgery.Field}']")?.GetAttribute("value"));
        Assert.NotNull(logout.QuerySelector("input[name='returnUrl']"));
        Assert.Contains("Sign out", logout.QuerySelector("button[type='submit']")?.TextContent);
        Assert.NotNull(menu.Find("a[href='/me/profile']"));
        Assert.Empty(menu.FindAll("a[href^='Account/Manage'], a[href^='/Account/Manage']"));
    }

    [Fact]
    public void The_profile_menu_trigger_has_an_accessible_name()
    {
        this.runtime.SetUser(Guid.NewGuid());

        var menu = Render<AccountMenu>();

        var trigger = menu.Find("details.account-menu__profile > summary");
        Assert.Null(trigger.GetAttribute("aria-label"));
        Assert.Equal("Account menu: ", trigger.QuerySelector(".visually-hidden")?.TextContent);
        Assert.Contains("owner@example.com", trigger.QuerySelector(".account-menu__name")?.TextContent);
        Assert.Equal("true", trigger.QuerySelector(".account-menu__avatar")?.GetAttribute("aria-hidden"));
        Assert.All(trigger.QuerySelectorAll(".material-symbol"), icon => Assert.Equal("true", icon.GetAttribute("aria-hidden")));
    }

    [Fact]
    public void An_impersonation_turns_the_trigger_to_the_warning_tone_and_its_panel_ends_it()
    {
        this.runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        this.runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);
        this.runtime.StartImpersonation(Guid.NewGuid(), Guid.NewGuid());

        var menu = Render<AccountMenu>();

        var trigger = menu.Find("details.account-menu__profile > summary");
        Assert.Contains("account-menu__trigger--impersonating", trigger.ClassList);
        Assert.Equal("true", trigger.GetAttribute("data-impersonating"));
        Assert.Equal("Impersonating", trigger.QuerySelector(".account-menu__context")?.TextContent);
        var end = menu.Find(".account-menu__running-as form[action='Account/Impersonation/End']");
        Assert.Equal("post", end.GetAttribute("method"));
        Assert.Contains("End impersonation", end.QuerySelector("button[type='submit']")?.TextContent);
        Assert.Empty(menu.FindAll("a[href='/me/profile']"));
    }

    [Theory]
    [InlineData("sam.taylor@company-a.example.com", "ST")]
    [InlineData("robin_lee@example.com", "RL")]
    [InlineData("owner@example.com", "OW")]
    [InlineData("a@example.com", "A")]
    [InlineData(null, "?")]
    public void The_avatar_shows_up_to_two_initials_of_the_user_name(string? name, string initials)
    {
        Assert.Equal(initials, AccountMenu.Initials(name));
    }

    [Fact]
    public async Task A_session_change_off_the_dispatcher_re_renders_the_menu_through_it()
    {
        this.runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        this.runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);
        this.runtime.StartImpersonation(Guid.NewGuid(), Guid.NewGuid());
        var menu = Render<AccountMenu>();
        Assert.Contains("Impersonating", menu.Markup);
        Assert.Empty(menu.FindAll("a[href='/me/profile']"));

        var session = Services.GetRequiredService<UserSessionService>();
        await Task.Run(session.SignOutAsync, Xunit.TestContext.Current.CancellationToken);

        menu.WaitForAssertion(() => Assert.DoesNotContain("Impersonating", menu.Markup));
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner@example.com")], "Test"))));
    }

    private sealed class FixedAntiforgery : AntiforgeryStateProvider
    {
        public const string Field = "__RequestVerificationToken";
        public const string Token = "request-token";

        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new(Token, Field);
    }
}
