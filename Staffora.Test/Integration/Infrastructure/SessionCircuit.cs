using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;

namespace Staffora.Test.Integration.Infrastructure;

public static class SessionCircuit
{
    public static Task<RuntimeExecutionContext> ResolveAsync(RealDatabase database, Guid userId, params string[] roleClaims) =>
        ResolveAsync(database, Cookie(userId, roleClaims));

    public static async Task<RuntimeExecutionContext> ResolveAsync(RealDatabase database, ClaimsPrincipal cookie)
    {
        var (host, runtime) = Open(database, cookie);
        await using (host)
        {
            await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        }

        return runtime;
    }

    public static async Task<RuntimeExecutionContext> ResolveAsIssuedAsync(RealDatabase database, ClaimsPrincipal cookie)
    {
        var (host, runtime) = Open(database, cookie, configure: null, stamp: false);
        await using (host)
        {
            await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        }

        return runtime;
    }

    public static (ServiceHost Host, RuntimeExecutionContext Runtime) Open(RealDatabase database, ClaimsPrincipal cookie, Action<IServiceCollection>? configure = null) =>
        Open(database, cookie, configure, stamp: true);

    private static (ServiceHost Host, RuntimeExecutionContext Runtime) Open(RealDatabase database, ClaimsPrincipal cookie, Action<IServiceCollection>? configure, bool stamp)
    {
        cookie = stamp ? WithCurrentStamp(database, cookie) : cookie;
        var runtime = new RuntimeExecutionContext();
        var host = ServiceHost.Create(database, runtime, services =>
        {
            services.AddSingleton<AuthenticationStateProvider>(new SignedIn(cookie));
            services.AddScoped<UserSessionService>();
            configure?.Invoke(services);
        });
        return (host, runtime);
    }

    public static ClaimsPrincipal Cookie(Guid userId, params string[] roleClaims)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        claims.AddRange(roleClaims.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    public static ClaimsPrincipal WithCurrentStamp(RealDatabase database, ClaimsPrincipal cookie)
    {
        if (cookie.Identity is not ClaimsIdentity { IsAuthenticated: true } identity
            || identity.HasClaim(claim => claim.Type == UserSessionService.SecurityStampClaimType)
            || !Guid.TryParse(identity.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            return cookie;
        }

        using var context = database.CreateSystemContext();
        using var lease = context.BeginSystemScope("test: the current security stamp of a cookie's user");
        var stamp = context.Users.AsNoTracking().Where(user => user.Id == userId).Select(user => user.SecurityStamp).FirstOrDefault();
        if (string.IsNullOrEmpty(stamp))
        {
            return cookie;
        }

        var stamped = identity.Clone();
        stamped.AddClaim(new Claim(UserSessionService.SecurityStampClaimType, stamp));
        return new ClaimsPrincipal(stamped);
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        private readonly ClaimsPrincipal principal;

        public SignedIn(ClaimsPrincipal principal)
        {
            this.principal = principal;
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(this.principal));
    }
}
