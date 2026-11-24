using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Layout;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;

namespace Staffora.Test.Web.Layout;

public class SessionGateTests : WebTestContext
{
    private readonly HeldAuthenticationState authentication = new();

    public SessionGateTests()
    {
        Services.AddSingleton<IRuntimeExecutionContext>(new RuntimeExecutionContext());
        Services.AddSingleton<IApplicationDbContextFactory<ApplicationDbContext>>(new UnreachableDbContextFactory("The session gate"));
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<PermissionService>();
        Services.AddScoped<ImpersonationAuthorizer>();
        Services.AddScoped<ImpersonationAuditWriter>();
        Services.AddScoped<UserSessionService>();

        Services.RemoveAll<IAuthorizationService>();
        Services.RemoveAll<IAuthorizationPolicyProvider>();
        Services.AddAuthorizationCore();
        Services.AddSingleton<AuthenticationStateProvider>(this.authentication);
    }

    [Theory]
    [InlineData(typeof(MainLayout))]
    [InlineData(typeof(SuperAdminLayout))]
    public async Task The_layout_renders_only_the_loader_until_the_session_is_ready(Type layout)
    {
        var rendered = Render<CascadingAuthenticationState>(parameters => parameters.AddChildContent(builder =>
        {
            builder.OpenComponent(0, layout);
            builder.AddAttribute(1, nameof(LayoutComponentBase.Body), (RenderFragment)(body => body.AddContent(0, "Page content")));
            builder.CloseComponent();
        }));

        Assert.Contains("Loading your session", rendered.Markup);
        Assert.DoesNotContain("Page content", rendered.Markup);
        Assert.DoesNotContain("data-session=\"ready\"", rendered.Markup);
        Assert.False(Services.GetRequiredService<UserSessionService>().IsReady);

        Assert.Empty(rendered.FindComponents<StateEntityDialogRestoreHost>());

        await Task.Run(this.authentication.Release, Xunit.TestContext.Current.CancellationToken);

        rendered.WaitForAssertion(() =>
        {
            Assert.Contains("Page content", rendered.Markup);
            Assert.Contains("data-session=\"ready\"", rendered.Markup);
            Assert.DoesNotContain("Loading your session", rendered.Markup);
        });

        Assert.Empty(rendered.FindComponents<StateEntityDialogRestoreHost>());
    }

    [Theory]
    [InlineData(typeof(MainLayout))]
    [InlineData(typeof(SuperAdminLayout))]
    public void The_layout_marks_the_page_interactive_once_it_has_rendered_in_the_circuit(Type layout)
    {
        var urlState = Services.GetRequiredService<Staffora.Blazor.Helper.UrlState.UrlStateService>();
        Assert.False(urlState.IsInteractive);

        Render<CascadingAuthenticationState>(parameters => parameters.AddChildContent(builder =>
        {
            builder.OpenComponent(0, layout);
            builder.AddAttribute(1, nameof(LayoutComponentBase.Body), (RenderFragment)(body => body.AddContent(0, "Page content")));
            builder.CloseComponent();
        }));

        Assert.True(urlState.IsInteractive);
    }

    private sealed class HeldAuthenticationState : AuthenticationStateProvider
    {
        private readonly TaskCompletionSource<AuthenticationState> state = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => this.state.Task;

        public void Release() => this.state.SetResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
