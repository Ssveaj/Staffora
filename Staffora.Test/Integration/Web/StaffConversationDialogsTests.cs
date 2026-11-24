using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Services.Authorization;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using static Staffora.Test.Integration.Infrastructure.RenderedClicks;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class StaffConversationDialogsTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private readonly RealDatabase database;

    public StaffConversationDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_staff_members_question_shows_on_the_jobs_Staff_members_tab_and_the_reply_from_it_clears_it_and_tells_the_staff_member()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Conversation Screens", cancellationToken);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", cancellationToken);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, cancellationToken, ["Lead"]);
        var assignment = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, cancellationToken));
        await using (var teaching = scenario.HostOf(this.database, ada))
        {
            Assert.True((await teaching.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, "Is the room on the ground floor?", cancellationToken)).IsSuccess);
        }

        var adaBefore = (await scenario.NoticesOfAsync(this.database, ada, cancellationToken)).Count;
        await using (var circuit = await CircuitAsync(scenario.Coordinator))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => JobDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<JobService>(), job));
            WaitForDialog(rendered);

            var tabs = rendered.FindAll(".state-entity-dialog-side [role=tab]");
            Assert.Equal(["internal", "conversations"], tabs.Select(tab => tab.GetAttribute("data-audience")));
            Assert.Equal(("Staff, 1 conversation, 1 awaits your reply", "true"), (tabs[1].GetAttribute("aria-label"), tabs[1].GetAttribute("data-attention")));

            ClickOnce(rendered, () => rendered.Find(".state-entity-dialog-side [role=tab][data-audience=conversations]"));
            rendered.WaitForAssertion(() => Assert.Equal("true", rendered.Find(".staff-talk__row").GetAttribute("data-attention")), Wait);
            Assert.Contains("Ada Example", rendered.Find(".staff-talk__row").TextContent, StringComparison.Ordinal);
            Assert.Contains(", awaits your reply", rendered.Find(".staff-talk__row .visually-hidden").TextContent, StringComparison.Ordinal);
            Assert.Contains("Is the room on the ground floor?", rendered.Find(".staff-talk__row").TextContent, StringComparison.Ordinal);
            ClickOnce(rendered, () => rendered.Find(".staff-talk__row"));
            rendered.WaitForAssertion(() => Assert.Equal("Unanswered", rendered.Find(".staff-talk__open [data-marker]").TextContent), Wait);
            Assert.Equal("Awaits your reply", rendered.Find(".staff-talk__open [data-attention-text]").TextContent);

            var input = rendered.Find(".staff-talk__open textarea");
            input.Input("Yes, room 4.");
            ClickOnce(rendered, () => rendered.Find(".staff-talk__open button[data-post-comment]"));
            rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".staff-talk__open [data-attention-text]")), Wait);
            Assert.Equal(["Is the room on the ground floor?", "Yes, room 4."], rendered.FindAll(".staff-talk__open .comment-bubble__text").Select(text => text.TextContent));
            ClickOnce(rendered, () => rendered.Find(".staff-talk__back"));
            rendered.WaitForAssertion(() => Assert.Equal("false", rendered.Find(".staff-talk__row").GetAttribute("data-attention")), Wait);
            Assert.Equal("false", rendered.Find(".state-entity-dialog-side [role=tab][data-audience=conversations]").GetAttribute("data-attention"));
        }

        var told = await scenario.NoticesOfAsync(this.database, ada, cancellationToken);
        Assert.Equal(adaBefore + 1, told.Count);
        Assert.EndsWith(" wrote about Algebra", told[0].Subject, StringComparison.Ordinal);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_staff_members_assignment_dialog_marks_a_reply_they_have_not_seen_and_a_staff_member_reaches_no_job_dialog()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Unseen On Screen", cancellationToken);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", cancellationToken);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Biology", PeriodStart, PeriodEnd, cancellationToken);
        var assignment = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, cancellationToken));
        await using (var coordinating = scenario.HostOf(this.database, scenario.Coordinator))
        {
            Assert.True((await coordinating.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, "Can you start a week early?", cancellationToken)).IsSuccess);
            Assert.True((await coordinating.Get<JobService>().AddJobCommentAsync(job, CommentAudience.Internal, "Budget is tight", cancellationToken)).IsSuccess);
        }

        await using var circuit = await CircuitAsync(ada);
        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => SlotAssignmentDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<SlotAssignmentService>(), assignment));
            WaitForDialog(rendered);
            var side = rendered.Find(".state-entity-dialog-side");
            Assert.Equal(["staff"], side.QuerySelectorAll("[role=tab]").Select(tab => tab.GetAttribute("data-audience")));
            Assert.Equal("New reply", side.QuerySelector("[data-attention-text]")!.TextContent);
            Assert.Equal("New", side.QuerySelector("[data-marker]")!.TextContent);
            Assert.DoesNotContain("Budget is tight", rendered.Markup, StringComparison.Ordinal);

            Assert.Equal(SlotAssignmentDialogContext.StaffThreadTitle, side.QuerySelector(".state-entity-dialog-side__title")!.TextContent.Trim());
            Assert.StartsWith(SlotAssignmentDialogContext.StaffThreadTitle, side.QuerySelector("[role=tab]")!.GetAttribute("aria-label"), StringComparison.Ordinal);
        }

        await using (var coordinator = await CircuitAsync(scenario.Coordinator))
        {
            using var page = coordinator.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => SlotAssignmentDialog.OpenAsync(coordinator.Dialogs(page), coordinator.Get<SlotAssignmentService>(), assignment));
            WaitForDialog(rendered);
            Assert.Equal("Staff", rendered.Find(".state-entity-dialog-side__title").TextContent.Trim());
        }

        Assert.False(Assert.Single((await circuit.Get<SlotAssignmentService>().QueryOwnAssignmentsAsync(new ListQuery(), cancellationToken)).Value!.Rows).HasUnseenReply);
        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => SlotAssignmentDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<SlotAssignmentService>(), assignment));
            WaitForDialog(rendered);
            Assert.Empty(rendered.FindAll(".state-entity-dialog-side [data-attention-text]"));
            Assert.Empty(rendered.FindAll(".state-entity-dialog-side [data-marker]"));
        }

        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => JobDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<JobService>(), job));
            rendered.WaitForAssertion(() => Assert.Equal("couldnotopen", rendered.Find(".state-entity-dialog").GetAttribute("data-load-state")), Wait);
            Assert.Empty(rendered.FindAll(".state-entity-dialog-side"));
            Assert.DoesNotContain("Budget is tight", rendered.Markup, StringComparison.Ordinal);
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_jobs_Staff_members_tab_reads_again_when_a_slot_dialog_on_top_of_it_closes_having_asked_a_staff_member()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Staff Tab Refresh", cancellationToken);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", cancellationToken);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, cancellationToken, ["Lead"]);

        await using var circuit = await CircuitAsync(scenario.Coordinator);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => JobDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<JobService>(), job));
        WaitForDialog(rendered);
        const string StaffMembersTab = ".state-entity-dialog-side [role=tab][data-audience=conversations]";
        Assert.Equal("Staff, 0 conversations", rendered.Find(StaffMembersTab).GetAttribute("aria-label"));

        ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=slots]"));
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-entity-dialog tr.list-row")), Wait);
        ClickOnce(rendered, () => rendered.Find(".state-entity-dialog tr.list-row button.list-row__open"));
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog").Count), Wait);
        WaitForDialog(rendered);
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], ada, cancellationToken));
        ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog-header__close").Last());
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-entity-dialog")), Wait);

        rendered.WaitForAssertion(() => Assert.Equal("Staff, 1 conversation", rendered.Find(StaffMembersTab).GetAttribute("aria-label")), Wait);
        ClickOnce(rendered, () => rendered.Find(StaffMembersTab));
        rendered.WaitForAssertion(() => Assert.Contains("Ada Example", rendered.Find(".staff-talk__row").TextContent, StringComparison.Ordinal), Wait);
    }

    private static Guid Made(Staffora.Core.Models.Result<Guid> made)
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
            page.Services.AddSingleton(this.host.Get<Staffora.Core.Entities.Jobs.IJobSlotGuard>());
            page.Services.AddSingleton(this.host.Get<Staffora.Core.Entities.Jobs.ISlotAssignmentGuard>());
            page.Services.AddSingleton(this.host.Get<Staffora.Core.Entities.Jobs.IJobGuard>());
            return page;
        }

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public DialogService Dialogs(PageContext page) => page.Services.GetRequiredService<DialogService>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
