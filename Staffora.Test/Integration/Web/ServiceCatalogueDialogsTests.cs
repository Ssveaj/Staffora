using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Components.Settings;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Catalogue;
using Staffora.Services.Customers;
using Staffora.Services.Jobs;
using Staffora.Services.Resources;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using static Staffora.Test.Integration.Infrastructure.RenderedClicks;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class ServiceCatalogueDialogsTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public ServiceCatalogueDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_coordinator_makes_a_slot_from_a_service_chosen_in_the_slots_view()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, coordinator, job, cleaning, firstAid) = await ArrangeAsync("Picker Staffing", cancellationToken);

        await using (var circuit = await CircuitAsync(coordinator))
        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => JobDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<JobService>(), job));
            WaitForDialog(rendered);
            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=slots]"));
            rendered.WaitForAssertion(() => Assert.Single(rendered.FindComponents<RadzenDropDown<string>>(), box => box.Instance.Name == "slot-service"), Wait);

            var picker = rendered.FindComponents<RadzenDropDown<string>>().Single(box => box.Instance.Name == "slot-service");
            var offered = ((IEnumerable<ListOption>?)picker.Instance.Data ?? []).ToList();
            Assert.Equal(["No service", "Office cleaning"], offered.Select(option => option.Label));
            await rendered.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(offered[1].Value));

            ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog button").Single(button => TextOf(button) == "Add slot"));
            rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog").Count), Wait);
            WaitForDialog(rendered);
            Assert.Equal("Office cleaning", rendered.Find("[name=slot-name]").GetAttribute("value"));
            Assert.Contains("Office cleaning", rendered.Find("[data-service]").TextContent, StringComparison.Ordinal);
            Assert.Equal(
                [("Cleaning", "required"), ("First aid", "preferred")],
                rendered.FindAll(".state-slot-qualifications__item").Select(item => (item.GetAttribute("data-qualification")!, item.GetAttribute("data-level")!)).OrderBy(item => item.Item1));

            Assert.Equal("2", rendered.Find("[name=slot-count]").GetAttribute("value"));
            rendered.WaitForAssertion(() => rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{JobSlotWorkflowAttribute.AddStep}']")!.Click(), Wait);
            rendered.WaitForAssertion(() => Assert.NotNull(rendered.FindAll(".state-entity-dialog").Last().QuerySelector("[role=tab][data-view=staff]")), Wait);
        }

        await using var context = this.database.CreateSystemContext();
        var made = await context.JobSlots.AsNoTracking().Include(row => row.Qualifications).Where(row => row.JobId == job && row.Name == "Office cleaning").ToListAsync(cancellationToken);
        Assert.Equal(2, made.Count);
        Assert.All(made, slot =>
        {
            Assert.NotNull(slot.ServiceId);
            Assert.Equal(
                new[] { (cleaning, RequirementLevel.Required), (firstAid, RequirementLevel.Preferred) }.OrderBy(row => row.Item1),
                slot.Qualifications.OrderBy(row => row.QualificationId).Select(row => (row.QualificationId, row.Level)));
        });

        var reader = await scenario.AddPersonAsync(this.database, "Reader", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, reader, cancellationToken, RolePermission.JobsView);
        await using (var circuit = await CircuitAsync(reader))
        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => JobDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<JobService>(), job));
            WaitForDialog(rendered);
            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=slots]"));
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".state-entity-dialog tr.list-row")), Wait);
            Assert.DoesNotContain(rendered.FindComponents<RadzenDropDown<string>>(), box => box.Instance.Name == "slot-service");
            Assert.DoesNotContain(rendered.FindAll("button"), button => TextOf(button) == "Add slot");
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_services_panel_lists_the_catalogue_and_offers_a_new_service_to_its_keeper_only()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, _, _, _, _) = await ArrangeAsync("Panel Staffing", cancellationToken);

        await using (var circuit = await CircuitAsync(scenario.Onboarded.OwnerId))
        using (var page = circuit.Page())
        {
            var rendered = page.Render(builder =>
            {
                builder.OpenComponent<RadzenDialog>(0);
                builder.CloseComponent();
                builder.OpenComponent<ServicesPanel>(1);
                builder.AddAttribute(2, nameof(ServicesPanel.AddressPrefix), "services");
                builder.CloseComponent();
            });
            rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll("tr.list-row").Count), Wait);
            Assert.Contains(rendered.FindAll("button"), button => TextOf(button) == "New service");
            Assert.Contains("Office cleaning", rendered.FindAll("tr.list-row")[0].TextContent, StringComparison.Ordinal);
        }

        var reader = await scenario.AddPersonAsync(this.database, "Reader", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, reader, cancellationToken, RolePermission.SettingsView);
        await using (var circuit = await CircuitAsync(reader))
        using (var page = circuit.Page())
        {
            var rendered = page.Render(builder =>
            {
                builder.OpenComponent<RadzenDialog>(0);
                builder.CloseComponent();
                builder.OpenComponent<ServicesPanel>(1);
                builder.CloseComponent();
            });
            rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll("tr.list-row").Count), Wait);
            Assert.DoesNotContain(rendered.FindAll("button"), button => TextOf(button) == "New service");
        }
    }

    private async Task<(StaffScenario Scenario, Guid Coordinator, Guid Job, Guid Cleaning, Guid FirstAid)> ArrangeAsync(string name, CancellationToken cancellationToken)
    {
        var scenario = await StaffScenario.CreateAsync(this.database, cancellationToken, TwoTenantScenario.UniqueName(name));
        var coordinator = await scenario.AddPersonAsync(this.database, "Casey", cancellationToken);
        Guid cleaning, firstAid;
        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(coordinator, scenario.RootId, scenario.Onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
            var qualifications = owner.Get<QualificationService>();
            cleaning = (await qualifications.CreateQualificationAsync(new Qualification { Name = "Cleaning" }, QualificationWorkflowAttribute.CreateStep, cancellationToken)).Value;
            firstAid = (await qualifications.CreateQualificationAsync(new Qualification { Name = "First aid" }, QualificationWorkflowAttribute.CreateStep, cancellationToken)).Value;
            var catalogue = owner.Get<ServiceCatalogueService>();
            var office = new Service { Name = "Office cleaning", DefaultSlotCount = 2 };
            office.Qualifications.Add(new ServiceQualification { QualificationId = cleaning, Level = RequirementLevel.Required });
            office.Qualifications.Add(new ServiceQualification { QualificationId = firstAid, Level = RequirementLevel.Preferred });
            Assert.True((await catalogue.CreateServiceAsync(office, ServiceWorkflowAttribute.CreateStep, cancellationToken)).IsSuccess);
            var windows = (await catalogue.CreateServiceAsync(new Service { Name = "Window cleaning" }, ServiceWorkflowAttribute.CreateStep, cancellationToken)).Value;
            var updater = (await catalogue.LoadServiceUpdaterAsync(windows, cancellationToken)).Value!;
            Assert.True((await catalogue.UpdateServiceAndTransitionAsync(updater, ServiceWorkflowAttribute.ArchiveStep, null, cancellationToken)).IsSuccess);
        }

        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var customer = (await host.Get<CustomerService>().CreateCustomerAsync(new Customer { Name = "Brightpath Learning" }, CustomerWorkflowAttribute.ActivateStep, cancellationToken)).Value;
        var jobs = host.Get<JobService>();
        var created = await jobs.CreateJobAsync(
            new Job { Name = "Head office", CustomerId = customer, OrganizationUnitId = scenario.RootId, PeriodStart = new DateOnly(2027, 1, 11), PeriodEnd = new DateOnly(2027, 5, 28), TimeSlots = [new JobTimeSlot { Weekday = Weekday.Tuesday, Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0) }] },
            JobWorkflowAttribute.RegisterStep,
            cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        Assert.True((await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = created.Value, Name = "Supervisor" }, JobSlotWorkflowAttribute.AddStep, cancellationToken)).IsSuccess);
        return (scenario, coordinator, created.Value, cleaning, firstAid);
    }

    private static string TextOf(IElement button) => (button.QuerySelector(".rz-button-text") ?? button).TextContent.Trim();

    private static IRenderedComponent<IComponent> HostDialogs(PageContext page) => page.Render(builder =>
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
    });

    private static void WaitForDialog(IRenderedComponent<IComponent> rendered)
    {
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("false", rendered.FindAll(".state-entity-dialog-footer__bar").Last().GetAttribute("aria-busy")), Wait);
    }

    private async Task<Circuit> CircuitAsync(Guid userId)
    {
        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(userId));
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

        public PageContext Page()
        {
            var page = new PageContext();
            page.Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
            page.Services.AddSingleton(TimeProvider.System);
            page.Services.AddSingleton(this.host.Get<WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<JobService>());
            page.Services.AddSingleton(this.host.Get<JobSlotService>());
            page.Services.AddSingleton(this.host.Get<SlotAssignmentService>());
            page.Services.AddSingleton(this.host.Get<RequestRoundService>());
            page.Services.AddSingleton(this.host.Get<ServiceCatalogueService>());
            page.Services.AddSingleton(this.host.Get<IJobGuard>());
            page.Services.AddSingleton(this.host.Get<IJobSlotGuard>());
            page.Services.AddSingleton(this.host.Get<IServiceGuard>());
            page.Services.AddSingleton(this.host.Get<ISlotAssignmentGuard>());
            return page;
        }

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public DialogService Dialogs(PageContext page) => page.Services.GetRequiredService<DialogService>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
