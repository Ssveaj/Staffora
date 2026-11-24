using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Data;

namespace Staffora.Services.Authorization;

public enum SignInRefusal
{
    None = 0,
    AccountInactive = 1,
    TenantNotActive = 2,
}

public sealed class ApplicationSignInManager : SignInManager<ApplicationUser>
{
    private readonly IUserStore<ApplicationUser> userStore;

    public ApplicationSignInManager(UserManager<ApplicationUser> userManager, IUserStore<ApplicationUser> userStore, IHttpContextAccessor contextAccessor, IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory, IOptions<IdentityOptions> optionsAccessor, ILogger<SignInManager<ApplicationUser>> logger, IAuthenticationSchemeProvider schemes, IUserConfirmation<ApplicationUser> confirmation, TimeProvider clock) : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
        this.userStore = userStore;
        this.clock = clock;
    }

    public ApplicationUser? SignedInUser { get; private set; }

    public SignInRefusal Refusal { get; private set; }

    public const string AccountInactiveMessage = "Error: Your account is not active, so you cannot sign in. Contact your organization's administrator.";

    public const string TenantNotActiveMessage = "Error: Your organization's account is not active, so nobody in it can sign in. Contact the person who manages your organization's account.";

    public static string? MessageFor(SignInRefusal refusal) => refusal switch
    {
        SignInRefusal.AccountInactive => AccountInactiveMessage,
        SignInRefusal.TenantNotActive => TenantNotActiveMessage,
        _ => null,
    };

    private ClaimsPrincipal? refreshing;

    private bool refreshingSignIn;

    private readonly TimeProvider clock;

    private static string? unknownUserHash;

    public override async Task<SignInResult> PasswordSignInAsync(string userName, string password, bool isPersistent, bool lockoutOnFailure)
    {
        var user = await UserManager.FindByNameAsync(userName);
        if (user is null)
        {
            UserManager.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), UnknownUserHash(), password ?? string.Empty);
            return SignInResult.Failed;
        }

        return await PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
    }

    public override async Task<SignInResult> CheckPasswordSignInAsync(ApplicationUser user, string password, bool lockoutOnFailure)
    {
        ArgumentNullException.ThrowIfNull(user);
        Refusal = SignInRefusal.None;
        var refusal = await RefusalOfAsync(user);
        if (refusal is not { Kind: not SignInRefusal.None } named)
        {
            var result = await base.CheckPasswordSignInAsync(user, password, lockoutOnFailure);

            Refusal = SignInRefusal.None;
            return result;
        }

        if (UserManager.SupportsUserLockout && await UserManager.IsLockedOutAsync(user))
        {
            return SignInResult.LockedOut;
        }

        var verified = UserManager.PasswordHasher.VerifyHashedPassword(user, user.PasswordHash ?? UnknownUserHash(), password ?? string.Empty) != PasswordVerificationResult.Failed;
        if (!verified || user.PasswordHash is null)
        {
            if (lockoutOnFailure && UserManager.SupportsUserLockout)
            {
                await UserManager.AccessFailedAsync(user);
                if (await UserManager.IsLockedOutAsync(user))
                {
                    return SignInResult.LockedOut;
                }
            }

            return SignInResult.Failed;
        }

        Logger.LogWarning("Refused sign-in of user {UserId} with the right password: {Reason}", user.Id, named.Reason);
        Refusal = named.Kind;
        return SignInResult.NotAllowed;
    }

    private string UnknownUserHash() =>
        unknownUserHash ??= UserManager.PasswordHasher.HashPassword(new ApplicationUser(), Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    public override Task<bool> IsTwoFactorEnabledAsync(ApplicationUser user) => Task.FromResult(false);

    public override async Task<bool> CanSignInAsync(ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (await RefusalOfAsync(user) is { } refusal)
        {
            Logger.LogWarning("Refused sign-in of user {UserId}: {Reason}", user.Id, refusal.Reason);
            Refusal = refusal.Kind;
            return false;
        }

        return await base.CanSignInAsync(user);
    }

    private async Task<(SignInRefusal Kind, string Reason)?> RefusalOfAsync(ApplicationUser user)
    {
        if (user.IsSystemAccount)
        {
            return (SignInRefusal.None, "a tenant's system account never signs in.");
        }

        var store = this.userStore as ApplicationUserStore;
        var state = store is null ? user.State : await store.FindStoredStateAsync(user);
        if (user.State != UserState.Active || state != UserState.Active)
        {
            return (SignInRefusal.AccountInactive, "the user is not active.");
        }

        var admission = store is null ? new TenantAdmissionFacts(null, false) : await store.FindTenantAdmissionAsync(user);
        return TenantStates.AdmitsUser(admission.TenantState, admission.IsActiveSystemOwner)
            ? null
            : (SignInRefusal.TenantNotActive, $"the user's tenant does not admit them ({admission.TenantState?.ToString() ?? "unknown"}{(admission.TenantState == TenantState.Setup ? ", and they are no system owner of it" : string.Empty)}).");
    }

    public override async Task SignInWithClaimsAsync(ApplicationUser user, AuthenticationProperties? authenticationProperties, IEnumerable<Claim> additionalClaims)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (await RefusalOfAsync(user) is { } refusal)
        {
            Logger.LogWarning("Refused to sign in user {UserId}: {Reason}", user.Id, refusal.Reason);
            throw new InvalidOperationException(user.IsSystemAccount ? "A tenant's system account never signs in." : "The user may not sign in now.");
        }

        await SignInAndRememberAsync(user, authenticationProperties, additionalClaims);
    }

    public override async Task RefreshSignInAsync(ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var current = (await Context.AuthenticateAsync(AuthenticationScheme)).Principal;
        this.refreshing = current is not null && ImpersonationClaims.IsPresent(current) ? current : null;
        this.refreshingSignIn = true;
        try
        {
            await base.RefreshSignInAsync(user);
        }
        finally
        {
            this.refreshing = null;
            this.refreshingSignIn = false;
        }
    }

    public override async Task<ClaimsPrincipal> CreateUserPrincipalAsync(ApplicationUser user)
    {
        var principal = await base.CreateUserPrincipalAsync(user);
        if (this.refreshing is { } current && UserManager.GetUserId(current) == await UserManager.GetUserIdAsync(user))
        {
            ImpersonationClaims.CarryOver(current, principal);
        }

        return principal;
    }

    private async Task SignInAndRememberAsync(ApplicationUser user, AuthenticationProperties? authenticationProperties, IEnumerable<Claim> additionalClaims)
    {
        await base.SignInWithClaimsAsync(user, authenticationProperties, additionalClaims);
        SignedInUser = user;
        if (!this.refreshingSignIn)
        {
            await StampSignInAsync(user);
        }
    }

    private async Task StampSignInAsync(ApplicationUser user)
    {
        try
        {
            user.LastSignedIn = this.clock.GetUtcNow().UtcDateTime;
            var stamped = await UserManager.UpdateAsync(user);
            if (!stamped.Succeeded)
            {
                Logger.LogWarning("Could not record the sign-in of user {UserId}: {Errors}", user.Id, string.Join("; ", stamped.Errors.Select(error => error.Description)));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(exception, "Could not record the sign-in of user {UserId}.", user.Id);
        }
    }

    public async Task<bool> AdmitsCookieAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (this.userStore is not ApplicationUserStore store)
        {
            return await ValidateSecurityStampAsync(principal) is not null;
        }

        if (!Guid.TryParse(UserManager.GetUserId(principal), out var userId))
        {
            return false;
        }

        var facts = await store.FindCookieFactsAsync(userId, cancellationToken);
        var refusal = facts switch
        {
            null => "the user is not stored.",
            { IsSystemAccount: true } => "a tenant's system account never signs in.",
            { State: not UserState.Active } => "the user is not active.",
            _ when !TenantStates.AdmitsUser(facts.TenantState, facts.IsActiveSystemOwner) => $"the user's tenant does not admit them ({facts.TenantState?.ToString() ?? "unknown"}).",
            _ => null,
        };
        if (refusal is not null)
        {
            Logger.LogWarning("Refused the cookie of user {UserId}: {Reason}", userId, refusal);
            return false;
        }

        var carried = principal.FindFirstValue(Options.ClaimsIdentity.SecurityStampClaimType);
        return !string.IsNullOrEmpty(carried) && string.Equals(carried, facts!.SecurityStamp, StringComparison.Ordinal);
    }

    public override async Task<bool> ValidateSecurityStampAsync(ApplicationUser? user, string? securityStamp)
    {
        if (user is not null && await RefusalOfAsync(user) is { } refusal)
        {
            Logger.LogWarning("Refused the cookie of user {UserId}: {Reason}", user.Id, refusal.Reason);
            return false;
        }

        return await base.ValidateSecurityStampAsync(user, securityStamp);
    }
}
