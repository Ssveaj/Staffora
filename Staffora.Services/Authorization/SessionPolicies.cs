using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Staffora.Services.Authorization;

public static class SessionPolicies
{
    public const string PlatformConsole = "PlatformConsole";

    public const string TenantPages = "TenantPages";

    public const string PlatformTenantClaim = "staffora:platform-tenant";

    public static bool IsPlatformOwner(ClaimsPrincipal user) =>
        user.IsInRole(AppRoles.Admin) && user.HasClaim(PlatformTenantClaim, "true");

    public static bool OpensPlatformConsole(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated is true && IsPlatformOwner(user) && !ImpersonationClaims.IsPresent(user);

    public static bool OpensTenantPages(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated is true && (ImpersonationClaims.IsPresent(user) || !IsPlatformOwner(user));

    public static AuthorizationBuilder AddSessionPolicies(this AuthorizationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .AddPolicy(PlatformConsole, policy => policy.RequireAuthenticatedUser().RequireAssertion(context => OpensPlatformConsole(context.User)))
            .AddPolicy(TenantPages, policy => policy.RequireAuthenticatedUser().RequireAssertion(context => OpensTenantPages(context.User)));
    }
}
