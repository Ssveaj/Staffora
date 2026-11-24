using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Test.Unit.Data;

namespace Staffora.Test.Unit.Execution;

public class RuntimeSnapshotTests
{
    private static readonly Guid owner = Guid.NewGuid();
    private static readonly Guid platformTenant = Guid.NewGuid();
    private static readonly Guid target = Guid.NewGuid();
    private static readonly Guid targetTenant = Guid.NewGuid();

    private static readonly RuntimeSession impersonating = new(owner, IsSuperAdmin: true, platformTenant, IsInPlatformTenant: true, target, targetTenant);

    [Fact]
    public void Platform_reach_is_never_granted_from_two_sessions_read_while_another_thread_applies_them()
    {
        var runtime = new RuntimeExecutionContext();
        runtime.ApplySession(impersonating);
        Assert.False(runtime.HasPlatformReach());

        var granted = WhileSwapping(() => runtime.ApplySession(impersonating), runtime.Reset, iterations: 50_000_000, () => runtime.HasPlatformReach());

        Assert.Equal(0, granted);
    }

    [Fact]
    public void A_save_stamps_the_creator_and_the_impersonator_of_one_session_while_another_thread_applies_another()
    {
        var ownerInTheTenant = new RuntimeSession(owner, IsSuperAdmin: false, targetTenant, IsInPlatformTenant: false, ImpersonatedUserId: null, ImpersonatedTenantId: null);
        var runtime = new RuntimeExecutionContext();
        runtime.ApplySession(impersonating);
        using var context = CreateContext(runtime);

        var mixed = WhileSwapping(() => runtime.ApplySession(impersonating), () => runtime.ApplySession(ownerInTheTenant), iterations: 2_000, () =>
        {
            var role = new TenantRole { Name = "Stamped", TenantId = targetTenant };
            context.TenantRoles.Add(role);
            context.SaveChanges();
            context.ChangeTracker.Clear();
            var stamp = (role.CreatedById, role.ImpersonatedById);
            return stamp != (target, owner) && stamp != (owner, null);
        });

        Assert.Equal(0, mixed);
    }

    private static int WhileSwapping(Action first, Action second, int iterations, Func<bool> read)
    {
        using var done = new CancellationTokenSource();
        var swaps = 0L;
        var writer = Task.Factory.StartNew(() =>
        {
            while (!done.IsCancellationRequested)
            {
                first();
                second();
                Interlocked.Increment(ref swaps);
            }
        }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var hits = 0;
        long swapsBefore;
        try
        {
            SpinWait.SpinUntil(() => Interlocked.Read(ref swaps) > 0 || writer.IsCompleted);
            swapsBefore = Interlocked.Read(ref swaps);
            for (var i = 0; i < iterations; i++)
            {
                if (read())
                {
                    hits++;
                }
            }
        }
        finally
        {
            done.Cancel();
            writer.Wait(TestContext.Current.CancellationToken);
        }

        Assert.True(Interlocked.Read(ref swaps) > swapsBefore, "the runtime was never swapped while it was read");
        return hits;
    }

    private static ApplicationDbContext CreateContext(IRuntimeExecutionContext runtime)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>(ApplicationDbContextOptions.Create(ModelUnderTest.UnopenedConnection))
            .AddInterceptors(new SuppressedSaves());
        return new ApplicationDbContext(options.Options) { RuntimeContext = runtime };
    }

    private sealed class SuppressedSaves : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
            InterceptionResult<int>.SuppressWithResult(0);
    }
}
