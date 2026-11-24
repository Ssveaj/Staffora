using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Web;

public partial class StateEntityDialogTests
{
    [Fact]
    public async Task The_dialogs_thread_shows_in_the_side_column_one_tab_per_audience_with_its_count_and_attention()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.InternalThreadPermission, TestParentDialogService.StaffThreadPermission);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, colleague, cancellationToken, TestParentDialogService.InternalThreadPermission);
        var coordinator = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Coordinator", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, coordinator, cancellationToken, TestParentDialogService.StaffThreadPermission, TestParentDialogService.StaffPostPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await PostAsync(scenario.A.Runtime(), id, CommentAudience.Internal, "Who takes the morning group?", cancellationToken);
        await PostAsync(scenario.A.RuntimeOf(colleague), id, CommentAudience.Internal, "I can, from week 2.", cancellationToken);
        await PostAsync(scenario.A.RuntimeOf(coordinator), id, CommentAudience.Staff, "Can you take Monday?", cancellationToken);

        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        var side = dialog.Host.Find(".state-entity-dialog__main--with-side .state-entity-dialog-side");
        Assert.Equal("Comments", dialog.Host.Find($"#{side.GetAttribute("aria-labelledby")}").TextContent);
        var tabs = side.QuerySelectorAll("[role=tablist] [role=tab]");
        Assert.Equal(
            [("internal", "2", "true", "true"), ("staff", "1", "true", "false")],
            tabs.Select(tab => (tab.GetAttribute("data-audience"), tab.QuerySelector(".dialog-side-tabs__count")!.TextContent, tab.GetAttribute("data-attention"), tab.GetAttribute("aria-selected"))));
        Assert.Equal("Internal, 2 comments, awaits your reply", tabs[0].GetAttribute("aria-label"));
        Assert.Equal("true", tabs[0].QuerySelector(".dialog-side-tabs__count")!.GetAttribute("aria-hidden"));
        Assert.Equal("Awaits your reply", side.QuerySelector("[data-attention-text]")!.TextContent);

        var panel = side.QuerySelector("[role=tabpanel]")!;
        Assert.Equal((tabs[0].Id, panel.Id), (panel.GetAttribute("aria-labelledby"), tabs[0].GetAttribute("aria-controls")));
        Assert.Equal(
            ["Who takes the morning group?", "I can, from week 2."],
            panel.QuerySelectorAll(".comment-bubbles__list li .comment-bubble__text").Select(text => text.TextContent));
        Assert.Equal("Internal comments", panel.QuerySelector("ol")!.GetAttribute("aria-label"));
        Assert.NotNull(panel.QuerySelector("textarea[name=comment-internal]"));

        tabs[1].Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("staff", dialog.Host.Find(".state-entity-dialog-side [role=tabpanel]").GetAttribute("data-thread")), Wait);
        var staff = dialog.Host.Find(".state-entity-dialog-side [role=tabpanel]");
        Assert.Equal(["Can you take Monday?"], staff.QuerySelectorAll(".comment-bubble__text").Select(text => text.TextContent));
        Assert.Null(staff.QuerySelector("textarea"));
        Assert.Equal(1, dialog.Context().CommentThreadRequests);
    }

    [Fact]
    public async Task A_draft_survives_a_switch_to_another_thread_and_back_and_never_carries_over_into_it()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.InternalThreadPermission, TestParentDialogService.StaffThreadPermission, TestParentDialogService.StaffPostPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);

        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        dialog.Host.Find(".state-entity-dialog-side textarea[name=comment-internal]").Input("Half a note");

        dialog.Host.Find(".state-entity-dialog-side [role=tab][data-audience=staff]").Click();
        dialog.Host.WaitForAssertion(() => Assert.NotNull(dialog.Host.Find(".state-entity-dialog-side textarea[name=comment-staff]")), Wait);
        Assert.True(string.IsNullOrEmpty(dialog.Host.Find(".state-entity-dialog-side textarea[name=comment-staff]").GetAttribute("value")));
        dialog.Host.Find(".state-entity-dialog-side textarea[name=comment-staff]").Input("Half a message");

        dialog.Host.Find(".state-entity-dialog-side [role=tab][data-audience=internal]").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Half a note", dialog.Host.Find(".state-entity-dialog-side textarea[name=comment-internal]").GetAttribute("value")), Wait);
        dialog.Host.Find(".state-entity-dialog-side [role=tab][data-audience=staff]").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Half a message", dialog.Host.Find(".state-entity-dialog-side textarea[name=comment-staff]").GetAttribute("value")), Wait);

        await dialog.Host.Find(".state-entity-dialog-side button[data-post-comment=comment-staff]").ClickAsync(new());
        dialog.Host.WaitForAssertion(() => Assert.Equal(["Half a message"], dialog.Host.FindAll(".state-entity-dialog-side .comment-bubble__text").Select(text => text.TextContent)), Wait);
        dialog.Host.Find(".state-entity-dialog-side [role=tab][data-audience=internal]").Click();
        dialog.Host.WaitForAssertion(() => Assert.NotNull(dialog.Host.Find(".state-entity-dialog-side textarea[name=comment-internal]")), Wait);
        dialog.Host.Find(".state-entity-dialog-side [role=tab][data-audience=staff]").Click();
        dialog.Host.WaitForAssertion(() => Assert.True(string.IsNullOrEmpty(dialog.Host.Find(".state-entity-dialog-side textarea[name=comment-staff]").GetAttribute("value"))), Wait);
    }

    [Fact]
    public async Task A_post_in_the_side_column_saves_at_once_and_leaves_the_edits_and_the_footer_alone()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.InternalThreadPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);

        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        Assert.Equal("No notes yet.", dialog.Host.Find(".state-entity-dialog-side .comment-bubbles__empty").TextContent);
        var asked = dialog.Context().OfferRequests;

        dialog.Host.Find("[name=test-parent-name]").Change("Autumn period job");
        dialog.WaitForEdit();
        dialog.Host.Find(".state-entity-dialog-side textarea[name=comment-internal]").Input("Renaming it for the autumn.");
        await dialog.Host.Find(".state-entity-dialog-side button[data-post-comment=comment-internal]").ClickAsync(new());

        dialog.Host.WaitForAssertion(() => Assert.Equal(["Renaming it for the autumn."], dialog.Host.FindAll(".state-entity-dialog-side .comment-bubble__text").Select(text => text.TextContent)), Wait);
        Assert.Equal("1", dialog.Host.Find(".state-entity-dialog-side .dialog-side-tabs__count").TextContent);
        Assert.Equal("Comment posted.", dialog.Host.Find(".state-entity-dialog-side .comment-bubbles [role=status]").TextContent);
        Assert.True(dialog.Context().HasChanges);
        Assert.Equal("Spring period job", (await LoadAsync(id, cancellationToken))!.Name);
        Assert.Equal(asked, dialog.Context().OfferRequests);

        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read the parent's comments");
        var stored = await system.Comments.AsNoTracking().SingleAsync(comment => comment.CommentContainer!.EntityId == id, cancellationToken);
        Assert.Equal(("Renaming it for the autumn.", CommentKind.Comment, scenario.A.OwnerId), (stored.Text, stored.Kind, stored.CreatedById));
    }

    [Fact]
    public async Task A_steps_comment_prompt_answer_shows_in_the_side_column_after_the_step()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, RolePermission.SettingsEdit, TestParentDialogService.InternalThreadPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, "Spring period job", cancellationToken);

        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        dialog.Step("test-parent-cancel").Click();

        var prompt = dialog.Host.WaitForElement(".form-dialog textarea");
        Assert.Equal(Comment.MaxTextLength.ToString(System.Globalization.CultureInfo.InvariantCulture), prompt.GetAttribute("maxlength"));
        Assert.Equal($"0 of {Comment.MaxTextLength} characters", dialog.Host.Find(".form-dialog .prompt-dialog__count").TextContent);
        prompt.Change("The period was called off.");
        dialog.ConfirmButton("Canceled").Click();

        dialog.Host.WaitForAssertion(() => Assert.Equal(TestParentState.Canceled, dialog.Context().Entity!.State), Wait);
        dialog.Host.WaitForAssertion(() =>
        {
            var note = dialog.Host.Find(".state-entity-dialog-side li[data-kind=transitionnote]");
            Assert.Equal("The period was called off.", note.QuerySelector(".comment-bubble__text")!.TextContent);
            Assert.Null(note.QuerySelector(".comment-bubble__step-name"));
        }, Wait);
        Assert.Equal(2, dialog.Context().CommentThreadRequests);
    }

    [Fact]
    public async Task No_side_column_for_a_reader_without_a_threads_permission_for_a_new_parent_or_after_a_refused_read()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var reader = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Reader", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, reader, cancellationToken, TestParentDialogService.ViewPermission, TestParentDialogService.InternalThreadPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);

        await using (var circuit = Circuit(scenario.A.Runtime()))
        {
            var opened = await circuit.OpenLoadedAsync(id);
            Assert.Equal(1, opened.Context().CommentThreadRequests);
            Assert.Empty(opened.Host.FindAll(".state-entity-dialog-side"));
            Assert.Empty(opened.Host.FindAll(".state-entity-dialog__main--with-side"));
            Assert.NotNull(opened.Host.Find(".state-entity-dialog__main .state-entity-dialog__body"));

            var shell = circuit.Dialog.Find(".rz-dialog");
            Assert.Contains(StateEntityDialogOptions.WidensForSideColumnClass, shell.ClassList);
            Assert.Contains($"width: {StateEntityDialogOptions.DefaultWidth}", shell.GetAttribute("style"), StringComparison.Ordinal);
        }

        await using (var circuit = Circuit(scenario.A.RuntimeOf(reader)))
        {
            var allowed = await circuit.OpenLoadedAsync(id);
            Assert.NotNull(allowed.Host.Find(".state-entity-dialog__main--with-side .state-entity-dialog-side"));
        }

        await using (var circuit = Circuit(scenario.A.RuntimeOf(reader)))
        {
            var create = await circuit.OpenCreateAsync();
            Assert.Empty(create.Host.FindAll(".state-entity-dialog-side"));
            Assert.Empty(create.Host.FindAll(".state-entity-dialog__main--with-side"));
            Assert.Equal(0, create.Context().CommentThreadRequests);
        }

        await using (var circuit = Circuit(scenario.A.RuntimeOf(reader)))
        {
            circuit.Service.CommentsThrow = "The comments' query failed.";
            var broken = await circuit.OpenLoadedAsync(id);
            var alert = broken.Host.Find(".state-entity-dialog-side [role=alert]");
            Assert.Contains(StateEntityDialogContext.CommentsFailed, alert.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("query failed", broken.Host.Markup, StringComparison.Ordinal);
            Assert.NotNull(broken.Host.Find("[name=test-parent-name]"));
        }
    }

    private async Task PostAsync(IRuntimeExecutionContext runtime, Guid id, CommentAudience audience, string text, CancellationToken cancellationToken)
    {
        await using var host = ServiceHost.Create(this.database, runtime, services => services.AddScoped<TestParentDialogService>());
        var posted = await host.Get<TestParentDialogService>().AddTestParentCommentAsync(id, audience, text, cancellationToken);
        Assert.True(posted.IsSuccess, posted.Error);
    }
}
