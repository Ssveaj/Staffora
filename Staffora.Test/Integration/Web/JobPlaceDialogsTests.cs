using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Pages.Portal;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Jobs;
using Staffora.Services.Portal;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using static Staffora.Test.Integration.Infrastructure.RenderedClicks;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class JobPlaceDialogsTests : IClassFixture<RealDatabase>
{
    private const string AccessNotes = "Door code 4512, key box by the gate.";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 5, 28);

    private readonly RealDatabase database;

    public JobPlaceDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task An_assigned_staff_member_reads_the_place_and_access_notes_and_a_requested_one_only_the_place()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Place Staff", ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", ct);
        var bo = await scenario.AddStaffAsync(this.database, "Bo", ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Office cleaning", PeriodStart, PeriodEnd, ct, ["Cleaner", "Supervisor"]);
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var jobs = coordinator.Get<JobService>();
            var updater = (await jobs.LoadJobUpdaterAsync(job, ct)).Value!;
            (updater.Entity.PlaceName, updater.Entity.StreetAddress, updater.Entity.PostalCode, updater.Entity.City, updater.Entity.AccessNotes) =
                ("Hillview School", "Main Street 1", "111 22", "Stockholm", AccessNotes);
            var saved = await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveCoordinatingStep, null, ct);
            Assert.True(saved.IsSuccess, saved.Error);
        }

        var assigned = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, ct));
        var requested = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[1], bo, ct));

        await using (var circuit = await CircuitAsync(ada))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => SlotAssignmentDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<SlotAssignmentService>(), assigned));
            WaitForDialog(rendered);
            var dialog = rendered.FindAll(".state-entity-dialog").Last();
            Assert.Equal("Hillview School, Main Street 1, 111 22 Stockholm", dialog.QuerySelector("[data-place]")!.TextContent.Trim());
            Assert.Equal(AccessNotes, dialog.QuerySelector("[data-access-notes]")!.TextContent.Trim());
        }

        await using (var circuit = await CircuitAsync(bo))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => SlotAssignmentDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<SlotAssignmentService>(), requested));
            WaitForDialog(rendered);
            var dialog = rendered.FindAll(".state-entity-dialog").Last();
            Assert.Equal("Hillview School, Main Street 1, 111 22 Stockholm", dialog.QuerySelector("[data-place]")!.TextContent.Trim());
            Assert.Null(dialog.QuerySelector("[data-access-notes]"));
            Assert.DoesNotContain("4512", rendered.Markup, StringComparison.Ordinal);
        }

        await using (var circuit = await CircuitAsync(scenario.Coordinator))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => JobDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<JobService>(), job));
            WaitForDialog(rendered);
            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=place]"));
            rendered.WaitForAssertion(() => Assert.Equal("Hillview School", rendered.Find("[name=job-place-name]").GetAttribute("value")), Wait);
            Assert.Contains(AccessNotes, rendered.Find("[name=job-access-notes]").GetAttribute("value") ?? rendered.Find("[name=job-access-notes]").TextContent, StringComparison.Ordinal);
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_customer_reads_its_orders_place_and_never_the_jobs_access_notes()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var portal = await PortalScenario.CreateAsync(this.database, "Place Portal", ct);
        var orderer = await portal.AddOrdererAsync(this.database, "Olive", ct);
        Guid order;
        await using (var writer = ServiceHost.Create(this.database, portal.RuntimeOf(orderer)))
        {
            var orders = writer.Get<OrderService>();
            var fresh = (await orders.NewOrderAsync(ct)).Value!;
            (fresh.Name, fresh.PeriodStart, fresh.PeriodEnd, fresh.PlaceName, fresh.StreetAddress, fresh.City) = ("Office cleaning", PeriodStart, PeriodEnd, "Hillview School", "Main Street 1", "Stockholm");
            fresh.Schedule = JobSchedule.Recurring;
            order = Made(await orders.CreateOrderAsync(fresh, OrderWorkflowAttribute.SaveDraftStep, ct));
            var sent = await orders.UpdateOrderAndTransitionAsync((await orders.LoadOrderUpdaterAsync(order, ct)).Value!, OrderWorkflowAttribute.SendStep, null, ct);
            Assert.True(sent.IsSuccess, sent.Error);
        }

        Guid job;
        await using (var system = this.database.CreateSystemContext())
        {
            job = await system.Jobs.AsNoTracking().Where(row => row.TenantId == portal.Supplier.TenantId).Select(row => row.Id).SingleAsync(ct);
        }

        await using (var supplier = ServiceHost.Create(this.database, portal.Supplier.OwnerRuntime()))
        {
            var jobs = supplier.Get<JobService>();
            var updater = (await jobs.LoadJobUpdaterAsync(job, ct)).Value!;
            updater.Entity.AccessNotes = AccessNotes;
            var saved = await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveAwaitingStep, null, ct);
            Assert.True(saved.IsSuccess, saved.Error);
        }

        await using (var circuit = await CircuitAsync(orderer))
        {
            using var page = circuit.Page();
            page.Services.AddSingleton(circuit.Get<Staffora.Services.Home.TenantHomeService>());
            page.Services.AddSingleton(circuit.Get<OrderService>());
            page.Services.AddSingleton(circuit.Get<TenantClock>());
            var authorization = page.AddAuthorization();
            authorization.SetAuthorized(orderer.ToString());
            var details = page.Render<RequestDetails>(parameters => parameters.Add(shown => shown.RequestId, order));
            details.WaitForAssertion(() => Assert.Equal("ready", details.Find("[data-order-details]").GetAttribute("data-order-details")), Wait);
            Assert.Equal("Hillview School, Main Street 1, Stockholm", details.Find("[data-order-place]").TextContent.Trim());
            Assert.DoesNotContain("4512", details.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Access notes", details.Markup, StringComparison.Ordinal);
        }
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

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
        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(userId), AssignmentScenario.UseClock);
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
            page.Services.AddSingleton(AssignmentScenario.Clock);
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

        public Radzen.DialogService Dialogs(PageContext page) => page.Services.GetRequiredService<Radzen.DialogService>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
