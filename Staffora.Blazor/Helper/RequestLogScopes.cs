using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;
using Staffora.Services.Diagnostics;

namespace Staffora.Blazor.Helper;

public static class RequestLogScopes
{
    private const string Category = "Staffora.Blazor.Requests";

    public static IServiceCollection AddRequestLogScopes(this IServiceCollection services)
    {
        services.AddSingleton<IStartupFilter, RequestScopeStartupFilter>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, CircuitLogScopeHandler>());
        return services;
    }

    public static IApplicationBuilder UseRequestLogIdentity(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Features.Get<LogScope>() is { } scope)
        {
            var session = context.RequestServices.GetRequiredService<IRuntimeExecutionContext>().Current;
            scope.Identify(session.IsInitialized
                ? LogIdentity.From(session)
                : await context.RequestServices.GetRequiredService<UserSessionService>().DescribeRequestAsync(context.User, context.RequestAborted));
        }

        await next(context);
    });

    private sealed class RequestScopeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(Category);
            app.Use(async (context, nextMiddleware) =>
            {
                var correlationId = LogScope.CorrelationIdFrom(context.Request.Headers[LogScope.CorrelationHeader].FirstOrDefault());

                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[LogScope.CorrelationHeader] = correlationId;
                    return Task.CompletedTask;
                });

                var scope = new LogScope(correlationId, context.Request.Path.Value);
                context.Features.Set(scope);
                using (logger.BeginScope(scope))
                {
                    await nextMiddleware(context);
                }
            });
            next(app);
        };
    }

    private sealed class CircuitLogScopeHandler : CircuitHandler
    {
        private readonly IRuntimeExecutionContext runtime;
        private readonly NavigationManager navigation;
        private readonly ILogger<CircuitLogScopeHandler> logger;

        public CircuitLogScopeHandler(IRuntimeExecutionContext runtime, NavigationManager navigation, ILogger<CircuitLogScopeHandler> logger)
        {
            this.runtime = runtime;
            this.navigation = navigation;
            this.logger = logger;
        }

        public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) => async context =>
        {
            var session = this.runtime.Current;
            var scope = new LogScope(LogScope.CorrelationIdFrom(null), CurrentPath(), session.IsInitialized ? LogIdentity.From(session) : null, followsTrace: true);
            using (this.logger.BeginScope(scope))
            {
                await next(context);
            }
        };

        private string? CurrentPath()
        {
            try
            {
                return new Uri(this.navigation.Uri).AbsolutePath;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
