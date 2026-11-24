using System.Net;
using Microsoft.Data.SqlClient;
using Staffora.Blazor.Helper;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.E2E;

[Trait("Category", "E2E")]
[Collection(E2EDatabaseCollection.Name)]
public sealed class HealthEndpointTests : IDisposable
{
    private static readonly Dictionary<string, string> skipSeeding = new() { [StartupSeeding.SkipSetting] = "1" };

    private readonly string statePath = E2EDatabase.NewStatePath();

    public void Dispose() => E2EDatabase.Remove(this.statePath);

    [Fact]
    public async Task Health_is_healthy_with_the_database_reachable_and_needs_no_sign_in()
    {
        await E2EDatabase.PrepareAsync(this.statePath);
        await using var app = AppHost.Start(E2EDatabase.ConnectionString, "Development", skipSeeding);
        using var browser = app.CreateBrowser();

        using var response = await browser.Http.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Health_is_unhealthy_without_the_database_and_says_nothing_more()
    {
        await E2EDatabase.PrepareAsync(this.statePath);
        await using var app = AppHost.Start(E2EDatabase.ConnectionString, "Development", skipSeeding);
        using var browser = app.CreateBrowser();

        await SetOnlineAsync(online: false);
        try
        {
            using var response = await browser.Http.GetAsync("/health", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await SetOnlineAsync(online: true);
        }

        using var recovered = await browser.Http.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    private static async Task SetOnlineAsync(bool online)
    {
        var builder = new SqlConnectionStringBuilder(E2EDatabase.ConnectionString);
        var database = builder.InitialCatalog.Replace("]", "]]", StringComparison.Ordinal);
        builder.InitialCatalog = "master";

        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new SqlCommand($"ALTER DATABASE [{database}] SET {(online ? "ONLINE" : "OFFLINE WITH ROLLBACK IMMEDIATE")}", connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        SqlConnection.ClearAllPools();
    }
}
