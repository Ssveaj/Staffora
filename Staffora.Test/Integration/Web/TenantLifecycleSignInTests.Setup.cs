using System.Net;
using Staffora.Services.Authorization;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Web;

public partial class TenantLifecycleSignInTests
{
    [Fact]
    public async Task A_tenant_in_Setup_signs_in_its_onboarded_system_owner_and_refuses_its_other_users_until_it_is_activated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var onboarded = await OnboardedScenario.CreateAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);
        var member = await CreateUserAsync(app, onboarded.TenantId, "member");

        using (var owner = app.CreateBrowser())
        {
            using var signedIn = await SignInAsync(owner, onboarded.OwnerEmail, onboarded.OwnerPassword);
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
            Assert.NotNull(AuthCookieOf(signedIn));
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(owner, "/test/claims")).Status);
        }

        using var browser = app.CreateBrowser();
        using (var refused = await SignInAsync(browser, member, Password))
        {
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            Assert.Contains(ApplicationSignInManager.TenantNotActiveMessage, await PageTextAsync(refused));
            Assert.Null(AuthCookieOf(refused));
        }

        await MoveAsync(onboarded.Platform, onboarded.TenantId, Staffora.Core.Workflow.WorkflowAttributes.TenantWorkflowAttribute.ActivateStep, cancellationToken);
        using var admitted = await SignInAsync(browser, member, Password);
        Assert.Equal(HttpStatusCode.Redirect, admitted.StatusCode);
        Assert.NotNull(AuthCookieOf(admitted));
    }
}
