using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Blazor.Pages.Me;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public class TenantPageIdentityTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public TenantPageIdentityTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task While_impersonating_the_tenant_overview_shows_the_impersonated_user_and_never_the_platform_owner()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var (ownerName, ownerEmail) = await NameAndEmailAsync(platform.OwnerId, cancellationToken);
        var (targetName, _) = await NameAndEmailAsync(scenario.A.OwnerId, cancellationToken);

        System.Security.Claims.ClaimsPrincipal cookie;
        await using (var endpoint = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var started = await endpoint.Get<ImpersonationService>().StartAsync(SessionCircuit.Cookie(platform.OwnerId, AppRoles.Admin), scenario.A.OwnerId, cancellationToken);
            Assert.True(started.IsSuccess, started.Error);
            cookie = started.Value!;
        }

        var (host, _) = SessionCircuit.Open(this.database, cookie);
        await using (host)
        {
            var session = host.Get<UserSessionService>();
            await session.InitializeFromClaimsAsync();
            Assert.True(session.IsImpersonating);

            using var page = new PageContext();
            page.Services.AddSingleton(session);
            page.Services.AddSingleton(host.Get<RoleHoldingService>());

            var overview = page.Render<Overview>();

            Assert.Contains(targetName, overview.Markup);
            Assert.DoesNotContain(ownerName, overview.Markup);
            Assert.DoesNotContain(ownerEmail, overview.Markup);
        }
    }

    private async Task<(string UserName, string Email)> NameAndEmailAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var user = await context.Users.AsNoTracking().SingleAsync(row => row.Id == userId, cancellationToken);
        return (user.UserName!, user.Email!);
    }

    private sealed class PageContext : WebTestContext;
}
