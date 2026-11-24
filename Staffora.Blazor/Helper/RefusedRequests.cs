using Microsoft.AspNetCore.Antiforgery;

namespace Staffora.Blazor.Helper;

public static class RefusedRequests
{
    public const string Category = "Staffora.Blazor.RefusedRequests";

    public static IApplicationBuilder UseRefusedRequests(this IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(Category);
        return app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception exception) when (StatusOf(exception) is { } status && !context.Response.HasStarted)
            {
                logger.LogWarning("Refused {Method} {Path} with {StatusCode}: {Reason}", context.Request.Method, context.Request.Path.Value, status, exception.Message);
                context.Response.StatusCode = status;
                context.Response.ContentLength = null;
                context.Response.ContentType = null;
                return;
            }

            if (context.Response is { HasStarted: false, StatusCode: StatusCodes.Status400BadRequest }
                && context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false })
            {
                context.Response.ContentType = null;
            }
        });
    }

    public static int? StatusOf(Exception exception) => exception switch
    {
        AntiforgeryValidationException => StatusCodes.Status400BadRequest,
        BadHttpRequestException refused => refused.StatusCode,
        _ => null,
    };
}
