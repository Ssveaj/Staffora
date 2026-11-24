using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Pages.Staffing;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Jobs;
using Staffora.Services.Customers;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using static Staffora.Test.Integration.Infrastructure.RenderedClicks;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class JobDialogsTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public JobDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Staff_register_a_job_with_a_timetable_and_start_its_coordination_with_two_slots_through_the_dialogs()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, coordinator) = await ArrangeAsync("Dialog Jobs Staffing", cancellationToken);
        var (customer, contact) = await ActiveCustomerAsync(scenario, coordinator, cancellationToken);
        await using var circuit = await CircuitAsync(coordinator);

        using (var page = circuit.Page())
        {
            var rendered = page.Render(builder => Host<Jobs>(builder));
            rendered.WaitForAssertion(() => Assert.Contains("No job yet", rendered.Markup, StringComparison.Ordinal), Wait);

            ClickOnce(rendered, () => rendered.FindAll("button").Single(button => TextOf(button) == "New job"));
            WaitForDialog(rendered);
            rendered.Find("[name=job-name]").Change("Algebra for adults");
            var dialog = rendered.FindComponents<JobDialog>().Last().Instance.Context;
            var customers = rendered.FindComponents<RadzenDropDown<Guid>>().Single(box => box.Instance.Name == "job-customer");
            Assert.Equal(["Brightpath Learning"], ((IEnumerable<Customer>?)customers.Instance.Data ?? []).Select(offered => offered.Name));
            await rendered.InvokeAsync(async () =>
            {
                await customers.Instance.ValueChanged.InvokeAsync(customer);
                dialog.NotifyEdited();
            });
            rendered.WaitForAssertion(() => Assert.Single(((IEnumerable<CustomerContact>?)rendered.FindComponents<RadzenDropDown<Guid?>>().Single(box => box.Instance.Name == "job-contact").Instance.Data) ?? []), Wait);
            var contacts = rendered.FindComponents<RadzenDropDown<Guid?>>().Single(box => box.Instance.Name == "job-contact");
            await rendered.InvokeAsync(async () =>
            {
                await contacts.Instance.ValueChanged.InvokeAsync(contact);
                dialog.NotifyEdited();
            });

            var schedule = rendered.FindComponents<RadzenDropDown<JobSchedule>>().Single(box => box.Instance.Name == "job-schedule");
            await rendered.InvokeAsync(() => schedule.Instance.ValueChanged.InvokeAsync(JobSchedule.Recurring));

            ClickOnce(rendered, () => rendered.Find($".state-entity-dialog button[data-transition='{JobWorkflowAttribute.RegisterStep}']"));
            rendered.WaitForAssertion(() => Assert.Equal("Algebra for adults", rendered.Find(".state-entity-dialog-header__title").TextContent.Trim()), Wait);
            WaitForDialog(rendered);
            Assert.Equal("Registration", rendered.Find(".state-pill__button").GetAttribute("data-state"));

            rendered.Edit();
            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=timetable]"));
            ClickOnce(rendered, () => rendered.Find("button[data-add-day='1']"));
            ClickOnce(rendered, () => rendered.Find("button[data-add-day='3']"));
            rendered.Find("[name=job-occurrence-3-1-start]").Change("10:00");
            rendered.Find("[name=job-occurrence-3-1-end]").Change("11:30");
            Assert.Empty(rendered.FindAll("[data-problems] li"));
            rendered.WaitForAssertion(() => Assert.Null(StepButton(rendered, JobWorkflowAttribute.SaveRegistrationStep).GetAttribute("aria-disabled")), Wait);
            ClickOnce(rendered, () => StepButton(rendered, JobWorkflowAttribute.SaveRegistrationStep));
            rendered.WaitForAssertion(() => Assert.Equal(2, TimeSlotCount(scenario.TenantId)), Wait);
            WaitForDialog(rendered);

            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=slots]"));
            rendered.WaitForAssertion(() => Assert.Contains("No slot yet", rendered.Markup, StringComparison.Ordinal), Wait);
            foreach (var (name, count) in new[] { ("Lead", 1), ("Assistant", 2) })
            {
                ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog button").Single(button => TextOf(button) == "Add slot"));
                rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog").Count), Wait);
                WaitForDialog(rendered);
                Assert.Contains("Algebra for adults", rendered.Find("[data-job]").TextContent, StringComparison.Ordinal);
                rendered.Find("[name=slot-name]").Change(name);
                rendered.WaitForAssertion(() => rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{JobSlotWorkflowAttribute.AddStep}']")!.Click(), Wait);
                rendered.WaitForAssertion(() => Assert.Equal(name, rendered.FindAll(".state-entity-dialog-header__title").Last().TextContent.Trim()), Wait);
                WaitForDialog(rendered);
                await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
                rendered.WaitForAssertion(() => Assert.Equal(count, rendered.FindAll(".state-entity-dialog tr.list-row").Count), Wait);
            }

            Assert.All(rendered.FindAll(".state-entity-dialog tr.list-row"), row => Assert.Contains("Uncovered", row.TextContent, StringComparison.Ordinal));

            ClickOnce(rendered, () => rendered.Find($".state-entity-dialog button[data-transition='{JobWorkflowAttribute.HandOverStep}']"));
            rendered.WaitForAssertion(() => Assert.Equal("AwaitingCoordination", rendered.Find(".state-pill__button").GetAttribute("data-state")), Wait);
            WaitForDialog(rendered);
            ClickOnce(rendered, () => rendered.Find($".state-entity-dialog button[data-transition='{JobWorkflowAttribute.StartStep}']"));
            rendered.WaitForAssertion(() => Assert.Equal("Coordinating", rendered.Find(".state-pill__button").GetAttribute("data-state")), Wait);
            WaitForDialog(rendered);
            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=info]"));
            rendered.WaitForAssertion(() => Assert.Equal(["Lead", "Assistant"], rendered.FindAll("[data-job-staff] [data-staff-slot]").Select(slot => slot.GetAttribute("data-staff-slot"))), Wait);
            Assert.All(rendered.FindAll("[data-job-staff] [data-staff-slot]"), slot => Assert.Equal("Open", slot.QuerySelector("[data-open]")!.TextContent.Trim()));
            var info = rendered.Find(".state-entity-dialog .job-dialog");
            Assert.Empty(info.QuerySelectorAll("[data-coordinator], [data-coverage], .job-dialog__facts"));
            Assert.DoesNotContain("Coordinator", info.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Coverage", info.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Register the job, then", info.TextContent, StringComparison.Ordinal);

            await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
            rendered.WaitForAssertion(() => Assert.Contains("Uncovered (2 slots)", rendered.Find("tr.list-row").TextContent, StringComparison.Ordinal), Wait);
        }

        await using var context = this.database.CreateSystemContext();
        var job = await context.Jobs.AsNoTracking().Include(row => row.TimeSlots).SingleAsync(row => row.TenantId == scenario.TenantId, cancellationToken);
        Assert.Equal(("Algebra for adults", JobState.Coordinating, customer, contact, coordinator, scenario.RootId), (job.Name, job.State, job.CustomerId, job.ContactId, job.CoordinatorUserId, job.OrganizationUnitId));
        Assert.Equal([(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 0)), (Weekday.Wednesday, new TimeOnly(10, 0), new TimeOnly(11, 30))], job.TimeSlots.OrderBy(timeSlot => timeSlot.Weekday).Select(timeSlot => (timeSlot.Weekday, timeSlot.Start, timeSlot.End)));
        Assert.Equal(["Assistant", "Lead"], await context.JobSlots.AsNoTracking().Where(slot => slot.JobId == job.Id).OrderBy(slot => slot.Name).Select(slot => slot.Name).ToListAsync(cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_new_job_offers_only_its_info_and_once_registered_its_place_timetable_slots_and_comments()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, coordinator) = await ArrangeAsync("Dialog New Job Views", cancellationToken);
        var (customer, _) = await ActiveCustomerAsync(scenario, coordinator, cancellationToken);
        await using var circuit = await CircuitAsync(coordinator);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => JobDialog.OpenCreateAsync(circuit.Dialogs(page), circuit.Get<JobService>()));
        WaitForDialog(rendered);

        Assert.Empty(rendered.FindAll(".state-entity-dialog [role=tab][data-view]"));
        Assert.Empty(rendered.FindAll(".state-entity-dialog-side"));
        Assert.NotNull(rendered.Find("[name=job-name]"));
        Assert.Empty(rendered.FindAll("[name=job-place-name], button[data-add-day]"));

        rendered.Find("[name=job-name]").Change("Office cleaning");
        var dialog = rendered.FindComponents<JobDialog>().Last().Instance.Context;
        var customers = rendered.FindComponents<RadzenDropDown<Guid>>().Single(box => box.Instance.Name == "job-customer");
        await rendered.InvokeAsync(async () =>
        {
            await customers.Instance.ValueChanged.InvokeAsync(customer);
            dialog.NotifyEdited();
        });
        var schedule = rendered.FindComponents<RadzenDropDown<JobSchedule>>().Single(box => box.Instance.Name == "job-schedule");
        await rendered.InvokeAsync(() => schedule.Instance.ValueChanged.InvokeAsync(JobSchedule.Recurring));
        rendered.WaitForAssertion(() => Assert.Null(StepButton(rendered, JobWorkflowAttribute.RegisterStep).GetAttribute("aria-disabled")), Wait);
        ClickOnce(rendered, () => StepButton(rendered, JobWorkflowAttribute.RegisterStep));
        rendered.WaitForAssertion(() => Assert.Equal("Office cleaning", rendered.Find(".state-entity-dialog-header__title").TextContent.Trim()), Wait);
        WaitForDialog(rendered);

        rendered.WaitForAssertion(() => Assert.Equal(
            [JobDialogContext.InfoView, JobDialogContext.PlaceView, JobDialogContext.TimetableView, JobDialogContext.SlotsView],
            rendered.FindAll(".state-entity-dialog [role=tab][data-view]").Select(tab => tab.GetAttribute("data-view"))), Wait);
        rendered.WaitForAssertion(() => Assert.NotNull(rendered.Find(".state-entity-dialog-side [data-thread=internal]")), Wait);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_reader_of_jobs_sees_a_job_its_timetable_and_its_slots_read_only()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, coordinator) = await ArrangeAsync("Dialog Reading Jobs", cancellationToken);
        var (customer, _) = await ActiveCustomerAsync(scenario, coordinator, cancellationToken);
        Guid job;
        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            var jobs = keeper.Get<JobService>();
            var created = await jobs.CreateJobAsync(
                new Job { Name = "Literature", CustomerId = customer, OrganizationUnitId = scenario.RootId, PeriodStart = new DateOnly(2027, 1, 11), PeriodEnd = new DateOnly(2027, 5, 28), TimeSlots = [new JobTimeSlot { Weekday = Weekday.Tuesday, Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0) }] },
                JobWorkflowAttribute.RegisterStep,
                cancellationToken);
            Assert.True(created.IsSuccess, created.Error);
            job = created.Value;
            Assert.True((await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Lead" }, JobSlotWorkflowAttribute.AddStep, cancellationToken)).IsSuccess);
        }

        var reader = await scenario.AddPersonAsync(this.database, "Reader", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, reader, cancellationToken, RolePermission.JobsView);
        await using var circuit = await CircuitAsync(reader);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => JobDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<JobService>(), job));
        WaitForDialog(rendered);
        Assert.True(rendered.Find("[name=job-name]").HasAttribute("readonly"));
        Assert.Empty(rendered.FindAll(".state-entity-dialog button[data-transition]"));
        ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=timetable]"));
        rendered.WaitForAssertion(() => Assert.Equal("read-only", rendered.Find(".state-job-timetable").GetAttribute("data-access")), Wait);
        Assert.Contains("09:00–10:00", rendered.Find("[data-occurrences]").TextContent, StringComparison.Ordinal);
        Assert.Empty(rendered.FindAll("button[data-add-day]"));
        ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=slots]"));
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-entity-dialog tr.list-row")), Wait);
        Assert.DoesNotContain(rendered.FindAll("button"), button => TextOf(button) == "Add slot");
        ClickOnce(rendered, () => rendered.Find(".state-entity-dialog tr.list-row").QuerySelector("button.list-row__open")!);
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<JobSlotDialog>()), Wait);
        WaitForDialog(rendered);
        Assert.True(rendered.Find("[name=slot-name]").HasAttribute("readonly"));
        Assert.Empty(rendered.FindAll(".state-entity-dialog").Last().QuerySelectorAll("button[data-transition]"));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_registrar_who_may_not_save_sees_hand_over_wait_while_the_job_holds_edits()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, coordinator) = await ArrangeAsync("Dialog Registrar Jobs", cancellationToken);
        var (customer, _) = await ActiveCustomerAsync(scenario, coordinator, cancellationToken);
        Guid job;
        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            var jobs = keeper.Get<JobService>();
            var created = await jobs.CreateJobAsync(
                new Job { Name = "Rhetoric", CustomerId = customer, OrganizationUnitId = scenario.RootId, PeriodStart = new DateOnly(2027, 1, 11), PeriodEnd = new DateOnly(2027, 5, 28), TimeSlots = [new JobTimeSlot { Weekday = Weekday.Tuesday, Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0) }] },
                JobWorkflowAttribute.RegisterStep,
                cancellationToken);
            Assert.True(created.IsSuccess, created.Error);
            job = created.Value;
            Assert.True((await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Lead" }, JobSlotWorkflowAttribute.AddStep, cancellationToken)).IsSuccess);
        }

        var registrar = await scenario.AddPersonAsync(this.database, "Rita", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, registrar, cancellationToken, RolePermission.JobsView, RolePermission.JobsCreate);
        await using var circuit = await CircuitAsync(registrar);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => JobDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<JobService>(), job));
        WaitForDialog(rendered);
        Assert.True(rendered.Find("[name=job-name]").HasAttribute("readonly"));
        Assert.Null(StepButton(rendered, JobWorkflowAttribute.HandOverStep).GetAttribute("aria-disabled"));
        Assert.Null(rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{JobWorkflowAttribute.SaveRegistrationStep}']"));

        var dialog = rendered.FindComponents<JobDialog>().Last().Instance.Context;
        await rendered.InvokeAsync(() =>
        {
            dialog.Entity!.Description = "Not the registrar's to write.";
            dialog.NotifyEdited();
        });
        rendered.WaitForAssertion(() =>
        {
            Assert.Equal("true", StepButton(rendered, JobWorkflowAttribute.HandOverStep).GetAttribute("aria-disabled"));
            Assert.Contains(TransitionRefusals.EditsNotYours, StepButton(rendered, JobWorkflowAttribute.HandOverStep).Closest(".transition-button")!.QuerySelector(".transition-button__reasons")!.TextContent, StringComparison.Ordinal);
        }, Wait);
        ClickOnce(rendered, () => StepButton(rendered, JobWorkflowAttribute.HandOverStep));
        await using (var context = this.database.CreateSystemContext())
        {
            Assert.Equal((JobState.Registration, (string?)null), await context.Jobs.AsNoTracking().Where(row => row.Id == job).Select(row => ValueTuple.Create(row.State, row.Description)).SingleAsync(cancellationToken));
        }

        await rendered.InvokeAsync(() =>
        {
            dialog.Entity!.Description = null;
            dialog.NotifyEdited();
        });
        rendered.WaitForAssertion(() => Assert.Null(StepButton(rendered, JobWorkflowAttribute.HandOverStep).GetAttribute("aria-disabled")), Wait);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_save_refused_for_a_holders_clash_is_saved_in_the_same_dialog_once_the_occurrence_moves_to_a_free_time()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Dialog Clash Corrected", cancellationToken);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", cancellationToken);
        var (algebra, algebraSlots) = await scenario.StartedJobAsync(this.database, "Algebra", new DateOnly(2027, 2, 1), new DateOnly(2027, 3, 26), cancellationToken, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0)));
        var (_, physicsSlots) = await scenario.StartedJobAsync(this.database, "Physics", new DateOnly(2027, 2, 1), new DateOnly(2027, 3, 26), cancellationToken, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(8, 0), new TimeOnly(10, 0)));
        Assert.True((await scenario.AssignAsync(this.database, scenario.Coordinator, algebraSlots[0], ada, cancellationToken)).IsSuccess);
        Assert.True((await scenario.AssignAsync(this.database, scenario.Coordinator, physicsSlots[0], ada, cancellationToken)).IsSuccess);

        await using var circuit = await CircuitAsync(scenario.Coordinator, AssignmentScenario.UseClock);
        using var page = circuit.Page(AssignmentScenario.Clock);
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => JobDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<JobService>(), algebra));
        WaitForDialog(rendered);
        var dialog = rendered.FindComponents<JobDialog>().Last().Instance.Context;

        rendered.Edit();
        await rendered.InvokeAsync(() =>
        {
            dialog.Entity!.TimeSlots.First().Weekday = Weekday.Tuesday;
            dialog.NotifyEdited();
        });
        rendered.WaitForAssertion(() => Assert.Null(StepButton(rendered, JobWorkflowAttribute.SaveCoordinatingStep).GetAttribute("aria-disabled")), Wait);
        ClickOnce(rendered, () => StepButton(rendered, JobWorkflowAttribute.SaveCoordinatingStep));
        rendered.WaitForAssertion(() => Assert.Contains("Ada Example is not free", rendered.Markup, StringComparison.Ordinal), Wait);
        Assert.Equal(Weekday.Monday, await StoredWeekdayAsync(algebra, cancellationToken));
        Assert.Equal(Weekday.Tuesday, dialog.Entity!.TimeSlots.First().Weekday);

        await rendered.InvokeAsync(() =>
        {
            dialog.Entity!.TimeSlots.First().Weekday = Weekday.Thursday;
            dialog.NotifyEdited();
        });
        rendered.WaitForAssertion(() =>
        {
            var save = StepButton(rendered, JobWorkflowAttribute.SaveCoordinatingStep);
            Assert.Null(save.GetAttribute("aria-disabled"));
            Assert.DoesNotContain("is not free", save.Closest(".transition-button")!.TextContent, StringComparison.Ordinal);
        }, Wait);
        ClickOnce(rendered, () => StepButton(rendered, JobWorkflowAttribute.SaveCoordinatingStep));
        await WaitForStoredWeekdayAsync(algebra, Weekday.Thursday, cancellationToken);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_job_dialog_lists_who_works_each_slot_and_staffs_a_slot_in_place_from_Find_staff()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Dialog Job Staff", cancellationToken);
        var (ada, bo, cy) = (await scenario.AddStaffAsync(this.database, "Ada", cancellationToken), await scenario.AddStaffAsync(this.database, "Bo", cancellationToken), await scenario.AddStaffAsync(this.database, "Cy", cancellationToken));
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", new DateOnly(2027, 2, 1), new DateOnly(2027, 3, 26), cancellationToken, ["Lead", "Assistant", "Tutor"]);
        Assert.True((await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, cancellationToken)).IsSuccess);
        Assert.True((await scenario.RequestAsync(this.database, scenario.Coordinator, slots[1], bo, cancellationToken)).IsSuccess);

        await using var circuit = await CircuitAsync(scenario.Coordinator, AssignmentScenario.UseClock);
        using var page = circuit.Page(AssignmentScenario.Clock);
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => JobDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<JobService>(), job));
        WaitForDialog(rendered);

        rendered.WaitForAssertion(() => Assert.Equal(3, rendered.FindAll("[data-job-staff] [data-staff-slot]").Count), Wait);
        Assert.Equal(("Ada Example", 0), StaffOf(rendered, "Lead"));
        Assert.Equal((string.Empty, 1), StaffOf(rendered, "Assistant"));
        Assert.Equal("Asked: Bo Example", SlotLine(rendered, "Assistant").QuerySelector("[data-waiting]")!.TextContent.Trim());
        Assert.Equal((string.Empty, 1), StaffOf(rendered, "Tutor"));
        Assert.Null(SlotLine(rendered, "Lead").QuerySelector("[data-find-staff]"));

        ClickOnce(rendered, () => SlotLine(rendered, "Tutor").QuerySelector("button[data-find-staff]")!);
        rendered.WaitForAssertion(() => Assert.Equal("true", rendered.Find(".state-entity-dialog [role=tab][data-view=staffing]").GetAttribute("aria-selected")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("true", rendered.Find("[data-staffing-slot='Tutor']").GetAttribute("aria-pressed")), Wait);
        rendered.WaitForAssertion(() => Assert.NotNull(rendered.Find("[data-slot-staffing] [data-candidate='Cy Example'] button[data-staff-action='assign']")), Wait);
        Assert.NotNull(rendered.Find("[data-candidate='Cy Example'] button[data-staff-action='request']"));

        ClickOnce(rendered, () => rendered.Find("[data-candidate='Cy Example'] button[data-staff-action='assign']"));
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll("[data-slot-staffing] [data-candidate='Cy Example']")), Wait);
        Assert.Single(rendered.FindAll(".state-entity-dialog"));
        ClickOnce(rendered, () => rendered.Find(".state-entity-dialog [role=tab][data-view=info]"));
        rendered.WaitForAssertion(() => Assert.Equal(("Cy Example", 0), StaffOf(rendered, "Tutor")), Wait);
        _ = cy;
    }

    private static IElement SlotLine(IRenderedComponent<IComponent> rendered, string slot) =>
        rendered.FindAll("[data-job-staff] [data-staff-slot]").Single(line => line.GetAttribute("data-staff-slot") == slot);

    private static (string Assigned, int Open) StaffOf(IRenderedComponent<IComponent> rendered, string slot)
    {
        var line = SlotLine(rendered, slot);
        return (string.Join(", ", line.QuerySelectorAll("[data-assigned]").Select(name => name.TextContent.Trim())), line.QuerySelectorAll("[data-open]").Length);
    }

    private int TimeSlotCount(Guid tenantId)
    {
        using var context = this.database.CreateSystemContext();
        return context.JobTimeSlots.AsNoTracking().Count(timeSlot => timeSlot.TenantId == tenantId);
    }

    private async Task<Weekday> StoredWeekdayAsync(Guid job, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.JobTimeSlots.AsNoTracking().Where(timeSlot => timeSlot.JobId == job).Select(timeSlot => timeSlot.Weekday).SingleAsync(cancellationToken);
    }

    private async Task WaitForStoredWeekdayAsync(Guid job, Weekday expected, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + Wait;
        var stored = await StoredWeekdayAsync(job, cancellationToken);
        while (stored != expected && DateTime.UtcNow < until)
        {
            await Task.Delay(50, cancellationToken);
            stored = await StoredWeekdayAsync(job, cancellationToken);
        }

        Assert.Equal(expected, stored);
    }

    private static IElement StepButton(IRenderedComponent<IComponent> rendered, string key) =>
        rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{key}']") ?? throw new InvalidOperationException($"No button for {key}.");

    private async Task<(StaffScenario Scenario, Guid Coordinator)> ArrangeAsync(string name, CancellationToken cancellationToken)
    {
        var scenario = await StaffScenario.CreateAsync(this.database, cancellationToken, TwoTenantScenario.UniqueName(name));
        var coordinator = await scenario.AddPersonAsync(this.database, "Casey", cancellationToken);
        await using var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(coordinator, scenario.RootId, scenario.Onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
        return (scenario, coordinator);
    }

    private async Task<(Guid Customer, Guid Contact)> ActiveCustomerAsync(StaffScenario scenario, Guid coordinator, CancellationToken cancellationToken)
    {
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var customers = host.Get<CustomerService>();
        var customer = (await customers.CreateCustomerAsync(new Customer { Name = "Brightpath Learning" }, CustomerWorkflowAttribute.ActivateStep, cancellationToken)).Value;
        var contact = (await customers.Contacts.CreateCustomerContactAsync(new CustomerContact { CustomerId = customer, FirstName = "Ada", LastName = "Moreno" }, CustomerContactWorkflowAttribute.ActivateStep, cancellationToken)).Value;
        return (customer, contact);
    }

    private static string TextOf(IElement button) => (button.QuerySelector(".rz-button-text") ?? button).TextContent.Trim();

    private static IRenderedComponent<IComponent> HostDialogs(PageContext page) => page.Render(builder =>
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
    });

    private static void Host<TPage>(RenderTreeBuilder builder) where TPage : IComponent
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
        builder.OpenComponent<Staffora.Test.Web.ShellOutlets>(2);
        builder.CloseComponent();
        builder.OpenComponent<TPage>(1);
        builder.CloseComponent();
    }

    private static void WaitForDialog(IRenderedComponent<IComponent> rendered)
    {
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("false", rendered.FindAll(".state-entity-dialog-footer__bar").Last().GetAttribute("aria-busy")), Wait);
    }

    private async Task<Circuit> CircuitAsync(Guid userId, Action<IServiceCollection>? configure = null)
    {
        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(userId), configure);
        await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        return new Circuit(host, runtime);
    }

    private sealed class Circuit : IAsyncDisposable
    {
        private readonly ServiceHost host;
        private readonly RuntimeExecutionContext runtime;

        public Circuit(ServiceHost host, RuntimeExecutionContext runtime)
        {
            this.host = host;
            this.runtime = runtime;
        }

        public PageContext Page(TimeProvider? clock = null)
        {
            var page = new PageContext();
            page.Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
            page.Services.AddSingleton(clock ?? TimeProvider.System);
            page.Services.AddSingleton(this.host.Get<WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<JobService>());
            page.Services.AddSingleton(this.host.Get<JobSlotService>());
            page.Services.AddSingleton(this.host.Get<SlotAssignmentService>());
            page.Services.AddSingleton(this.host.Get<RequestRoundService>());
            page.Services.AddSingleton(this.host.Get<IJobGuard>());
            page.Services.AddSingleton(this.host.Get<IJobSlotGuard>());
            page.Services.AddSingleton(this.host.Get<ISlotAssignmentGuard>());
            return page;
        }

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public DialogService Dialogs(PageContext page) => page.Services.GetRequiredService<DialogService>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
