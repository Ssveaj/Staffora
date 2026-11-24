using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Staffora.Blazor.Helper;
using Staffora.E2ESetup;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.E2E;

[Trait("Category", "E2E")]
[Collection(E2EDatabaseCollection.Name)]
public sealed class StartupSeedGateTests : IDisposable
{
    private static readonly Dictionary<string, string> skipSeeding = new() { [StartupSeeding.SkipSetting] = "1" };

    private readonly string statePath = E2EDatabase.NewStatePath();

    public void Dispose() => E2EDatabase.Remove(this.statePath);

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    public void Only_1_or_true_switches_startup_seeding_off(string? value, bool skipped)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [StartupSeeding.SkipSetting] = value })
            .Build();

        Assert.Equal(skipped, StartupSeeding.IsRequested(configuration));
        Assert.Equal(skipped, StartupSeeding.IsSkipped(configuration, new HostingEnvironment { EnvironmentName = Environments.Development }));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public void The_setting_is_ignored_outside_development(string environment)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [StartupSeeding.SkipSetting] = "1" })
            .Build();

        Assert.True(StartupSeeding.IsRequested(configuration));
        Assert.False(StartupSeeding.IsSkipped(configuration, new HostingEnvironment { EnvironmentName = environment }));
    }

    [Fact]
    public async Task Starting_the_app_after_prep_leaves_the_tenant_count_unchanged()
    {
        var state = await E2EDatabase.PrepareAsync(this.statePath);
        var tenants = await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Tenants");
        var users = await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM AspNetUsers");

        await using (AppHost.Start(E2EDatabase.ConnectionString, "Development", skipSeeding))
        {
        }

        Assert.Equal(tenants, await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Tenants"));
        Assert.Equal(users, await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM AspNetUsers"));

        await using var services = PrepServices.Build(E2EDatabase.ConnectionString, TimeProvider.System);
        await PersonaVerifier.VerifyAsync(services, Personas.PlatformOwner, state.Persona(Personas.PlatformOwner.Key).Password, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Without_the_gate_a_development_start_adds_its_demo_tenants()
    {
        await E2EDatabase.PrepareAsync(this.statePath);
        var tenants = await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Tenants");

        await using (AppHost.Start(E2EDatabase.ConnectionString, "Development"))
        {
        }

        Assert.True(await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Tenants") > tenants);

        await E2EDatabase.PrepareAsync(this.statePath);
    }
}
