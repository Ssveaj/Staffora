using Microsoft.EntityFrameworkCore;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class DetachedEntityUpdaterTests : IClassFixture<RealDatabase>
{
    private static readonly DetachedEntityUpdaterOptions<TestParent> withChildren = new DetachedEntityUpdaterOptions<TestParent>()
        .WithCollection(parent => parent.Lines)
        .WithCollection(parent => parent.Children);

    private readonly RealDatabase database;

    public DetachedEntityUpdaterTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task An_untouched_column_survives_a_concurrent_edit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var updater = await LoadAsync(scenario.A, id, cancellationToken);

        await EditStoredAsync(scenario.A, id, parent => parent.Name = "Renamed by someone else", cancellationToken);

        updater.Entity.Notes = "Notes from the dialog";
        var result = await SaveAsync(scenario.A, updater, cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        var stored = await StoredAsync(id, cancellationToken);
        Assert.Equal("Renamed by someone else", stored.Name);
        Assert.Equal("Notes from the dialog", stored.Notes);
    }

    [Fact]
    public async Task The_same_field_is_last_save_wins()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var first = await LoadAsync(scenario.A, id, cancellationToken);
        var second = await LoadAsync(scenario.A, id, cancellationToken);

        first.Entity.Notes = "Saved first";
        second.Entity.Notes = "Saved last";
        Assert.True((await SaveAsync(scenario.A, first, cancellationToken)).IsSuccess);
        Assert.True((await SaveAsync(scenario.A, second, cancellationToken)).IsSuccess);

        Assert.Equal("Saved last", (await StoredAsync(id, cancellationToken)).Notes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_after_a_state_change_by_someone_else_is_refused(bool onlyChildrenChanged)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var updater = await LoadAsync(scenario.A, id, cancellationToken);

        await using (var other = this.database.CreateContext(scenario.A.Runtime()))
        {
            await other.TestParents.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.State, TestParentState.Active), cancellationToken);
        }

        if (onlyChildrenChanged)
        {
            updater.Entity.Lines.Add(new TestParentLine { Code = "L9", Text = "Added after the move" });
        }
        else
        {
            updater.Entity.Notes = "Edited after the move";
        }

        var result = await SaveAsync(scenario.A, updater, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("someone else", result.Error);
        Assert.True(updater.HasChanges());
        Assert.Null((await StoredAsync(id, cancellationToken)).Notes);
        Assert.Empty(await StoredLinesAsync(id, cancellationToken));
    }

    [Fact]
    public async Task Children_are_inserted_updated_and_removed_by_key_including_composite_key_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, lines: ["L1", "L2"], children: ["First child"]);
        var updater = await LoadAsync(scenario.A, id, cancellationToken);

        updater.Entity.Lines.Single(line => line.Code == "L1").Text = "Line one, edited";
        updater.Entity.Lines.Remove(updater.Entity.Lines.Single(line => line.Code == "L2"));
        updater.Entity.Lines.Add(new TestParentLine { Code = "L3", Text = "Line three" });
        updater.Entity.Children.Single().Name = "First child, renamed";
        var newChild = new TestChild { Name = "Second child" };
        updater.Entity.Children.Add(newChild);

        Assert.Equal(
            [$"Lines[{id}|L1].Text", $"Lines[{id}|L3] added", $"Lines[{id}|L2] removed", $"Children[{updater.Entity.Children.First().Id}].Name", "Children[new] added"],
            updater.GetChangedMembers());
        var result = await SaveAsync(scenario.A, updater, cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal([("L1", "Line one, edited"), ("L3", "Line three")], (await StoredLinesAsync(id, cancellationToken)).Select(line => (line.Code, line.Text)));
        Assert.Equal(["First child, renamed", "Second child"], (await StoredChildrenAsync(id, cancellationToken)).Select(child => child.Name));

        Assert.NotEqual(Guid.Empty, newChild.Id);
        Assert.Equal(scenario.A.TenantId, newChild.TenantId);
        Assert.Equal(id, updater.Entity.Lines.Single(line => line.Code == "L3").ParentId);
        Assert.False(updater.HasChanges());
        Assert.True((await SaveAsync(scenario.A, updater, cancellationToken)).IsSuccess);
        Assert.Equal(2, (await StoredChildrenAsync(id, cancellationToken)).Count);
    }

    [Fact]
    public async Task Children_added_meanwhile_by_someone_else_are_kept()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, lines: ["L1"]);
        var updater = await LoadAsync(scenario.A, id, cancellationToken);

        await using (var other = this.database.CreateContext(scenario.A.Runtime()))
        {
            other.TestParentLines.Add(new TestParentLine { ParentId = id, Code = "L8", Text = "Added by someone else" });
            await other.SaveChangesAsync(cancellationToken);
        }

        updater.Entity.Lines.Clear();
        updater.Entity.Lines.Add(new TestParentLine { Code = "L3", Text = "Added in the dialog" });
        var result = await SaveAsync(scenario.A, updater, cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(["L3", "L8"], (await StoredLinesAsync(id, cancellationToken)).Select(line => line.Code));
    }

    [Fact]
    public async Task A_save_that_removes_the_last_row_of_a_collection_the_stored_state_makes_Required_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, lines: ["L1"]);

        var updater = await LoadAsync(scenario.A, id, cancellationToken);
        updater.Entity.Lines.Clear();
        var refused = await SaveAsync(scenario.A, updater, cancellationToken);
        Assert.Equal((false, "Lines is required while the TestParent is Draft."), (refused.IsSuccess, refused.Error));
        Assert.True(updater.HasChanges());
        Assert.Equal(["L1"], (await StoredLinesAsync(id, cancellationToken)).Select(line => line.Code));

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.TestParentLines.Remove(await context.TestParentLines.SingleAsync(line => line.ParentId == id, cancellationToken));
            var exception = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.Equal(["Lines"], exception.EmptiedRequiredFields);
        }

        updater.Entity.Lines.Add(new TestParentLine { Code = "L2", Text = "Line two" });
        Assert.True((await SaveAsync(scenario.A, updater, cancellationToken)).IsSuccess);
        Assert.Equal(["L2"], (await StoredLinesAsync(id, cancellationToken)).Select(line => line.Code));
    }

    [Fact]
    public async Task A_parent_stored_without_a_row_of_a_Required_collection_still_saves_its_other_edits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        var updater = await LoadAsync(scenario.A, id, cancellationToken);
        updater.Entity.Notes = "A note";
        Assert.True((await SaveAsync(scenario.A, updater, cancellationToken)).IsSuccess);
        Assert.Equal("A note", (await StoredAsync(id, cancellationToken)).Notes);
    }

    [Fact]
    public async Task A_state_entity_child_is_never_removed_by_its_parents_updater()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, children: ["Kept"]);
        var updater = await LoadAsync(scenario.A, id, cancellationToken);

        updater.Entity.Children.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync(scenario.A, updater, cancellationToken));
        Assert.Equal(["Kept"], (await StoredChildrenAsync(id, cancellationToken)).Select(child => child.Name));
    }

    [Fact]
    public async Task Reference_navigations_are_not_written_while_foreign_keys_are()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, children: ["Child"]);
        var otherId = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var childId = (await StoredChildrenAsync(id, cancellationToken)).Single().Id;

        DetachedEntityUpdater<TestChild> updater;
        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var loaded = await context.TestChildren.AsNoTracking().Include(child => child.Parent).SingleAsync(child => child.Id == childId, cancellationToken);
            updater = new DetachedEntityUpdater<TestChild>(loaded, context);
        }

        updater.Entity.Parent!.Name = "Changed through the navigation";
        updater.Entity.Parent = new TestParent { Name = "Planted through the navigation" };
        Assert.False(updater.HasChanges());

        updater.Entity.Name = "Renamed child";
        Assert.Equal(["Name"], updater.GetChangedMembers());
        Assert.True((await SaveAsync(scenario.A, updater, cancellationToken)).IsSuccess);

        Assert.Equal("Parent", (await StoredAsync(id, cancellationToken)).Name);
        Assert.Equal("Renamed child", (await StoredChildrenAsync(id, cancellationToken)).Single().Name);
        await using (var reader = this.database.CreateSystemContext())
        {
            Assert.False(await reader.TestParents.AnyAsync(parent => parent.Name == "Planted through the navigation", cancellationToken));
        }

        updater.Entity.ParentId = otherId;
        Assert.True((await SaveAsync(scenario.A, updater, cancellationToken)).IsSuccess);
        Assert.Equal(["Renamed child"], (await StoredChildrenAsync(otherId, cancellationToken)).Select(child => child.Name));
    }

    [Fact]
    public async Task HasChanges_follows_the_edits_and_is_false_after_a_save()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, lines: ["L1"]);
        var updater = await LoadAsync(scenario.A, id, cancellationToken);

        Assert.False(updater.HasChanges());
        Assert.Empty(updater.GetChangedMembers());
        Assert.Equal("Nothing to save.", (await SaveAsync(scenario.A, updater, cancellationToken)).Message);

        updater.Entity.Notes = "Typed";
        Assert.True(updater.HasChanges());
        Assert.Equal(["Notes"], updater.GetChangedMembers());

        updater.Entity.Notes = null;
        Assert.False(updater.HasChanges());

        updater.Entity.Lines.Single().Text = "Edited line";
        Assert.True(updater.HasChanges());
        Assert.True((await SaveAsync(scenario.A, updater, cancellationToken)).IsSuccess);
        Assert.False(updater.HasChanges());
        Assert.Equal("Edited line", (await StoredLinesAsync(id, cancellationToken)).Single().Text);
    }

    [Fact]
    public async Task An_updater_over_another_tenants_row_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.B, TestParentState.Draft, cancellationToken, lines: ["L1"]);

        await using (var contextA = this.database.CreateContext(scenario.A.Runtime()))
        {
            Assert.False(await contextA.TestParents.AnyAsync(parent => parent.Id == id, cancellationToken));
        }

        var updater = await LoadAsync(scenario.B, id, cancellationToken);
        updater.Entity.Notes = "Written from A";
        var rootResult = await SaveAsync(scenario.A, updater, cancellationToken);

        var linesOnly = await LoadAsync(scenario.B, id, cancellationToken);
        linesOnly.Entity.Lines.Single().Text = "Line written from A";
        var lineResult = await SaveAsync(scenario.A, linesOnly, cancellationToken);

        Assert.False(rootResult.IsSuccess);
        Assert.Equal("The TestParent was not found.", rootResult.Error);
        Assert.False(lineResult.IsSuccess);
        Assert.Null((await StoredAsync(id, cancellationToken)).Notes);
        Assert.Equal("Line L1", (await StoredLinesAsync(id, cancellationToken)).Single().Text);
    }

    [Fact]
    public async Task An_updater_save_that_leaves_a_locked_field_unchanged_succeeds_and_one_that_changes_it_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);

        var updater = await LoadAsync(scenario.A, id, cancellationToken);
        updater.Entity.Notes = "Notes stay editable while Active";
        var unchangedName = await SaveAsync(scenario.A, updater, cancellationToken);

        updater.Entity.Name = "Renamed while Active";
        var changedName = await SaveAsync(scenario.A, updater, cancellationToken);

        Assert.True(unchangedName.IsSuccess, unchangedName.Error);
        Assert.False(changedName.IsSuccess);
        Assert.Equal("Name cannot be changed while the TestParent is Active.", changedName.Error);
        var stored = await StoredAsync(id, cancellationToken);
        Assert.Equal("Parent", stored.Name);
        Assert.Equal("Notes stay editable while Active", stored.Notes);
    }

    [Fact]
    public async Task An_updater_needs_a_saved_entity_and_a_fresh_context()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var updater = await LoadAsync(scenario.A, id, cancellationToken);
        updater.Entity.Notes = "Edited";

        await using var context = this.database.CreateContext(scenario.A.Runtime());
        Assert.Throws<ArgumentException>(() => new DetachedEntityUpdater<TestParent>(new TestParent { Name = "New" }, context));

        await context.TestParents.SingleAsync(parent => parent.Id == id, cancellationToken);
        Assert.Throws<InvalidOperationException>(() => updater.ApplyChanges(context));
    }

    [Theory]
    [InlineData("edit line")]
    [InlineData("add line")]
    [InlineData("remove line")]
    [InlineData("add child")]
    [InlineData("rename child")]
    public async Task A_change_in_a_collection_the_parents_state_locks_is_refused(string change)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken, lines: ["L1", "L2"], children: ["Child"]);
        var updater = await LoadAsync(scenario.A, id, cancellationToken);

        var collection = change.EndsWith("line") ? nameof(TestParent.Lines) : nameof(TestParent.Children);
        switch (change)
        {
            case "edit line":
                updater.Entity.Lines.Single(line => line.Code == "L1").Text = "Edited while Active";
                break;
            case "add line":
                updater.Entity.Lines.Add(new TestParentLine { Code = "L3", Text = "Added while Active" });
                break;
            case "remove line":
                updater.Entity.Lines.Remove(updater.Entity.Lines.Single(line => line.Code == "L2"));
                break;
            case "add child":
                updater.Entity.Children.Add(new TestChild { Name = "Added while Active" });
                break;
            default:
                updater.Entity.Children.Single().Name = "Renamed while Active";
                break;
        }

        updater.Entity.Notes = "Notes stay editable";
        var result = await SaveAsync(scenario.A, updater, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal($"{collection} cannot be changed while the TestParent is Active.", result.Error);
        Assert.True(updater.HasChanges());
        Assert.Null((await StoredAsync(id, cancellationToken)).Notes);
        Assert.Equal([("L1", "Line L1"), ("L2", "Line L2")], (await StoredLinesAsync(id, cancellationToken)).Select(line => (line.Code, line.Text)));
        Assert.Equal(["Child"], (await StoredChildrenAsync(id, cancellationToken)).Select(child => child.Name));
    }

    [Fact]
    public async Task An_updater_over_a_row_a_transition_holds_is_refused_with_a_failed_result()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using (var system = this.database.CreateSystemContext())
        {
            await system.TestParents.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsInTransition, true), cancellationToken);
        }

        var updater = await LoadAsync(scenario.A, id, cancellationToken);
        updater.Entity.Notes = "Saved while a transition holds the row";
        var result = await SaveAsync(scenario.A, updater, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("The TestParent is being moved by a transition right now. Reload it once the transition has finished and try again.", result.Error);
        Assert.Null((await StoredAsync(id, cancellationToken)).Notes);
    }

    [Theory]
    [InlineData(nameof(TestParent.Children))]
    [InlineData(nameof(TestParent.Lines))]
    public async Task A_child_moved_to_another_parent_through_its_parents_updater_is_refused_with_a_failed_result(string collection)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, lines: ["L1"], children: ["Child"]);
        var otherId = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);
        var updater = await LoadAsync(scenario.A, id, cancellationToken);

        if (collection == nameof(TestParent.Children))
        {
            updater.Entity.Children.Single().ParentId = otherId;
        }
        else
        {
            updater.Entity.Lines.Single().ParentId = otherId;
        }

        var result = await SaveAsync(scenario.A, updater, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains($"{collection}[", result.Error);
        Assert.EndsWith("names another TestParent; a child is never moved through its parent.", result.Error);
        Assert.Single(await StoredChildrenAsync(id, cancellationToken));
        Assert.Single(await StoredLinesAsync(id, cancellationToken));
        Assert.Empty(await StoredChildrenAsync(otherId, cancellationToken));
        Assert.Empty(await StoredLinesAsync(otherId, cancellationToken));
    }

    [Theory]
    [InlineData("move a child into an Active parent")]
    [InlineData("edit a line of an Active parent")]
    [InlineData("edit a line of a parent a transition holds")]
    public async Task An_updater_over_a_child_row_itself_is_judged_against_its_parents_state(string edit)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var draft = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, lines: ["L1"], children: ["Child"]);
        var active = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken, lines: ["L1"]);

        Result result;
        string expected;
        if (edit == "move a child into an Active parent")
        {
            var childId = (await StoredChildrenAsync(draft, cancellationToken)).Single().Id;
            DetachedEntityUpdater<TestChild> updater;
            await using (var context = this.database.CreateContext(scenario.A.Runtime()))
            {
                updater = new DetachedEntityUpdater<TestChild>(await context.TestChildren.AsNoTracking().SingleAsync(row => row.Id == childId, cancellationToken), context);
            }

            updater.Entity.ParentId = active;
            result = await SaveAsync(scenario.A, updater, cancellationToken);
            expected = "Children cannot be changed while the TestParent is Active.";
        }
        else
        {
            var parent = edit == "edit a line of an Active parent" ? active : draft;
            DetachedEntityUpdater<TestParentLine> updater;
            await using (var context = this.database.CreateContext(scenario.A.Runtime()))
            {
                updater = new DetachedEntityUpdater<TestParentLine>(await context.TestParentLines.AsNoTracking().SingleAsync(row => row.ParentId == parent, cancellationToken), context);
            }

            if (parent == draft)
            {
                await using var system = this.database.CreateSystemContext();
                await system.TestParents.Where(row => row.Id == draft).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsInTransition, true), cancellationToken);
            }

            updater.Entity.Text = "Edited through the line's own updater";
            result = await SaveAsync(scenario.A, updater, cancellationToken);
            expected = parent == active
                ? "Lines cannot be changed while the TestParent is Active."
                : "The TestParent is being moved by a transition right now. Reload it once the transition has finished and try again.";
        }

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error);
        Assert.Equal(["Child"], (await StoredChildrenAsync(draft, cancellationToken)).Select(child => child.Name));
        Assert.Empty(await StoredChildrenAsync(active, cancellationToken));
        Assert.Equal(["Line L1"], (await StoredLinesAsync(draft, cancellationToken)).Select(line => line.Text));
        Assert.Equal(["Line L1"], (await StoredLinesAsync(active, cancellationToken)).Select(line => line.Text));
    }

    [Fact]
    public async Task A_new_child_belongs_to_the_loaded_root_even_when_the_dialog_changed_the_roots_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var otherId = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);
        var updater = await LoadAsync(scenario.A, id, cancellationToken);

        updater.Entity.Id = otherId;
        updater.Entity.Lines.Add(new TestParentLine { Code = "L1", Text = "Added in the dialog" });
        var result = await SaveAsync(scenario.A, updater, cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(["L1"], (await StoredLinesAsync(id, cancellationToken)).Select(line => line.Code));
        Assert.Empty(await StoredLinesAsync(otherId, cancellationToken));
        Assert.Equal(id, updater.Entity.Id);
    }

    private async Task<Guid> AddParentAsync(ScenarioTenant tenant, TestParentState state, CancellationToken cancellationToken, string[]? lines = null, string[]? children = null)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());

        using var lease = writer.BeginSystemScope("test: insert a parent as a transition left it");
        var parent = new TestParent { Name = "Parent", State = state };
        foreach (var code in lines ?? [])
        {
            parent.Lines.Add(new TestParentLine { Code = code, Text = $"Line {code}" });
        }

        foreach (var name in children ?? [])
        {
            parent.Children.Add(new TestChild { Name = name });
        }

        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task<DetachedEntityUpdater<TestParent>> LoadAsync(ScenarioTenant tenant, Guid id, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateContext(tenant.Runtime());
        var parent = await context.TestParents.AsNoTracking()
            .Include(row => row.Lines.OrderBy(line => line.Code))
            .Include(row => row.Children.OrderBy(child => child.Name))
            .SingleAsync(row => row.Id == id, cancellationToken);
        return new DetachedEntityUpdater<TestParent>(parent, context, withChildren);
    }

    private async Task<Result> SaveAsync<T>(ScenarioTenant tenant, DetachedEntityUpdater<T> updater, CancellationToken cancellationToken)
        where T : class
    {
        await using var context = this.database.CreateContext(tenant.Runtime());
        return await updater.SaveAsync(context, cancellationToken);
    }

    private async Task EditStoredAsync(ScenarioTenant tenant, Guid id, Action<TestParent> edit, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateContext(tenant.Runtime());
        edit(await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken));
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<TestParent> StoredAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestParents.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken);
    }

    private async Task<List<TestParentLine>> StoredLinesAsync(Guid parentId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestParentLines.AsNoTracking().Where(line => line.ParentId == parentId).OrderBy(line => line.Code).ToListAsync(cancellationToken);
    }

    private async Task<List<TestChild>> StoredChildrenAsync(Guid parentId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestChildren.AsNoTracking().Where(child => child.ParentId == parentId).OrderBy(child => child.Name).ToListAsync(cancellationToken);
    }
}
