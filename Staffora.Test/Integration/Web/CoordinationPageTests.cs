using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Blazor.Pages.Staffing;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Coordination;
using Staffora.Services.Jobs;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class CoordinationPageTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 3, 26);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly RealDatabase database;

    public CoordinationPageTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_second_coordinators_assignment_updates_the_first_ones_lists_without_a_reload_at_most_every_five_seconds_on_a_fake_clock()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Live Workspace", ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var morgan = await SecondCoordinatorAsync(scenario);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var pageClock = new ManualClock(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));

        await using var first = await CircuitAsync(scenario.Coordinator, bus);
        var page = first.Page(pageClock);

        page.Services.GetRequiredService<NavigationManager>().NavigateTo("/coordination?day=2027-02-01");
        var rendered = page.Render(builder => Host<Coordination>(builder));
        rendered.WaitForAssertion(() => Assert.NotNull(OpenSlot(rendered, slots[0])), Wait);
        rendered.WaitForAssertion(() => Assert.True(rendered.FindComponent<LiveRefresh>().Instance.IsListening), Wait);
        Assert.Equal(1, bus.SubscriptionCount);
        Assert.Equal(0, rendered.FindComponent<LiveRefresh>().Instance.Since);
        Assert.Equal(0, rendered.FindComponent<LiveRefresh>().Instance.Throttle!.Reloads);
        rendered.WaitForAssertion(() => Assert.Contains("Brightpath Learning", PriorityRows(rendered).Single().TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("Algebra, 1 of 1 open", PriorityRows(rendered).Single().TextContent, StringComparison.Ordinal);

        Guid assignment;
        await using (var second = await CircuitAsync(morgan, bus))
        {
            assignment = Made(await second.Get<SlotAssignmentService>().AssignStaffAsync(slots[0], await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), Ct));
        }

        rendered.WaitForAssertion(() => Assert.Null(OpenSlot(rendered, slots[0])), Wait);
        rendered.WaitForAssertion(() => Assert.Empty(PriorityRows(rendered)), Wait);
        rendered.WaitForAssertion(() => Assert.Single(HeldBars(rendered, "Ada Example", "Algebra")), Wait);
        var throttle = rendered.FindComponent<LiveRefresh>().Instance.Throttle!;
        Assert.Equal(1, throttle.Reloads);

        await using (var second = await CircuitAsync(morgan, bus))
        {
            Made(await AssignmentScenario.StepAsync(second.Get<SlotAssignmentService>(), assignment, SlotAssignmentWorkflowAttribute.CancelAssignedStep, Ct));
        }

        rendered.WaitForAssertion(() => Assert.True(throttle.IsWaiting), Wait);
        pageClock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(1, throttle.Reloads);
        Assert.Null(OpenSlot(rendered, slots[0]));

        pageClock.Advance(TimeSpan.FromSeconds(1));
        rendered.WaitForAssertion(() => Assert.NotNull(OpenSlot(rendered, slots[0])), Wait);
        rendered.WaitForAssertion(() => Assert.Single(PriorityRows(rendered)), Wait);
        rendered.WaitForAssertion(() => Assert.Empty(HeldBars(rendered, "Ada Example", "Algebra")), Wait);
        Assert.Equal(2, throttle.Reloads);

        await page.DisposeAsync();
        Assert.Equal(0, bus.SubscriptionCount);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_day_of_the_calendar_rail_writes_the_day_into_the_address_an_old_list_address_shows_the_timeline_and_an_open_slot_opens_its_Staffing_view()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Workspace Screen", ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        await using var circuit = await CircuitAsync(scenario.Coordinator, new TenantChangeBus(NullLogger<TenantChangeBus>.Instance));
        using var page = circuit.Page(AssignmentScenario.Clock);
        var writer = page.JSInterop.SetupModule(JsUrlStateWriter.ModulePath);
        writer.SetupVoid("apply", _ => true).SetVoidResult();
        page.Services.GetRequiredService<NavigationManager>().NavigateTo("/coordination?view=list");
        var urlState = page.Services.GetRequiredService<UrlStateService>();
        urlState.EnterPage(typeof(Coordination));
        urlState.MarkInteractive();
        var rendered = page.Render(builder => Host<Coordination>(builder));

        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find(".coordination-timeline").GetAttribute("data-board-state")), Wait);
        Assert.Empty(rendered.FindAll("[data-view-choice], [data-workspace-list], #coordination-slots, .timeline-segmented"));
        Assert.NotNull(rendered.Find(".timeline-scroll [role=grid]"));

        Assert.Equal(["2026-11-01", "2026-12-01", "2027-01-01"], rendered.FindAll("table.mini-month").Select(month => month.GetAttribute("data-month")));
        Assert.Equal("date", rendered.Find("button.mini-month__day[data-day='2026-11-09']").GetAttribute("aria-current"));
        Assert.Equal("Show one month earlier, October", rendered.Find("button[data-calendar-move='earlier']").GetAttribute("aria-label"));
        Assert.Equal("Show one month later, February", rendered.Find("button[data-calendar-move='later']").GetAttribute("aria-label"));
        rendered.Find("button[data-calendar-move='later']").Click();
        rendered.WaitForAssertion(() => Assert.Equal(["2026-12-01", "2027-01-01", "2027-02-01"], rendered.FindAll("table.mini-month").Select(month => month.GetAttribute("data-month"))), Wait);
        Assert.NotNull(rendered.Find("button.mini-month__day[data-day='2027-02-01'] [data-mark='open']"));
        Assert.Contains("1 open", rendered.Find("button.mini-month__day[data-day='2027-02-01']").GetAttribute("aria-label"), StringComparison.Ordinal);
        rendered.WaitForAssertion(() => Assert.Contains("None this day", rendered.Find("[data-board-row='staff']").TextContent, StringComparison.Ordinal), Wait);

        Assert.Empty(rendered.FindAll("button.mini-month__week"));
        rendered.Find("button.mini-month__day[data-day='2027-02-01']").Click();
        rendered.WaitForAssertion(() => Assert.Equal("2027-02-01", Written(writer, UrlStateKeys.Day)), Wait);
        Assert.DoesNotContain(writer.Invocations["apply"], call => ((IReadOnlyDictionary<string, string?>)call.Arguments[1]!).Keys.Any(key => key is "week" or "mode" or "view"));
        rendered.WaitForAssertion(() => Assert.Equal("true", rendered.Find("button.mini-month__day[data-day='2027-02-01']").GetAttribute("aria-pressed")), Wait);
        rendered.WaitForAssertion(() => Assert.NotNull(OpenSlot(rendered, slots[0])), Wait);
        rendered.WaitForAssertion(() => Assert.NotNull(PriorityRows(rendered).SingleOrDefault(row => row.TextContent.Contains("Algebra, 1 of 1 open", StringComparison.Ordinal))), Wait);

        rendered.WaitForAssertion(() =>
        {
            try
            {
                OpenSlot(rendered, slots[0]).Click();
            }
            catch (Bunit.Rendering.UnknownEventHandlerIdException exception)
            {
                Assert.Fail(exception.Message);
            }
        }, Wait);
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("true", rendered.Find(".state-entity-dialog [role=tab][data-view=staffing]").GetAttribute("aria-selected")), Wait);
        Assert.Equal(slots[0], rendered.FindComponents<JobSlotDialog>().Single().Instance.Context.EntityId);
        Assert.Equal(JobSlotDialogContext.StaffingView, DialogViewOnPageAttribute.For(typeof(Coordination), DialogTokens.JobSlot));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Folding_the_Priorities_dock_goes_into_the_address_and_a_page_opened_on_that_address_keeps_it_folded()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Workspace Dock", ct);
        await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        await using var circuit = await CircuitAsync(scenario.Coordinator, new TenantChangeBus(NullLogger<TenantChangeBus>.Instance));
        using (var page = circuit.Page(AssignmentScenario.Clock))
        {
            var writer = page.JSInterop.SetupModule(JsUrlStateWriter.ModulePath);
            writer.SetupVoid("apply", _ => true).SetVoidResult();
            page.Services.GetRequiredService<NavigationManager>().NavigateTo("/coordination");
            var urlState = page.Services.GetRequiredService<UrlStateService>();
            urlState.EnterPage(typeof(Coordination));
            urlState.MarkInteractive();
            var rendered = page.Render(builder => Host<Coordination>(builder));
            rendered.WaitForAssertion(() => Assert.Equal("open", rendered.Find("[data-priorities]").GetAttribute("data-dock")), Wait);
            rendered.WaitForAssertion(() => Assert.Equal("1", rendered.Find("[data-priorities-total]").TextContent), Wait);

            Assert.Empty(rendered.FindAll("[data-priorities-focus], .priorities-focus"));
            var list = rendered.Find("#priorities-panel > ul.priorities-list");
            Assert.All(list.Children, row => Assert.True(row.HasAttribute("data-priorities-item"), row.OuterHtml));
            Assert.Equal(["Uncovered"], list.QuerySelectorAll(".priorities-pill").Select(pill => pill.TextContent.Trim()));
            Assert.Empty(rendered.FindAll("[data-priorities-group], .priorities-group, .priorities-groups, [data-priorities-show-all]"));
            Assert.DoesNotContain("Show all", rendered.Find("[data-priorities]").TextContent, StringComparison.Ordinal);
            Assert.Empty(rendered.FindAll("[data-priorities-more]"));

            rendered.Find("button[data-priorities-fold]").Click();
            rendered.WaitForAssertion(() => Assert.Equal("closed", Written(writer, UrlStateKeys.Dock)), Wait);
            rendered.WaitForAssertion(() => Assert.Equal("closed", rendered.Find("[data-priorities]").GetAttribute("data-dock")), Wait);
            Assert.Equal("Show Priorities", rendered.Find("button[data-priorities-fold]").GetAttribute("aria-label"));
            Assert.Equal(["Slots 1"], rendered.FindAll(".priorities__bar-tabs li").Select(tab => tab.TextContent.Trim()));
            Assert.DoesNotContain("Most urgent", rendered.Find(".priorities__bar").TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Algebra", rendered.Find(".priorities__bar").TextContent, StringComparison.Ordinal);
            Assert.Empty(rendered.FindAll("[data-priorities-item]"));
        }

        using var reloaded = circuit.Page(AssignmentScenario.Clock);
        reloaded.Services.GetRequiredService<NavigationManager>().NavigateTo("/coordination?dock=closed");
        var again = reloaded.Render(builder => Host<Coordination>(builder));
        again.WaitForAssertion(() => Assert.Equal("closed", again.Find("[data-priorities]").GetAttribute("data-dock")), Wait);
        again.Find("button[data-priorities-fold]").Click();
        again.WaitForAssertion(() => Assert.Single(PriorityRows(again)), Wait);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_Priorities_row_is_the_job_once_per_kind_and_opens_the_jobs_dialog_with_no_Request_or_Assign_button()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Workspace Job Rows", ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant", "Tutor"]);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        await using var circuit = await CircuitAsync(scenario.Coordinator, new TenantChangeBus(NullLogger<TenantChangeBus>.Instance));
        using var page = circuit.Page(AssignmentScenario.Clock);
        page.Services.GetRequiredService<NavigationManager>().NavigateTo("/coordination");
        var rendered = page.Render(builder => Host<Coordination>(builder));

        rendered.WaitForAssertion(() => Assert.Single(PriorityRows(rendered)), Wait);
        var row = PriorityRows(rendered).Single();
        Assert.Contains("Algebra, 2 of 3 open", row.TextContent, StringComparison.Ordinal);
        Assert.Equal(["open"], row.QuerySelectorAll("button[data-priorities-action]").Select(button => button.GetAttribute("data-priorities-action")));
        Assert.DoesNotContain(rendered.FindAll("[data-priorities] button"), button => button.TextContent.Trim() is "Request" or "Assign" or "Staff" or "Pick" or "Replace");
        Assert.Equal("1", rendered.Find("[data-priorities-total]").TextContent);
        Assert.Empty(rendered.FindAll("#priorities-tab-behind"));

        rendered.WaitForAssertion(() =>
        {
            try
            {
                PriorityRows(rendered).Single().QuerySelector("button[data-priorities-action='open']")!.Click();
            }
            catch (Bunit.Rendering.UnknownEventHandlerIdException exception)
            {
                Assert.Fail(exception.Message);
            }
        }, Wait);
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
        Assert.Equal(job, rendered.FindComponents<JobDialog>().Single().Instance.Context.EntityId);
        Assert.Empty(rendered.FindComponents<JobSlotDialog>());
        rendered.WaitForAssertion(() => Assert.Equal(["Ada Example"], rendered.FindAll("[data-job-staff] [data-assigned]").Select(name => name.TextContent.Trim())), Wait);
        Assert.Equal(2, rendered.FindAll("[data-job-staff] [data-open]").Count);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_Open_slots_link_narrows_Priorities_to_the_shown_day_and_a_staff_members_name_opens_their_schedule()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Workspace Day Link", ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var record = await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct);
        await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        await using var circuit = await CircuitAsync(scenario.Coordinator, new TenantChangeBus(NullLogger<TenantChangeBus>.Instance));
        using var page = circuit.Page(AssignmentScenario.Clock);
        var writer = page.JSInterop.SetupModule(JsUrlStateWriter.ModulePath);
        writer.SetupVoid("apply", _ => true).SetVoidResult();
        page.Services.GetRequiredService<NavigationManager>().NavigateTo("/coordination?day=2027-02-01");
        var urlState = page.Services.GetRequiredService<UrlStateService>();
        urlState.EnterPage(typeof(Coordination));
        urlState.MarkInteractive();
        var rendered = page.Render(builder => Host<Coordination>(builder));

        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find(".coordination-timeline").GetAttribute("data-board-state")), Wait);
        Assert.Equal("Monday 1 February", rendered.Find("#timeline-title").TextContent.Trim());
        Assert.Equal("Week 5 · 2027", rendered.Find("[data-week-line]").TextContent.Trim());
        rendered.WaitForAssertion(() => Assert.Contains("1 on Monday", rendered.Find("button[data-open-slots-link]").TextContent, StringComparison.Ordinal), Wait);
        rendered.Find("button[data-open-slots-link]").Click();
        rendered.WaitForAssertion(() => Assert.Equal("2027-02-01", Written(writer, "prio.day")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("2027-02-01", rendered.Find("button[data-priorities-day]").GetAttribute("data-priorities-day")), Wait);
        rendered.WaitForAssertion(() => Assert.Contains("Algebra, 1 of 1 open", PriorityRows(rendered).Single().TextContent, StringComparison.Ordinal), Wait);

        await rendered.InvokeAsync(() => rendered.FindComponent<Staffora.Blazor.Components.Coordination.PrioritiesDock>().Instance.ShowDayAsync(new DateOnly(2027, 2, 2)));
        rendered.WaitForAssertion(() => Assert.Empty(PriorityRows(rendered)), Wait);

        var name = rendered.Find("button[data-open-schedule='Ada Example']");
        Assert.Equal("Ada Example", name.TextContent.Trim());
        Assert.Equal("Open the schedule of Ada Example", name.GetAttribute("title"));
        name.Click();
        rendered.WaitForAssertion(() => Assert.Equal(record, rendered.FindComponents<PersonScheduleDialog>().Single().Instance.ResourceId), Wait);
        Assert.Equal(record.ToString(), rendered.Find("[data-person-schedule]").GetAttribute("data-person-schedule"));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_change_committed_before_a_page_listened_is_read_once_it_listens_and_a_page_with_nothing_missed_reads_nothing_more()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Live Mark", ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var morgan = await SecondCoordinatorAsync(scenario);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var pageClock = new ManualClock(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));
        await using var first = await CircuitAsync(scenario.Coordinator, bus);
        using var page = first.Page(pageClock);

        var mark = first.Get<TenantChangeFeed>().Mark();
        Assert.Equal(0, mark);
        await using (var second = await CircuitAsync(morgan, bus))
        {
            Made(await second.Get<SlotAssignmentService>().AssignStaffAsync(slots[0], await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), Ct));
        }

        await bus.DeliveredAsync().WaitAsync(ct);
        Assert.Equal(1, first.Get<TenantChangeFeed>().Mark());

        var (missed, current) = (0, 0);
        var late = page.Render<LiveRefresh>(parameters => parameters
            .Add(refresh => refresh.Interest, ChangeInterest.Coordination)
            .Add(refresh => refresh.OnChange, () => { missed++; return Task.CompletedTask; })
            .Add(refresh => refresh.Since, mark));
        var upToDate = page.Render<LiveRefresh>(parameters => parameters
            .Add(refresh => refresh.Interest, ChangeInterest.Coordination)
            .Add(refresh => refresh.OnChange, () => { current++; return Task.CompletedTask; })
            .Add(refresh => refresh.Since, first.Get<TenantChangeFeed>().Mark()));
        late.WaitForAssertion(() => Assert.True(late.Instance.IsListening), Wait);
        upToDate.WaitForAssertion(() => Assert.True(upToDate.Instance.IsListening), Wait);
        late.WaitForAssertion(() => Assert.Equal(1, missed), Wait);
        Assert.Equal(0, current);
        Assert.Equal((1, 0), (late.Instance.Throttle!.Reloads, upToDate.Instance.Throttle!.Reloads));
    }

    private static string? Written(BunitJSModuleInterop writer, string key) =>
        writer.Invocations["apply"].Select(call => (IReadOnlyDictionary<string, string?>)call.Arguments[1]!).LastOrDefault(keys => keys.ContainsKey(key))?[key];

    private static IElement OpenSlot(IRenderedComponent<IComponent> rendered, Guid slot) =>
        rendered.FindAll($"[data-board-row='staff'] [data-open-slot='{slot}']").SingleOrDefault()!;

    private static IReadOnlyList<IElement> HeldBars(IRenderedComponent<IComponent> rendered, string staff, string job) =>
        rendered.FindAll($".timeline-row[data-staff='{staff}'] button.timeline-bar--slot[data-slot='{job}']");

    private static IReadOnlyList<IElement> PriorityRows(IRenderedComponent<IComponent> rendered) => rendered.FindAll("[data-priorities-item]");

    private async Task<Guid> SecondCoordinatorAsync(AssignmentScenario scenario)
    {
        var morgan = await scenario.StaffMembers.AddPersonAsync(this.database, "Morgan", Ct);
        await using var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime());
        var given = await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(morgan, scenario.StaffMembers.RootId, scenario.StaffMembers.Onboarded.Roles.Coordinator, scenario.TenantId);
        Assert.True(given.IsSuccess, given.Error);
        return morgan;
    }

    private static void Host<TPage>(RenderTreeBuilder builder) where TPage : IComponent
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
        builder.OpenComponent<Staffora.Test.Web.ShellOutlets>(2);
        builder.CloseComponent();
        builder.OpenComponent<TPage>(1);
        builder.CloseComponent();
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private static void Made(Result done) => Assert.True(done.IsSuccess, done.Error);

    private async Task<Circuit> CircuitAsync(Guid userId, TenantChangeBus bus)
    {
        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(userId), services =>
        {
            AssignmentScenario.UseClock(services);
            services.AddSingleton(bus);
        });
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

        public PageContext Page(TimeProvider pageClock)
        {
            var page = new PageContext();
            page.Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
            page.Services.AddSingleton(pageClock);
            page.Services.AddSingleton(this.host.Get<WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<CoordinationService>());
            page.Services.AddSingleton(this.host.Get<Staffora.Services.Settings.TenantClock>());
            page.Services.AddSingleton(this.host.Get<Staffora.Services.Scheduling.ClosedDayService>());
            page.Services.AddSingleton(this.host.Get<Staffora.Services.Scheduling.StaffAvailabilityService>());
            page.Services.AddSingleton(this.host.Get<Staffora.Services.Scheduling.ResourceAbsenceService>());
            page.Services.AddSingleton(this.host.Get<JobService>());
            page.Services.AddSingleton(this.host.Get<JobSlotService>());
            page.Services.AddSingleton(this.host.Get<SlotAssignmentService>());
            page.Services.AddSingleton(this.host.Get<RequestRoundService>());
            page.Services.AddSingleton(this.host.Get<TenantChangeFeed>());
            page.Services.AddSingleton(this.host.Get<IJobGuard>());
            page.Services.AddSingleton(this.host.Get<IJobSlotGuard>());
            page.Services.AddSingleton(this.host.Get<ISlotAssignmentGuard>());
            var authorization = page.AddAuthorization();
            authorization.SetAuthorized(this.runtime.EffectiveUserId?.ToString() ?? "user");
            return page;
        }

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
