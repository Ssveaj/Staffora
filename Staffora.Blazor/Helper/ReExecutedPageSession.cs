using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Staffora.Services.Authorization;

namespace Staffora.Blazor.Helper;

public static class ReExecutedPageSession
{
    public static IApplicationBuilder UseReExecutedPageSession(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (IsReExecuted(context))
        {
            context.Request.Method = HttpMethods.Get;
            var services = context.RequestServices;

            var user = IsRateLimited(context) ? new ClaimsPrincipal(new ClaimsIdentity()) : context.User;
            if (services.GetRequiredService<AuthenticationStateProvider>() is IHostEnvironmentAuthenticationStateProvider authentication)
            {
                authentication.SetAuthenticationState(Task.FromResult(new AuthenticationState(user)));
            }

            context.User = user;

            await services.GetRequiredService<UserSessionService>().InitializeFromClaimsAsync();
        }

        await next(context);
    });

    public static bool IsRateLimited(HttpContext context) =>
        context.Features.Get<IStatusCodeReExecuteFeature>() is not null && context.Response.StatusCode == StatusCodes.Status429TooManyRequests;

    public static bool IsReExecuted(HttpContext context) =>
        context.Features.Get<IStatusCodeReExecuteFeature>() is not null || context.Features.Get<IExceptionHandlerFeature>() is not null;
}
