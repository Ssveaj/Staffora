using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Staffora.Core.Entities.Identity;
using Staffora.Data;

namespace Staffora.Services.Authorization;

public sealed class ApplicationClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>
{
    private readonly IUserStore<ApplicationUser> userStore;

    public ApplicationClaimsPrincipalFactory(UserManager<ApplicationUser> userManager, RoleManager<ApplicationRole> roleManager, IOptions<IdentityOptions> options, IUserStore<ApplicationUser> userStore) : base(userManager, roleManager, options)
    {
        this.userStore = userStore;
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        if (this.userStore is ApplicationUserStore store && await store.IsInPlatformTenantAsync(user))
        {
            identity.AddClaim(new Claim(SessionPolicies.PlatformTenantClaim, "true"));
        }

        return identity;
    }
}
