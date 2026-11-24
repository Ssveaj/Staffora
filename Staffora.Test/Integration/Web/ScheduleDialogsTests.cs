using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using static Staffora.Test.Integration.Infrastructure.RenderedClicks;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class ScheduleDialogsTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 30, 0, TimeSpan.Zero);

    private static readonly DateOnly ThisMonday = ScheduleRules.MondayOf(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, TimeZoneInfo.FindSystemTimeZoneById(ServiceHost.PlatformTimeZone)).DateTime));

    private readonly RealDatabase database;

    public ScheduleDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_staff_members_Schedule_view_shows_the_pattern_across_a_period()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Viewing Schedules", ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", ct);

        var pattern = await scenario.PublishPatternAsync(this.database, "Full time", ct, [.. ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(16, 0)).Take(4), new PatternSegment(4, new TimeOnly(8, 0), new TimeOnly(12, 0))]);
        var start = ThisMonday.AddDays(-7);
        await using (var admin = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin)))
        {
            var assignments = admin.Get<PatternAssignmentService>();
            var id = (await ScheduleScenario.CreateAssignmentAsync(assignments, kim, pattern, start, null, ct)).Value;
            Assert.True((await ScheduleScenario.StepAsync(assignments, id, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, ct)).IsSuccess);
        }

        await using var circuit = await CircuitAsync(scenario.Admin);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), kim));
        WaitForDialog(rendered);
        ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=schedule]"));
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-entity-dialog tr.list-row")), Wait);

        var table = rendered.Find("[data-staff-schedule]");
        Assert.Equal("region", table.GetAttribute("role"));
        Assert.Contains(ServiceHost.PlatformTimeZone, table.QuerySelector("caption")!.TextContent, StringComparison.Ordinal);
        Assert.Equal(["Week of", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun", "Hours"], table.QuerySelectorAll("thead th").Select(header => header.TextContent.Trim()));
        var weeks = table.QuerySelectorAll("tbody tr[data-week]").ToList();
        Assert.Equal(StaffSchedule.PeriodWeeks, weeks.Count);
        Assert.Equal(Enumerable.Range(0, StaffSchedule.PeriodWeeks).Select(week => ScheduleRules.DateText(ThisMonday.AddDays(7 * week))), weeks.Select(week => week.GetAttribute("data-week")));
        Assert.All(weeks, week =>
        {
            Assert.Equal("row", week.QuerySelector("th")!.GetAttribute("scope"));
            Assert.Equal(["08:00–16:00", "08:00–16:00", "08:00–16:00", "08:00–16:00", "08:00–12:00", "Off", "Off"], week.QuerySelectorAll("td[data-date]").Select(day => day.TextContent.Trim()));
            Assert.Equal("36 h", week.QuerySelector("td[data-worked]")!.TextContent.Trim());
        });

        var row = rendered.Find(".state-entity-dialog tr.list-row");
        Assert.Contains("Full time", row.TextContent, StringComparison.Ordinal);
        Assert.Contains($"{ScheduleRules.DateText(start)} until further notice", row.TextContent, StringComparison.Ordinal);
        Assert.Contains(rendered.FindAll(".state-entity-dialog button"), button => TextOf(button) == "Assign work pattern");

        ClickOnce(rendered, () => rendered.Find(".state-entity-dialog tr.list-row button.list-row__open"));
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog").Count), Wait);
        WaitForDialog(rendered);
        var top = rendered.FindAll(".state-entity-dialog").Last();
        Assert.Contains($"Full time from {ScheduleRules.DateText(start)}", top.TextContent, StringComparison.Ordinal);
        Assert.Contains("Kim Example", top.QuerySelector("[data-staff]")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(top.QuerySelector($"button[data-transition='{ResourcePatternAssignmentWorkflowAttribute.DeactivateStep}']"));
        Assert.NotNull(top.QuerySelector(".state-entity-dialog-footer button[data-dialog-action='edit']"));
        rendered.Edit();
        top = rendered.FindAll(".state-entity-dialog").Last();
        Assert.NotNull(top.QuerySelector($"button[data-transition='{ResourcePatternAssignmentWorkflowAttribute.SaveActiveStep}']"));
        Assert.True(rendered.FindComponents<RadzenDatePicker<DateOnly>>().Single(picker => picker.Instance.Name == "assignment-from").Instance.ReadOnly);
        Assert.False(rendered.FindComponents<RadzenDatePicker<DateOnly?>>().Single(picker => picker.Instance.Name == "assignment-to").Instance.ReadOnly);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Assign_work_pattern_creates_and_activates_an_assignment_and_refuses_one_over_the_same_dates()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Assigning Schedules", ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", ct);
        var pattern = await scenario.PublishPatternAsync(this.database, "Mornings", ct, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(12, 0)));
        await using var circuit = await CircuitAsync(scenario.Admin);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), kim));
        WaitForDialog(rendered);
        ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=schedule]"));
        rendered.WaitForAssertion(() => Assert.Contains("No work pattern yet", rendered.Markup, StringComparison.Ordinal), Wait);
        Assert.All(rendered.FindAll("[data-staff-schedule] td[data-date]"), day => Assert.Equal("Off", day.TextContent.Trim()));

        for (var round = 0; round < 2; round++)
        {
            ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog button").First(button => TextOf(button) == "Assign work pattern"));
            rendered.WaitForAssertion(() => Assert.Single(rendered.FindComponents<RadzenDropDown<Guid>>(), box => box.Instance.Name == "assignment-pattern"), Wait);
            WaitForDialog(rendered);
            var box = rendered.FindComponents<RadzenDropDown<Guid>>().Single(box => box.Instance.Name == "assignment-pattern");
            Assert.Equal(["Mornings"], ((IEnumerable<WorkPattern>?)box.Instance.Data ?? []).Select(offered => offered.Name));
            var dialog = rendered.FindComponents<PatternAssignmentDialog>().Last().Instance.Context;
            await rendered.InvokeAsync(async () =>
            {
                await box.Instance.ValueChanged.InvokeAsync(pattern);
                dialog.NotifyEdited();
            });
            ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{ResourcePatternAssignmentWorkflowAttribute.SaveStep}']")!);
            rendered.WaitForAssertion(() => Assert.NotNull(rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{ResourcePatternAssignmentWorkflowAttribute.ActivateStep}']")), Wait);
            WaitForDialog(rendered);

            ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{ResourcePatternAssignmentWorkflowAttribute.ActivateStep}']")!);
            if (round == 0)
            {
                rendered.WaitForAssertion(() => Assert.NotNull(rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{ResourcePatternAssignmentWorkflowAttribute.DeactivateStep}']")), Wait);
            }
            else
            {
                rendered.WaitForAssertion(() => Assert.Contains("It overlaps the active assignment of Mornings", rendered.FindAll(".state-entity-dialog").Last().TextContent, StringComparison.Ordinal), Wait);
            }

            await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
            rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-entity-dialog")), Wait);
            rendered.WaitForAssertion(() => Assert.Equal(round + 1, rendered.FindAll(".state-entity-dialog tr.list-row").Count), Wait);
        }

        rendered.WaitForAssertion(() => Assert.Equal("08:00–12:00", rendered.Find($"[data-staff-schedule] tr[data-week='{ScheduleRules.DateText(ThisMonday.AddDays(7))}'] td[data-date]").TextContent.Trim()), Wait);
        var states = rendered.FindAll(".state-entity-dialog tr.list-row").Select(row => row.TextContent).ToList();
        Assert.Contains(states, text => text.Contains("Active", StringComparison.Ordinal));
        Assert.Contains(states, text => text.Contains("Registration", StringComparison.Ordinal));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_reader_of_staff_sees_the_view_without_the_add_action_and_a_user_who_is_no_staff_member_has_none()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Reading Schedules", ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", ct);
        var plain = await scenario.StaffMembers.AddPersonAsync(this.database, "Plain", ct);
        var reader = await scenario.StaffMembers.AddPersonAsync(this.database, "Reader", ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, reader, ct, RolePermission.UsersView, RolePermission.ResourcesView);
        await using var circuit = await CircuitAsync(reader);

        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), kim));
            WaitForDialog(rendered);
            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=schedule]"));
            rendered.WaitForAssertion(() => Assert.Contains("No work pattern yet", rendered.Markup, StringComparison.Ordinal), Wait);
            Assert.Equal(StaffSchedule.PeriodWeeks, rendered.FindAll("[data-staff-schedule] tbody tr").Count);
            Assert.DoesNotContain(rendered.FindAll("button"), button => TextOf(button) == "Assign work pattern");
        }

        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), plain));
            WaitForDialog(rendered);
            Assert.Empty(rendered.FindAll("[role=tab][data-view=schedule]"));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_user_dialog_offers_the_Schedule_view_only_with_its_permission_to_a_reader_the_assignment_service_would_answer()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Gating Schedules", ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", ct);
        var pattern = await scenario.PublishPatternAsync(this.database, "Mornings", ct, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(12, 0)));
        await using (var admin = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin)))
        {
            Assert.True((await ScheduleScenario.CreateAssignmentAsync(admin.Get<PatternAssignmentService>(), kim, pattern, ThisMonday, null, ct)).IsSuccess);
        }

        var assigner = await scenario.StaffMembers.AddPersonAsync(this.database, "Assigner", ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, assigner, ct, RolePermission.UsersView, RolePermission.AssignWorkPatterns);
        await using var circuit = await CircuitAsync(assigner);
        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), kim, new Staffora.Blazor.Helper.StateEntityDialogOptions { View = UserDialogContext.ScheduleView }));
            WaitForDialog(rendered);
            Assert.Empty(rendered.FindAll("[role=tab][data-view=schedule]"));
            Assert.Empty(rendered.FindAll("[data-staff-schedule]"));
            Assert.NotEqual(UserDialogContext.ScheduleView, rendered.FindComponents<UserDialog>().Single().Instance.Context.ActiveViewKey);
        }

        Assert.Single((await circuit.Get<PatternAssignmentService>().QueryAssignmentsOfUserAsync(kim, new Staffora.Core.Lists.ListQuery(), ct)).Value!.Rows);
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
        var clock = new FakeClock(Now);
        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(userId), services => services.AddSingleton<TimeProvider>(clock));
        await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        return new Circuit(host, runtime, clock);
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
            page.Services.AddSingleton(this.host.Get<UserService>());
            page.Services.AddSingleton(this.host.Get<ResourceService>());
            page.Services.AddSingleton(this.host.Get<PatternAssignmentService>());
            page.Services.AddSingleton(this.host.Get<IPatternAssignmentGuard>());
            page.Services.AddSingleton(this.host.Get<Staffora.Core.Entities.Identity.IUserAccountGuard>());
            page.Services.AddSingleton(this.host.Get<TenantService>());
            page.Services.AddSingleton(this.host.Get<TenantRoleService>());
            page.Services.AddSingleton(this.host.Get<RoleHoldingService>());
            page.Services.AddSingleton(this.host.Get<CustomFieldService>());
            return page;
        }

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public DialogService Dialogs(PageContext page) => page.Services.GetRequiredService<DialogService>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
