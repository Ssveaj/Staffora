using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Staffora.Blazor.Helper;
using Staffora.Services.Authorization;

namespace Staffora.Blazor.Components.Account;

internal static class ImpersonationEndpoints
{
    public const string TargetField = "targetUserId";

    public static IEndpointConventionBuilder MapImpersonationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/Account/Impersonation").RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Impersonation);

        group.MapPost("/Start", async (HttpContext context, [FromServices] IAntiforgery antiforgery, [FromServices] ImpersonationService impersonation) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Guid.TryParse(form[TargetField], out var targetUserId))
            {
                return Results.BadRequest();
            }

            var cookie = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (cookie.Principal is null)
            {
                return Results.Challenge();
            }

            var started = await impersonation.StartAsync(cookie.Principal, targetUserId, context.RequestAborted);
            if (!started.IsSuccess)
            {
                return Results.Forbid();
            }

            try
            {
                await context.SignInAsync(IdentityConstants.ApplicationScheme, started.Value!, cookie.Properties);
            }
            catch
            {
                await impersonation.AbandonStartAsync(cookie.Principal, started.Value!, CancellationToken.None);
                throw;
            }

            return Results.LocalRedirect("~/");
        });

        group.MapPost("/End", async (HttpContext context, [FromServices] IAntiforgery antiforgery, [FromServices] ImpersonationService impersonation) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }

            var cookie = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (cookie.Principal is null)
            {
                return Results.Challenge();
            }

            var ended = await impersonation.EndAsync(cookie.Principal, context.RequestAborted);
            if (!ended.IsSuccess)
            {
                return Results.Forbid();
            }

            await context.SignInAsync(IdentityConstants.ApplicationScheme, ended.Value!, cookie.Properties);
            return Results.LocalRedirect("~/superadmin/overview");
        });

        return group;
    }
}
