using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Staffora.Blazor.Helper;

public sealed class WindowLimit
{
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; }
}

public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    public WindowLimit SignIn { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) };

    public WindowLimit Impersonation { get; set; } = new() { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) };
}

public static class RateLimitPolicies
{
    public const string SignIn = "sign-in";
    public const string Impersonation = "impersonation";

    public static IServiceCollection AddRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.Section));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };

            options.AddPolicy(SignIn, context => HttpMethods.IsPost(context.Request.Method)
                ? FixedWindow($"address:{AddressOf(context)}", Limits(context).SignIn)
                : RateLimitPartition.GetNoLimiter("view"));

            options.AddPolicy(Impersonation, context => FixedWindow(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId ? $"user:{userId}" : $"address:{AddressOf(context)}",
                Limits(context).Impersonation));
        });

        return services;
    }

    private static RateLimitOptions Limits(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;

    private static string AddressOf(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? IPAddress.None.ToString();

    private static RateLimitPartition<string> FixedWindow(string key, WindowLimit limit) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit.PermitLimit,
            Window = limit.Window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
}
