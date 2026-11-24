using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Services.Coordination;

namespace Staffora.Test.Unit.Coordination;

public class TenantChangeBusTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_change_reaches_its_own_tenants_pages_only_with_its_ids()
    {
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var heard = new List<(string Page, TenantChange Change)>();
        using var first = bus.Subscribe(TenantA, change => Record(heard, "a1", change));
        using var second = bus.Subscribe(TenantA, change => Record(heard, "a2", change));
        using var other = bus.Subscribe(TenantB, change => Record(heard, "b", change));

        var job = Guid.NewGuid();
        var staff = Guid.NewGuid();
        var change = new TenantChange(TenantA, new HashSet<Guid> { job }, new HashSet<Guid> { staff });
        bus.Publish(change);
        await bus.DeliveredAsync().WaitAsync(Ct);

        Assert.Equal(["a1", "a2"], heard.Select(entry => entry.Page).Order());
        Assert.All(heard, entry => Assert.Same(change, entry.Change));
        Assert.DoesNotContain(heard, entry => entry.Page == "b");
    }

    [Fact]
    public async Task An_empty_change_or_one_without_a_tenant_reaches_nobody()
    {
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var heard = new List<(string Page, TenantChange Change)>();
        using var page = bus.Subscribe(TenantA, change => Record(heard, "a", change));
        bus.Publish(new TenantChange(TenantA, new HashSet<Guid>(), new HashSet<Guid>()));
        bus.Publish(new TenantChange(Guid.Empty, new HashSet<Guid> { Guid.NewGuid() }, new HashSet<Guid>()));
        await bus.DeliveredAsync().WaitAsync(Ct);
        Assert.Empty(heard);
        Assert.Equal(0, bus.Mark(TenantA));
        Assert.Throws<ArgumentException>(() => bus.Subscribe(Guid.Empty, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task A_failing_page_reaches_neither_the_publisher_nor_another_page_and_a_disposed_one_hears_nothing()
    {
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var heard = new List<(string Page, TenantChange Change)>();
        using var failing = bus.Subscribe(TenantA, _ => throw new InvalidOperationException("A circuit that is gone."));
        var gone = bus.Subscribe(TenantA, change => Record(heard, "gone", change));
        using var page = bus.Subscribe(TenantA, change => Record(heard, "page", change));
        Assert.Equal(3, bus.SubscriptionCount);

        gone.Dispose();
        gone.Dispose();
        Assert.Equal(2, bus.SubscriptionCount);
        bus.Publish(new TenantChange(TenantA, new HashSet<Guid> { Guid.NewGuid() }, new HashSet<Guid>()));
        await bus.DeliveredAsync().WaitAsync(Ct);
        Assert.Equal(["page"], heard.Select(entry => entry.Page));
    }

    [Fact]
    public async Task A_page_that_blocks_or_throws_as_it_is_told_never_holds_up_the_publisher_nor_keeps_another_page_from_hearing()
    {
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var heard = new List<(string Page, TenantChange Change)>();
        var publisherThread = Environment.CurrentManagedThreadId;
        using var released = new ManualResetEventSlim();
        var slowThread = 0;
        var slowSawRelease = false;

        using var slow = bus.Subscribe(TenantA, _ =>
        {
            slowThread = Environment.CurrentManagedThreadId;
            slowSawRelease = released.Wait(TimeSpan.FromSeconds(10), Ct);
            return Task.CompletedTask;
        });

        using var throwing = bus.Subscribe(TenantA, _ => throw new InvalidOperationException("A circuit that broke as it was told."));
        using var faulting = bus.Subscribe(TenantA, async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("A circuit that broke while it read again.");
        });
        using var page = bus.Subscribe(TenantA, change => Record(heard, "page", change));

        bus.Publish(new TenantChange(TenantA, new HashSet<Guid> { Guid.NewGuid() }, new HashSet<Guid>()));
        var heardWhileBlocked = SpinWait.SpinUntil(() =>
        {
            lock (heard)
            {
                return heard.Count == 1;
            }
        }, TimeSpan.FromSeconds(10));
        released.Set();
        await bus.DeliveredAsync().WaitAsync(Ct);

        Assert.True(heardWhileBlocked);
        Assert.True(slowSawRelease, "The slow page was told on the publisher's thread: Publish waited for it.");
        Assert.NotEqual(publisherThread, slowThread);
        Assert.Equal(["page"], heard.Select(entry => entry.Page));
        Assert.Equal(1, bus.Mark(TenantA));
        Assert.Equal(0, bus.Mark(TenantB));
    }

    private static Task Record(List<(string Page, TenantChange Change)> heard, string page, TenantChange change)
    {
        lock (heard)
        {
            heard.Add((page, change));
        }

        return Task.CompletedTask;
    }
}
