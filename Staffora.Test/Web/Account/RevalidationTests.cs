using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Blazor.Components.Account;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;

namespace Staffora.Test.Web.Account;

public class RevalidationTests
{
    [Fact]
    public async Task A_revalidation_that_throws_signs_the_circuit_out_and_clears_its_session()
    {
        var runtime = new RuntimeExecutionContext();
        runtime.ApplySession(new RuntimeSession(Guid.NewGuid(), IsSuperAdmin: true, Guid.NewGuid(), IsInPlatformTenant: true, Guid.NewGuid(), Guid.NewGuid()));
        await using var circuit = CircuitServices(runtime);
        var session = circuit.GetRequiredService<UserSessionService>();
        using var revalidation = new IdentityRevalidatingAuthenticationStateProvider(NullLoggerFactory.Instance, new FailingScopes(), circuit);
        var cookie = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test"));

        var stillSignedIn = await revalidation.RevalidateAsync(cookie, TestContext.Current.CancellationToken);

        Assert.False(stillSignedIn);
        Assert.Equal(RuntimeSnapshot.Empty, runtime.Current);
        Assert.True(session.IsReady);
        Assert.Equal(Guid.Empty, session.UserId);
    }

    private static ServiceProvider CircuitServices(IRuntimeExecutionContext runtime)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(runtime);
        services.AddSingleton<IApplicationDbContextFactory<ApplicationDbContext>>(new UnreachableDbContextFactory("Revalidation"));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuthenticationStateProvider, Anonymous>();
        services.AddScoped<PermissionService>();
        services.AddScoped<ImpersonationAuthorizer>();
        services.AddScoped<ImpersonationAuditWriter>();
        services.AddScoped<UserSessionService>();
        return services.BuildServiceProvider();
    }

    private sealed class FailingScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("The revalidation could not read.");
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal()));
    }
}
