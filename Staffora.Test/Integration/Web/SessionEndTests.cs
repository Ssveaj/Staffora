using System.Net;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Services;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public class SessionEndTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public SessionEndTests(RealDatabase database)
    {
        this.database = database;
    }

    public static TheoryData<string, string> StepsAndPages
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var step in new[] { ApplicationUserWorkflowAttribute.NewPasswordStep, ApplicationUserWorkflowAttribute.DeactivateStep })
            {
                foreach (var page in new[] { "/me/overview", "/me/profile" })
                {
                    data.Add(step, page);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(StepsAndPages))]
    public async Task A_cookie_issued_before_the_step_opens_no_page_and_is_sent_to_sign_in(string step, string page)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        var member = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Member", cancellationToken);

        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(member);
        using (var open = await browser.Http.GetAsync(page, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        }

        await using (var admin = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var stepped = await admin.Get<UserService>().StepUserAsync(member, step);
            Assert.True(stepped.IsSuccess, stepped.Error);
        }

        using var refused = await browser.Http.GetAsync(page, cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Contains("/Account/Login", refused.Headers.Location!.ToString(), StringComparison.Ordinal);

        using var claims = await browser.Http.GetAsync("/test/claims", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, claims.StatusCode);
    }

    public static TheoryData<string> GonePages =>
    [
        "/Account/Manage",
        "/Account/Manage/ChangePassword",
        "/Account/Manage/SetPassword",
        "/Account/Manage/Email",
        "/Account/Manage/Passkeys",
        "/Account/Manage/TwoFactorAuthentication",
        "/Account/Manage/EnableAuthenticator",
        "/Account/Manage/PersonalData",
        "/Account/Manage/DeletePersonalData",
        "/Account/LoginWith2fa",
        "/Account/LoginWithRecoveryCode",
        "/Account/InvalidUser",
    ];

    [Theory]
    [MemberData(nameof(GonePages))]
    public async Task No_self_service_account_page_is_part_of_the_app(string page)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(scenario.A.OwnerId);

        using var response = await browser.Http.GetAsync(page, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/Account/PasskeyCreationOptions")]
    [InlineData("/Account/PasskeyRequestOptions")]
    [InlineData("/Account/Manage/ChangePassword")]
    [InlineData("/Account/Manage/LinkExternalLogin")]
    [InlineData("/Account/Manage/DownloadPersonalData")]
    public async Task No_self_service_account_endpoint_takes_a_post(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(scenario.A.OwnerId);

        using var response = await browser.PostFormAsync(path, new() { ["Input.OldPassword"] = "anything", ["Input.NewPassword"] = "Battery-Staple-8", ["Input.ConfirmPassword"] = "Battery-Staple-8" });
        Assert.False(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}");
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
    }
}
