using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class AppWiringTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public AppWiringTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task The_app_registers_one_dialog_route_registry_over_its_own_dialogs()
    {
        await using var app = AppHost.Start(this.database);

        var registered = app.Services.GetRequiredService<DialogRouteRegistry>();
        var expected = DialogRouteRegistry.FromAssemblies(typeof(Program).Assembly);
        Assert.Equal(expected.Routes.Select(route => route.Token).Order(StringComparer.Ordinal), registered.Routes.Select(route => route.Token).Order(StringComparer.Ordinal));
        Assert.All(registered.Routes, route => Assert.Equal(expected.Find(route.Token)!.Opener, route.Opener));

        using var circuit = app.Services.CreateScope();
        Assert.Same(registered, circuit.ServiceProvider.GetRequiredService<DialogRouteRegistry>());
    }

    [Fact]
    public async Task The_test_routes_answer_a_caller_on_this_machine_only()
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, Xunit.TestContext.Current.CancellationToken);
        await using var app = AppHost.Start(this.database);

        foreach (var (address, signsIn) in new[] { ("203.0.113.7", false), ("10.0.0.5", false), ("::ffff:192.0.2.1", false), ("127.0.0.1", true), ("::1", true) })
        {
            var context = await app.Server.SendAsync(request =>
            {
                request.Request.Method = HttpMethods.Post;
                request.Request.Scheme = "https";
                request.Request.Path = $"/test/sign-in/{scenario.A.OwnerId}";
                request.Connection.RemoteIpAddress = IPAddress.Parse(address);
            }, Xunit.TestContext.Current.CancellationToken);

            Assert.Equal(signsIn ? StatusCodes.Status200OK : StatusCodes.Status404NotFound, context.Response.StatusCode);
            Assert.Equal(signsIn, context.Response.Headers.SetCookie.Count > 0);
        }
    }

    [Theory]
    [InlineData("http://localhost:5281", true)]
    [InlineData("http://127.0.0.1:5281", true)]
    [InlineData("http://[::1]:5281", true)]
    [InlineData("http://0.0.0.0:5281", false)]
    [InlineData("http://[::]:5281", false)]
    [InlineData("http://+:5281", false)]
    [InlineData("http://192.0.2.10:5281", false)]
    public void The_browser_host_starts_only_on_the_loopback_address(string address, bool loopback)
    {
        if (loopback)
        {
            AppHost.EnsureLoopback([address]);
        }
        else
        {
            Assert.Contains("loopback address only", Assert.Throws<InvalidOperationException>(() => AppHost.EnsureLoopback(["http://localhost:5281", address])).Message, StringComparison.Ordinal);
        }

        Assert.Throws<InvalidOperationException>(() => AppHost.EnsureLoopback([]));
    }
}
