using Microsoft.EntityFrameworkCore;

namespace Staffora.Test.Integration.Infrastructure;

[Trait("Category", "Integration")]
public class RealDatabaseRoundTripTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public RealDatabaseRoundTripTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_saved_DateTime_reads_back_with_Kind_Utc()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken: cancellationToken);
        var instant = new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Utc);
        Guid id;

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = new TestParent { Name = "Clock", DueAt = DateTime.SpecifyKind(instant, DateTimeKind.Unspecified) };
            writer.TestParents.Add(parent);
            await writer.SaveChangesAsync(cancellationToken);
            id = parent.Id;
        }

        await using var reader = this.database.CreateContext(scenario.A.Runtime());
        var read = await reader.TestParents.SingleAsync(p => p.Id == id, cancellationToken);

        Assert.Equal(DateTimeKind.Utc, read.DueAt!.Value.Kind);
        Assert.Equal(instant, read.DueAt.Value);
        Assert.Equal(DateTimeKind.Utc, read.Created.Kind);
    }

    [Fact]
    public async Task A_parent_with_a_child_and_a_grandchild_saves_and_loads_as_one_tree()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken: cancellationToken);
        Guid id;

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = new TestParent { Name = "Parent" };
            var child = new TestChild { Name = "Child", Parent = parent };
            child.Grandchildren.Add(new TestGrandchild { Name = "Grandchild" });
            parent.Children.Add(child);
            writer.TestParents.Add(parent);
            await writer.SaveChangesAsync(cancellationToken);
            id = parent.Id;
        }

        await using var reader = this.database.CreateContext(scenario.A.Runtime());
        var loaded = await reader.TestParents.Include(p => p.Children).ThenInclude(c => c.Grandchildren).SingleAsync(p => p.Id == id, cancellationToken);

        var grandchild = Assert.Single(Assert.Single(loaded.Children).Grandchildren);
        Assert.Equal(TestParentState.Draft, loaded.State);
        Assert.Equal(TestGrandchildState.Open, grandchild.State);
        Assert.Equal(scenario.A.TenantId, grandchild.TenantId);
    }

    [Fact]
    public async Task A_save_over_a_row_whose_state_moved_meanwhile_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken: cancellationToken);
        Guid id;

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = new TestParent { Name = "Contested" };
            writer.TestParents.Add(parent);
            await writer.SaveChangesAsync(cancellationToken);
            id = parent.Id;
        }

        await using var editor = this.database.CreateContext(scenario.A.Runtime());
        var edited = await editor.TestParents.SingleAsync(p => p.Id == id, cancellationToken);

        await using (var other = this.database.CreateContext(scenario.A.Runtime()))
        {
            await other.TestParents.Where(p => p.Id == id).ExecuteUpdateAsync(set => set.SetProperty(p => p.State, TestParentState.Active), cancellationToken);
        }

        edited.Notes = "Edited while the state moved";

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => editor.SaveChangesAsync(cancellationToken));
    }
}
