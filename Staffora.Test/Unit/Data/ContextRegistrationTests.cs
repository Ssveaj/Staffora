using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Execution;
using Staffora.Data;

namespace Staffora.Test.Unit.Data;

public class ContextRegistrationTests
{
    [Fact]
    public async Task The_factory_is_one_singleton_and_the_injected_context_is_a_system_context_per_scope()
    {
        await using var provider = Register();
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        var injected = first.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<IApplicationDbContextFactory<ApplicationDbContext>>(),
            second.ServiceProvider.GetRequiredService<IApplicationDbContextFactory<ApplicationDbContext>>());
        Assert.Same(injected, first.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        Assert.NotSame(injected, second.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        Assert.Null(injected.RuntimeContext);
        Assert.False(injected.IsInSystemScope);
    }

    [Fact]
    public async Task A_system_context_takes_its_scope_from_a_lease_only_and_a_runtime_context_keeps_its_runtime()
    {
        await using var provider = Register();
        var factory = provider.GetRequiredService<IApplicationDbContextFactory<ApplicationDbContext>>();
        var runtime = new RuntimeExecutionContext(Guid.NewGuid(), Guid.NewGuid());

        await using var system = factory.CreateSystemDbContext();
        await using var tenant = factory.CreateDbContext(runtime);

        Assert.False(system.IsInSystemScope);
        using (system.BeginSystemScope("unit test: platform read"))
        {
            Assert.True(system.IsInSystemScope);
            Assert.False(tenant.IsInSystemScope);
        }

        Assert.False(system.IsInSystemScope);
        Assert.Same(runtime, tenant.RuntimeContext);
        Assert.Equal(runtime.TenantId, tenant.CurrentTenantId);
        Assert.Same(provider.GetRequiredService<TimeProvider>(), tenant.Clock);
    }

    private static ServiceProvider Register()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeClock(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero)));
        services.AddApplicationDbContexts(ModelUnderTest.UnopenedConnection);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
