using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Staffora.Core.Entities.Identity;
using Staffora.Services.Authorization;
using System.Security.Claims;

namespace Staffora.Blazor.Components.Account
{
    internal sealed class IdentityRevalidatingAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
    {
        private readonly ILogger logger;

        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        private readonly IServiceScopeFactory scopeFactory;
        private readonly IServiceProvider circuitServices;

        public IdentityRevalidatingAuthenticationStateProvider(ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory, IServiceProvider circuitServices) : base(loggerFactory)
        {
            this.scopeFactory = scopeFactory;
            this.circuitServices = circuitServices;
            this.logger = loggerFactory.CreateLogger<IdentityRevalidatingAuthenticationStateProvider>();
        }

        protected override TimeSpan RevalidationInterval => Interval;

        protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
            RevalidateAsync(authenticationState.User, cancellationToken);

        internal async Task<bool> RevalidateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
        {
            var session = this.circuitServices.GetRequiredService<UserSessionService>();
            try
            {
                var stampValid = await IsStillValidAsync(principal);
                if (await session.RevalidateAsync(principal, cancellationToken) && stampValid)
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                this.logger.LogError(exception, "Revalidating the circuit's session failed; the circuit is signed out.");
            }

            await session.SignOutAsync();
            return false;
        }

        internal async Task<bool> IsStillValidAsync(ClaimsPrincipal principal)
        {
            await using var scope = this.scopeFactory.CreateAsyncScope();
            var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
            return await signInManager.ValidateSecurityStampAsync(principal) is not null;
        }
    }
}
