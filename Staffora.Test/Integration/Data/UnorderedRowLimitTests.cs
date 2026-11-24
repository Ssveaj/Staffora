using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Execution;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class UnorderedRowLimitTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public UnorderedRowLimitTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_query_that_cuts_rows_without_an_order_throws_in_the_tests()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = this.database.CreateSystemContext();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Users.Take(2).ToListAsync(cancellationToken));
        Assert.Contains(nameof(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.RowLimitingOperationWithoutOrderByWarning), refused.Message, StringComparison.Ordinal);
        Assert.NotNull(await context.Users.OrderBy(user => user.Id).Take(2).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task The_user_stores_address_lookup_orders_the_rows_it_reads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        string email;
        await using (var system = this.database.CreateSystemContext())
        {
            email = await system.Users.Where(user => user.Id == scenario.A.OwnerId).Select(user => user.Email!).SingleAsync(cancellationToken);
        }

        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        Assert.Equal(scenario.A.OwnerId, (await host.Get<UserManager<ApplicationUser>>().FindByEmailAsync(email))?.Id);
    }
}
