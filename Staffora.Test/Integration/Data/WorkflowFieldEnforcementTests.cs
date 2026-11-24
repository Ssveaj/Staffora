using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class WorkflowFieldEnforcementTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public WorkflowFieldEnforcementTests(RealDatabase database)
    {
        this.database = database;
    }

    [Theory]
    [InlineData(nameof(TestParent.State))]
    [InlineData(nameof(TestParent.IsInTransition))]
    public async Task A_tracked_edit_of_State_or_IsInTransition_outside_a_transition_is_refused(string field)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            if (field == nameof(TestParent.State))
            {
                parent.State = TestParentState.Active;
            }
            else
            {
                parent.IsInTransition = true;
            }

            var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.Equal([field], refusal.Fields);
            Assert.Contains("only through a transition", refusal.Message);
        }

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(TestParentState.Draft, stored.State);
        Assert.False(stored.IsInTransition);
    }

    [Fact]
    public async Task A_tracked_edit_of_a_field_the_persisted_state_locks_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            parent.Name = "Renamed while Active";
            parent.Notes = "Notes stay editable";

            var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.Equal([nameof(TestParent.Name)], refusal.Fields);
            Assert.Equal(TestParentState.Active, refusal.PersistedState);
        }

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal("Parent", stored.Name);
        Assert.Null(stored.Notes);
    }

    [Fact]
    public async Task The_same_field_is_editable_while_the_persisted_state_allows_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            (await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken)).Name = "Renamed in Draft";
            await context.SaveChangesAsync(cancellationToken);
        }

        Assert.Equal("Renamed in Draft", (await LoadAsync(id, cancellationToken)).Name);
    }

    [Fact]
    public async Task Fields_are_judged_by_the_state_the_database_holds_not_the_one_tracked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            await MoveStoredStateAsync(id, TestParentState.Active, cancellationToken);
            parent.Name = "Renamed by a stale dialog";

            var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.Equal(TestParentState.Active, refusal.PersistedState);
        }

        Assert.Equal("Parent", (await LoadAsync(id, cancellationToken)).Name);
    }

    [Fact]
    public async Task An_unchanged_locked_field_passes_even_when_Update_marks_it_modified()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);
        var detached = await LoadAsync(id, cancellationToken);

        detached.Notes = "Saved over a whole entity";
        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.Update(detached);
            Assert.True(context.Entry(detached).Property(row => row.Name).IsModified);
            await context.SaveChangesAsync(cancellationToken);
        }

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal("Saved over a whole entity", stored.Notes);
        Assert.Equal("Parent", stored.Name);
    }

    [Fact]
    public async Task A_locked_field_set_back_to_its_stored_value_passes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            parent.Name = "Typed";
            parent.Name = "Parent";
            context.Entry(parent).Property(row => row.Name).IsModified = true;
            parent.Notes = "Edited";
            await context.SaveChangesAsync(cancellationToken);
        }

        Assert.Equal("Edited", (await LoadAsync(id, cancellationToken)).Notes);
    }

    [Fact]
    public async Task A_transition_save_scope_lets_its_entities_change_state_and_locked_fields_and_ends_on_dispose()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var otherId = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            var other = await context.TestParents.SingleAsync(row => row.Id == otherId, cancellationToken);

            using (context.BeginTransitionSaveScope([parent], "test: activate"))
            {
                Assert.True(context.IsInTransitionSaveScope(parent));
                Assert.False(context.IsInTransitionSaveScope(other));

                other.State = TestParentState.Active;
                await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
                context.Entry(other).Property(row => row.State).CurrentValue = TestParentState.Draft;
                context.Entry(other).Property(row => row.State).IsModified = false;

                parent.State = TestParentState.Active;
                await context.SaveChangesAsync(cancellationToken);

                parent.Name = "Set by a transition action";
                await context.SaveChangesAsync(cancellationToken);
            }

            Assert.False(context.IsInTransitionSaveScope(parent));
            parent.State = TestParentState.Canceled;
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
        }

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(TestParentState.Active, stored.State);
        Assert.Equal("Set by a transition action", stored.Name);
        Assert.Equal(TestParentState.Draft, (await LoadAsync(otherId, cancellationToken)).State);
    }

    [Fact]
    public async Task Transition_save_scopes_nest_per_entity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using var context = this.database.CreateContext(scenario.A.Runtime());
        var parent = await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken);

        var outer = context.BeginTransitionSaveScope([parent], "test: outer");
        var inner = context.BeginTransitionSaveScope([parent, parent], "test: inner");
        inner.Dispose();
        inner.Dispose();
        Assert.True(context.IsInTransitionSaveScope(parent));

        outer.Dispose();
        Assert.False(context.IsInTransitionSaveScope(parent));
    }

    [Fact]
    public async Task A_tenants_state_edited_in_system_scope_outside_a_transition_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        await using (var system = this.database.CreateSystemContext())
        {
            (await system.Tenants.SingleAsync(tenant => tenant.Id == scenario.B.TenantId, cancellationToken)).State = TenantState.Suspended;

            var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => system.SaveChangesAsync(cancellationToken));
            Assert.Equal([nameof(Tenant.State)], refusal.Fields);
        }

        await using var reader = this.database.CreateSystemContext();
        Assert.Equal(TenantState.Active, (await reader.Tenants.SingleAsync(tenant => tenant.Id == scenario.B.TenantId, cancellationToken)).State);
    }

    [Fact]
    public async Task An_edit_of_another_tenants_row_is_refused_by_the_tenant_check_before_its_fields_are_judged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.B, TestParentState.Active, cancellationToken);
        var detached = await LoadAsync(id, cancellationToken);

        detached.Name = "Locked and foreign";
        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            writerA.Update(detached);

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        Assert.Equal("Parent", (await LoadAsync(id, cancellationToken)).Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_row_deleted_and_inserted_again_with_the_same_key_in_one_save_is_refused(bool stateEntity)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken, lines: ["L1"]);

        var before = await LoadAsync(id, cancellationToken);
        var lineBefore = await LoadLineAsync(id, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            if (stateEntity)
            {
                context.Remove(await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken));
                context.Add(new TestParent { Id = id, Name = "Replaced", State = TestParentState.Canceled });
            }
            else
            {
                context.Remove(await context.TestParentLines.SingleAsync(row => row.ParentId == id, cancellationToken));
                context.Add(new TestParentLine { ParentId = id, Code = "L1", Text = "Replaced" });
            }

            Assert.Equal([EntityState.Deleted, EntityState.Added], context.ChangeTracker.Entries().Select(entry => entry.State).Order());
            var refusal = await Assert.ThrowsAsync<TenantIsolationException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.Contains("inserted again with the same key", refusal.Message);
        }

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((TestParentState.Active, "Parent", before.Created, before.CreatedById), (stored.State, stored.Name, stored.Created, stored.CreatedById));
        var storedLine = await LoadLineAsync(id, cancellationToken);
        Assert.Equal((lineBefore.Text, lineBefore.Created, lineBefore.CreatedById), (storedLine.Text, storedLine.Created, storedLine.CreatedById));
    }

    [Theory]
    [InlineData(EntityState.Modified, false)]
    [InlineData(EntityState.Deleted, false)]
    [InlineData(EntityState.Deleted, true)]
    public async Task A_row_a_transition_holds_is_not_written_outside_its_transition(EntityState write, bool inSystemScope)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        await HoldAsync(id, cancellationToken);

        await using (var context = inSystemScope ? this.database.CreateSystemContext() : this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            if (write == EntityState.Modified)
            {
                parent.Name = "Edited while a transition holds it";
            }
            else
            {
                context.Remove(parent);
            }

            var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.True(refusal.HeldByTransition);
            Assert.Contains("a transition holds it", refusal.Message);
        }

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(("Parent", true), (stored.Name, stored.IsInTransition));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_tracked_delete_of_a_state_entity_outside_a_transition_save_scope_is_refused_so_a_two_save_replace_fails_at_its_first_save(bool inSystemScope)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using (var context = inSystemScope ? this.database.CreateSystemContext() : this.database.CreateContext(scenario.A.Runtime()))
        {
            context.Remove(await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken));

            var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.Equal((nameof(TestParent), id.ToString(), TestParentState.Draft, false), (refusal.EntityType, refusal.Key, (TestParentState)refusal.PersistedState!, refusal.HeldByTransition));
            Assert.Contains("removed only by its delete transition", refusal.Message, StringComparison.Ordinal);
        }

        await using (var context = inSystemScope ? this.database.CreateSystemContext() : this.database.CreateContext(scenario.A.Runtime()))
        {
            context.TestParents.Add(new TestParent { Id = id, TenantId = scenario.A.TenantId, CreatedById = scenario.A.OwnerId, Name = "Replaced", State = TestParentState.Active });
            if (inSystemScope)
            {
                await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
            }
            else
            {
                await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
            }
        }

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((TestParentState.Draft, "Parent", false), (stored.State, stored.Name, stored.IsInTransition));
    }

    [Theory]
    [InlineData("a parent", "tenant scope", false)]
    [InlineData("a parent", "system scope", true)]
    [InlineData("a parent", "a transition save scope", true)]
    [InlineData("a child", "tenant scope", false)]
    [InlineData("a child", "system scope", true)]
    public async Task An_insert_of_a_state_entity_outside_its_entry_state_is_refused_outside_system_scope_and_a_transition_save_scope(string entity, string scope, bool allowed)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var parentId = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        IStateEntity row = entity == "a parent"
            ? new TestParent { Name = "Inserted", State = TestParentState.Canceled }
            : new TestChild { ParentId = parentId, Name = "Inserted", State = TestChildState.Deleted };
        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            using var lease = scope == "system scope" ? context.BeginSystemScope("test: insert a later state") : null;
            using var saveScope = scope == "a transition save scope" ? context.BeginTransitionSaveScope([row], "test: a transition inserts it") : null;
            context.Add(row);

            if (allowed)
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            else
            {
                var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
                Assert.Equal([nameof(TestParent.State)], refusal.Fields);
                Assert.Contains("starts in Draft and reaches", refusal.Message, StringComparison.Ordinal);
            }
        }

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.Add(new TestChild { ParentId = parentId, Name = "Draft child" });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var system = this.database.CreateSystemContext();
        var stored = entity == "a parent"
            ? await system.TestParents.AnyAsync(stored => stored.Id == row.Id, cancellationToken)
            : await system.TestChildren.AnyAsync(stored => stored.Id == row.Id, cancellationToken);
        Assert.Equal(allowed, stored);
    }

    [Theory]
    [InlineData("tenant scope", false)]
    [InlineData("system scope", false)]
    [InlineData("a transition save scope", true)]
    public async Task An_insert_of_a_state_entity_already_held_is_refused_outside_a_transition_save_scope_in_every_scope(string scope, bool allowed)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        var row = new TestParent { Name = "Held from the start", IsInTransition = true };
        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            using var lease = scope == "system scope" ? context.BeginSystemScope("test: insert a held row") : null;
            using var saveScope = scope == "a transition save scope" ? context.BeginTransitionSaveScope([row], "test: a transition inserts it") : null;
            context.Add(row);

            if (allowed)
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            else
            {
                var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
                Assert.Equal([nameof(TestParent.IsInTransition)], refusal.Fields);
                Assert.Contains("a new record starts released", refusal.Message, StringComparison.Ordinal);
            }
        }

        await using var system = this.database.CreateSystemContext();
        Assert.Equal(allowed, await system.TestParents.AnyAsync(stored => stored.Id == row.Id, cancellationToken));
    }

    [Fact]
    public async Task The_transition_holding_a_row_writes_it_inside_its_save_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        await HoldAsync(id, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            using (context.BeginTransitionSaveScope([parent], "test: activate"))
            {
                parent.State = TestParentState.Active;
                parent.IsInTransition = false;
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((TestParentState.Active, false), (stored.State, stored.IsInTransition));
    }

    [Theory]
    [InlineData("insert a line")]
    [InlineData("update a line")]
    [InlineData("delete a line")]
    [InlineData("update a child")]
    [InlineData("insert a child")]
    public async Task A_tracked_write_of_a_child_row_in_a_collection_the_parents_state_locks_is_refused(string write)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken, lines: ["L1"], children: ["Child"]);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            switch (write)
            {
                case "insert a line":
                    context.TestParentLines.Add(new TestParentLine { ParentId = id, Code = "L2", Text = "Added while Active" });
                    break;
                case "update a line":
                    (await context.TestParentLines.SingleAsync(row => row.ParentId == id, cancellationToken)).Text = "Edited while Active";
                    break;
                case "delete a line":
                    context.Remove(await context.TestParentLines.SingleAsync(row => row.ParentId == id, cancellationToken));
                    break;
                case "update a child":
                    (await context.TestChildren.SingleAsync(row => row.ParentId == id, cancellationToken)).Name = "Renamed while Active";
                    break;
                default:
                    context.TestChildren.Add(new TestChild { ParentId = id, Name = "Added while Active" });
                    break;
            }

            var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
            var collection = write.EndsWith("line", StringComparison.Ordinal) ? nameof(TestParent.Lines) : nameof(TestParent.Children);
            Assert.Equal((nameof(TestParent), id.ToString(), TestParentState.Active, false), (refusal.EntityType, refusal.Key, (TestParentState)refusal.PersistedState!, refusal.HeldByTransition));
            Assert.Equal([collection], refusal.Fields);
            Assert.EndsWith($"its {collection} cannot be edited while it is Active.", refusal.Message, StringComparison.Ordinal);
        }

        Assert.Equal("Line", (await LoadLineAsync(id, cancellationToken)).Text);
        Assert.Equal(["Child"], await ChildNamesAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_tracked_write_of_a_line_of_a_parent_a_transition_holds_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, lines: ["L1"]);
        await HoldAsync(id, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.TestParentLines.Add(new TestParentLine { ParentId = id, Code = "L2", Text = "Added while held" });
            (await context.TestParentLines.SingleAsync(row => row.ParentId == id && row.Code == "L1", cancellationToken)).Text = "Edited while held";

            var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.True(refusal.HeldByTransition);
            Assert.Equal((nameof(TestParent), id.ToString()), (refusal.EntityType, refusal.Key));
        }

        Assert.Equal("Line", (await LoadLineAsync(id, cancellationToken)).Text);
    }

    [Theory]
    [InlineData(TestParentState.Draft, TestParentState.Active, false)]
    [InlineData(TestParentState.Active, TestParentState.Draft, false)]
    [InlineData(TestParentState.Draft, TestParentState.Draft, true)]
    public async Task A_child_moved_to_another_parent_is_judged_against_the_parent_it_leaves_and_the_one_it_joins(TestParentState from, TestParentState to, bool allowed)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var source = await AddParentAsync(scenario.A, from, cancellationToken, children: ["Moved"]);
        var target = await AddParentAsync(scenario.A, to, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            (await context.TestChildren.SingleAsync(row => row.ParentId == source, cancellationToken)).ParentId = target;
            if (allowed)
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            else
            {
                var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
                Assert.Equal(((from == TestParentState.Active ? source : target).ToString(), TestParentState.Active), (refusal.Key, (TestParentState)refusal.PersistedState!));
            }
        }

        string[] moved = ["Moved"];
        Assert.Equal(allowed ? [] : moved, await ChildNamesAsync(source, cancellationToken));
        Assert.Equal(allowed ? moved : [], await ChildNamesAsync(target, cancellationToken));
    }

    [Fact]
    public async Task A_transition_save_scope_on_the_parent_or_the_child_lets_the_child_be_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken, lines: ["L1"], children: ["Child"]);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            using (context.BeginTransitionSaveScope([parent], "test: an action writes the parent's lines"))
            {
                (await context.TestParentLines.SingleAsync(row => row.ParentId == id, cancellationToken)).Text = "Written by a transition";
                await context.SaveChangesAsync(cancellationToken);
            }

            var child = await context.TestChildren.SingleAsync(row => row.ParentId == id, cancellationToken);
            using (context.BeginTransitionSaveScope([child], "test: a cascade moves the child"))
            {
                child.State = TestChildState.Canceled;
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        Assert.Equal("Written by a transition", (await LoadLineAsync(id, cancellationToken)).Text);
        await using var system = this.database.CreateSystemContext();
        Assert.Equal(TestChildState.Canceled, (await system.TestChildren.SingleAsync(row => row.ParentId == id, cancellationToken)).State);
    }

    [Fact]
    public async Task Update_on_a_whole_detached_graph_that_leaves_the_children_as_they_are_passes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken, lines: ["L1"], children: ["Child"]);

        TestParent detached;
        await using (var system = this.database.CreateSystemContext())
        {
            detached = await system.TestParents.AsNoTracking().Include(row => row.Lines).Include(row => row.Children).SingleAsync(row => row.Id == id, cancellationToken);
        }

        detached.Notes = "Notes stay editable";
        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.Update(detached);
            await context.SaveChangesAsync(cancellationToken);
        }

        Assert.Equal("Notes stay editable", (await LoadAsync(id, cancellationToken)).Notes);
        Assert.Equal("Line", (await LoadLineAsync(id, cancellationToken)).Text);
    }

    [Fact]
    public async Task A_collection_that_only_follows_its_parents_state_default_is_not_judged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        var tenantWorkflow = WorkflowRegistry.Default.Get(typeof(Tenant));
        Assert.False(tenantWorkflow.DeclaresField(nameof(Tenant.TenantRoles)));
        Assert.False(tenantWorkflow.GetFieldAccess(TenantState.Active, nameof(Tenant.TenantRoles)).HasFlag(FieldAccessFlags.Editable));

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.TenantRoles.Add(new TenantRole { Name = "Viewer" });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var system = this.database.CreateSystemContext();
        Assert.True(await system.TenantRoles.AnyAsync(role => role.TenantId == scenario.A.TenantId && role.Name == "Viewer", cancellationToken));
    }

    private async Task<List<string>> ChildNamesAsync(Guid parentId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestChildren.AsNoTracking().Where(row => row.ParentId == parentId).OrderBy(row => row.Name).Select(row => row.Name).ToListAsync(cancellationToken);
    }

    private async Task HoldAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var other = this.database.CreateSystemContext();
        await other.TestParents.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsInTransition, true), cancellationToken);
    }

    private async Task<TestParentLine> LoadLineAsync(Guid parentId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestParentLines.AsNoTracking().SingleAsync(row => row.ParentId == parentId, cancellationToken);
    }

    private async Task<Guid> AddParentAsync(ScenarioTenant tenant, TestParentState state, CancellationToken cancellationToken, string[]? lines = null, string[]? children = null)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());

        using var lease = writer.BeginSystemScope("test: insert a parent as a transition left it");
        var parent = new TestParent { Name = "Parent", State = state };
        foreach (var code in lines ?? [])
        {
            parent.Lines.Add(new TestParentLine { Code = code, Text = "Line" });
        }

        foreach (var name in children ?? [])
        {
            parent.Children.Add(new TestChild { Name = name });
        }

        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task MoveStoredStateAsync(Guid id, TestParentState state, CancellationToken cancellationToken)
    {
        await using var other = this.database.CreateSystemContext();
        await other.TestParents.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.State, state), cancellationToken);
    }

    private async Task<TestParent> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestParents.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken);
    }
}
