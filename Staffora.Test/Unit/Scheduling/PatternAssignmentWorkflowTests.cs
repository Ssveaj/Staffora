using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Scheduling;

public class PatternAssignmentWorkflowTests
{
    private static readonly DateOnly Monday = new(2026, 11, 9);

    private static Workflow<ResourcePatternAssignmentState, ResourcePatternAssignment> Workflow =>
        WorkflowRegistry.Default.Get<ResourcePatternAssignmentState, ResourcePatternAssignment>();

    [Fact]
    public void The_lifecycle_is_prepared_active_withdrawn_or_deleted_with_its_stored_values()
    {
        Assert.Equal(
            [(ResourcePatternAssignmentState.Registration, 0), (ResourcePatternAssignmentState.Active, 1), (ResourcePatternAssignmentState.Inactive, 2), (ResourcePatternAssignmentState.Deleted, 3)],
            Enum.GetValues<ResourcePatternAssignmentState>().Select(state => (state, (int)state)));
        Assert.Equal(
            [
                (ResourcePatternAssignmentWorkflowAttribute.SaveStep, ResourcePatternAssignmentState.Registration, ResourcePatternAssignmentState.Registration),
                (ResourcePatternAssignmentWorkflowAttribute.ActivateStep, ResourcePatternAssignmentState.Registration, ResourcePatternAssignmentState.Active),
                (ResourcePatternAssignmentWorkflowAttribute.DeleteStep, ResourcePatternAssignmentState.Registration, ResourcePatternAssignmentState.Deleted),
                (ResourcePatternAssignmentWorkflowAttribute.SaveActiveStep, ResourcePatternAssignmentState.Active, ResourcePatternAssignmentState.Active),
                (ResourcePatternAssignmentWorkflowAttribute.DeactivateStep, ResourcePatternAssignmentState.Active, ResourcePatternAssignmentState.Inactive),
                (ResourcePatternAssignmentWorkflowAttribute.ReactivateStep, ResourcePatternAssignmentState.Inactive, ResourcePatternAssignmentState.Active),
            ],
            Workflow.Transitions.Select(transition => (transition.Key, transition.From, transition.To)));
        Assert.All(Workflow.Transitions, transition => Assert.Equal(RolePermission.AssignWorkPatterns, transition.RequiredPermission));
    }

    [Fact]
    public void A_prepared_assignment_edits_everything_but_its_staff_member_and_an_active_one_only_its_end()
    {
        var prepared = Workflow.GetFieldConfig(Assignment(ResourcePatternAssignmentState.Registration));
        var active = Workflow.GetFieldConfig(Assignment(ResourcePatternAssignmentState.Active));
        var withdrawn = Workflow.GetFieldConfig(Assignment(ResourcePatternAssignmentState.Inactive));

        Assert.Equal(FieldAccessFlags.Required, prepared.GetFieldAccess(nameof(ResourcePatternAssignment.WorkPatternId)));
        Assert.Equal(FieldAccessFlags.Required, prepared.GetFieldAccess(nameof(ResourcePatternAssignment.EffectiveFrom)));
        Assert.Equal(FieldAccessFlags.ReadWrite, prepared.GetFieldAccess(nameof(ResourcePatternAssignment.EffectiveTo)));
        Assert.Equal(FieldAccessFlags.Required, prepared.GetFieldAccess(nameof(ResourcePatternAssignment.CycleAnchor)));
        Assert.Equal(FieldAccessFlags.ReadOnly, prepared.GetFieldAccess(nameof(ResourcePatternAssignment.ResourceId)));
        Assert.Equal(FieldAccessFlags.ReadWrite, active.GetFieldAccess(nameof(ResourcePatternAssignment.EffectiveTo)));
        foreach (var field in new[] { nameof(ResourcePatternAssignment.WorkPatternId), nameof(ResourcePatternAssignment.EffectiveFrom), nameof(ResourcePatternAssignment.CycleAnchor), nameof(ResourcePatternAssignment.ResourceId) })
        {
            Assert.Equal(FieldAccessFlags.ReadOnly, active.GetFieldAccess(field));
            Assert.Equal(FieldAccessFlags.ReadOnly, withdrawn.GetFieldAccess(field));
        }

        Assert.Equal(FieldAccessFlags.ReadOnly, withdrawn.GetFieldAccess(nameof(ResourcePatternAssignment.EffectiveTo)));
    }

