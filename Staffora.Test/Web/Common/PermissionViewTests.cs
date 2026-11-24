using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Blazor.Components.Common;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;

namespace Staffora.Test.Web.Common;

public class PermissionViewTests : WebTestContext
{
    private readonly RuntimeExecutionContext runtime = new();

    public PermissionViewTests()
    {
        AddAuthorization();
        Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
        Services.AddSingleton<IApplicationDbContextFactory<ApplicationDbContext>>(new UnreachableDbContextFactory("PermissionView"));
        Services.AddScoped<PermissionService>();
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<ImpersonationAuthorizer>();
        Services.AddScoped<ImpersonationAuditWriter>();
        Services.AddScoped<UserSessionService>();
    }

    [Fact]
    public void Shows_only_the_loading_content_until_the_session_has_a_user()
    {
        var view = RenderGate();

        Assert.Equal("Loading", view.Markup.Trim());
    }

    [Fact]
    public void Shows_the_gated_content_to_a_platform_owner_who_is_not_impersonating()
    {
        this.runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        this.runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);

        var view = RenderGate();

        Assert.Equal("Gated", view.Markup.Trim());
    }

    [Fact]
    public void Shows_the_not_authorized_content_to_a_user_who_holds_no_permission()
    {
        this.runtime.SetUser(Guid.NewGuid());

        var view = RenderGate();

        Assert.Equal("Denied", view.Markup.Trim());
    }

    [Fact]
    public void Gives_an_impersonating_platform_owner_only_the_impersonated_users_permissions()
    {
        this.runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        this.runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);
        this.runtime.StartImpersonation(Guid.NewGuid(), tenantId: null);

        var view = RenderGate();

        Assert.Equal("Denied", view.Markup.Trim());
    }

    [Fact]
    public void Prefers_the_Authorized_fragment_over_the_child_content()
    {
        this.runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        this.runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);

        var view = RenderGate(authorized: Text("Authorized"));

        Assert.Equal("Authorized", view.Markup.Trim());
    }

    [Fact]
    public async Task Withdraws_the_gated_content_when_the_session_signs_out()
    {
        this.runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        this.runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);
        var view = RenderGate();
        var session = Services.GetRequiredService<UserSessionService>();

        await view.InvokeAsync(session.SignOutAsync);

        view.WaitForAssertion(() => Assert.Equal("Loading", view.Markup.Trim()));
    }

    [Fact]
    public async Task A_permission_set_that_expires_while_the_view_checks_it_is_read_again_instead_of_throwing()
    {
        Services.AddSingleton<TimeProvider>(new TickingClock(TimeSpan.FromMinutes(3)));
        this.runtime.SetUser(Guid.NewGuid());
        await Services.GetRequiredService<PermissionService>().LoadPermissionsAsync(Xunit.TestContext.Current.CancellationToken);

        var view = RenderGate();

        Assert.Equal("Denied", view.Markup.Trim());
    }

    private IRenderedComponent<PermissionView> RenderGate(RenderFragment? authorized = null) =>
        Render<PermissionView>(parameters =>
        {
            parameters
                .Add(view => view.Permission, RolePermission.UsersView)
                .Add(view => view.ChildContent, Text("Gated"))
                .Add(view => view.NotAuthorized, Text("Denied"))
                .Add(view => view.Loading, Text("Loading"));
            if (authorized is not null)
            {
                parameters.Add(view => view.Authorized, authorized);
            }
        });

    private static RenderFragment Text(string text) => builder => builder.AddContent(0, text);

    private sealed class TickingClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        private readonly TimeSpan step;

        public TickingClock(TimeSpan step)
        {
            this.step = step;
        }

        public override DateTimeOffset GetUtcNow()
        {
            var now = this.now;
            this.now += this.step;
            return now;
        }
    }
}
