using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Execution;
using Staffora.Data;

namespace Staffora.Test.Unit.Data;

public class ActorInTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid UserOfA = Guid.NewGuid();

    [Fact]
    public void In_its_own_tenant_the_runtime_acts_as_its_user_and_in_any_other_as_that_tenants_system_account()
    {
        using var context = CreateContext(new RuntimeExecutionContext(TenantA, UserOfA));

        Assert.Equal(UserOfA, context.ActorIn(TenantA));
        Assert.Equal(SystemAccount.IdFor(TenantB), context.ActorIn(TenantB));
    }

    [Fact]
    public void An_impersonating_platform_owner_acts_as_the_impersonated_user_in_that_tenant_and_never_in_another()
    {
        var (platform, owner) = (Guid.NewGuid(), Guid.NewGuid());
        var runtime = new RuntimeExecutionContext(platform, owner);
        runtime.StartImpersonation(UserOfA, TenantA);
        using var context = CreateContext(runtime);

        Assert.Equal(UserOfA, context.ActorIn(TenantA));
        Assert.Equal(SystemAccount.IdFor(TenantB), context.ActorIn(TenantB));

        Assert.Equal(SystemAccount.IdFor(platform), context.ActorIn(platform));
    }

    [Fact]
    public void A_context_without_a_runtime_or_a_user_acts_as_each_tenants_system_account()
    {
        using var system = CreateContext(null);
        using var sessionless = CreateContext(new RuntimeExecutionContext());

        Assert.Equal(SystemAccount.IdFor(TenantA), system.ActorIn(TenantA));
        Assert.Equal(SystemAccount.IdFor(TenantB), sessionless.ActorIn(TenantB));
    }

    [Fact]
    public void Inside_a_lease_rows_of_another_tenant_are_stamped_with_its_system_account_and_the_runtimes_own_are_left_to_the_save()
    {
        using var context = CreateContext(new RuntimeExecutionContext(TenantA, UserOfA));
        var intoB = new OutgoingMessage { TenantId = TenantB, MessageType = "test" };
        var named = new OutgoingMessage { TenantId = TenantB, MessageType = "test", CreatedById = Guid.NewGuid() };
        var changedInB = new OutgoingMessage { Id = Guid.NewGuid(), TenantId = TenantB, MessageType = "test", CreatedById = SystemAccount.IdFor(TenantB) };
        var ownRow = new OutgoingMessage { TenantId = TenantA, MessageType = "test" };
        context.AddRange(intoB, named, ownRow);
        context.Attach(changedInB).Property(row => row.Status).IsModified = true;

        context.StampOnOtherTenantsBehalf();
        Assert.Equal((Guid.Empty, (Guid?)null), (intoB.CreatedById, changedInB.ModifiedById));

        using (context.BeginSystemScope("unit test: write into tenant B"))
        {
            context.StampOnOtherTenantsBehalf();
        }

        Assert.Equal(SystemAccount.IdFor(TenantB), intoB.CreatedById);
        Assert.NotEqual(SystemAccount.IdFor(TenantB), named.CreatedById);
        Assert.Equal(SystemAccount.IdFor(TenantB), changedInB.ModifiedById);
        Assert.Equal(Guid.Empty, ownRow.CreatedById);
        Assert.Equal(EntityState.Modified, context.Entry(changedInB).State);
    }

    private static ApplicationDbContext CreateContext(IRuntimeExecutionContext? runtime)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>(ApplicationDbContextOptions.Create(ModelUnderTest.UnopenedConnection));
        return new ApplicationDbContext(options.Options) { RuntimeContext = runtime };
    }
}
