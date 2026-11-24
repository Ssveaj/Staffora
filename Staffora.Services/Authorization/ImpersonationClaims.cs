using System.Security.Claims;

namespace Staffora.Services.Authorization;

public sealed record ImpersonationClaim(Guid TargetUserId, Guid SessionId);

public static class ImpersonationClaims
{
    public const string TargetUserId = "staffora:impersonation:target";

    public const string SessionId = "staffora:impersonation:session";

    public static ImpersonationClaim? Read(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Guid.TryParse(principal.FindFirst(TargetUserId)?.Value, out var target) && Guid.TryParse(principal.FindFirst(SessionId)?.Value, out var session)
            ? new ImpersonationClaim(target, session)
            : null;
    }

    public static bool IsPresent(ClaimsPrincipal principal) =>
        principal.HasClaim(claim => claim.Type is TargetUserId or SessionId);

    public static ClaimsPrincipal Reissue(ClaimsPrincipal current, IEnumerable<string> roles, ImpersonationClaim? impersonation)
    {
        ArgumentNullException.ThrowIfNull(current);
        var identity = current.Identity as ClaimsIdentity;
        var roleType = identity?.RoleClaimType ?? ClaimTypes.Role;
        var kept = current.Claims.Where(claim => claim.Type != roleType && claim.Type != ClaimTypes.Role && claim.Type is not (TargetUserId or SessionId));
        var reissued = new ClaimsIdentity(kept, identity?.AuthenticationType, identity?.NameClaimType ?? ClaimTypes.Name, roleType);
        foreach (var role in roles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            reissued.AddClaim(new Claim(roleType, role));
        }

        if (impersonation is not null)
        {
            reissued.AddClaim(new Claim(TargetUserId, impersonation.TargetUserId.ToString()));
            reissued.AddClaim(new Claim(SessionId, impersonation.SessionId.ToString()));
        }

        return new ClaimsPrincipal(reissued);
    }

    public static void CarryOver(ClaimsPrincipal? current, ClaimsPrincipal? fresh)
    {
        if (current is null || fresh?.Identity is not ClaimsIdentity freshIdentity || !IsPresent(current))
        {
            return;
        }

        foreach (var role in freshIdentity.FindAll(freshIdentity.RoleClaimType).ToList())
        {
            freshIdentity.RemoveClaim(role);
        }

        var currentRoleType = (current.Identity as ClaimsIdentity)?.RoleClaimType ?? ClaimTypes.Role;
        foreach (var carried in current.Claims.Where(claim => claim.Type == currentRoleType || claim.Type is TargetUserId or SessionId))
        {
            freshIdentity.AddClaim(new Claim(carried.Type == currentRoleType ? freshIdentity.RoleClaimType : carried.Type, carried.Value));
        }
    }
}
