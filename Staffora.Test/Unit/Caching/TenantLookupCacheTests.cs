using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Caching;
using Staffora.Test.Unit.Data;

namespace Staffora.Test.Unit.Caching;

public class TenantLookupCacheTests
{
    private static readonly Guid tenantA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid tenantB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");

    [Fact]
    public async Task A_second_read_of_an_entry_loads_nothing_and_gets_the_same_snapshot()
    {
        var store = new Store();
        store.Set(tenantA, "Team");
        using var cache = NewCache();
        var catalogue = store.Catalogue();

        var first = await cache.GetAsync(catalogue, tenantA, TestContext.Current.CancellationToken);
        var second = await cache.GetAsync(catalogue, tenantA, TestContext.Current.CancellationToken);

        Assert.Equal(["Team"], first.Select(row => row.Name));
        Assert.Same(first, second);
        Assert.Equal(1, store.Loads);
    }

    [Fact]
    public async Task Entries_are_kept_per_tenant_and_per_catalogue_and_an_invalidation_drops_only_its_tenants()
    {
        var store = new Store();
        store.Set(tenantA, "Team of A");
        store.Set(tenantB, "Team of B");
        using var cache = NewCache();
        var catalogue = store.Catalogue();
        var namesake = store.Catalogue();
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(["Team of A"], (await cache.GetAsync(catalogue, tenantA, cancellationToken)).Select(row => row.Name));
        Assert.Equal(["Team of B"], (await cache.GetAsync(catalogue, tenantB, cancellationToken)).Select(row => row.Name));
        Assert.Equal(["Team of A"], (await cache.GetAsync(namesake, tenantA, cancellationToken)).Select(row => row.Name));
        Assert.Equal(3, store.Loads);

        store.Set(tenantA, "Teams of A");
        store.Set(tenantB, "Teams of B");
        cache.Invalidate(catalogue, tenantA);

        Assert.Equal(["Teams of A"], (await cache.GetAsync(catalogue, tenantA, cancellationToken)).Select(row => row.Name));
        Assert.Equal(["Team of B"], (await cache.GetAsync(catalogue, tenantB, cancellationToken)).Select(row => row.Name));
        Assert.Equal(["Team of A"], (await cache.GetAsync(namesake, tenantA, cancellationToken)).Select(row => row.Name));
        Assert.Equal(4, store.Loads);
    }

    [Fact]
    public async Task Invalidating_every_tenant_reads_each_tenants_entry_again()
    {
        var store = new Store();
        store.Set(tenantA, "Team of A");
        store.Set(tenantB, "Team of B");
        using var cache = NewCache();
        var catalogue = store.Catalogue();
        var cancellationToken = TestContext.Current.CancellationToken;
        await cache.GetAsync(catalogue, tenantA, cancellationToken);
        await cache.GetAsync(catalogue, tenantB, cancellationToken);

        store.AddGlobal("Employee number");
        cache.InvalidateEveryTenant(catalogue);

        Assert.Equal(["Team of A", "Employee number"], (await cache.GetAsync(catalogue, tenantA, cancellationToken)).Select(row => row.Name));
        Assert.Equal(["Team of B", "Employee number"], (await cache.GetAsync(catalogue, tenantB, cancellationToken)).Select(row => row.Name));
        Assert.Equal(4, store.Loads);
    }

    [Fact]
    public async Task A_read_that_began_before_an_invalidation_never_puts_its_rows_back()
    {
        var store = new Store();
        store.Set(tenantA, "Old name");
        var reading = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        store.HoldFirstLoad(reading, release);
        using var cache = NewCache();
        var catalogue = store.Catalogue();
        var cancellationToken = TestContext.Current.CancellationToken;

        var early = cache.GetAsync(catalogue, tenantA, cancellationToken);
        await reading.Task.WaitAsync(cancellationToken);

        store.Set(tenantA, "New name");
        cache.Invalidate(catalogue, tenantA);
        var late = cache.GetAsync(catalogue, tenantA, cancellationToken);
        release.SetResult();

        Assert.Equal(["Old name"], (await early).Select(row => row.Name));
        Assert.Equal(["New name"], (await late).Select(row => row.Name));
        Assert.Equal(["New name"], (await cache.GetAsync(catalogue, tenantA, cancellationToken)).Select(row => row.Name));
        Assert.Equal(2, store.Loads);
    }

