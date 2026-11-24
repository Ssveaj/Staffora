using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Account;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Pages.Me;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Jobs;
using Staffora.Services.CustomFields;
using Staffora.Services.Messaging;
using Staffora.Services.Scheduling;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using static Staffora.Test.Integration.Infrastructure.RenderedClicks;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class StaffPagesTests : IClassFixture<RealDatabase>
{
    public static readonly IReadOnlyList<string> CheckedDialogs = [nameof(PersonScheduleDialog)];

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private static readonly DateOnly Today = new(2026, 11, 9);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly RealDatabase database;

    public StaffPagesTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_staff_member_sees_a_new_request_in_the_bell_answers_it_in_the_dialog_reports_absence_and_the_coordinator_is_told_of_both()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Staff Pages", ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)));
        var assignment = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], ada, ct));

        await using (var circuit = await CircuitAsync(ada))
        {
            using var page = circuit.Page();
            var dialogs = HostDialogs(page);
            var bell = page.Render<NotificationBell>();
            bell.WaitForAssertion(() => Assert.Equal("1", bell.Find("[data-notice-bell]").GetAttribute("data-unread-count")), Wait);
            bell.Find(".notice-bell__trigger").Click();
            bell.WaitForAssertion(() => Assert.Contains("You are asked to work Algebra", bell.Find(".notice-row--unread").TextContent, StringComparison.Ordinal), Wait);
            bell.Find(".notice-row").Click();
            dialogs.WaitForAssertion(() => Assert.NotEmpty(dialogs.FindComponents<SlotAssignmentDialog>()), Wait);
            WaitForDialog(dialogs);
            Assert.Equal(assignment, dialogs.FindComponents<SlotAssignmentDialog>().Single().Instance.Context.EntityId);
            Assert.Equal([SlotAssignmentWorkflowAttribute.AcceptStep, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, SlotAssignmentWorkflowAttribute.DeclineStep], Steps(dialogs).Order(StringComparer.Ordinal));
            ClickOnce(dialogs, () => StepButton(dialogs, SlotAssignmentWorkflowAttribute.AcceptStep));
            dialogs.WaitForAssertion(() => Assert.Equal("Accepted", dialogs.Find(".state-entity-dialog .state-pill__button").GetAttribute("data-state")), Wait);
        }

        await using (var circuit = await CircuitAsync(ada))
        {
            using var page = circuit.Page();
            var rendered = page.Render(builder => Host<MyAssignments>(builder));
            rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".list-row")), Wait);
            var row = rendered.Find(".list-row");
            Assert.Contains("Algebra", row.TextContent, StringComparison.Ordinal);
            Assert.Contains("Accepted", row.TextContent, StringComparison.Ordinal);
            Assert.Contains("the coordinator picks who works", row.TextContent, StringComparison.Ordinal);
        }

        await using (var circuit = await CircuitAsync(ada))
        {
            using var page = circuit.Page();
            var rendered = page.Render(builder => Host<MySchedule>(builder));
            rendered.WaitForAssertion(() => Assert.Equal("2026-11", rendered.Find("[data-schedule-month]").GetAttribute("data-schedule-month")), Wait);
            Assert.Equal("08:00–16:00", rendered.Find($"li[data-date='{ScheduleText(Today)}'] [data-hours]").TextContent.Trim());
            ClickOnce(rendered, () => rendered.FindAll("button").Single(button => TextOf(button) == "Report absence"));
            WaitForDialog(rendered);
            Assert.Equal([ResourceAbsenceWorkflowAttribute.ReportStep], Steps(rendered));
            Assert.Equal(Today, rendered.FindComponents<RadzenDatePicker<DateOnly>>().Single(picker => picker.Instance.Name == "absence-first-day").Instance.Value);
            rendered.Find("[name=absence-start-time]").Change("13:00");
            ClickOnce(rendered, () => StepButton(rendered, ResourceAbsenceWorkflowAttribute.ReportStep));
            rendered.WaitForAssertion(() => Assert.NotNull(rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{ResourceAbsenceWorkflowAttribute.CancelStep}']")), Wait);
            await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
            rendered.WaitForAssertion(() => Assert.Contains("Planned leave from 13:00", rendered.Find($"li[data-date='{ScheduleText(Today)}'] [data-absence]").TextContent, StringComparison.Ordinal), Wait);
            Assert.Contains("Waiting for approval", rendered.Find($"li[data-date='{ScheduleText(Today)}'] [data-absence]").TextContent, StringComparison.Ordinal);
            Assert.Equal(["Work pattern Weekdays of Ada starts", "Planned leave starts"], rendered.FindAll($"li[data-date='{ScheduleText(Today)}'] [data-note]").Select(note => note.TextContent.Trim()));
        }

        var notices = await scenario.NoticesOfAsync(this.database, scenario.Coordinator, ct);
        Assert.Contains(notices, notice => notice.Subject == "Ada Example accepted Algebra" && notice.Link == $"?dlg=assignment-{assignment}");
        var leave = Assert.Single(notices, notice => notice.Subject == "Leave requested by Ada Example");
        Assert.StartsWith("?dlg=absence-", leave.Link, StringComparison.Ordinal);
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, Guid.Parse(leave.Link!["?dlg=absence-".Length..]), ct))!.State);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_row_of_My_assignments_opens_the_staff_members_own_dialog_with_Accept_greyed_while_they_are_not_free_and_their_no_taken_with_its_reason()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Staff Not Free", ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)));
        var assignment = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], ada, ct));

        await using (var host = scenario.HostOf(this.database, scenario.Coordinator))
        {
            Made(await AbsenceScenario.RegisterAsync(host.Get<ResourceAbsenceService>(), ada, AbsenceKind.SickLeave, new DateOnly(2027, 3, 8), new DateOnly(2027, 3, 8), ct));
        }

        await using var circuit = await CircuitAsync(ada);
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<MyAssignments>(builder));
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".list-row")), Wait);
        Assert.Contains("Your answer is needed", rendered.Find(".list-row").TextContent, StringComparison.Ordinal);
        ClickOnce(rendered, () => rendered.Find(".list-row").QuerySelector("button.list-row__open")!);
        WaitForDialog(rendered);
        Assert.Equal(assignment, rendered.FindComponents<SlotAssignmentDialog>().Single().Instance.Context.EntityId);

        rendered.WaitForAssertion(() => Assert.Equal("true", StepButton(rendered, SlotAssignmentWorkflowAttribute.AcceptStep).GetAttribute("aria-disabled")), Wait);
        var reasons = StepButton(rendered, SlotAssignmentWorkflowAttribute.AcceptStep).Closest(".transition-button")!.QuerySelector(".transition-button__reasons")!.TextContent;
        Assert.Contains("not free", reasons, StringComparison.Ordinal);
        Assert.Contains("sick leave", reasons, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2027-03-08", reasons, StringComparison.Ordinal);
        Assert.Null(StepButton(rendered, SlotAssignmentWorkflowAttribute.DeclineStep).GetAttribute("aria-disabled"));

        ClickOnce(rendered, () => StepButton(rendered, SlotAssignmentWorkflowAttribute.DeclineStep));
        var prompt = rendered.WaitForElement(".form-dialog textarea");
        prompt.Change("Sick that Monday, sorry.");
        ClickOnce(rendered, () => rendered.FindAll(".form-dialog button").Last(button => TextOf(button) == "Decline"));
        rendered.WaitForAssertion(() => Assert.Equal("Declined", rendered.Find(".state-entity-dialog .state-pill__button").GetAttribute("data-state")), Wait);
        Assert.Equal(["Sick that Monday, sorry."], await AssignmentScenario.StaffThreadAsync(this.database, assignment, ct));

        await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".list-row")), Wait);
        Assert.Contains("1 closed request or assignment is hidden.", rendered.Markup, StringComparison.Ordinal);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_assigned_staff_member_opens_their_assignment_from_My_assignments_and_reads_only_the_job_fields_of_their_audience()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Staff Fields", ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, ct);
        var room = await FieldAsync(scenario, "Room", JobFieldAudience.AssignedStaff);
        var budget = await FieldAsync(scenario, "Budget code", JobFieldAudience.Coordinators);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)));
        await SetValuesAsync(scenario, job, (room, "B12"), (budget, "BX-4200"));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, ct));

        await using var circuit = await CircuitAsync(ada);
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<MyAssignments>(builder));
        rendered.WaitForAssertion(() => Assert.Contains("You are assigned", rendered.Find(".list-row").TextContent, StringComparison.Ordinal), Wait);
        ClickOnce(rendered, () => rendered.Find(".list-row").QuerySelector("button.list-row__open")!);
        WaitForDialog(rendered);
        var fields = rendered.Find(".state-entity-dialog [data-job-fields]");
        Assert.Equal(["Room"], fields.QuerySelectorAll("[data-job-field]").Select(field => field.GetAttribute("data-job-field")));
        Assert.Contains("B12", fields.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Budget code", rendered.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("BX-4200", rendered.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("assigned staff member", fields.TextContent, StringComparison.Ordinal);
        Assert.Empty(Steps(rendered));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task My_schedule_shows_the_month_its_address_names_reads_a_month_it_cannot_show_as_this_month_and_keeps_a_move_in_the_address()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Staff Month Address", ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, ct);

        await using (var circuit = await CircuitAsync(ada))
        {
            foreach (var (asked, shown) in new[] { ("2027-03", "2027-03"), ("0001-01", "2026-11"), ("2027-3", "2026-11"), ("2027-03-10", "2026-11") })
            {
                using var page = circuit.Page();
                var (rendered, writer) = RenderMonth(page, asked);
                rendered.WaitForAssertion(() => Assert.Equal(shown, rendered.Find("[data-schedule-month]").GetAttribute("data-schedule-month")), Wait);
                Assert.Empty(rendered.FindAll(".person-schedule [role=alert]"));

                Assert.All(MonthsWritten(writer), Assert.Null);
                Assert.False(rendered.Find("button[aria-label='Previous month']").HasAttribute("disabled"));
            }

            using (var page = circuit.Page())
            {
                var (rendered, writer) = RenderMonth(page, "2027-03");
                rendered.WaitForAssertion(() => Assert.Equal("2027-03", rendered.Find("[data-schedule-month]").GetAttribute("data-schedule-month")), Wait);
                ClickOnce(rendered, () => rendered.Find("button[aria-label='Next month']"));
                rendered.WaitForAssertion(() => Assert.Equal("2027-04", rendered.Find("[data-schedule-month]").GetAttribute("data-schedule-month")), Wait);
                rendered.WaitForAssertion(() => Assert.Equal(["2027-04"], MonthsWritten(writer)), Wait);
                ClickOnce(rendered, () => rendered.FindAll("button").Single(button => TextOf(button) == "Today"));
                rendered.WaitForAssertion(() => Assert.Equal("2026-11", rendered.Find("[data-schedule-month]").GetAttribute("data-schedule-month")), Wait);
                rendered.WaitForAssertion(() => Assert.Equal(new string?[] { "2027-04", null }, MonthsWritten(writer)), Wait);
                Assert.True(rendered.FindAll("button").Single(button => TextOf(button) == "Today").HasAttribute("disabled"));
            }
        }

        var pat = await scenario.StaffMembers.AddPersonAsync(this.database, "Pat", ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, pat, ct, Staffora.Core.Entities.Tenancy.RolePermission.ViewOwnSchedule);
        await using (var circuit = await CircuitAsync(pat))
        {
            using var page = circuit.Page();
            var rendered = page.Render(builder => Host<MySchedule>(builder));
            rendered.WaitForAssertion(() => Assert.Contains(StaffAvailabilityService.StaffNotFound, rendered.Find(".person-schedule [role=alert]").TextContent, StringComparison.Ordinal), Wait);
            Assert.False(rendered.FindAll("button").Single(button => TextOf(button) == "Today").HasAttribute("disabled"));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task My_schedule_draws_each_day_in_three_layers_on_one_axis_and_shows_no_request()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Staff Month Layers", ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, ct);
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)));
        var (_, physics) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(8, 0), new TimeOnly(9, 30)));
        var assigned = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, algebra[0], ada, ct));
        var asked = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, physics[0], ada, ct));

        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            Made(await AbsenceScenario.RegisterAsync(coordinator.Get<ResourceAbsenceService>(), ada, AbsenceKind.PlannedLeave, new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 8), ct));
        }

        await using var circuit = await CircuitAsync(ada);
        using var page = circuit.Page();
        var (rendered, _) = RenderMonth(page, "2027-02");
        rendered.WaitForAssertion(() => Assert.Equal("2027-02", rendered.Find("[data-schedule-month]").GetAttribute("data-schedule-month")), Wait);

        Assert.Equal(28, rendered.FindAll("li[data-date]").Count);
        Assert.Equal(["2027-02-01", "2027-02-08", "2027-02-15", "2027-02-22"], rendered.FindAll("[data-week]").Select(week => week.GetAttribute("data-week")));
        Assert.Equal("480-960", rendered.Find("[data-axis]").GetAttribute("data-axis"));

        var monday = rendered.Find("li[data-date='2027-02-01']");
        Assert.Contains("--x0: 0; --x1: 1;", monday.QuerySelector("[data-work]")!.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Null(rendered.Find("li[data-date='2027-02-07']").QuerySelector("[data-work]"));
        var bar = monday.QuerySelector($"[data-occurrence='{assigned}']")!;
        Assert.Contains("--x0: 0; --x1: 0.1875;", bar.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Equal("Algebra", bar.QuerySelector(".person-schedule__job")!.TextContent.Trim());
        Assert.Equal("08:00–09:30 · Brightpath Learning", bar.QuerySelector(".person-schedule__meta")!.TextContent.Trim());
        Assert.Empty(rendered.FindAll($"[data-occurrence='{asked}']"));
        Assert.DoesNotContain("Physics", rendered.Find(".person-schedule__days").TextContent, StringComparison.Ordinal);

        var away = rendered.Find("li[data-date='2027-02-08']");
        var leave = away.QuerySelector("[data-absence]")!;
        Assert.Contains("person-schedule__bar--whole", leave.ClassList);
        Assert.Contains("Planned leave, all day", leave.TextContent, StringComparison.Ordinal);
        Assert.Contains("Approved", leave.TextContent, StringComparison.Ordinal);
        Assert.Contains("person-schedule__bar--under", away.QuerySelector($"[data-occurrence='{assigned}']")!.ClassList);

        Assert.Contains("160 h", rendered.Find("[data-hours-working]").TextContent, StringComparison.Ordinal);
        Assert.Contains("4.5 h", rendered.Find("[data-hours-assignments]").TextContent, StringComparison.Ordinal);
        Assert.Contains("8 h", rendered.Find("[data-hours-absence]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Weekdays of Ada", rendered.Find("[data-pattern-name]").TextContent, StringComparison.Ordinal);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_coordinator_opens_a_staff_members_schedule_in_a_dialog_that_moves_its_own_month_and_offers_Register_absence()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Staff Month Dialog", ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, ct);
        var record = await AssignmentScenario.ResourceOfAsync(this.database, ada, ct);

        await using var circuit = await CircuitAsync(scenario.Coordinator);
        using var page = circuit.Page();
        var writer = page.JSInterop.SetupModule(Staffora.Blazor.Helper.UrlState.JsUrlStateWriter.ModulePath);
        writer.SetupVoid("apply", _ => true).SetVoidResult();
        page.Services.GetRequiredService<Staffora.Blazor.Helper.UrlState.UrlStateService>().MarkInteractive();
        var rendered = HostDialogs(page);
        var dialogs = page.Services.GetRequiredService<DialogService>();
        _ = rendered.InvokeAsync(() => PersonScheduleDialog.OpenAsync(dialogs, record, "Ada Example"));

        rendered.WaitForAssertion(() => Assert.Equal("2026-11", rendered.Find("[data-schedule-month]").GetAttribute("data-schedule-month")), Wait);
        Assert.Equal(record.ToString(), rendered.Find("[data-person-schedule]").GetAttribute("data-person-schedule"));
        var element = rendered.Find("[role=dialog]");
        Assert.Equal("Schedule of Ada Example", element.GetAttribute("aria-labelledby") is { } labelledBy ? rendered.Find($"#{labelledBy}").TextContent.Trim() : element.GetAttribute("aria-label"));
        Assert.Equal("08:00–16:00", rendered.Find($"li[data-date='{ScheduleText(Today)}'] [data-hours]").TextContent.Trim());

        Assert.All(rendered.FindAll("[role=dialog] button"), button => Assert.False(string.IsNullOrWhiteSpace(button.GetAttribute("aria-label") ?? button.TextContent), button.OuterHtml));
        Assert.Single(rendered.FindAll("button"), button => TextOf(button) == "Register absence");
        Assert.DoesNotContain(rendered.FindAll("button"), button => TextOf(button) == "Report absence");

        ClickOnce(rendered, () => rendered.Find("button[aria-label='Next month']"));
        rendered.WaitForAssertion(() => Assert.Equal("2026-12", rendered.Find("[data-schedule-month]").GetAttribute("data-schedule-month")), Wait);
        Assert.Empty(MonthsWritten(writer));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task My_schedule_and_My_assignments_follow_a_coordinators_edit_of_a_job_the_staff_member_holds_without_a_reload()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Staff Live", ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, ct);
        var bo = await scenario.Absence.AddWorkingStaffAsync(this.database, "Bo", Today, ct);
        var (algebra, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)));
        var (_, physics) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(8, 0), new TimeOnly(9, 30)));
        var assignment = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, ct));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, physics[0], bo, ct));
        var bus = new Staffora.Services.Coordination.TenantChangeBus(Microsoft.Extensions.Logging.Abstractions.NullLogger<Staffora.Services.Coordination.TenantChangeBus>.Instance);
        var pageClock = new ManualClock(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));

        await using var adaCircuit = await CircuitAsync(ada, bus);
        await using var boCircuit = await CircuitAsync(bo, bus);
        using var schedulePage = adaCircuit.Page(pageClock);
        var schedule = RenderMonth(schedulePage, "2027-02").Rendered;
        schedule.WaitForAssertion(() => Assert.NotNull(schedule.Find("li[data-date='2027-02-01'] [data-occurrence]")), Wait);
        schedule.WaitForAssertion(() => Assert.True(schedule.FindComponent<Staffora.Blazor.Components.Common.LiveRefresh>().Instance.IsListening), Wait);
        Assert.NotNull(schedule.FindComponent<Staffora.Blazor.Components.Common.LiveRefresh>().Instance.Since);
        using var assignmentsPage = adaCircuit.Page(pageClock);
        var assignments = assignmentsPage.Render(builder => Host<MyAssignments>(builder));
        assignments.WaitForAssertion(() => Assert.Contains("Algebra", assignments.Find(".list-row").TextContent, StringComparison.Ordinal), Wait);
        assignments.WaitForAssertion(() => Assert.True(assignments.FindComponent<Staffora.Blazor.Components.Common.LiveRefresh>().Instance.IsListening), Wait);
        Assert.NotNull(assignments.FindComponent<Staffora.Blazor.Components.Common.LiveRefresh>().Instance.Since);
        using var boPage = boCircuit.Page(pageClock);
        var boSchedule = RenderMonth(boPage, "2027-02").Rendered;
        boSchedule.WaitForAssertion(() => Assert.NotNull(boSchedule.Find("li[data-date='2027-02-02'] [data-occurrence]")), Wait);
        boSchedule.WaitForAssertion(() => Assert.True(boSchedule.FindComponent<Staffora.Blazor.Components.Common.LiveRefresh>().Instance.IsListening), Wait);

        await using (var coordinator = await CircuitAsync(scenario.Coordinator, bus))
        {
            var jobs = coordinator.Get<JobService>();
            var updater = (await jobs.LoadJobUpdaterAsync(algebra, ct)).Value!;
            updater.Entity.TimeSlots.First().Weekday = Weekday.Wednesday;
            var moved = await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveCoordinatingStep, null, ct);
            Assert.True(moved.IsSuccess, moved.Error);
        }

        schedule.WaitForAssertion(() => Assert.Equal(assignment.ToString(), schedule.Find("li[data-date='2027-02-03'] [data-occurrence]").GetAttribute("data-occurrence")), Wait);
        Assert.Null(schedule.Find("li[data-date='2027-02-01']").QuerySelector("[data-occurrence]"));
        await bus.DeliveredAsync().WaitAsync(ct);
        Assert.Equal(1, schedule.FindComponent<Staffora.Blazor.Components.Common.LiveRefresh>().Instance.Throttle!.Reloads);
        Assert.Equal(0, boSchedule.FindComponent<Staffora.Blazor.Components.Common.LiveRefresh>().Instance.Throttle!.Reloads);

        assignments.WaitForAssertion(() => Assert.Equal(1, assignments.FindComponent<Staffora.Blazor.Components.Common.LiveRefresh>().Instance.Throttle!.Reloads), Wait);
        pageClock.Advance(TimeSpan.FromSeconds(5));
        await using (var coordinator = await CircuitAsync(scenario.Coordinator, bus))
        {
            var jobs = coordinator.Get<JobService>();
            var updater = (await jobs.LoadJobUpdaterAsync(algebra, ct)).Value!;
            updater.Entity.Name = "Algebra for adults";
            var renamed = await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveCoordinatingStep, null, ct);
            Assert.True(renamed.IsSuccess, renamed.Error);
        }

        assignments.WaitForAssertion(() => Assert.Contains("Algebra for adults", assignments.Find(".list-row").TextContent, StringComparison.Ordinal), Wait);
        await bus.DeliveredAsync().WaitAsync(ct);
        Assert.Equal(0, boSchedule.FindComponent<Staffora.Blazor.Components.Common.LiveRefresh>().Instance.Throttle!.Reloads);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task My_assignments_sections_count_the_readers_own_rows_and_mark_the_one_the_address_names()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Staff Sections", ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, ct);
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)));
        var (_, physics) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(8, 0), new TimeOnly(9, 30)));
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, algebra[0], ada, ct));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, physics[0], ada, ct));

        await using var circuit = await CircuitAsync(ada);

        using (var page = circuit.Page())
        {
            var rendered = RenderAt<MyAssignments>(page, "/me/assignments?status=requested");
            rendered.WaitForAssertion(() => Assert.Equal("page", rendered.Find("a[data-section='requests']").GetAttribute("aria-current")), Wait);
            rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".list-row")), Wait);
            Assert.Contains("Algebra", rendered.Find(".list-row").TextContent, StringComparison.Ordinal);
            Assert.Equal("Requests", rendered.Find("#my-assignments-title").TextContent.Trim());
            Assert.Equal("/me/assignments?status=requested", rendered.Find("a[data-section='requests']").GetAttribute("href"));
            rendered.WaitForAssertion(() => Assert.Equal(
                [("requests", "1"), ("questions", "0"), ("assigned", "1"), ("answered", "0"), ("all", "2")],
                rendered.FindAll("a[data-section]").Select(link => (link.GetAttribute("data-section")!, link.QuerySelector("[data-count]")?.FirstChild?.TextContent.Trim()))), Wait);
            Assert.Contains("my-assignments__count--urgent", rendered.Find("a[data-section='requests'] [data-count]").ClassList);
            Assert.Null(rendered.Find("a[data-section='all']").GetAttribute("aria-current"));
        }

        using (var page = circuit.Page())
        {
            var rendered = RenderAt<MyAssignments>(page, "/me/assignments");
            rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".list-row").Count), Wait);
            Assert.Equal("page", rendered.Find("a[data-section='all']").GetAttribute("aria-current"));
            Assert.Equal("All open", rendered.Find("#my-assignments-title").TextContent.Trim());
        }
    }

    private static IRenderedComponent<IComponent> RenderAt<TPage>(PageContext page, string address) where TPage : IComponent
    {
        var writer = page.JSInterop.SetupModule(Staffora.Blazor.Helper.UrlState.JsUrlStateWriter.ModulePath);
        writer.SetupVoid("apply", _ => true).SetVoidResult();
        page.Services.GetRequiredService<NavigationManager>().NavigateTo(address);
        var urlState = page.Services.GetRequiredService<Staffora.Blazor.Helper.UrlState.UrlStateService>();
        urlState.EnterPage(typeof(TPage));
        urlState.MarkInteractive();
        return page.Render(builder => Host<TPage>(builder));
    }

    private static (IRenderedComponent<IComponent> Rendered, BunitJSModuleInterop Writer) RenderMonth(PageContext page, string month)
    {
        var writer = page.JSInterop.SetupModule(Staffora.Blazor.Helper.UrlState.JsUrlStateWriter.ModulePath);
        writer.SetupVoid("apply", _ => true).SetVoidResult();
        page.Services.GetRequiredService<NavigationManager>().NavigateTo($"/me/schedule?month={Uri.EscapeDataString(month)}");
        var urlState = page.Services.GetRequiredService<Staffora.Blazor.Helper.UrlState.UrlStateService>();
        urlState.EnterPage(typeof(MySchedule));
        urlState.MarkInteractive();
        return (page.Render(builder => Host<MySchedule>(builder)), writer);
    }

    private static List<string?> MonthsWritten(BunitJSModuleInterop writer) =>
        [.. writer.Invocations["apply"].Select(call => (IReadOnlyDictionary<string, string?>)call.Arguments[1]!).Where(changes => changes.ContainsKey(Staffora.Blazor.Helper.UrlState.UrlStateKeys.Month)).Select(changes => changes[Staffora.Blazor.Helper.UrlState.UrlStateKeys.Month])];

    private async Task<Guid> FieldAsync(AssignmentScenario scenario, string name, JobFieldAudience audience)
    {
        var onboarded = scenario.StaffMembers.Onboarded;
        await using var console = ServiceHost.Create(this.database, onboarded.Platform.PlatformOwnerRuntime());
        var created = await console.Get<CustomFieldService>().CreateFieldAsync(CustomFieldTarget.Job, new JobAttributeDefinition { State = CustomFieldState.Active, Name = name, ValueType = AttributeValueType.Text, Audience = audience }, onboarded.TenantId);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task SetValuesAsync(AssignmentScenario scenario, Guid job, params (Guid Field, string Value)[] values)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();
        var updater = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        foreach (var (field, value) in values)
        {
            updater.Entity.Attributes.Add(new JobAttribute { EntityAttributeDefinitionId = field, StringValue = value });
        }

        var saved = await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveCoordinatingStep, null, Ct);
        Assert.True(saved.IsSuccess, saved.Error);
    }

    private static string ScheduleText(DateOnly date) => Staffora.Core.Scheduling.ScheduleRules.DateText(date);

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private static List<string> Steps(IRenderedComponent<IComponent> rendered) =>
        [.. rendered.FindAll(".state-entity-dialog").Last().QuerySelectorAll("button[data-transition]").Select(button => button.GetAttribute("data-transition")!)];

    private static IElement StepButton(IRenderedComponent<IComponent> rendered, string key) =>
        rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{key}']") ?? throw new InvalidOperationException($"No button for {key}.");

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

    private async Task<Circuit> CircuitAsync(Guid userId, Staffora.Services.Coordination.TenantChangeBus? bus = null)
    {
        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(userId), services =>
        {
            AssignmentScenario.UseClock(services);
            if (bus is not null)
            {
                services.AddSingleton(bus);
            }
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

        public PageContext Page(TimeProvider? pageClock = null)
        {
            var page = new PageContext();
            page.Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
            page.Services.AddSingleton(pageClock ?? AssignmentScenario.Clock);
            page.Services.AddSingleton(this.host.Get<WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<NoticeService>());
            page.Services.AddSingleton(this.host.Get<SlotAssignmentService>());
            page.Services.AddSingleton(this.host.Get<RequestRoundService>());
            page.Services.AddSingleton(this.host.Get<StaffAvailabilityService>());
            page.Services.AddSingleton(this.host.Get<ResourceAbsenceService>());
            page.Services.AddSingleton(this.host.Get<ISlotAssignmentGuard>());
            page.Services.AddSingleton(this.host.Get<IJobSlotGuard>());
            page.Services.AddSingleton(this.host.Get<IResourceAbsenceGuard>());
            page.Services.AddSingleton(this.host.Get<Staffora.Services.Coordination.TenantChangeFeed>());
            var authorization = page.AddAuthorization();
            authorization.SetAuthorized(this.runtime.EffectiveUserId?.ToString() ?? "user");
            return page;
        }

        public DialogService Dialogs(PageContext page) => page.Services.GetRequiredService<DialogService>();

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
