using System.Security.Claims;
using Staffora.Services.Authorization;

namespace Staffora.Test.Unit.Authorization;

public class ImpersonationClaimsTests
{
    private static readonly Guid owner = Guid.NewGuid();
    private static readonly Guid target = Guid.NewGuid();
    private static readonly Guid session = Guid.NewGuid();

    [Fact]
    public void An_impersonation_is_read_only_when_both_claims_parse()
    {
        Assert.Equal(new ImpersonationClaim(target, session), ImpersonationClaims.Read(Impersonating()));
        Assert.Null(ImpersonationClaims.Read(Cookie(AppRoles.Admin)));
        Assert.Null(ImpersonationClaims.Read(Cookie(new Claim(ImpersonationClaims.TargetUserId, target.ToString()))));
        Assert.Null(ImpersonationClaims.Read(Cookie(new Claim(ImpersonationClaims.TargetUserId, target.ToString()), new Claim(ImpersonationClaims.SessionId, "not a row"))));

        Assert.True(ImpersonationClaims.IsPresent(Cookie(new Claim(ImpersonationClaims.SessionId, session.ToString()))));
        Assert.False(ImpersonationClaims.IsPresent(Cookie(AppRoles.Admin)));
    }

    [Fact]
    public void A_reissue_keeps_the_users_own_claims_and_replaces_roles_and_impersonation()
    {
        var signedIn = Cookie(AppRoles.Admin);

        var started = ImpersonationClaims.Reissue(signedIn, ["Staff", "Coordinator"], new ImpersonationClaim(target, session));
        Assert.Equal(["Staff", "Coordinator"], Roles(started));
        Assert.Equal(new ImpersonationClaim(target, session), ImpersonationClaims.Read(started));
        Assert.Equal((owner.ToString(), "owner@example.com", "stamp"), (started.FindFirstValue(ClaimTypes.NameIdentifier), started.FindFirstValue(ClaimTypes.Name), started.FindFirstValue("AspNet.Identity.SecurityStamp")));
        Assert.Equal("Identity.Application", started.Identity!.AuthenticationType);

        var ended = ImpersonationClaims.Reissue(started, [AppRoles.Admin], impersonation: null);
        Assert.Equal([AppRoles.Admin], Roles(ended));
        Assert.False(ImpersonationClaims.IsPresent(ended));
        Assert.Equal(signedIn.Claims.Select(claim => (claim.Type, claim.Value)).Order(), ended.Claims.Select(claim => (claim.Type, claim.Value)).Order());
    }

    [Fact]
    public void The_security_stamp_refresh_keeps_the_impersonation_and_changes_nothing_without_one()
    {
        var refreshed = Cookie(AppRoles.Admin);
        ImpersonationClaims.CarryOver(Impersonating(), refreshed);
        Assert.Equal(["Staff"], Roles(refreshed));
        Assert.Equal(new ImpersonationClaim(target, session), ImpersonationClaims.Read(refreshed));

        var notImpersonating = Cookie(AppRoles.Admin);
        ImpersonationClaims.CarryOver(Cookie(AppRoles.Admin), notImpersonating);
        Assert.Equal([AppRoles.Admin], Roles(notImpersonating));
        Assert.False(ImpersonationClaims.IsPresent(notImpersonating));
    }

    [Fact]
    public void The_console_opens_only_to_a_platform_owner_without_an_impersonation_and_tenant_pages_to_everyone_else()
    {
        var owner = PlatformOwnerCookie();
        var impersonating = Impersonating();
        var tenantUser = Cookie("Staff");
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Equal((true, false), (SessionPolicies.OpensPlatformConsole(owner), SessionPolicies.OpensTenantPages(owner)));
        Assert.Equal((false, true), (SessionPolicies.OpensPlatformConsole(impersonating), SessionPolicies.OpensTenantPages(impersonating)));
        Assert.Equal((false, true), (SessionPolicies.OpensPlatformConsole(tenantUser), SessionPolicies.OpensTenantPages(tenantUser)));
        Assert.Equal((false, false), (SessionPolicies.OpensPlatformConsole(anonymous), SessionPolicies.OpensTenantPages(anonymous)));

        var both = ImpersonationClaims.Reissue(owner, [AppRoles.Admin], new ImpersonationClaim(target, session));
        Assert.False(SessionPolicies.OpensPlatformConsole(both));
    }

    [Fact]
    public void An_Admin_outside_the_platform_tenant_is_a_tenant_user_for_the_page_policies()
    {
        var tenantAdmin = Cookie(AppRoles.Admin);
        var platformStaff = Cookie(new Claim(SessionPolicies.PlatformTenantClaim, "true"));

        Assert.Equal((false, true), (SessionPolicies.OpensPlatformConsole(tenantAdmin), SessionPolicies.OpensTenantPages(tenantAdmin)));
        Assert.Equal((false, true), (SessionPolicies.OpensPlatformConsole(platformStaff), SessionPolicies.OpensTenantPages(platformStaff)));
    }

    private static ClaimsPrincipal PlatformOwnerCookie() =>
        Cookie(new Claim(ClaimTypes.Role, AppRoles.Admin), new Claim(SessionPolicies.PlatformTenantClaim, "true"));

    private static ClaimsPrincipal Cookie(params string[] roles) =>
        Cookie([.. roles.Select(role => new Claim(ClaimTypes.Role, role))]);

    private static ClaimsPrincipal Cookie(params Claim[] extra) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, owner.ToString()),
                new Claim(ClaimTypes.Name, "owner@example.com"),
                new Claim("AspNet.Identity.SecurityStamp", "stamp"),
                .. extra,
            ],
            "Identity.Application"));

    private static ClaimsPrincipal Impersonating() =>
        ImpersonationClaims.Reissue(PlatformOwnerCookie(), ["Staff"], new ImpersonationClaim(target, session));

    private static List<string> Roles(ClaimsPrincipal principal) => [.. principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value)];
}
