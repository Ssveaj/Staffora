using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

public sealed partial class JobLifecycleTests
{
    [Fact]
    public async Task A_registration_past_the_service_that_carries_a_slot_is_refused_and_stores_nothing()
    {
        var (scenario, coordinator) = await ArrangeAsync("Carried Slot Job Staffing");
        Guid customer, contact, archived;
        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            (customer, contact) = await ActiveCustomerAsync(keeper, "Carried School", "Ada", "Lund");
            archived = await QualificationAsync(keeper, "Archived skill");
            await ArchiveQualificationAsync(keeper, archived);
        }

        var registrar = await scenario.AddPersonAsync(this.database, "Rita", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, registrar, Ct, RolePermission.JobsView, RolePermission.JobsCreate);
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(registrar));
        var draft = Draft("Carrier", customer, contact, scenario.RootId, Occurrence(Weekday.Tuesday, 8, 0, 9, 0));
        draft.TenantId = scenario.TenantId;
        draft.Slots = [new JobSlot { TenantId = scenario.TenantId, State = JobSlotState.Active, Name = "Ghost", Qualifications = [new JobSlotQualification { TenantId = scenario.TenantId, QualificationId = archived }] }];

        var carried = await host.Get<WorkflowService>().CreateAndTransitionAsync<JobState, Job>(draft, JobWorkflowAttribute.RegisterStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);

        Assert.False(carried.IsSuccess);
        Assert.Equal("A new Job is created on its own: each JobSlot is added afterwards by its own step, so nothing was created.", carried.Error);
        Assert.Empty(await StoredJobsNamedAsync(scenario.TenantId, "Carrier"));

        draft.Slots = [];
        var registered = await host.Get<WorkflowService>().CreateAndTransitionAsync<JobState, Job>(draft, JobWorkflowAttribute.RegisterStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.True(registered.IsSuccess, registered.Error);
        Assert.Empty(await StoredSlotsAsync(registered.Value));
    }

    [Fact(Timeout = 120000)]
    public async Task Edits_that_reach_a_staffers_Start_or_a_registrars_Hand_over_while_it_waits_for_its_locks_are_refused_and_nothing_is_stored()
    {
        var (scenario, coordinator) = await ArrangeAsync("Late Edit Job Staffing");
        Guid job, second;
        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            var (customer, contact) = await ActiveCustomerAsync(keeper, "Late Edit School", "Nora", "Vik");
            job = await RegisterAsync(keeper.Get<JobService>(), "Late edit job", customer, contact, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0));
            await AddSlotAsync(keeper.Get<JobService>().Slots, job, "Lead");
            second = await RegisterAsync(keeper.Get<JobService>(), "Late time job", customer, contact, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0));
            await AddSlotAsync(keeper.Get<JobService>().Slots, second, "Lead");
        }

        var staffer = await scenario.AddPersonAsync(this.database, "Stig", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, staffer, Ct, RolePermission.JobsView, RolePermission.PerformCoordination);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(staffer), services => services.AddSingleton<IStepPreparation>(new PauseOnce(typeof(Job), entered, gate))))
        {
            var jobs = host.Get<JobService>();
            var updater = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
            var step = jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.StartFromRegistrationStep, null, Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            updater.Entity.Description = "Late edit";
            updater.Entity.TimeSlots.Add(new JobTimeSlot { TenantId = scenario.TenantId, JobId = job, Weekday = Weekday.Friday, Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0) });
            gate.SetResult();

            var started = await step;

            Assert.False(started.IsSuccess);
            Assert.Equal(NoSaveAccessFor("Start coordination"), started.Error);
            var stored = await StoredJobAsync(job);
            Assert.Equal((JobState.Registration, (string?)null, 1), (stored.State, stored.Description, stored.TimeSlots.Count));
            Assert.Equal(["job-register"], await HistoryKeysAsync(nameof(Job), job));
        }

        var registrar = await scenario.AddPersonAsync(this.database, "Rita", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, registrar, Ct, RolePermission.JobsView, RolePermission.JobsCreate);
        var enteredAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(registrar), services => services.AddSingleton<IStepPreparation>(new PauseOnce(typeof(Job), enteredAgain, gateAgain))))
        {
            var updater = (await host.Get<JobService>().LoadJobUpdaterAsync(second, Ct)).Value!;
            var step = host.Get<WorkflowService>().SaveAndTransitionAsync<JobState, Job>(updater, JobWorkflowAttribute.HandOverStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            await enteredAgain.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            updater.Entity.TimeSlots.First().End = new TimeOnly(9, 45);
            gateAgain.SetResult();

            var handedOver = await step;

            Assert.False(handedOver.IsSuccess);
            Assert.Equal(NoSaveAccessFor("Hand over"), handedOver.Error);
            var stored = await StoredJobAsync(second);
            Assert.Equal((JobState.Registration, new TimeOnly(9, 0)), (stored.State, stored.TimeSlots.Single().End));
            Assert.Equal(["job-register"], await HistoryKeysAsync(nameof(Job), second));
        }
    }

    private static string NoSaveAccessFor(string step) => Staffora.Core.Workflow.TransitionRefusals.EditsRequireSaveAccess(nameof(Job), step, NoSaveAccess);
}
