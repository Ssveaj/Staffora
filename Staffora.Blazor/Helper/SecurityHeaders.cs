using System.Security.Cryptography;

namespace Staffora.Blazor.Helper;

public static class SecurityHeaders
{
    public const string ContentSecurityPolicyHeader = "Content-Security-Policy";
    public const string ContentTypeOptionsHeader = "X-Content-Type-Options";
    public const string ReferrerPolicyHeader = "Referrer-Policy";

    private static readonly object nonceKey = new();

    public static string NonceOf(HttpContext context)
    {
        if (context.Items.TryGetValue(nonceKey, out var existing) && existing is string nonce)
        {
            return nonce;
        }

        nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        context.Items[nonceKey] = nonce;
        return nonce;
    }

    public static string ContentSecurityPolicy(string nonce) => string.Join("; ",
        "default-src 'self'",
        $"script-src 'self' 'nonce-{nonce}'",
        $"script-src-elem 'self' 'nonce-{nonce}'",
        "script-src-attr 'unsafe-inline'",
        "style-src 'self' 'unsafe-inline'",
        "img-src 'self' data:",
        "font-src 'self'",
        "connect-src 'self'",
        "object-src 'none'",
        "base-uri 'self'",
        "form-action 'self'",
        "frame-ancestors 'none'");

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var headers = context.Response.Headers;
        headers[ContentTypeOptionsHeader] = "nosniff";
        headers[ReferrerPolicyHeader] = "same-origin";
        headers[ContentSecurityPolicyHeader] = ContentSecurityPolicy(NonceOf(context));
        await next(context);
    });
}
