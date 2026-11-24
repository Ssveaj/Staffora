using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Layout;
using Staffora.Blazor.Pages;
using Staffora.Blazor.Pages.Me;
using Staffora.Blazor.Pages.Staffing;
using Staffora.Blazor.Pages.TenantAdmin;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Jobs;
using Staffora.Services.Resources;
using Staffora.Services.Home;
using Staffora.Services.Scheduling;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class HomePagesTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset Afternoon = new(2026, 11, 9, 14, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Evening = new(2026, 11, 9, 18, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 9);
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly RealDatabase database;

    public HomePagesTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task The_start_page_sends_each_session_to_its_own_home_and_an_impersonating_platform_owner_to_the_impersonated_users()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Homes", Ct);
        var tenant = scenario.StaffMembers.Onboarded.Tenant;
        var platformOwner = scenario.StaffMembers.Onboarded.Platform.OwnerId;
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var pat = await scenario.StaffMembers.AddPersonAsync(this.database, "Pat", Ct);

        var teachingCoordinator = await scenario.AddStaffAsync(this.database, "Cam", Ct);
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(teachingCoordinator, scenario.StaffMembers.RootId, scenario.StaffMembers.Onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
        }

        Assert.Equal(AppHome.SetupChecklist, await LandAsync(SessionCircuit.Cookie(tenant.OwnerId)));
        Assert.Equal(AppHome.SetupChecklist, await LandAsync(SessionCircuit.Cookie(scenario.Absence.Schedule.Admin)));
        Assert.Equal(AppHome.CoordinatorOverview, await LandAsync(SessionCircuit.Cookie(scenario.Coordinator)));
        Assert.Equal(AppHome.CoordinatorOverview, await LandAsync(SessionCircuit.Cookie(teachingCoordinator)));
        Assert.Equal(AppHome.MyWork, await LandAsync(SessionCircuit.Cookie(ada)));
        Assert.Equal(AppHome.MyOverview, await LandAsync(SessionCircuit.Cookie(pat)));
        Assert.Equal(AppHome.PlatformConsole, await LandAsync(SessionCircuit.Cookie(platformOwner, AppRoles.Admin)));

        Assert.Equal(AppHome.MyWork, await LandAsync(await ImpersonatingAsync(platformOwner, ada)));
        Assert.Equal(AppHome.CoordinatorOverview, await LandAsync(await ImpersonatingAsync(platformOwner, scenario.Coordinator)));
        Assert.Equal(AppHome.SetupChecklist, await LandAsync(await ImpersonatingAsync(platformOwner, tenant.OwnerId)));
    }

    [Fact]
    public async Task My_work_shows_the_staff_members_own_request_assignment_and_absence_and_a_request_opens_its_dialog()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "My Work Page", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, Ct);
        var ben = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ben", Today, Ct);
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var (_, history) = await scenario.StartedJobAsync(this.database, "History", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Friday, new TimeOnly(12, 0), new TimeOnly(13, 0)));
        var request = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, algebra[0], ada, Ct));
        var assigned = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, history[0], ada, Ct));
        var benRequest = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, algebra[1], ben, Ct));
        await using (var host = scenario.HostOf(this.database, ada))
        {
            Made(await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), ada, AbsenceKind.PlannedLeave, new DateOnly(2027, 3, 10), new DateOnly(2027, 3, 10), Ct));
        }

        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(ada));
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<MyWork>(builder));
        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find("[data-my-work]").GetAttribute("data-my-work")), Wait);

        Assert.Equal([request.ToString()], rendered.FindAll("[data-own-request]").Select(row => row.GetAttribute("data-own-request")));
        Assert.Contains("Algebra: Lead", rendered.Find("[data-own-request]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Your answer is needed", rendered.Find("[data-own-request]").TextContent, StringComparison.Ordinal);
        Assert.Equal([assigned.ToString()], rendered.FindAll("[data-own-next]").Select(row => row.GetAttribute("data-own-next")));
        Assert.Empty(rendered.FindAll($"[data-own-request='{benRequest}']"));
        Assert.DoesNotContain("Assistant", rendered.Markup, StringComparison.Ordinal);
        Assert.Equal("Next: planned leave on 2027-03-10 (1 waiting for approval)", rendered.Find("[data-absence-status]").TextContent.Trim());
        Assert.Single(rendered.FindAll("[data-own-absence]"));

        Assert.StartsWith("Monday 9 November · ", rendered.Find(".home-head__eyebrow").TextContent.Trim(), StringComparison.Ordinal);
        Assert.Equal("My work", Assert.Single(rendered.FindAll("h1")).TextContent.Trim());
        Assert.Equal(("1", "attention"), FigureOf(rendered, "requests"));

        rendered.Find("[data-own-request]").Click();
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<SlotAssignmentDialog>()), Wait);
        Assert.Equal(request, rendered.FindComponents<SlotAssignmentDialog>().Single().Instance.Context.EntityId);
    }

    [Fact]
    public async Task The_administration_overview_shows_the_setup_checklist_of_the_readers_own_tenant_with_each_items_link()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Checklist Page", Ct);

        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.StaffMembers.Onboarded.Tenant.OwnerId));
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<TenantManagementOverview>(builder));
        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find("[data-setup-checklist]").GetAttribute("data-setup-checklist")), Wait);

        var items = rendered.FindAll("[data-setup-item]");
        Assert.Equal(
            [TenantHomeService.People, TenantHomeService.StaffMembers, TenantHomeService.Qualifications, TenantHomeService.WorkPatterns, TenantHomeService.Customers],
            items.Select(item => item.GetAttribute("data-setup-item")));

        IElement Item(string key) => items.Single(item => item.GetAttribute("data-setup-item") == key);
        Assert.Equal("true", Item(TenantHomeService.People).GetAttribute("data-done"));
        Assert.Equal("true", Item(TenantHomeService.Customers).GetAttribute("data-done"));
        Assert.Equal("false", Item(TenantHomeService.StaffMembers).GetAttribute("data-done"));
        Assert.Contains("To do", Item(TenantHomeService.StaffMembers).TextContent, StringComparison.Ordinal);
        Assert.Contains("Nobody holds the Staff role yet", Item(TenantHomeService.StaffMembers).TextContent, StringComparison.Ordinal);
        Assert.Equal("/staffing/staff", Item(TenantHomeService.StaffMembers).QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("2 of 5 done", rendered.Find("[data-setup-progress]").TextContent.Trim());
    }

    [Fact]
    public async Task The_administrators_home_names_itself_and_shows_a_calm_day_in_calm_tones_with_the_checklist_while_setup_is_open()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Calm Admin Home", Ct);
        var (_, later) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        var owner = scenario.StaffMembers.Onboarded.Tenant.OwnerId;

        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(owner), Afternoon);
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<TenantManagementOverview>(builder));
        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find("[data-admin-home]").GetAttribute("data-admin-home")), Wait);

        Assert.Equal("Organization", Assert.Single(rendered.FindAll("h1")).TextContent.Trim());
        Assert.EndsWith("· Monday 9 November · 15:30", rendered.Find(".home-head__eyebrow").TextContent.Trim(), StringComparison.Ordinal);
        Assert.Equal(("0", "calm"), FigureOf(rendered, "jobs"));
        Assert.Equal(("1", "calm"), FigureOf(rendered, "open"));
        Assert.Contains("none within 7 days", rendered.Find("[data-figure='open']").TextContent, StringComparison.Ordinal);
        Assert.Equal(("0", "calm"), FigureOf(rendered, "absent"));
        Assert.Equal(("0", "calm"), FigureOf(rendered, "waiting"));

        Assert.Empty(rendered.FindAll("[data-segment]"));
        Assert.Equal(("No slots", "calm"), (rendered.Find(".home-ring__title").TextContent.Trim(), rendered.Find(".home-ring__centre").GetAttribute("data-tone")));

        Assert.Equal([later[0].ToString()], rendered.FindAll("[data-short-slot]").Select(row => row.GetAttribute("data-short-slot")));
        Assert.NotNull(rendered.Find("[data-setup-checklist='ready']"));
        Assert.Single(rendered.FindAll("[data-admin-section='setup']"));
    }

    [Fact]
    public async Task The_administrators_home_shows_a_busy_day_through_amber_figures_ring_and_legend_and_the_people_card()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Busy Admin Home", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, Ct);
        var ben = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ben", Today, Ct);
        var (_, week) = await scenario.StartedJobAsync(this.database, "Windows", Today, Today.AddDays(4), Ct, ["Front", "Back"], AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(10, 0), new TimeOnly(11, 0)));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, week[0], ada, Ct));
        Guid sick;
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            sick = Made(await AbsenceScenario.RegisterAsync(coordinator.Get<ResourceAbsenceService>(), ben, AbsenceKind.SickLeave, Today, Today, Ct));
        }

        Guid waiting;
        await using (var host = scenario.HostOf(this.database, ada))
        {
            waiting = Made(await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), ada, AbsenceKind.PlannedLeave, new DateOnly(2027, 3, 10), new DateOnly(2027, 3, 10), Ct));
        }

        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.StaffMembers.Onboarded.Tenant.OwnerId), Afternoon);
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<TenantManagementOverview>(builder));
        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find("[data-admin-home]").GetAttribute("data-admin-home")), Wait);

        Assert.Equal(("1", "calm"), FigureOf(rendered, "jobs"));
        Assert.Equal(("1", "attention"), FigureOf(rendered, "open"));
        Assert.Contains("1 within 7 days", rendered.Find("[data-figure='open']").TextContent, StringComparison.Ordinal);
        Assert.Equal(("1", "calm"), FigureOf(rendered, "absent"));
        Assert.Contains("Ben Example", rendered.Find("[data-figure='absent']").TextContent, StringComparison.Ordinal);
        Assert.Equal(("1", "attention"), FigureOf(rendered, "waiting"));
        Assert.Contains("(needs a look)", rendered.Find("[data-figure='waiting'] dt").TextContent, StringComparison.Ordinal);

        Assert.Equal(
            [("held", "home-ring__segment home-ring__segment--calm"), ("open", "home-ring__segment home-ring__segment--attention")],
            rendered.FindAll("[data-segment]").Select(segment => (segment.GetAttribute("data-segment")!, segment.GetAttribute("class")!)));
        Assert.Equal(("1 of 2", "attention"), (rendered.Find(".home-ring__title").TextContent.Trim(), rendered.Find(".home-ring__centre").GetAttribute("data-tone")));
        Assert.Equal("attention", rendered.Find("[data-legend='open']").GetAttribute("data-tone"));

        Assert.Equal([sick.ToString()], rendered.FindAll("[data-absent-today]").Select(row => row.GetAttribute("data-absent-today")));
        Assert.Equal([waiting.ToString()], rendered.FindAll("[data-absence-waiting]").Select(row => row.GetAttribute("data-absence-waiting")));
        Assert.Equal([week[1].ToString()], rendered.FindAll("[data-short-slot]").Select(row => row.GetAttribute("data-short-slot")));
        Assert.Equal(nameof(PriorityKind.Uncovered), rendered.Find("[data-short-slot]").GetAttribute("data-kind"));
    }

    [Fact]
    public async Task The_administrators_home_hides_the_checklist_card_once_setup_is_complete()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Set Up Admin Home", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, Ct);
        await using (var admin = scenario.HostOf(this.database, scenario.Absence.Schedule.Admin))
        {
            var qualifications = admin.Get<QualificationService>();
            var skill = Made(await qualifications.CreateQualificationAsync(new Qualification { Name = "Floor care" }, QualificationWorkflowAttribute.CreateStep, Ct));
            Assert.True((await qualifications.GiveQualificationAsync(ada, skill, Ct)).IsSuccess);
        }

        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.StaffMembers.Onboarded.Tenant.OwnerId), Afternoon);
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<TenantManagementOverview>(builder));
        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find("[data-admin-home]").GetAttribute("data-admin-home")), Wait);

        Assert.Empty(rendered.FindAll("[data-setup-checklist]"));
        Assert.Empty(rendered.FindAll("[data-admin-section='setup']"));
        Assert.Single(rendered.FindAll("[data-admin-section='people']"));
        Assert.Single(rendered.FindAll("[data-admin-section='customers']"));
    }

    [Fact]
    public async Task My_work_names_itself_and_shows_a_busy_week_in_its_figures_ring_and_strip_and_a_calm_one_calmly()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "My Week", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, Ct);
        var cam = await scenario.Absence.AddWorkingStaffAsync(this.database, "Cam", Today, Ct);
        var (_, week) = await scenario.StartedJobAsync(this.database, "Windows", Today, Today.AddDays(4), Ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(10, 0), new TimeOnly(11, 0)));
        var (_, later) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        var assigned = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, week[0], ada, Ct));
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, later[0], ada, Ct));

        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(ada), Afternoon))
        {
            using var page = circuit.Page();
            var rendered = page.Render(builder => Host<MyWork>(builder));
            rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find("[data-my-work]").GetAttribute("data-my-work")), Wait);

            Assert.Equal("My work", Assert.Single(rendered.FindAll("h1")).TextContent.Trim());
            Assert.Equal("Monday 9 November · 15:30", rendered.Find(".home-head__eyebrow").TextContent.Trim());
            Assert.Equal(("Tomorrow 10:00", "calm"), FigureOf(rendered, "next"));
            Assert.Contains("Windows", rendered.Find("[data-figure='next']").TextContent, StringComparison.Ordinal);
            Assert.Equal(("1", "attention"), FigureOf(rendered, "requests"));
            Assert.Equal(("1", "calm"), FigureOf(rendered, "week"));

            Assert.Equal(
                [("2026-11-10", "home-ring__segment home-ring__segment--calm-soft")],
                rendered.FindAll("[data-segment]").Select(segment => (segment.GetAttribute("data-segment")!, segment.GetAttribute("class")!)));
            Assert.Equal(("1 to go", "of 1 this week"), (rendered.Find(".home-ring__title").TextContent.Trim(), rendered.Find(".home-ring__caption").TextContent.Trim()));

            Assert.Equal([assigned.ToString()], rendered.FindAll("[data-own-next]").Select(row => row.GetAttribute("data-own-next")));
            Assert.Equal([assigned.ToString()], rendered.FindAll("[data-day='2026-11-10'] [data-week-time]").Select(time => time.GetAttribute("data-week-time")));
            Assert.Contains("week-day--today", rendered.Find("[data-day='2026-11-09']").GetAttribute("class"), StringComparison.Ordinal);
            rendered.Find("[data-week-time]").Click();
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<SlotAssignmentDialog>()), Wait);
            Assert.Equal(assigned, rendered.FindComponents<SlotAssignmentDialog>().Single().Instance.Context.EntityId);
        }

        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(cam), Afternoon))
        {
            using var page = circuit.Page();
            var rendered = page.Render(builder => Host<MyWork>(builder));
            rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find("[data-my-work]").GetAttribute("data-my-work")), Wait);

            Assert.Equal("My work", rendered.Find("h1").TextContent.Trim());
            Assert.Equal(("None ahead", "calm"), FigureOf(rendered, "next"));
            Assert.Equal(("0", "calm"), FigureOf(rendered, "requests"));
            Assert.Equal(("0", "calm"), FigureOf(rendered, "week"));
            Assert.Empty(rendered.FindAll("[data-segment]"));
            Assert.Equal(("Free week", "calm"), (rendered.Find(".home-ring__title").TextContent.Trim(), rendered.Find(".home-ring__centre").GetAttribute("data-tone")));
            Assert.Empty(rendered.FindAll("[data-own-request]"));
            Assert.Empty(rendered.FindAll("[data-own-next]"));
        }
    }

    [Fact]
    public async Task The_coordinators_overview_names_itself_in_the_evening_and_shows_a_calm_day_in_calm_tones()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Calm Coordinator Home", Ct);
        var (_, later) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);

        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.Coordinator), Evening);
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<CoordinatorOverview>(builder));
        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find("[data-coordinator-home]").GetAttribute("data-coordinator-home")), Wait);

        Assert.Equal("Staffing overview", Assert.Single(rendered.FindAll("h1")).TextContent.Trim());
        Assert.EndsWith("· Monday 9 November · 19:30", rendered.Find(".home-head__eyebrow").TextContent.Trim(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(rendered.Find(".home-head__eyebrow").TextContent.Split('·')[0]));
        Assert.Equal(("0", "calm"), FigureOf(rendered, "open-soon"));
        Assert.Contains("1 open slot in all", rendered.Find("[data-figure='open-soon']").TextContent, StringComparison.Ordinal);
        Assert.Equal(("0", "calm"), FigureOf(rendered, "unanswered"));
        Assert.Equal(("0", "calm"), FigureOf(rendered, "yeses"));
        Assert.Equal(("0", "calm"), FigureOf(rendered, "approval"));

        Assert.Empty(rendered.FindAll("[data-segment]"));
        Assert.Equal(("No slots", "calm"), (rendered.Find(".home-ring__title").TextContent.Trim(), rendered.Find(".home-ring__centre").GetAttribute("data-tone")));

        Assert.Equal([later[0].ToString()], rendered.FindAll("[data-short-slot]").Select(row => row.GetAttribute("data-short-slot")));
        Assert.Contains("No yes waits for your pick. No absence waits for approval.", rendered.Find("[data-coordinator-section='waiting']").TextContent, StringComparison.Ordinal);
        Assert.Empty(rendered.FindAll("[data-yes], [data-approval], [data-customer-message]"));
        var noPattern = rendered.Find("[data-coordinator-section='waiting'] [data-waiting='no-pattern']");
        Assert.Equal(("/staffing/staff?pattern=none", "calm"), (noPattern.GetAttribute("href"), noPattern.GetAttribute("data-tone")));
        Assert.Contains("Every staff member has a work pattern", noPattern.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("(needs a look)", noPattern.TextContent, StringComparison.Ordinal);
        Assert.Contains("No job has a time today.", rendered.Find("[data-coordinator-section='today']").TextContent, StringComparison.Ordinal);
        Assert.Empty(rendered.FindAll("[data-coordinator-section='portals']"));
    }

    [Fact]
    public async Task The_coordinators_overview_shows_a_busy_day_through_amber_figures_and_its_rows_open_their_dialogs()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Busy Coordinator Home", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, Ct);
        var ben = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ben", Today, Ct);
        var cam = await scenario.Absence.AddWorkingStaffAsync(this.database, "Cam", Today, Ct);
        await scenario.AddStaffAsync(this.database, "Dee", Ct);
        var (windows, week) = await scenario.StartedJobAsync(this.database, "Windows", Today, Today.AddDays(4), Ct, ["Front", "Back"], AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(16, 0), new TimeOnly(17, 0)));
        var (_, later) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, week[0], ada, Ct));
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, week[1], ben, Ct));
        var yes = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, later[0], cam, Ct));
        Assert.True((await scenario.StepAsync(this.database, cam, yes, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Guid leave;
        await using (var host = scenario.HostOf(this.database, ada))
        {
            leave = Made(await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), ada, AbsenceKind.PlannedLeave, new DateOnly(2027, 3, 10), new DateOnly(2027, 3, 10), Ct));
        }

        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.Coordinator), Afternoon);
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<CoordinatorOverview>(builder));
        rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find("[data-coordinator-home]").GetAttribute("data-coordinator-home")), Wait);

        Assert.Equal("Staffing overview", Assert.Single(rendered.FindAll("h1")).TextContent.Trim());
        Assert.Equal(("1", "attention"), FigureOf(rendered, "open-soon"));
        Assert.Equal(("1", "calm"), FigureOf(rendered, "unanswered"));
        Assert.Equal(("1", "attention"), FigureOf(rendered, "yeses"));
        Assert.Contains("(needs a look)", rendered.Find("[data-figure='yeses'] dt").TextContent, StringComparison.Ordinal);
        Assert.Equal(("1", "attention"), FigureOf(rendered, "approval"));

        Assert.Equal(("1 of 2", "attention"), (rendered.Find(".home-ring__title").TextContent.Trim(), rendered.Find(".home-ring__centre").GetAttribute("data-tone")));
        Assert.Equal("attention", rendered.Find("[data-legend='open']").GetAttribute("data-tone"));

        Assert.Equal([yes.ToString()], rendered.FindAll("[data-yes]").Select(row => row.GetAttribute("data-yes")));
        Assert.Contains("Cam", rendered.Find("[data-yes]").TextContent, StringComparison.Ordinal);
        Assert.Equal([leave.ToString()], rendered.FindAll("[data-approval]").Select(row => row.GetAttribute("data-approval")));
        Assert.Equal([windows.ToString()], rendered.FindAll("[data-today-job]").Select(row => row.GetAttribute("data-today-job")));
        Assert.Contains("16:00-17:00", rendered.Find("[data-today-job]").TextContent, StringComparison.Ordinal);
        Assert.Contains("1 of 2 held", rendered.Find("[data-today-job]").TextContent, StringComparison.Ordinal);

        var noPattern = rendered.Find("[data-coordinator-section='waiting'] [data-waiting='no-pattern']");
        Assert.Equal(("/staffing/staff?pattern=none", "attention"), (noPattern.GetAttribute("href"), noPattern.GetAttribute("data-tone")));
        Assert.Contains("1 staff member without a work pattern (needs a look)", noPattern.TextContent, StringComparison.Ordinal);

        rendered.Find("[data-yes]").Click();
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<SlotAssignmentDialog>()), Wait);
        Assert.Equal(yes, rendered.FindComponents<SlotAssignmentDialog>().Single().Instance.Context.EntityId);
    }

    [Fact]
    public async Task The_coordinators_overview_is_refused_to_a_staff_member_who_does_not_coordinate()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Refused Coordinator Home", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);

        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(ada), Afternoon);
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<CoordinatorOverview>(builder));
        rendered.WaitForAssertion(() => Assert.Contains("Access denied", rendered.Markup, StringComparison.Ordinal), Wait);

        Assert.Contains("You do not have permission to view the staffing overview.", rendered.Markup, StringComparison.Ordinal);
        Assert.Empty(rendered.FindAll("[data-coordinator-home]"));
        Assert.Empty(rendered.FindAll("[data-figure]"));
    }

    private static (string Value, string Tone) FigureOf(IRenderedComponent<IComponent> rendered, string key) =>
        (rendered.Find($"[data-figure='{key}'] .home-hero-figure__value").TextContent.Trim(), rendered.Find($"[data-figure='{key}']").GetAttribute("data-tone")!);

    private async Task<string> LandAsync(ClaimsPrincipal cookie)
    {
        await using var circuit = await CircuitAsync(cookie);
        using var page = circuit.Page();
        var navigation = page.Services.GetRequiredService<NavigationManager>();
        var start = navigation.Uri;
        var rendered = page.Render<Home>();
        rendered.WaitForAssertion(() => Assert.NotEqual(start, navigation.Uri), Wait);
        return "/" + navigation.ToBaseRelativePath(navigation.Uri);
    }

    private async Task<ClaimsPrincipal> ImpersonatingAsync(Guid platformOwner, Guid target)
    {
        await using var endpoint = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var started = await endpoint.Get<ImpersonationService>().StartAsync(SessionCircuit.Cookie(platformOwner, AppRoles.Admin), target, Ct);
        Assert.True(started.IsSuccess, started.Error);
        return started.Value!;
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

    private async Task<Circuit> CircuitAsync(ClaimsPrincipal cookie, DateTimeOffset? at = null)
    {
        TimeProvider clock = at is { } now ? new ManualClock(now) : AssignmentScenario.Clock;
        var (host, runtime) = SessionCircuit.Open(this.database, cookie, services => services.AddSingleton(clock));
        await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        return new Circuit(host, runtime, clock);
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private sealed class Circuit : IAsyncDisposable
    {
        private readonly ServiceHost host;
        private readonly RuntimeExecutionContext runtime;
        private readonly TimeProvider clock;

        public Circuit(ServiceHost host, RuntimeExecutionContext runtime, TimeProvider clock)
        {
            this.host = host;
            this.runtime = runtime;
            this.clock = clock;
        }

        public PageContext Page()
        {
            var page = new PageContext();
            page.Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
            page.Services.AddSingleton(this.clock);
            page.Services.AddSingleton(this.host.Get<WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<TenantService>());
            page.Services.AddSingleton(this.host.Get<TenantRoleService>());
            page.Services.AddSingleton(this.host.Get<RoleHoldingService>());
            page.Services.AddSingleton(this.host.Get<SlotAssignmentService>());
            page.Services.AddSingleton(this.host.Get<RequestRoundService>());
            page.Services.AddSingleton(this.host.Get<ResourceAbsenceService>());
            page.Services.AddSingleton(this.host.Get<ISlotAssignmentGuard>());
            page.Services.AddSingleton(this.host.Get<IJobSlotGuard>());
            page.Services.AddSingleton(this.host.Get<IResourceAbsenceGuard>());
            page.Services.AddSingleton(this.host.Get<Staffora.Services.Coordination.TenantChangeFeed>());
            page.Services.AddSingleton(this.host.Get<TenantClock>());
            page.Services.AddSingleton(this.host.Get<TenantHomeService>());
            page.Services.AddSingleton(this.host.Get<StaffAvailabilityService>());
            page.Services.AddSingleton(this.host.Get<Staffora.Services.Coordination.CoordinationService>());
            page.Services.AddSingleton(this.host.Get<JobService>());
            var authorization = page.AddAuthorization();
            authorization.SetAuthorized(this.runtime.EffectiveUserId?.ToString() ?? "user");
            return page;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