    [Fact]
    public void A_published_pattern_is_given_and_a_draft_or_archived_one_is_not()
    {
        var environment = WithGuard([]);

        Assert.Empty(Workflow.GetBlockingReasons(Assignment(ResourcePatternAssignmentState.Registration), ResourcePatternAssignmentWorkflowAttribute.SaveStep, environment));
        Assert.Empty(Workflow.GetBlockingReasons(Assignment(ResourcePatternAssignmentState.Registration), ResourcePatternAssignmentWorkflowAttribute.ActivateStep, environment));
        foreach (var state in new[] { WorkPatternState.Draft, WorkPatternState.Archived })
        {
            foreach (var step in new[] { ResourcePatternAssignmentWorkflowAttribute.SaveStep, ResourcePatternAssignmentWorkflowAttribute.ActivateStep })
            {
                Assert.Equal([ResourcePatternAssignmentWorkflowAttribute.PatternPublishedReason], Workflow.GetBlockingReasons(Assignment(ResourcePatternAssignmentState.Registration, state), step, environment));
            }

            Assert.Equal([ResourcePatternAssignmentWorkflowAttribute.PatternPublishedReason], Workflow.GetBlockingReasons(Assignment(ResourcePatternAssignmentState.Inactive, state), ResourcePatternAssignmentWorkflowAttribute.ReactivateStep, environment));
        }

        Assert.Empty(Workflow.GetBlockingReasons(Assignment(ResourcePatternAssignmentState.Active, WorkPatternState.Archived), ResourcePatternAssignmentWorkflowAttribute.SaveActiveStep, environment));
        Assert.Empty(Workflow.GetBlockingReasons(Assignment(ResourcePatternAssignmentState.Active, WorkPatternState.Archived), ResourcePatternAssignmentWorkflowAttribute.DeactivateStep, environment));
    }

    [Fact]
    public void A_cycle_that_does_not_start_on_a_Monday_and_an_end_before_the_start_are_refused_by_name()
    {
        var assignment = Assignment(ResourcePatternAssignmentState.Registration);
        assignment.CycleAnchor = Monday.AddDays(2);
        assignment.EffectiveTo = assignment.EffectiveFrom.AddDays(-1);

        Assert.Equal([ScheduleRules.EndsBeforeStart, ScheduleRules.AnchorNotMonday], Workflow.GetBlockingReasons(assignment, ResourcePatternAssignmentWorkflowAttribute.SaveStep, WithGuard([])));
        Assert.Equal([ScheduleRules.EndsBeforeStart, ScheduleRules.AnchorNotMonday], Workflow.GetBlockingReasons(assignment, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, WithGuard([])));
    }

    [Fact]
    public void A_staff_member_whose_record_is_inactive_is_given_nothing()
    {
        var assignment = Assignment(ResourcePatternAssignmentState.Registration);
        assignment.Resource.State = ResourceState.Inactive;

        Assert.Equal([ResourcePatternAssignmentWorkflowAttribute.StaffSetUpOpenReason], Workflow.GetBlockingReasons(assignment, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, WithGuard([])));
        Assert.Equal([ResourcePatternAssignmentWorkflowAttribute.StaffSetUpOpenReason], Workflow.GetBlockingReasons(assignment, ResourcePatternAssignmentWorkflowAttribute.SaveStep, WithGuard([])));
    }

    [Fact]
    public void A_staff_member_being_set_up_is_given_a_pattern_ahead_of_service_by_the_setting_up_rule()
    {
        var assignment = Assignment(ResourcePatternAssignmentState.Registration);
        assignment.Resource.State = ResourceState.Registration;

        Assert.Empty(Workflow.GetBlockingReasons(assignment, ResourcePatternAssignmentWorkflowAttribute.SaveStep, WithGuard([])));
        Assert.Empty(Workflow.GetBlockingReasons(assignment, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, WithGuard([])));
        Assert.True(StaffServiceRules.IsSetUpOpen(ResourceState.Registration));
        Assert.True(StaffServiceRules.IsSetUpOpen(ResourceState.Active));
        Assert.False(StaffServiceRules.IsSetUpOpen(ResourceState.Inactive));
        Assert.False(StaffServiceRules.IsInService(ResourceState.Registration, Staffora.Core.Entities.Identity.UserState.Active));
    }

    [Fact]
    public void An_activation_that_shares_a_date_with_another_active_assignment_is_refused_with_it_named()
    {
        var other = new ScheduledDates(Guid.NewGuid(), Monday.AddDays(-84), Monday.AddDays(6), "Full time");
        var assignment = Assignment(ResourcePatternAssignmentState.Registration);

        var reasons = Workflow.GetBlockingReasons(assignment, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, WithGuard([other]));

        Assert.Equal([$"It overlaps the active assignment of Full time from 2026-08-17 to 2026-11-15: a staff member works by one pattern on any date. End that assignment before this one starts, or start this one after it ends."], reasons);

        assignment.EffectiveFrom = Monday.AddDays(7);
        Assert.Empty(Workflow.GetBlockingReasons(assignment, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, WithGuard([other])));
        var withdrawn = Assignment(ResourcePatternAssignmentState.Inactive);
        withdrawn.EffectiveFrom = Monday.AddDays(7);
        Assert.Empty(Workflow.GetBlockingReasons(withdrawn, ResourcePatternAssignmentWorkflowAttribute.ReactivateStep, WithGuard([other])));
    }