    [Fact]
    public async Task Concurrent_readers_of_an_entry_share_one_load()
    {
        var store = new Store();
        store.Set(tenantA, "Team");
        var reading = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        store.HoldFirstLoad(reading, release);
        using var cache = NewCache();
        var catalogue = store.Catalogue();
        var cancellationToken = TestContext.Current.CancellationToken;

        var readers = Enumerable.Range(0, 5).Select(_ => cache.GetAsync(catalogue, tenantA, cancellationToken)).ToList();
        await reading.Task.WaitAsync(cancellationToken);
        release.SetResult();
        var entries = await Task.WhenAll(readers);

        Assert.All(entries, entry => Assert.Same(entries[0], entry));
        Assert.Equal(1, store.Loads);
    }

    [Fact]
    public async Task A_failed_load_is_not_cached()
    {
        var store = new Store();
        store.Set(tenantA, "Team");
        store.FailNextLoad();
        using var cache = NewCache();
        var catalogue = store.Catalogue();
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<TimeoutException>(() => cache.GetAsync(catalogue, tenantA, cancellationToken));
        Assert.Equal(["Team"], (await cache.GetAsync(catalogue, tenantA, cancellationToken)).Select(row => row.Name));
        Assert.Equal(2, store.Loads);
    }

    [Fact]
    public async Task A_load_that_read_another_tenants_row_fails_and_caches_nothing()
    {
        var store = new Store();
        store.Set(tenantA, "Team of A");
        store.Set(tenantB, "Secret of B");
        using var cache = NewCache();
        var careless = store.Catalogue(ignoreTenant: true);
        var cancellationToken = TestContext.Current.CancellationToken;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(careless, tenantA, cancellationToken));
        Assert.Contains("another tenant", refused.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(careless, tenantA, cancellationToken));
        Assert.Equal(2, store.Loads);
        Assert.Equal(0, cache.EntryCount);
    }

    [Fact]
    public async Task An_entry_is_read_again_once_its_time_to_live_has_passed_on_the_injected_clock()
    {
        var store = new Store();
        store.Set(tenantA, "Team");
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
        using var cache = NewCache(clock);
        var catalogue = store.Catalogue();
        var cancellationToken = TestContext.Current.CancellationToken;
        await cache.GetAsync(catalogue, tenantA, cancellationToken);

        clock.Advance(TenantLookupCache.TimeToLive - TimeSpan.FromSeconds(1));
        await cache.GetAsync(catalogue, tenantA, cancellationToken);
        Assert.Equal(1, store.Loads);

        clock.Advance(TimeSpan.FromSeconds(2));
        await cache.GetAsync(catalogue, tenantA, cancellationToken);
        Assert.Equal(2, store.Loads);
    }

    [Fact]
    public async Task The_cache_holds_no_more_entries_than_its_limit_and_still_answers_every_tenant()
    {
        var store = new Store();
        var tenants = Enumerable.Range(1, 12).Select(index => new Guid(index, 0, 0, new byte[8])).ToList();
        foreach (var tenant in tenants)
        {
            store.Set(tenant, $"Team of {tenant}");
        }

        using var cache = new TenantLookupCache(new UnopenedContexts(), TimeProvider.System, NullLogger<TenantLookupCache>.Instance, entryLimit: 3);
        var catalogue = store.Catalogue();
        foreach (var tenant in tenants)
        {
            Assert.Equal([$"Team of {tenant}"], (await cache.GetAsync(catalogue, tenant, TestContext.Current.CancellationToken)).Select(row => row.Name));
            Assert.True(cache.EntryCount <= 3, $"the cache holds {cache.EntryCount} entries");
        }
    }

    [Fact]
    public async Task An_entry_is_a_read_only_snapshot()
    {
        var store = new Store();
        store.Set(tenantA, "Team");
        using var cache = NewCache();
        var entry = await cache.GetAsync(store.Catalogue(), tenantA, TestContext.Current.CancellationToken);

        Assert.Throws<NotSupportedException>(() => ((IList<Row>)entry).Add(new Row(tenantA, "Injected")));
        Assert.Throws<NotSupportedException>(() => ((IList<Row>)entry)[0] = new Row(tenantA, "Replaced"));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetAsync(store.Catalogue(), Guid.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_shared_load_never_needs_the_context_of_the_reader_that_started_it()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var catalogue = new LookupCatalogue<Row>("held rows", async (_, tenantId, _) =>
        {
            reading.SetResult();
            await release.Task.ConfigureAwait(false);
            return [new Row(tenantId, "Team")];
        });
        using var cache = NewCache();
        var cancellationToken = TestContext.Current.CancellationToken;

        var stalled = new StalledContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(stalled);
        try
        {
            _ = cache.GetAsync(catalogue, tenantA, cancellationToken);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await reading.Task.WaitAsync(cancellationToken);

        var second = cache.GetAsync(catalogue, tenantA, cancellationToken);
        release.SetResult();
        var rows = await second.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        Assert.Equal(["Team"], rows.Select(row => row.Name));
        Assert.Equal(0, stalled.Posted);
    }

    private static TenantLookupCache NewCache(TimeProvider? clock = null) =>
        new(new UnopenedContexts(), clock ?? TimeProvider.System, NullLogger<TenantLookupCache>.Instance);

    private sealed record Row(Guid? TenantId, string Name) : ILookupRow;

    private sealed class Store
    {
        private readonly ConcurrentDictionary<Guid, string> names = new();
        private readonly List<string> global = [];
        private int loads;
        private bool fail;
        private (TaskCompletionSource Reading, TaskCompletionSource Release)? hold;

        public int Loads => this.loads;

        public void Set(Guid tenantId, string name) => this.names[tenantId] = name;

        public void AddGlobal(string name) => this.global.Add(name);

        public void FailNextLoad() => this.fail = true;

        public void HoldFirstLoad(TaskCompletionSource reading, TaskCompletionSource release) => this.hold = (reading, release);

        public LookupCatalogue<Row> Catalogue(bool ignoreTenant = false) => new("test rows", async (_, tenantId, _) =>
        {
            var load = Interlocked.Increment(ref this.loads);
            if (this.fail)
            {
                this.fail = false;
                throw new TimeoutException("The database did not answer.");
            }

            List<Row> rows = ignoreTenant
                ? [.. this.names.OrderBy(pair => pair.Key).Select(pair => new Row(pair.Key, pair.Value))]
                : [.. this.names.Where(pair => pair.Key == tenantId).Select(pair => new Row(pair.Key, pair.Value)), .. this.global.Select(name => new Row(null, name))];
            if (load == 1 && this.hold is { } hold)
            {
                hold.Reading.SetResult();
                await hold.Release.Task;
            }

            return rows;
        });
    }

    private sealed class StalledContext : SynchronizationContext
    {
        private int posted;

        public int Posted => this.posted;

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref this.posted);

        public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException("A stalled circuit runs nothing.");
    }

    private sealed class UnopenedContexts : IApplicationDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext(IRuntimeExecutionContext runtime) => throw new InvalidOperationException("The cache reads on system contexts only.");

        public Task<ApplicationDbContext> CreateDbContextAsync(IRuntimeExecutionContext runtime) => throw new InvalidOperationException("The cache reads on system contexts only.");

        public ApplicationDbContext CreateSystemDbContext() => new(ApplicationDbContextOptions.Create(ModelUnderTest.UnopenedConnection));
    }
}
