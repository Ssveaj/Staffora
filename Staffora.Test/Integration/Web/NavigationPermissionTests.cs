using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Blazor.Layout;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public class NavigationPermissionTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public NavigationPermissionTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_user_who_may_view_users_sees_the_users_link_and_not_the_links_their_role_does_not_open()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        await using (var context = this.database.CreateSystemContext())
        {
            var role = new TenantRole { TenantId = scenario.A.TenantId, Name = "User viewer", State = TenantRoleState.Active, CreatedById = scenario.A.OwnerId, Permissions = [new TenantRolePermission { TenantId = scenario.A.TenantId, Permission = RolePermission.UsersView, CreatedById = scenario.A.OwnerId }] };
            context.Add(role);
            context.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { TenantId = scenario.A.TenantId, UserId = scenario.A.OwnerId, OrganizationUnitId = rootId, TenantRole = role, CreatedById = scenario.A.OwnerId });
            await context.SaveChangesAsync(cancellationToken);
        }

        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(scenario.A.OwnerId));
        await using (host)
        {
            var session = host.Get<UserSessionService>();
            await session.InitializeFromClaimsAsync();

            using var page = new PageContext();
            page.Services.AddSingleton(session);
            page.Services.AddSingleton(host.Get<PermissionService>());
            page.Services.AddSingleton<IRuntimeExecutionContext>(runtime);

            var menu = page.Render<NavMenu>();
            var links = menu.FindAll("nav a").Select(link => link.GetAttribute("href")).ToList();

            Assert.Equal(["/me/overview", "/tenant/management/users"], links);
        }
    }

    private sealed class PageContext : WebTestContext;
}