    [Fact]
    public void An_active_assignments_later_end_is_judged_against_the_others_and_the_prepared_save_is_not()
    {
        var next = new ScheduledDates(Guid.NewGuid(), Monday.AddDays(28), null, "Part time");
        var active = Assignment(ResourcePatternAssignmentState.Active);
        active.EffectiveTo = Monday.AddDays(27);
        Assert.Empty(Workflow.GetBlockingReasons(active, ResourcePatternAssignmentWorkflowAttribute.SaveActiveStep, WithGuard([next])));

        active.EffectiveTo = Monday.AddDays(28);
        Assert.Single(Workflow.GetBlockingReasons(active, ResourcePatternAssignmentWorkflowAttribute.SaveActiveStep, WithGuard([next])));

        Assert.Empty(Workflow.GetBlockingReasons(Assignment(ResourcePatternAssignmentState.Registration), ResourcePatternAssignmentWorkflowAttribute.SaveStep, WithGuard([next])));
    }

    [Fact]
    public void Without_its_guard_the_one_assignment_rule_cannot_be_checked_and_blocks()
    {
        var assignment = Assignment(ResourcePatternAssignmentState.Registration);

        Assert.Throws<InvalidOperationException>(() => Workflow.GetBlockingReasons(assignment, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, new WorkflowEnvironment(new FakeClock(Environments.Noon), NavigationLoadState.Complete)));
        Assert.Throws<InvalidOperationException>(() => Workflow.GetBlockingReasons(assignment, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, new WorkflowEnvironment(new FakeClock(Environments.Noon), NavigationLoadState.Complete, new Services(new UnreadGuard()))));
    }

    [Fact]
    public void A_step_judged_without_its_pattern_or_staff_member_loaded_is_refused_never_passed()
    {
        var reasons = Workflow.GetBlockingReasons(Assignment(ResourcePatternAssignmentState.Registration), ResourcePatternAssignmentWorkflowAttribute.SaveStep, new WorkflowEnvironment(new FakeClock(Environments.Noon), NavigationLoadState.None, new Services(new FixedGuard([]))));

        Assert.Contains(reasons, reason => reason.StartsWith(ResourcePatternAssignmentWorkflowAttribute.PatternPublishedReason, StringComparison.Ordinal) && reason.Contains("not loaded", StringComparison.Ordinal));
        Assert.Contains(reasons, reason => reason.StartsWith(ResourcePatternAssignmentWorkflowAttribute.StaffSetUpOpenReason, StringComparison.Ordinal) && reason.Contains("not loaded", StringComparison.Ordinal));
    }

    private static ResourcePatternAssignment Assignment(ResourcePatternAssignmentState state, WorkPatternState patternState = WorkPatternState.Published)
    {
        var resource = new Resource { Id = Guid.NewGuid(), State = ResourceState.Active };
        var pattern = new WorkPattern { Id = Guid.NewGuid(), Name = "Weekly", State = patternState };
        return new ResourcePatternAssignment
        {
            Id = Guid.NewGuid(),
            State = state,
            ResourceId = resource.Id,
            Resource = resource,
            WorkPatternId = pattern.Id,
            WorkPattern = pattern,
            EffectiveFrom = Monday,
            CycleAnchor = Monday,
        };
    }

    private static WorkflowEnvironment WithGuard(IReadOnlyList<ScheduledDates> others) =>
        new(new FakeClock(Environments.Noon), NavigationLoadState.Complete, new Services(new FixedGuard(others)));

    private sealed class FixedGuard : IPatternAssignmentGuard
    {
        private readonly IReadOnlyList<ScheduledDates> others;

        public FixedGuard(IReadOnlyList<ScheduledDates> others)
        {
            this.others = others;
        }

        public IReadOnlyList<ScheduledDates> OtherActiveAssignmentsOf(ResourcePatternAssignment assignment) => this.others;
    }

    private sealed class UnreadGuard : IPatternAssignmentGuard
    {
        public IReadOnlyList<ScheduledDates> OtherActiveAssignmentsOf(ResourcePatternAssignment assignment) => throw new InvalidOperationException("Nothing was read.");
    }

    private sealed class Services : IServiceProvider
    {
        private readonly IPatternAssignmentGuard guard;

        public Services(IPatternAssignmentGuard guard)
        {
            this.guard = guard;
        }

        public object? GetService(Type serviceType) => serviceType == typeof(IPatternAssignmentGuard) ? this.guard : null;
    }
}
