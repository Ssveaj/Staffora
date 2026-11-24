using Microsoft.EntityFrameworkCore;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Test.Unit.Data;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class ConditionTests
{
    [Fact]
    public void A_condition_refuses_when_a_navigation_it_reads_is_not_loaded()
    {
        var parent = SampleTree.Build();

        var reasons = Environments.Parent.GetBlockingReasons(parent, "parent-activate", Environments.NothingLoaded());

        Assert.Equal(["Has at least one child (cannot be checked: Children not loaded)"], reasons);
    }

    [Fact]
    public void The_same_condition_passes_once_the_navigation_is_loaded()
    {
        var parent = SampleTree.Build();

        Assert.True(Environments.Parent.CanTransition(parent, "parent-activate", Environments.Complete()));
    }

    [Fact]
    public void The_change_tracker_load_state_refuses_until_the_query_loaded_the_navigation()
    {
        using var context = new SampleDbContext();
        var parent = SampleTree.Build();
        context.Attach(parent);
        var environment = new WorkflowEnvironment(new FakeClock(Environments.Noon), NavigationLoadState.From(context));

        Assert.False(Environments.Parent.CanTransition(parent, "parent-activate", environment));

        context.Entry(parent).Collection(p => p.Children).IsLoaded = true;

        Assert.True(Environments.Parent.CanTransition(parent, "parent-activate", environment));
    }

    [Fact]
    public void The_change_tracker_load_state_treats_new_entities_as_complete_and_untracked_ones_as_unknown()
    {
        using var context = new SampleDbContext();
        var added = SampleTree.Build();
        context.Add(added);
        var untracked = SampleTree.Build();
        var loadState = NavigationLoadState.From(context);

        Assert.True(loadState.IsLoaded(added, nameof(SampleParent.Children)));
        Assert.False(loadState.IsLoaded(untracked, nameof(SampleParent.Children)));
    }

    [Fact]
    public void A_nested_navigation_path_needs_every_level_loaded()
    {
        var parent = SampleTree.Build();
        var loadOnlyChildren = new WorkflowEnvironment(new FakeClock(Environments.Noon), new OnlyLoaded(nameof(SampleParent.Children)));
        var condition = new DelegateCondition<ParentState, SampleParent>(
            "Every child has a grandchild", e => e.Children.All(c => c.Grandchildren.Count > 0), "Children.Grandchildren");

        var refused = Evaluate(condition, parent, loadOnlyChildren);
        var passed = Evaluate(condition, parent, Environments.Complete());

        Assert.Equal(["Every child has a grandchild (cannot be checked: Children.Grandchildren not loaded)"], refused);
        Assert.Empty(passed);
    }

    [Fact]
    public void Blocking_reasons_are_the_failing_conditions_descriptions()
    {
        var parent = new SampleParent { Name = "  " };

        var reasons = Environments.Parent.GetBlockingReasons(parent, "parent-activate", Environments.Complete());

        Assert.Equal(["Name is required", "Has at least one child"], reasons);
    }

    [Fact]
    public void All_reports_each_failing_inner_condition()
    {
        var all = new AllCondition<ParentState, SampleParent>(
            "Ready",
            new RequiredFieldCondition<ParentState, SampleParent>(e => e.Name),
            new RequiredFieldCondition<ParentState, SampleParent>(e => e.Notes, "Notes are written"),
            new RequiredFieldCondition<ParentState, SampleParent>(e => e.Children));

        var reasons = Evaluate(all, new SampleParent { Name = "Named" }, Environments.Complete());

        Assert.Equal(["Notes are written", "Children is required"], reasons);
        Assert.Equal([nameof(SampleParent.Children)], all.Reads);
    }

    [Fact]
    public void Any_holds_when_one_inner_condition_holds_and_reports_itself_otherwise()
    {
        var any = new AnyCondition<ParentState, SampleParent>(
            "Has a name or notes",
            new RequiredFieldCondition<ParentState, SampleParent>(e => e.Name),
            new RequiredFieldCondition<ParentState, SampleParent>(e => e.Notes));

        Assert.Empty(Evaluate(any, new SampleParent { Notes = "Some" }, Environments.Complete()));
        Assert.Equal(["Has a name or notes"], Evaluate(any, new SampleParent(), Environments.Complete()));
    }

    [Fact]
    public void RequiredField_treats_blank_text_an_empty_Guid_and_an_empty_collection_as_missing()
    {
        var child = new SampleChild();
        var parentId = new RequiredFieldCondition<ChildState, SampleChild>(e => e.ParentId);
        var grandchildren = new RequiredFieldCondition<ChildState, SampleChild>(e => e.Grandchildren);

        Assert.Equal(["ParentId is required"], Evaluate(parentId, child));
        Assert.Equal(["Grandchildren is required"], Evaluate(grandchildren, child));

        child.ParentId = Guid.NewGuid();
        child.Grandchildren.Add(new SampleGrandchild());

        Assert.Empty(Evaluate(parentId, child));
        Assert.Empty(Evaluate(grandchildren, child));
        Assert.Empty(parentId.Reads);
        Assert.Equal([nameof(SampleChild.Grandchildren)], grandchildren.Reads);
    }

    [Fact]
    public void RequiredField_declares_only_navigations_as_reads()
    {
        Assert.Equal([nameof(SampleChild.Parent)], new RequiredFieldCondition<ChildState, SampleChild>(e => e.Parent).Reads);
        Assert.Empty(new RequiredFieldCondition<BrokenState, ColumnReadEntity>(e => e.Name).Reads);
        Assert.Empty(new RequiredFieldCondition<BrokenState, ColumnReadEntity>(e => e.Tags).Reads);
        Assert.Empty(new RequiredFieldCondition<BrokenState, ColumnReadEntity>(e => e.Address).Reads);
    }

    [Fact]
    public void All_and_Any_keep_their_conditions_when_the_callers_array_changes()
    {
        Condition<ParentState, SampleParent>[] inner = [new RequiredFieldCondition<ParentState, SampleParent>(e => e.Name)];
        var all = new AllCondition<ParentState, SampleParent>("Ready", inner);
        var any = new AnyCondition<ParentState, SampleParent>("Ready", inner);

        inner[0] = new DelegateCondition<ParentState, SampleParent>("Never", _ => false);

        Assert.IsType<RequiredFieldCondition<ParentState, SampleParent>>(Assert.Single(all.Conditions));
        Assert.IsType<RequiredFieldCondition<ParentState, SampleParent>>(Assert.Single(any.Conditions));
    }

    [Fact]
    public void A_condition_reads_the_injected_clock()
    {
        var clock = new FakeClock(Environments.Noon);
        var beforeOne = DelegateCondition<ParentState, SampleParent>.FromContext("Before one o'clock", c => c.TimeProvider.GetUtcNow().Hour < 13);

        Assert.Empty(Evaluate(beforeOne, new SampleParent(), Environments.Complete(clock)));

        clock.Now = Environments.Noon.AddHours(2);

        Assert.Equal(["Before one o'clock"], Evaluate(beforeOne, new SampleParent(), Environments.Complete(clock)));
    }

    private static IReadOnlyList<string> Evaluate<TState, TEntity>(Condition<TState, TEntity> condition, TEntity entity, WorkflowEnvironment? environment = null)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        var workflow = WorkflowRegistry.Default.Get<TState, TEntity>();
        var context = workflow.CreateContext(entity, workflow.Transitions[0].Key, environment ?? Environments.Complete());
        return condition.GetBlockingReasons(context);
    }

    private sealed class OnlyLoaded : INavigationLoadState
    {
        private readonly string[] navigations;

        public OnlyLoaded(params string[] navigations)
        {
            this.navigations = navigations;
        }

        public bool IsLoaded(object entity, string navigation) => this.navigations.Contains(navigation);
    }

    private sealed class SampleDbContext : DbContext
    {
        public SampleDbContext() : base(new DbContextOptionsBuilder<SampleDbContext>().UseSqlServer(ModelUnderTest.UnopenedConnection).Options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SampleParent>().HasMany(p => p.Children).WithOne(c => c.Parent).HasForeignKey(c => c.ParentId);
            modelBuilder.Entity<SampleChild>().HasMany(c => c.Grandchildren).WithOne(g => g.Child).HasForeignKey(g => g.ChildId);
        }
    }
}
