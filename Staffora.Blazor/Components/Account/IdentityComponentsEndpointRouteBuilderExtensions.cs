using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Staffora.Core.Entities.Identity;
using Staffora.Services.Authorization;
using System.Security.Claims;

namespace Microsoft.AspNetCore.Routing
{
    internal static class IdentityComponentsEndpointRouteBuilderExtensions
    {
        public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoints);

            var accountGroup = endpoints.MapGroup("/Account");

            var loggerFactory = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var logoutLogger = loggerFactory.CreateLogger("Logout");

            accountGroup.MapPost("/Logout", async (
                ClaimsPrincipal user,
                [FromServices] SignInManager<ApplicationUser> signInManager,
                [FromServices] ImpersonationService impersonation,
                [FromForm] string returnUrl,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    await impersonation.EndOnSignOutAsync(user, cancellationToken);
                }
                catch (Exception exception)
                {
                    logoutLogger.LogError(exception, "Could not close the impersonation audit row of a cookie that signed out.");
                }

                await signInManager.SignOutAsync();
                return TypedResults.LocalRedirect($"~/{returnUrl}");
            });

            return accountGroup;
        }
    }
}
