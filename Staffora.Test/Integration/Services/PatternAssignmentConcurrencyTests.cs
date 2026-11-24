using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Scheduling;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class PatternAssignmentConcurrencyTests : IClassFixture<RealDatabase>
{
    private const int Rounds = 12;
    private static readonly DateOnly Monday = new(2026, 11, 9);

    private readonly RealDatabase database;

    public PatternAssignmentConcurrencyTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_overlapping_activations_at_once_commit_exactly_one_every_time()
    {
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Racing Staffing", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var pattern = await scenario.PublishPatternAsync(this.database, "Weekly", Ct, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(16, 0)));
        var second = await scenario.StaffMembers.AddPersonAsync(this.database, "Second", Ct);
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(second, scenario.StaffMembers.RootId, scenario.StaffMembers.Onboarded.Roles.Administrator, scenario.TenantId)).IsSuccess);
        }

        for (var round = 0; round < Rounds; round++)
        {
            await using var left = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin));
            await using var right = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(second));
            var leftService = left.Get<PatternAssignmentService>();
            var rightService = right.Get<PatternAssignmentService>();

            var first = (await ScheduleScenario.CreateAssignmentAsync(leftService, kim, pattern, Monday, null, Ct)).Value;
            var other = (await ScheduleScenario.CreateAssignmentAsync(rightService, kim, pattern, Monday.AddDays(7), Monday.AddDays(60), Ct)).Value;
            var leftUpdater = (await leftService.LoadResourcePatternAssignmentUpdaterAsync(first, Ct)).Value!;
            var rightUpdater = (await rightService.LoadResourcePatternAssignmentUpdaterAsync(other, Ct)).Value!;

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var leftRun = Task.Run(async () => { await start.Task; return await leftService.UpdateResourcePatternAssignmentAndTransitionAsync(leftUpdater, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, null, Ct); }, Ct);
            var rightRun = Task.Run(async () => { await start.Task; return await rightService.UpdateResourcePatternAssignmentAndTransitionAsync(rightUpdater, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, null, Ct); }, Ct);
            start.SetResult();
            var results = await Task.WhenAll(leftRun, rightRun);

            Assert.Equal(1, results.Count(result => result.IsSuccess));
            var refused = results.Single(result => !result.IsSuccess);
            Assert.Contains("It overlaps the active assignment of Weekly", refused.Error, StringComparison.Ordinal);
            var states = new[] { (await ScheduleScenario.StoredAsync(this.database, first, Ct))!.State, (await ScheduleScenario.StoredAsync(this.database, other, Ct))!.State };
            Assert.Equal([ResourcePatternAssignmentState.Registration, ResourcePatternAssignmentState.Active], states.Order());

            var (winner, loser) = results[0].IsSuccess ? (first, other) : (other, first);
            Assert.True((await ScheduleScenario.StepAsync(leftService, winner, ResourcePatternAssignmentWorkflowAttribute.DeactivateStep, Ct)).IsSuccess);
            Assert.True((await ScheduleScenario.StepAsync(leftService, loser, ResourcePatternAssignmentWorkflowAttribute.DeleteStep, Ct)).IsSuccess);
        }
    }
}
