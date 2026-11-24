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
using Staffora.Blazor.Components.Coordination;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Blazor.Pages.Staffing;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Coordination;
using Staffora.Services.Jobs;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using static Staffora.Test.Integration.Infrastructure.RenderedClicks;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class StaffBoardPageTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DateOnly Today = new(2026, 11, 9);

    private readonly RealDatabase database;

    public StaffBoardPageTests(RealDatabase database)
    {
        this.database = database;
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Absence_added_from_a_staff_members_row_appears_on_the_board_beside_her_band_and_occurrence_without_a_reload()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Row Absence", ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today.AddDays(-7), Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", Today, Today.AddDays(60), Ct, ["Lead"], AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(13, 0), new TimeOnly(14, 0)));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));

        await using var circuit = await CircuitAsync(scenario.Coordinator, new TenantChangeBus(NullLogger<TenantChangeBus>.Instance));
        using var page = circuit.Page(AssignmentScenario.Clock);

        CoordinationTimeline? shown = null;
        var focusAskedWithAbsence = new List<bool>();
        var focusAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.JSInterop.SetupModule(CoordinationTimeline.ModulePath).SetupVoid("focusItem", _ =>
        {
            focusAskedWithAbsence.Add(shown?.Load.First?.StaffMembers.Any(staff => staff.Days.Any(day => day.Absences.Count > 0)) == true);
            focusAsked.TrySetResult();
            return true;
        }).SetVoidResult();
        page.Services.GetRequiredService<NavigationManager>().NavigateTo("/coordination");
        var rendered = page.Render(builder => Host<Coordination>(builder));
        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find(".coordination-timeline").GetAttribute("data-board-state")), Wait);
        shown = rendered.FindComponent<CoordinationTimeline>().Instance;

        var row = StaffRow(rendered, "Ada Example");
        Assert.Equal("08:00-16:00", Assert.Single(row.QuerySelectorAll("[data-band]")).GetAttribute("data-band"));
        Assert.Equal("2026-11-09", rendered.Find(".coordination-timeline").GetAttribute("data-board-week"));
        Assert.Contains("13:00-14:00", row.QuerySelector("[data-slot='Algebra'][data-day='2026-11-09']")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(row.QuerySelectorAll("[data-absence]"));
        var board = rendered.FindComponent<CoordinationTimeline>().Instance;
        var loads = board.Load.Loads;

        ClickOnce(rendered, () => StaffRow(rendered, "Ada Example").QuerySelector("button[data-open-schedule='Ada Example']")!);
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindComponents<PersonScheduleDialog>()), Wait);
        ClickOnce(rendered, () => rendered.FindAll("button").First(button => button.TextContent.Trim().EndsWith("Register absence", StringComparison.Ordinal)));
        WaitForDialog(rendered);
        var top = rendered.FindAll(".state-entity-dialog").Last();
        Assert.Contains("Ada Example", top.QuerySelector("[data-staff]")!.TextContent, StringComparison.Ordinal);
        var kind = rendered.FindComponents<RadzenDropDown<AbsenceKind>>().Single(box => box.Instance.Name == "absence-kind");
        var dialog = rendered.FindComponents<AbsenceDialog>().Last().Instance.Context;
        await rendered.InvokeAsync(async () =>
        {
            await kind.Instance.ValueChanged.InvokeAsync(AbsenceKind.SickLeave);
            dialog.NotifyEdited();
        });
        ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{ResourceAbsenceWorkflowAttribute.RegisterStep}']")!);
        rendered.WaitForAssertion(() => Assert.Equal("Approved", rendered.FindAll(".state-entity-dialog").Last().QuerySelector(".state-pill__button")?.GetAttribute("data-state")), Wait);
        await rendered.InvokeAsync(() => page.Services.GetRequiredService<DialogService>().Close());
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog")), Wait);
        await rendered.InvokeAsync(() => page.Services.GetRequiredService<DialogService>().Close());

        rendered.WaitForAssertion(() => Assert.Contains("Sick leave, handled", StaffRow(rendered, "Ada Example").QuerySelector("[data-absence][data-day='2026-11-09']")?.TextContent ?? string.Empty, StringComparison.Ordinal), Wait);
        Assert.Same(board, rendered.FindComponent<CoordinationTimeline>().Instance);
        Assert.True(board.Load.Loads > loads);
        row = StaffRow(rendered, "Ada Example");
        Assert.Contains("All day", row.QuerySelector("[data-absence][data-day='2026-11-09']")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("08:00-16:00", row.QuerySelector("[data-band][data-day='2026-11-09']")!.GetAttribute("data-band"));
        Assert.NotNull(row.QuerySelector("[data-slot='Algebra'][data-day='2026-11-09']"));
        Assert.Empty(rendered.FindAll(".state-entity-dialog"));

        await focusAsked.Task.WaitAsync(Wait, Ct);
        Assert.Equal([true], focusAskedWithAbsence);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_closed_day_marks_the_rows_it_applies_to_the_days_title_and_the_calendar_rail_each_with_its_name_in_words()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Closed Day", ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today.AddDays(-7), Ct);
        var bo = await scenario.StaffMembers.AddPersonAsync(this.database, "Bo", Ct);
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            var south = await owner.Get<OrganizationService>().CreateUnitIdAsync("Team South", Staffora.Core.Entities.Organization.OrganizationUnitType.Team, scenario.StaffMembers.RootId, Ct);
            Made(await owner.Get<Staffora.Services.Tenancy.RoleHoldingService>().AssignUserToRoleAsync(bo, south, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.TenantId));
        }

        await using (var admin = scenario.HostOf(this.database, scenario.Absence.Schedule.Admin))
        {
            var closedDays = admin.Get<ClosedDayService>();
            Made(await closedDays.CreateClosedDayAsync(new ClosedDayDraft("Founders' Day", Today, Today, null), Ct));
            Made(await closedDays.CreateClosedDayAsync(new ClosedDayDraft("North study day", Today, Today, scenario.StaffMembers.TeamId), Ct));
        }

        await using var circuit = await CircuitAsync(scenario.Coordinator, new TenantChangeBus(NullLogger<TenantChangeBus>.Instance));
        using var page = circuit.Page(AssignmentScenario.Clock);
        page.Services.GetRequiredService<NavigationManager>().NavigateTo("/coordination");
        var rendered = page.Render(builder => Host<Coordination>(builder));
        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find(".coordination-timeline").GetAttribute("data-board-state")), Wait);

        var adaBand = StaffRow(rendered, "Ada Example").QuerySelector("[data-closed][data-day='2026-11-09']")!;
        Assert.Equal("Closed for Founders' Day, Closed for North study day", adaBand.GetAttribute("data-closed"));
        Assert.Equal("Closed for Founders' Day, Closed for North study day, Monday 9 November, Ada Example", adaBand.QuerySelector(".visually-hidden")!.TextContent.Trim());
        Assert.Contains("timeline-closed", adaBand.ClassName, StringComparison.Ordinal);
        Assert.Equal("Closed for Founders' Day", StaffRow(rendered, "Bo Example").QuerySelector("[data-closed]")!.GetAttribute("data-closed"));
        Assert.Equal("Closed for Founders' Day, Closed for North study day in Team North", rendered.Find("[data-closed-day-note]").GetAttribute("data-closed-day-note"));

        var day = rendered.Find(".calendar-rail button.mini-month__day[data-day='2026-11-09']");
        Assert.Contains("mini-month__day--closed", day.ClassName, StringComparison.Ordinal);
        Assert.StartsWith("Monday 9 November 2026, Closed for Founders' Day, Closed for North study day in Team North", day.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Null(rendered.Find(".calendar-rail button.mini-month__day[data-day='2026-11-10']").GetAttribute("data-closed-day"));

        rendered.Find("button[data-step='next']").Click();
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll("[data-closed-day-note]")), Wait);
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".timeline-row [data-closed]")), Wait);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_second_coordinators_absence_registration_shows_on_the_first_ones_board_live_on_a_fake_clock()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Live", ct);
        var bo = await scenario.Absence.AddWorkingStaffAsync(this.database, "Bo", Today.AddDays(-7), Ct);
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var pageClock = new ManualClock(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));
        await using var first = await CircuitAsync(scenario.Coordinator, bus);
        using var page = first.Page(pageClock);
        page.Services.GetRequiredService<NavigationManager>().NavigateTo("/coordination?day=2026-11-12");
        var rendered = page.Render(builder => Host<Coordination>(builder));
        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find(".coordination-timeline").GetAttribute("data-board-state")), Wait);
        rendered.WaitForAssertion(() => Assert.True(rendered.FindComponent<LiveRefresh>().Instance.IsListening), Wait);
        Assert.Empty(StaffRow(rendered, "Bo Example").QuerySelectorAll("[data-absence]"));

        await using (var second = await CircuitAsync(scenario.Absence.Registrar, bus))
        {
            Made(await AbsenceScenario.RegisterAsync(second.Get<ResourceAbsenceService>(), bo, AbsenceKind.PlannedLeave, Today.AddDays(3), Today.AddDays(3), Ct));
        }

        rendered.WaitForAssertion(() => Assert.Contains("Planned leave, waiting for approval", StaffRow(rendered, "Bo Example").QuerySelector("[data-absence][data-day='2026-11-12']")?.TextContent ?? string.Empty, StringComparison.Ordinal), Wait);
        Assert.Equal(1, rendered.FindComponent<LiveRefresh>().Instance.Throttle!.Reloads);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_page_of_staff_members_is_read_from_the_address_written_back_by_the_pager_and_the_sort_goes_back_to_the_first_page()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Address Page", ct);
        for (var index = 0; index < BoardRules.PageSize + 1; index++)
        {
            await scenario.AddStaffAsync(this.database, $"Paged{index:000}", Ct);
        }

        await using var circuit = await CircuitAsync(scenario.Coordinator, new TenantChangeBus(NullLogger<TenantChangeBus>.Instance));
        using var page = circuit.Page(AssignmentScenario.Clock);
        var writer = page.JSInterop.SetupModule(JsUrlStateWriter.ModulePath);
        writer.SetupVoid("apply", _ => true).SetVoidResult();
        page.Services.GetRequiredService<NavigationManager>().NavigateTo("/coordination?board.page=2");
        var urlState = page.Services.GetRequiredService<UrlStateService>();
        urlState.EnterPage(typeof(Coordination));
        urlState.MarkInteractive();
        var rendered = page.Render(builder => Host<Coordination>(builder));

        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find(".coordination-timeline").GetAttribute("data-board-state")), Wait);
        Assert.Equal(["Paged025 Example"], rendered.FindAll(".timeline-row[data-staff]").Select(row => row.GetAttribute("data-staff")));
        Assert.Equal("Staff 26-26 of 26 · sorted by name", rendered.Find("[data-board-position]").TextContent);

        ClickOnce(rendered, () => rendered.Find("button[data-board-page='previous']"));
        rendered.WaitForAssertion(() => Assert.Equal(BoardRules.PageSize, rendered.FindAll(".timeline-row[data-staff]").Count), Wait);
        rendered.WaitForAssertion(() => Assert.False(PageInAddress(writer.Invocations["apply"][^1]) is { } shown && shown != "1"), Wait);

        ClickOnce(rendered, () => rendered.Find("button[data-board-page='next']"));
        rendered.WaitForAssertion(() => Assert.Equal("2", PageInAddress(writer.Invocations["apply"][^1])), Wait);
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".timeline-row[data-staff]")), Wait);

        ClickOnce(rendered, () => rendered.Find("button[data-staff-sort]"));
        rendered.WaitForAssertion(() => Assert.Equal(BoardRules.PageSize, rendered.FindAll(".timeline-row[data-staff]").Count), Wait);
        rendered.WaitForAssertion(() => Assert.False(PageInAddress(writer.Invocations["apply"][^1]) is { } shown && shown != "1"), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("Staff 1-25 of 26 · sorted by name Z to A", rendered.Find("[data-board-position]").TextContent), Wait);
    }

    private static string? Written(BunitJSModuleInterop writer, string key) =>
        writer.Invocations["apply"].Select(call => (IReadOnlyDictionary<string, string?>)call.Arguments[1]!).LastOrDefault(keys => keys.ContainsKey(key))?[key];

    private static string? PageInAddress(Bunit.JSRuntimeInvocation call) =>
        ((IReadOnlyDictionary<string, string?>)call.Arguments[1]!).TryGetValue("board.page", out var shown) ? shown : null;

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Every_row_shows_the_name_alone_the_rows_run_by_name_either_way_and_Find_staff_narrows_them()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Board Flat List", ct);
        await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today.AddDays(-7), Ct);
        await scenario.Absence.AddWorkingStaffAsync(this.database, "Bo", Today.AddDays(-7), Ct);
        await scenario.AddStaffAsync(this.database, "Cy", Ct);
        await using var circuit = await CircuitAsync(scenario.Coordinator, new TenantChangeBus(NullLogger<TenantChangeBus>.Instance));
        using var page = circuit.Page(AssignmentScenario.Clock);
        var writer = page.JSInterop.SetupModule(JsUrlStateWriter.ModulePath);
        writer.SetupVoid("apply", _ => true).SetVoidResult();
        page.Services.GetRequiredService<NavigationManager>().NavigateTo("/coordination");
        var urlState = page.Services.GetRequiredService<UrlStateService>();
        urlState.EnterPage(typeof(Coordination));
        urlState.MarkInteractive();
        var rendered = page.Render(builder => Host<Coordination>(builder));
        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find(".coordination-timeline").GetAttribute("data-board-state")), Wait);

        var names = rendered.FindAll(".timeline-row[data-staff]").Select(row => row.GetAttribute("data-staff")).ToList();
        Assert.Equal(names.Order(StringComparer.CurrentCultureIgnoreCase), names);
        Assert.Contains("Cy Example", names);
        Assert.Empty(rendered.FindAll(".timeline-group"));
        Assert.All(rendered.FindAll(".timeline-row[data-staff] .timeline-name"), cell => Assert.Equal(cell.ParentElement!.GetAttribute("data-staff"), cell.TextContent.Trim()));
        Assert.Empty(rendered.FindAll("button[data-add-absence]"));
        Assert.All(rendered.FindAll(".timeline-work"), band => Assert.Equal(string.Empty, band.TextContent));
        Assert.Contains("staff · sorted by name", rendered.Find("[data-board-position]").TextContent, StringComparison.Ordinal);

        ClickOnce(rendered, () => rendered.Find("button[data-staff-sort]"));
        rendered.WaitForAssertion(() => Assert.Equal("ztoa", Written(writer, "board.sort")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal(Enumerable.Reverse(names), rendered.FindAll(".timeline-row[data-staff]").Select(row => row.GetAttribute("data-staff"))), Wait);
        Assert.Equal("Sort, now by name Z to A", rendered.Find("button[data-staff-sort]").GetAttribute("aria-label"));

        var find = rendered.Find("input[data-find-staff]");
        find.Input("bo ex");
        find.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        rendered.WaitForAssertion(() => Assert.Equal("bo ex", Written(writer, "board.q")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal(["Bo Example"], rendered.FindAll(".timeline-row[data-staff]").Select(row => row.GetAttribute("data-staff"))), Wait);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_axis_spans_00_to_24_a_time_from_ten_to_half_past_eleven_sits_ten_hours_in_and_a_staff_member_without_times_has_a_row()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Timeline Place", ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today.AddDays(-7), Ct);
        await scenario.Absence.AddWorkingStaffAsync(this.database, "Bo", Today.AddDays(-7), Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", Today, Today.AddDays(60), Ct, ["Lead"], AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(10, 0), new TimeOnly(11, 30)));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        await using var circuit = await CircuitAsync(scenario.Coordinator, new TenantChangeBus(NullLogger<TenantChangeBus>.Instance));
        using var page = circuit.Page(AssignmentScenario.Clock);
        page.Services.GetRequiredService<NavigationManager>().NavigateTo("/coordination");
        var rendered = page.Render(builder => Host<Coordination>(builder));
        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find(".coordination-timeline").GetAttribute("data-board-state")), Wait);

        var canvas = rendered.Find(".timeline-canvas");
        Assert.Contains("--hours: 24;", canvas.GetAttribute("style"), StringComparison.Ordinal);
        var marks = rendered.FindAll(".timeline-hours__tick").Select(mark => mark.GetAttribute("data-hour")).ToList();
        Assert.Equal(Enumerable.Range(0, 25).Select(hour => hour.ToString("00", System.Globalization.CultureInfo.InvariantCulture)), marks);
        Assert.Equal(("00", "24"), (rendered.FindAll(".timeline-hours__label")[0].TextContent, rendered.FindAll(".timeline-hours__label")[^1].TextContent));
        Assert.Contains("00:00 to 24:00", rendered.Find(".timeline-hours").GetAttribute("aria-label"), StringComparison.Ordinal);
        var bar = StaffRow(rendered, "Ada Example").QuerySelector("button[data-slot='Algebra'][data-day='2026-11-09']")!;
        Assert.Contains("--from: 10; --len: 1.5;", bar.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Contains("--from: 8; --len: 8;", StaffRow(rendered, "Ada Example").QuerySelector("[data-band]")!.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Equal((640d, 96d), (TimelineRules.Pixels(10, 64), TimelineRules.Pixels(1.5, 64)));
        Assert.Equal("Algebra Brightpath Learning", bar.QuerySelector(".timeline-bar__caption--long")!.TextContent.Trim());
        Assert.Equal("Algebra, Lead, Monday 9 November 10:00-11:30, Assigned, Ada Example, Brightpath Learning", bar.QuerySelector("[data-bar-name]")!.TextContent);

        Assert.Single(rendered.FindAll(".timeline-row[data-staff='Bo Example']"));
        Assert.Empty(StaffRow(rendered, "Bo Example").QuerySelectorAll("button[data-slot]"));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_red_now_line_stands_at_the_minute_on_today_only_moves_with_the_minute_and_the_day_opens_scrolled_to_it()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Timeline Now", ct);
        await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today.AddDays(-7), Ct);
        await scenario.StartedJobAsync(this.database, "Algebra", Today, Today.AddDays(60), Ct, ["Lead"], AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(5, 30), new TimeOnly(7, 0)));
        var pageClock = new ManualClock(new DateTimeOffset(2026, 11, 9, 8, 40, 0, TimeSpan.Zero));
        await using var circuit = await CircuitAsync(scenario.Coordinator, new TenantChangeBus(NullLogger<TenantChangeBus>.Instance));
        using var page = circuit.Page(pageClock);
        var writer = page.JSInterop.SetupModule(JsUrlStateWriter.ModulePath);
        writer.SetupVoid("apply", _ => true).SetVoidResult();
        var module = page.JSInterop.SetupModule(CoordinationTimeline.ModulePath);
        module.SetupVoid("scrollToHour", _ => true).SetVoidResult();
        page.Services.GetRequiredService<NavigationManager>().NavigateTo("/coordination");
        var urlState = page.Services.GetRequiredService<UrlStateService>();
        urlState.EnterPage(typeof(Coordination));
        urlState.MarkInteractive();
        var rendered = page.Render(builder => Host<Coordination>(builder));
        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find(".coordination-timeline").GetAttribute("data-board-state")), Wait);

        var line = rendered.Find("[data-now-line]");
        Assert.Equal("09:40", line.GetAttribute("data-now-line"));
        Assert.Contains("--from: 9.6667;", line.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Equal("09:40", rendered.Find("[data-now-time]").TextContent.Trim());
        Assert.EndsWith(", now 09:40", rendered.Find(".timeline-hours").GetAttribute("aria-label"), StringComparison.Ordinal);

        rendered.WaitForAssertion(() => Assert.NotEmpty(module.Invocations["scrollToHour"]), Wait);
        Assert.Equal((9.6667, "third", false), ScrolledTo(module.Invocations["scrollToHour"][^1]));
        Assert.Equal("9.6667", rendered.Find(".timeline-scroll").GetAttribute("data-opens-at"));

        pageClock.Advance(TimeSpan.FromMinutes(1));
        rendered.WaitForAssertion(() => Assert.Equal("09:41", rendered.Find("[data-now-line]").GetAttribute("data-now-line")), Wait);
        Assert.Equal("09:41", rendered.Find("[data-now-time]").TextContent.Trim());

        var scrolls = module.Invocations["scrollToHour"].Count;
        ClickOnce(rendered, () => rendered.Find("button[data-step='next']"));
        rendered.WaitForAssertion(() => Assert.Equal("2026-11-10", rendered.Find(".coordination-timeline").GetAttribute("data-day")), Wait);
        Assert.Empty(rendered.FindAll("[data-now-line], [data-now-time]"));
        Assert.DoesNotContain("now", rendered.Find(".timeline-hours").GetAttribute("aria-label"), StringComparison.Ordinal);
        rendered.WaitForAssertion(() => Assert.Equal(scrolls + 1, module.Invocations["scrollToHour"].Count), Wait);
        Assert.Equal((5d, "start", false), ScrolledTo(module.Invocations["scrollToHour"][^1]));

        ClickOnce(rendered, () => rendered.Find("button[data-today]"));
        rendered.WaitForAssertion(() => Assert.Equal("09:41", rendered.Find("[data-now-line]").GetAttribute("data-now-line")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal((9.6833, "third", true), ScrolledTo(module.Invocations["scrollToHour"][^1])), Wait);
    }

    private static (double Hour, string Place, bool Smoothly) ScrolledTo(Bunit.JSRuntimeInvocation call) =>
        ((double)call.Arguments[1]!, (string)call.Arguments[2]!, (bool)call.Arguments[3]!);

    private static IElement StaffRow(IRenderedComponent<IComponent> rendered, string name) =>
        rendered.FindAll(".timeline-row[data-staff]").Single(row => row.GetAttribute("data-staff") == name);

    private static void WaitForDialog(IRenderedComponent<IComponent> rendered)
    {
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("false", rendered.FindAll(".state-entity-dialog-footer__bar").Last().GetAttribute("aria-busy")), Wait);
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
            page.Services.AddSingleton(this.host.Get<StaffAvailabilityService>());
            page.Services.AddSingleton(this.host.Get<JobService>());
            page.Services.AddSingleton(this.host.Get<JobSlotService>());
            page.Services.AddSingleton(this.host.Get<SlotAssignmentService>());
            page.Services.AddSingleton(this.host.Get<RequestRoundService>());
            page.Services.AddSingleton(this.host.Get<ResourceAbsenceService>());
            page.Services.AddSingleton(this.host.Get<TenantChangeFeed>());
            page.Services.AddSingleton(this.host.Get<IJobGuard>());
            page.Services.AddSingleton(this.host.Get<IJobSlotGuard>());
            page.Services.AddSingleton(this.host.Get<ISlotAssignmentGuard>());
            page.Services.AddSingleton(this.host.Get<IResourceAbsenceGuard>());
            var authorization = page.AddAuthorization();
            authorization.SetAuthorized(this.runtime.EffectiveUserId?.ToString() ?? "user");
            return page;
        }

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
