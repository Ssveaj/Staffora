using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Helper;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
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
public sealed class AbsenceDialogsTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 30, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 11, 11);

    private readonly RealDatabase database;

    public AbsenceDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_coordinator_registers_a_sick_leave_from_the_staff_members_Absence_view_and_it_blocks_at_once()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Viewing Absence", ct);
        var kim = await scenario.AddWorkingStaffAsync(this.database, "Kim", Today.AddDays(-2), ct);
        await using var circuit = await CircuitAsync(scenario.Coordinator);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), kim));
        WaitForDialog(rendered);
        ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=absence]"));
        rendered.WaitForAssertion(() => Assert.Contains("No absence yet", rendered.Markup, StringComparison.Ordinal), Wait);

        ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog button").First(button => TextOf(button) == "Register absence"));
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog").Count), Wait);
        WaitForDialog(rendered);
        var top = rendered.FindAll(".state-entity-dialog").Last();
        Assert.Contains("Kim Example", top.QuerySelector("[data-staff]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("it is approved at once", top.QuerySelector("[data-absence-note]")!.TextContent, StringComparison.Ordinal);

        Assert.Equal(["From", "Until"], top.QuerySelectorAll(".state-field-day-time").Select(field => field.Closest(".state-field")!.QuerySelector(".state-field__label")!.TextContent.Trim()));
        Assert.Equal(["absence-first-day", "absence-last-day"], top.QuerySelectorAll(".state-field-day-time").Select(field => field.Closest(".rz-form-field")!.QuerySelector("label")!.GetAttribute("for")));
        var startTime = top.QuerySelector("[name=absence-start-time]")!;
        Assert.Equal("From time", startTime.GetAttribute("aria-label"));
        Assert.Equal("Until time", top.QuerySelector("[name=absence-end-time]")!.GetAttribute("aria-label"));
        Assert.Equal("absence-start-time-placeholder", startTime.GetAttribute("aria-describedby"));
        Assert.Equal("All day", top.QuerySelector("#absence-start-time-placeholder")!.TextContent.Trim());
        Assert.True(startTime.Closest(".state-field-day-time")!.Closest(".state-field")!.ClassList.Contains("state-field--required"));
        Assert.Null(startTime.GetAttribute("aria-required"));
        Assert.Null(startTime.GetAttribute("readonly"));
        Assert.Empty(top.QuerySelectorAll("[data-field-helper]"));
        Assert.Equal(Today, rendered.FindComponents<RadzenDatePicker<DateOnly>>().Single(picker => picker.Instance.Name == "absence-first-day").Instance.Value);
        Assert.Equal([ResourceAbsenceWorkflowAttribute.RegisterStep], top.QuerySelectorAll("button[data-transition]").Select(button => button.GetAttribute("data-transition")));

        var kind = rendered.FindComponents<RadzenDropDown<AbsenceKind>>().Single(box => box.Instance.Name == "absence-kind");
        var dialog = rendered.FindComponents<AbsenceDialog>().Last().Instance.Context;
        await rendered.InvokeAsync(async () =>
        {
            await kind.Instance.ValueChanged.InvokeAsync(AbsenceKind.SickLeave);
            dialog.NotifyEdited();
        });
        ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{ResourceAbsenceWorkflowAttribute.RegisterStep}']")!);
        rendered.WaitForAssertion(() => Assert.Equal("Approved", rendered.FindAll(".state-entity-dialog").Last().QuerySelector(".state-pill__button")!.GetAttribute("data-state")), Wait);
        WaitForDialog(rendered);
        Assert.Null(rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{ResourceAbsenceWorkflowAttribute.CancelApprovedStep}']"));
        Assert.Contains("Approved", rendered.FindAll(".state-entity-dialog").Last().QuerySelector(".state-entity-dialog-header")!.TextContent, StringComparison.Ordinal);
        Assert.True(rendered.FindComponents<RadzenDatePicker<DateOnly>>().Single(picker => picker.Instance.Name == "absence-first-day").Instance.ReadOnly);
        var decided = rendered.FindAll(".state-entity-dialog").Last();
        Assert.NotNull(decided.QuerySelector("[name=absence-start-time]")!.GetAttribute("readonly"));
        Assert.Equal("All day", decided.QuerySelector("#absence-start-time-placeholder")!.TextContent.Trim());
        Assert.True(decided.QuerySelector("[name=absence-start-time]")!.Closest(".state-field")!.ClassList.Contains("state-field--read-only"));

        await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-entity-dialog")), Wait);
        rendered.WaitForAssertion(() => Assert.Contains("Blocks staffing", rendered.Find(".state-entity-dialog tr.list-row").TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("Sick leave", rendered.Find(".state-entity-dialog tr.list-row").TextContent, StringComparison.Ordinal);
        var resource = await ScheduleScenario.ResourceOfAsync(this.database, kim, ct);
        var block = Assert.Single((await circuit.Get<IStaffAvailability>().ReadAsync([resource], Today, Today, ct)).Value!.StaffMembers[resource].Blocks);
        Assert.Equal("On sick leave", block.Reason);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_user_dialog_offers_the_Absence_view_only_with_its_permission_to_a_reader_the_absence_service_would_answer()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Gating Absence", ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", ct);
        await using (var host = scenario.HostOf(this.database, kim))
        {
            Assert.True((await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), kim, AbsenceKind.PlannedLeave, Today.AddDays(7), Today.AddDays(9), ct)).IsSuccess);
        }

        var manager = await scenario.StaffMembers.AddPersonAsync(this.database, "Morgan", ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, manager, ct, RolePermission.UsersView, RolePermission.ResourcesView, RolePermission.ManageResourceAbsence, RolePermission.ApproveResourceAbsence);
        await using var circuit = await CircuitAsync(manager);
        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), kim, new StateEntityDialogOptions { View = UserDialogContext.AbsenceView }));
            WaitForDialog(rendered);

            Assert.NotEmpty(rendered.FindAll("[role=tab][data-view=staff]"));
            Assert.Empty(rendered.FindAll("[role=tab][data-view=absence]"));
            Assert.NotEqual(UserDialogContext.AbsenceView, rendered.FindComponents<UserDialog>().Single().Instance.Context.ActiveViewKey);
        }

        Assert.Single((await circuit.Get<ResourceAbsenceService>().QueryAbsencesOfUserAsync(kim, new ListQuery(), ct)).Value!.Rows);
        using (var page = circuit.Page())
        {
            var rendered = page.Render(builder => Host<Staffora.Blazor.Pages.Staffing.Absence>(builder));
            rendered.WaitForAssertion(() => Assert.Contains("Kim Example", rendered.Find("tr.list-row").TextContent, StringComparison.Ordinal), Wait);
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_absence_page_lists_every_absence_for_a_reader_and_their_own_for_a_staff_member_who_reports_from_it()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Listing Absence", ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", ct);
        var robin = await scenario.AddStaffAsync(this.database, "Robin", ct);
        await using (var host = scenario.HostOf(this.database, robin))
        {
            Assert.True((await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), robin, AbsenceKind.PlannedLeave, Today.AddDays(7), Today.AddDays(9), ct)).IsSuccess);
        }

        await using (var circuit = await CircuitAsync(kim))
        {
            using var page = circuit.Page();
            var rendered = page.Render(builder => Host<Staffora.Blazor.Pages.Staffing.Absence>(builder));
            rendered.WaitForAssertion(() => Assert.Contains("No absence yet", rendered.Markup, StringComparison.Ordinal), Wait);
            Assert.DoesNotContain(rendered.FindAll("button"), button => TextOf(button) == "Register absence");

            ClickOnce(rendered, () => rendered.FindAll("button").Single(button => TextOf(button) == "Report absence"));
            WaitForDialog(rendered);
            var top = rendered.FindAll(".state-entity-dialog").Last();
            Assert.Equal([ResourceAbsenceWorkflowAttribute.ReportStep], top.QuerySelectorAll("button[data-transition]").Select(button => button.GetAttribute("data-transition")));
            rendered.Find("[name=absence-start-time]").Change("13:00");
            ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{ResourceAbsenceWorkflowAttribute.ReportStep}']")!);
            rendered.WaitForAssertion(() => Assert.NotNull(rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{ResourceAbsenceWorkflowAttribute.CancelStep}']")), Wait);
            WaitForDialog(rendered);
            Assert.Contains("Planned leave on 2026-11-11 from 13:00", rendered.FindAll(".state-entity-dialog").Last().TextContent, StringComparison.Ordinal);
            Assert.Empty(rendered.FindAll(".state-entity-dialog-side"));

            await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
            rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll("tr.list-row")), Wait);
            Assert.Contains("Kim Example", rendered.Find("tr.list-row").TextContent, StringComparison.Ordinal);
        }

        await using (var circuit = await CircuitAsync(scenario.Coordinator))
        {
            using var page = circuit.Page();
            var rendered = page.Render(builder => Host<Staffora.Blazor.Pages.Staffing.Absence>(builder));
            rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll("tr.list-row").Count), Wait);
            Assert.Contains(rendered.FindAll("button"), button => TextOf(button) == "Register absence");
            Assert.DoesNotContain(rendered.FindAll("button"), button => TextOf(button) == "Report absence");
            Assert.Collection(
                rendered.FindAll("tr.list-row"),
                row => Assert.Contains("Robin Example", row.TextContent, StringComparison.Ordinal),
                row => Assert.Contains("Kim Example", row.TextContent, StringComparison.Ordinal));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_rejections_reason_shows_to_the_approver_and_the_staff_member_in_a_thread_without_a_composer()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Rejecting Absence", ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", ct);
        Guid id;
        await using (var host = scenario.HostOf(this.database, kim))
        {
            id = (await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), kim, AbsenceKind.PlannedLeave, Today, Today, ct)).Value;
        }

        await using (var host = scenario.HostOf(this.database, scenario.Approver))
        {
            Assert.True((await AbsenceScenario.StepAsync(host.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.RejectStep, ct, comment: "Exam week, sorry.")).IsSuccess);
        }

        await using (var circuit = await CircuitAsync(scenario.Approver))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => AbsenceDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<ResourceAbsenceService>(), id));
            WaitForDialog(rendered);
            rendered.WaitForAssertion(() => Assert.Contains("Exam week, sorry.", rendered.Find(".state-entity-dialog-side").TextContent, StringComparison.Ordinal), Wait);
        }

        await using (var circuit = await CircuitAsync(kim))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => AbsenceDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<ResourceAbsenceService>(), id));
            WaitForDialog(rendered);
            rendered.WaitForAssertion(() => Assert.Contains("Exam week, sorry.", rendered.Find(".state-entity-dialog-side").TextContent, StringComparison.Ordinal), Wait);
            Assert.Empty(rendered.FindAll("[data-thread-closed]"));
            Assert.Empty(rendered.FindAll(".state-entity-dialog-side textarea"));
            Assert.Empty(rendered.FindAll("[data-absence-note]"));
            Assert.Empty(rendered.FindAll(".state-entity-dialog button[data-transition]"));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_staff_member_writes_a_question_in_the_prompt_a_step_opens_and_the_approver_answers_it_there()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Asking Absence", ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", ct);
        Guid id;
        await using (var host = scenario.HostOf(this.database, kim))
        {
            id = (await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), kim, AbsenceKind.PlannedLeave, Today, Today, ct)).Value;
        }

        await using (var circuit = await CircuitAsync(kim))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => AbsenceDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<ResourceAbsenceService>(), id));
            WaitForDialog(rendered);

            Assert.Empty(rendered.FindAll(".state-entity-dialog-side"));
            Assert.Empty(rendered.FindAll($".state-entity-dialog button[data-transition='{ResourceAbsenceWorkflowAttribute.ApproveStep}']"));
            ClickOnce(rendered, () => StepButton(rendered, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep));
            rendered.WaitForAssertion(() => Assert.Equal("Write a question to the coordinator", rendered.Find(".form-dialog-header__title").TextContent), Wait);
            Assert.Equal("The coordinator sees your question on the absence.", rendered.Find(".prompt-dialog__message").TextContent.Trim());
            Assert.Equal("What do you want to ask about?", rendered.Find(".form-dialog textarea").GetAttribute("placeholder"));
            Assert.Empty(rendered.FindAll(".prompt-dialog__required"));
            PromptButton(rendered, "Write a question").Click();
            rendered.WaitForAssertion(() => Assert.Equal(ResourceAbsenceWorkflowAttribute.AskCoordinatorPrompt.RequiredMessage, rendered.Find(".prompt-dialog__required").TextContent.Trim()), Wait);
            PromptButton(rendered, "Cancel").Click();
            rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".form-dialog")), Wait);
            Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, id, ct))!.State);

            ClickOnce(rendered, () => StepButton(rendered, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep));
            rendered.WaitForElement(".form-dialog textarea").Change("Can I leave at noon instead?");
            PromptButton(rendered, "Write a question").Click();
            rendered.WaitForAssertion(() => Assert.Contains("Can I leave at noon instead?", rendered.Find(".state-entity-dialog-side").TextContent, StringComparison.Ordinal), Wait);
            Assert.Equal(ResourceAbsenceState.StaffQuestion, (await AbsenceScenario.StoredAsync(this.database, id, ct))!.State);
            Assert.Empty(rendered.FindAll("[data-thread-closed]"));
            Assert.Empty(rendered.FindAll(".state-entity-dialog-side textarea"));
            WaitForDialog(rendered);
            Assert.NotNull(StepButton(rendered, ResourceAbsenceWorkflowAttribute.AskCoordinatorAgainStep));
        }

        await using (var circuit = await CircuitAsync(scenario.Coordinator))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => AbsenceDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<ResourceAbsenceService>(), id));
            WaitForDialog(rendered);

            Assert.NotNull(StepButton(rendered, ResourceAbsenceWorkflowAttribute.QuestionHandledStep));
            Assert.Empty(rendered.FindAll($".state-entity-dialog button[data-transition='{ResourceAbsenceWorkflowAttribute.ApproveStep}']"));
            ClickOnce(rendered, () => StepButton(rendered, ResourceAbsenceWorkflowAttribute.AnswerStaffStep));
            rendered.WaitForElement(".form-dialog textarea").Change("Yes, change it and save.");
            PromptButton(rendered, "Answer the staff member").Click();
            rendered.WaitForAssertion(() => Assert.Contains("Yes, change it and save.", rendered.Find(".state-entity-dialog-side").TextContent, StringComparison.Ordinal), Wait);
            Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, id, ct))!.State);
        }
    }

    private static IElement PromptButton(IRenderedComponent<IComponent> rendered, string text) =>
        rendered.FindAll(".form-dialog button").Single(button => button.TextContent.Trim().Equals(text, StringComparison.Ordinal));

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_decision_is_not_offered_while_the_dialog_holds_edits_and_takes_the_absence_as_stored_once_they_are_gone()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Waiting Absence", ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", ct);
        Guid id;
        await using (var host = scenario.HostOf(this.database, kim))
        {
            id = (await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), kim, AbsenceKind.PlannedLeave, Today, Today, ct)).Value;
        }

        await using var circuit = await CircuitAsync(scenario.Coordinator);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => AbsenceDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<ResourceAbsenceService>(), id));
        WaitForDialog(rendered);
        string[] decisions = [ResourceAbsenceWorkflowAttribute.ApproveStep, ResourceAbsenceWorkflowAttribute.RejectStep];
        Assert.All(decisions, key => Assert.Null(StepButton(rendered, key).GetAttribute("aria-disabled")));
        Assert.Null(rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{ResourceAbsenceWorkflowAttribute.CancelStep}']"));
        Assert.NotNull(rendered.EditButton());

        rendered.Edit();
        rendered.Find("[name=absence-start-time]").Change("13:00");
        rendered.WaitForAssertion(() => Assert.Null(StepButton(rendered, ResourceAbsenceWorkflowAttribute.SaveStep).GetAttribute("aria-disabled")), Wait);
        Assert.All(decisions, key => Assert.Null(rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{key}']")));

        var stored = (await AbsenceScenario.StoredAsync(this.database, id, ct))!;
        Assert.Equal((ResourceAbsenceState.Requested, (TimeOnly?)null), (stored.State, stored.StartTime));

        ClickOnce(rendered, () => rendered.Find(".state-entity-dialog-footer button[data-dialog-action='cancel']"));
        rendered.WaitForAssertion(() => Assert.All(decisions, key => Assert.Null(StepButton(rendered, key).GetAttribute("aria-disabled"))), Wait);
        ClickOnce(rendered, () => StepButton(rendered, ResourceAbsenceWorkflowAttribute.ApproveStep));
        rendered.WaitForAssertion(() => Assert.Equal("Approved", rendered.FindAll(".state-entity-dialog").Last().QuerySelector(".state-pill__button")!.GetAttribute("data-state")), Wait);
        stored = (await AbsenceScenario.StoredAsync(this.database, id, ct))!;
        Assert.Equal((ResourceAbsenceState.Approved, (TimeOnly?)null), (stored.State, stored.StartTime));
    }

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
            page.Services.AddSingleton(this.host.Get<ResourceAbsenceService>());
            page.Services.AddSingleton(this.host.Get<IResourceAbsenceGuard>());
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
