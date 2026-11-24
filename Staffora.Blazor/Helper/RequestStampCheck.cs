using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticAssets;
using Staffora.Core.Entities.Identity;
using Staffora.Services.Authorization;

namespace Staffora.Blazor.Helper;

public static class RequestStampCheck
{
    private const string OutageItem = "Staffora.RequestStampCheck.Outage";

    public const string LogCategory = "Staffora.Blazor.Helper.RequestStampCheck";

    public static void CheckEveryRequest(this CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var then = options.Events.OnValidatePrincipal;
        options.Events.OnValidatePrincipal = async context =>
        {
            if (IsStaticAsset(context.HttpContext))
            {
                return;
            }

            try
            {
                await ValidateAsync(context);
                if (context.Principal is not null)
                {
                    await then(context);
                }
            }
            catch (Exception exception) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory)
                    .LogError(exception, "The sign-in cookie of a request could not be checked against the database; the request was refused.");
                context.RejectPrincipal();
                context.HttpContext.Items[OutageItem] = true;
            }
        };
    }

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Principal is not { } principal)
        {
            return;
        }

        await using var scope = context.HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        var admitted = signInManager is ApplicationSignInManager application
            ? await application.AdmitsCookieAsync(principal, context.HttpContext.RequestAborted)
            : await signInManager.ValidateSecurityStampAsync(principal) is not null;
        if (!admitted)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
        }
    }

    public static IApplicationBuilder UseRequestStampOutage(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Items.ContainsKey(OutageItem) && context.Features.Get<IStatusCodeReExecuteFeature>() is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        await next(context);
    });

    private static bool IsStaticAsset(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<StaticAssetDescriptor>() is not null;
}
