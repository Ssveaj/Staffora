using Microsoft.EntityFrameworkCore;
using Staffora.Core.Execution;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class TenantIsolationTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public TenantIsolationTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Reads_in_A_return_none_of_Bs_rows_and_a_foreign_id_reads_like_a_missing_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var aParent = await AddParentAsync(scenario.A, "Parent of A", cancellationToken);
        var bParent = await AddParentAsync(scenario.B, "Parent of B", cancellationToken);

        await using var readerA = this.database.CreateContext(scenario.A.Runtime());

        Assert.Equal([aParent], await readerA.TestParents.Select(parent => parent.Id).ToListAsync(cancellationToken));
        Assert.Null(await readerA.TestParents.FirstOrDefaultAsync(parent => parent.Id == bParent, cancellationToken));
        Assert.Null(await readerA.TestParents.FirstOrDefaultAsync(parent => parent.Id == Guid.NewGuid(), cancellationToken));
    }

    [Fact]
    public async Task Tenant_As_context_reads_neither_Tenant_Bs_users_nor_the_platform_owner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);

        await using var readerA = this.database.CreateContext(scenario.A.Runtime());
        Assert.Equal(
            new[] { scenario.A.OwnerId, colleague, scenario.A.SystemAccountId }.Order(),
            (await readerA.Users.Select(user => user.Id).ToListAsync(cancellationToken)).Order());
        Assert.Null(await readerA.Users.FirstOrDefaultAsync(user => user.Id == scenario.B.OwnerId, cancellationToken));
        Assert.Null(await readerA.Users.FirstOrDefaultAsync(user => user.Id == platform.OwnerId, cancellationToken));
        Assert.Equal(0, await readerA.Users.CountAsync(user => user.TenantId != scenario.A.TenantId, cancellationToken));
    }

    [Fact]
    public async Task A_context_without_a_runtime_tenant_reads_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await AddParentAsync(scenario.A, "Parent of A", cancellationToken);

        var signedInWithoutTenant = new RuntimeExecutionContext();
        signedInWithoutTenant.SetUser(scenario.A.OwnerId);
        await using var withoutTenant = this.database.CreateContext(signedInWithoutTenant);
        await using var withoutRuntime = this.database.CreateContext(runtime: null);

        Assert.Empty(await withoutTenant.TestParents.ToListAsync(cancellationToken));
        Assert.Empty(await withoutRuntime.TestParents.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task A_write_without_a_runtime_tenant_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var signedInWithoutTenant = new RuntimeExecutionContext();
        signedInWithoutTenant.SetUser(scenario.A.OwnerId);

        foreach (var runtime in new IRuntimeExecutionContext?[] { signedInWithoutTenant, null })
        {
            await using var context = this.database.CreateContext(runtime);
            context.TestParents.Add(new TestParent { Name = "Nobody's", TenantId = scenario.A.TenantId, CreatedById = scenario.A.OwnerId });

            await Assert.ThrowsAsync<TenantIsolationException>(() => context.SaveChangesAsync(cancellationToken));
        }

        Assert.Equal(0, await CountParentsAsync(scenario.A, cancellationToken));
    }

    [Fact]
    public async Task An_insert_for_another_tenant_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            writerA.TestParents.Add(new TestParent { Name = "Planted in B", TenantId = scenario.B.TenantId });

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        Assert.Equal(0, await CountParentsAsync(scenario.B, cancellationToken));
    }

    [Fact]
    public async Task Changing_a_rows_TenantId_is_refused_also_in_system_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, "Moving house", cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            (await writerA.TestParents.SingleAsync(parent => parent.Id == id, cancellationToken)).TenantId = scenario.B.TenantId;

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            (await system.TestParents.SingleAsync(parent => parent.Id == id, cancellationToken)).TenantId = scenario.B.TenantId;

            await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken));
        }

        Assert.Equal(scenario.A.TenantId, await StoredTenantOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task Update_on_a_detached_own_row_moved_to_another_tenant_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, "Moving house unseen", cancellationToken);
        var detached = await LoadDetachedAsync(id, cancellationToken);

        detached.TenantId = scenario.B.TenantId;
        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            writerA.Update(detached);

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        Assert.Equal(scenario.A.TenantId, await StoredTenantOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task Update_on_a_detached_row_moved_to_another_tenant_is_refused_in_system_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, "Moving house in system scope", cancellationToken);
        var detached = await LoadDetachedAsync(id, cancellationToken);

        detached.TenantId = scenario.B.TenantId;
        detached.ModifiedById = scenario.B.OwnerId;
        await using (var system = this.database.CreateSystemContext())
        {
            system.Update(detached);

            await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken));
        }

        Assert.Equal(scenario.A.TenantId, await StoredTenantOfAsync(id, cancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Updating_a_row_stored_for_another_tenant_is_refused(bool claimingTheRowIsAs)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.B, "Parent of B", cancellationToken);
        var foreign = await LoadDetachedAsync(id, cancellationToken);

        if (claimingTheRowIsAs)
        {
            foreign.TenantId = scenario.A.TenantId;
        }

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            writerA.TestParents.Attach(foreign);
            foreign.Notes = "Written from A";

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        var stored = await LoadDetachedAsync(id, cancellationToken);
        Assert.Null(stored.Notes);
        Assert.Equal(scenario.B.TenantId, stored.TenantId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_refused_write_of_another_tenants_row_reads_exactly_like_one_of_a_missing_row(bool delete)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var theirs = await AddParentAsync(scenario.B, "Parent of B", cancellationToken);
        var missing = Guid.NewGuid();

        async Task<string> RefusalAsync(Guid id)
        {
            await using var writerA = this.database.CreateContext(scenario.A.Runtime());
            var row = new TestParent { Id = id, TenantId = scenario.A.TenantId, Name = "Posted from A", State = TestParentState.Draft };
            if (delete)
            {
                writerA.TestParents.Attach(row);
                writerA.TestParents.Remove(row);
            }
            else
            {
                writerA.TestParents.Update(row);
            }

            var refusal = await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
            return refusal.Message.Replace(id.ToString(), "{id}", StringComparison.Ordinal);
        }

        var forTheirs = await RefusalAsync(theirs);
        var forMissing = await RefusalAsync(missing);

        Assert.Equal(forMissing, forTheirs);
        Assert.Contains("does not store the row for the runtime tenant", forTheirs, StringComparison.Ordinal);
        Assert.Equal(("Parent of B", scenario.B.TenantId), ((await LoadDetachedAsync(theirs, cancellationToken)).Name, await StoredTenantOfAsync(theirs, cancellationToken)));
    }

    [Fact]
    public async Task Deleting_a_row_stored_for_another_tenant_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.B, "Parent of B", cancellationToken);
        var foreign = await LoadDetachedAsync(id, cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            writerA.TestParents.Remove(foreign);

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        Assert.Equal(scenario.B.TenantId, await StoredTenantOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task Updates_and_deletes_of_the_runtime_tenants_own_rows_pass_the_checks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var kept = await AddParentAsync(scenario.A, "Kept", cancellationToken);
        var removed = await AddParentAsync(scenario.A, "Removed", cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            (await writerA.TestParents.SingleAsync(parent => parent.Id == kept, cancellationToken)).Notes = "Edited in A";

            var doomed = await writerA.TestParents.Include(parent => parent.Children).SingleAsync(parent => parent.Id == removed, cancellationToken);
            await doomed.GetWorkflow<TestParentState, TestParent>()
                .ExecuteTransitionAsync(doomed, "test-parent-delete", new WorkflowEnvironment(writerA.Clock, NavigationLoadState.From(writerA)), cancellationToken: cancellationToken);
            writerA.TestParents.Remove(doomed);
            using (writerA.BeginTransitionSaveScope([doomed], "test-parent-delete"))
            {
                await writerA.SaveChangesAsync(cancellationToken);
            }
        }

        await using var system = this.database.CreateSystemContext();
        Assert.Equal("Edited in A", (await system.TestParents.SingleAsync(parent => parent.Id == kept, cancellationToken)).Notes);
        Assert.False(await system.TestParents.AnyAsync(parent => parent.Id == removed, cancellationToken));
    }

    [Fact]
    public async Task A_system_scope_lease_reads_across_tenants_nests_and_ends_on_dispose_and_on_an_exception()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await AddParentAsync(scenario.A, "Parent of A", cancellationToken);
        await AddParentAsync(scenario.B, "Parent of B", cancellationToken);
        await using var context = this.database.CreateContext(scenario.A.Runtime());
        var mine = new[] { scenario.A.TenantId, scenario.B.TenantId };

        using (context.BeginSystemScope("test: outer"))
        {
            using (context.BeginSystemScope("test: inner"))
            {
                Assert.Equal(2, await context.TestParents.CountAsync(parent => mine.Contains(parent.TenantId), cancellationToken));
            }

            Assert.True(context.IsInSystemScope);
            Assert.Equal(2, await context.TestParents.CountAsync(parent => mine.Contains(parent.TenantId), cancellationToken));
        }

        Assert.False(context.IsInSystemScope);
        Assert.Equal(1, await context.TestParents.CountAsync(cancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using (context.BeginSystemScope("test: failing block"))
            {
                await context.TestParents.CountAsync(cancellationToken);
                throw new InvalidOperationException("The block failed.");
            }
        });

        Assert.False(context.IsInSystemScope);
        Assert.Equal(1, await context.TestParents.CountAsync(cancellationToken));
    }

    private async Task<Guid> AddParentAsync(ScenarioTenant tenant, string name, CancellationToken cancellationToken)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        var parent = new TestParent { Name = name };
        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task<TestParent> LoadDetachedAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestParents.AsNoTracking().SingleAsync(parent => parent.Id == id, cancellationToken);
    }

    private async Task<Guid> StoredTenantOfAsync(Guid id, CancellationToken cancellationToken) => (await LoadDetachedAsync(id, cancellationToken)).TenantId;

    private async Task<int> CountParentsAsync(ScenarioTenant tenant, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestParents.CountAsync(parent => parent.TenantId == tenant.TenantId, cancellationToken);
    }
}
