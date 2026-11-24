using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Messaging;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Comments;
using Staffora.Services.Messaging;
using Staffora.Services.Scheduling;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class AbsenceQuestionTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly Monday = new(2026, 11, 9);

    private readonly RealDatabase database;

    public AbsenceQuestionTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_staff_members_question_is_answered_handled_and_the_absence_then_approved()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Asking Coordinator", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        await using var approver = scenario.HostOf(this.database, scenario.Approver);
        var own = staff.Get<ResourceAbsenceService>();
        var decider = approver.Get<ResourceAbsenceService>();
        var id = (await AbsenceScenario.ReportAsync(own, kim, AbsenceKind.PlannedLeave, Monday.AddDays(14), Monday.AddDays(18), Ct)).Value;
        var reported = await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct);

        Assert.True((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: "Can I take the Monday after too?")).IsSuccess);
        Assert.Equal(ResourceAbsenceState.StaffQuestion, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);
        (string Subject, string Text, string? Link) question = ("New question about an absence", "Kim Example has a question about their planned leave from 2026-11-23 to 2026-11-27.\n\nCan I take the Monday after too?", $"?dlg=absence-{id}");
        Assert.Equal([question, .. reported], await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct));
        Assert.Equal(question, (await scenario.NoticesOfAsync(this.database, scenario.Coordinator, Ct))[0]);
        Assert.Empty(await scenario.NoticesOfAsync(this.database, scenario.Registrar, Ct));
        Assert.Empty(await scenario.NoticesOfAsync(this.database, scenario.Viewer, Ct));
        Assert.Empty(await scenario.NoticesOfAsync(this.database, kim, Ct));
        Assert.True((await decider.GetResourceAbsenceEditorOptionsAsync(id, null, Ct)).Value!.FieldsLocked);

        Assert.False((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).IsSuccess);
        Assert.False((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.RejectStep, Ct, comment: "No.")).IsSuccess);

        Assert.True((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.AnswerStaffStep, Ct, comment: "Yes, report it as its own absence.")).IsSuccess);
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);
        Assert.Equal(
            [("New answer about your absence", "Hello Kim, a coordinator answered your question about your planned leave from 2026-11-23 to 2026-11-27. It waits for an approver again.\n\nYes, report it as its own absence.", $"?dlg=absence-{id}")],
            await scenario.NoticesOfAsync(this.database, kim, Ct));

        Assert.True((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: "Thanks. And can it start at noon?")).IsSuccess);
        Assert.Equal("Kim Example has a question about their planned leave from 2026-11-23 to 2026-11-27.\n\nThanks. And can it start at noon?", (await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct))[0].Text);

        var toldBefore = (await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct)).Count + (await scenario.NoticesOfAsync(this.database, kim, Ct)).Count;
        Assert.True((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.QuestionHandledStep, Ct)).IsSuccess);
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);
        Assert.Equal(toldBefore, (await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct)).Count + (await scenario.NoticesOfAsync(this.database, kim, Ct)).Count);
        Assert.True((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).IsSuccess);
        Assert.Equal(ResourceAbsenceState.Approved, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);

        Assert.Equal(
            [
                ResourceAbsenceWorkflowAttribute.ReportStep, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, ResourceAbsenceWorkflowAttribute.AnswerStaffStep,
                ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, ResourceAbsenceWorkflowAttribute.QuestionHandledStep, ResourceAbsenceWorkflowAttribute.ApproveStep,
            ],
            await AbsenceScenario.HistoryAsync(this.database, id, Ct));

        var expected = new[]
        {
            ("Can I take the Monday after too?", "Kim Example"),
            ("Yes, report it as its own absence.", "Avery Example"),
            ("Thanks. And can it start at noon?", "Kim Example"),
        };
        foreach (var reader in new[] { own, decider })
        {
            var thread = Assert.Single((await reader.GetResourceAbsenceCommentsAsync(id, Ct)).Value!);
            Assert.Equal(CommentAudience.Staff, thread.Audience);
            Assert.Equal(expected, thread.Comments.Select(comment => (comment.Text, comment.Author)));
            Assert.All(thread.Comments, comment => Assert.Equal(CommentKind.TransitionNote, comment.Kind));
        }
    }

    [Fact]
    public async Task A_coordinators_question_is_answered_with_corrected_dates_or_words_or_left_without_an_answer()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Asking Staff", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        await using var approver = scenario.HostOf(this.database, scenario.Approver);
        await using var registrar = scenario.HostOf(this.database, scenario.Registrar);
        var own = staff.Get<ResourceAbsenceService>();
        var decider = approver.Get<ResourceAbsenceService>();
        var id = (await AbsenceScenario.ReportAsync(own, kim, AbsenceKind.PlannedLeave, Monday, Monday.AddDays(4), Ct)).Value;
        var reported = (await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct)).Count;

        Assert.True((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.AskStaffStep, Ct, comment: "Is it the whole week?")).IsSuccess);
        Assert.Equal(ResourceAbsenceState.Question, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);
        Assert.Equal(
            [("New question about your absence", "Hello Kim, a coordinator wrote to you about your planned leave from 2026-11-09 to 2026-11-13.\n\nIs it the whole week?", $"?dlg=absence-{id}")],
            await scenario.NoticesOfAsync(this.database, kim, Ct));

        Assert.False((await own.GetResourceAbsenceEditorOptionsAsync(id, null, Ct)).Value!.FieldsLocked);
        Assert.True((await registrar.Get<ResourceAbsenceService>().GetResourceAbsenceEditorOptionsAsync(id, null, Ct)).Value!.FieldsLocked);
        Assert.False((await AbsenceScenario.StepAsync(registrar.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.SaveQuestionStep, Ct, edit => edit.LastDay = Monday.AddDays(1))).IsSuccess);
        Assert.False((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.SaveQuestionStep, Ct, edit => edit.LastDay = Monday.AddDays(1))).IsSuccess);
        Assert.True((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.SaveQuestionStep, Ct, edit => edit.LastDay = Monday.AddDays(3))).IsSuccess);
        Assert.Equal((ResourceAbsenceState.Question, Monday.AddDays(3)), ((await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.LastDay));

        Assert.True((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.AskStaffAgainStep, Ct, comment: "And the Friday?")).IsSuccess);
        Assert.Equal(ResourceAbsenceState.Question, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);
        Assert.Equal(2, (await scenario.NoticesOfAsync(this.database, kim, Ct)).Count);

        Assert.True((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AnswerStep, Ct, edit => edit.LastDay = Monday.AddDays(2), comment: "  ")).IsSuccess);
        var stored = (await AbsenceScenario.StoredAsync(this.database, id, Ct))!;
        Assert.Equal((ResourceAbsenceState.Requested, Monday.AddDays(2)), (stored.State, stored.LastDay));
        Assert.Equal(
            ("New answer on an absence", "Kim Example answered the question about their planned leave from 2026-11-09 to 2026-11-11. It waits for an approver again.", $"?dlg=absence-{id}"),
            (await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct))[0]);
        Assert.Equal(["Is it the whole week?", "And the Friday?"], (await own.GetResourceAbsenceCommentsAsync(id, Ct)).Value!.Single().Comments.Select(comment => comment.Text));

        Assert.True((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.AskStaffStep, Ct, comment: "Morning or afternoon on the Wednesday?")).IsSuccess);
        Assert.True((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AnswerStep, Ct, comment: "The whole day.")).IsSuccess);
        Assert.EndsWith("\n\nThe whole day.", (await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct))[0].Text, StringComparison.Ordinal);
        Assert.Equal("The whole day.", (await own.GetResourceAbsenceCommentsAsync(id, Ct)).Value!.Single().Comments[^1].Text);

        Assert.True((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.AskStaffStep, Ct, comment: "Anything else?")).IsSuccess);
        var told = (await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct)).Count + (await scenario.NoticesOfAsync(this.database, kim, Ct)).Count;
        Assert.True((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.ContinueWithoutAnswerStep, Ct)).IsSuccess);
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);
        Assert.Equal(told, (await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct)).Count + (await scenario.NoticesOfAsync(this.database, kim, Ct)).Count);
        Assert.Equal(reported + 2, (await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct)).Count);

        Assert.Equal(
            [
                ResourceAbsenceWorkflowAttribute.ReportStep, ResourceAbsenceWorkflowAttribute.AskStaffStep, ResourceAbsenceWorkflowAttribute.SaveQuestionStep,
                ResourceAbsenceWorkflowAttribute.AskStaffAgainStep, ResourceAbsenceWorkflowAttribute.AnswerStep, ResourceAbsenceWorkflowAttribute.AskStaffStep,
                ResourceAbsenceWorkflowAttribute.AnswerStep, ResourceAbsenceWorkflowAttribute.AskStaffStep, ResourceAbsenceWorkflowAttribute.ContinueWithoutAnswerStep,
            ],
            await AbsenceScenario.HistoryAsync(this.database, id, Ct));
    }

    [Fact]
    public async Task Each_question_step_is_taken_only_by_its_side_and_never_on_ones_own_absence_by_the_coordinators_side()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Question Sides", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var robin = await scenario.AddStaffAsync(this.database, "Robin", Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        await using var robins = scenario.HostOf(this.database, robin);
        await using var approver = scenario.HostOf(this.database, scenario.Approver);
        await using var registrar = scenario.HostOf(this.database, scenario.Registrar);
        await using var viewer = scenario.HostOf(this.database, scenario.Viewer);
        var own = staff.Get<ResourceAbsenceService>();
        var id = (await AbsenceScenario.ReportAsync(own, kim, AbsenceKind.PlannedLeave, Monday, Monday, Ct)).Value;

        foreach (var host in new[] { staff, registrar, viewer })
        {
            Assert.False((await AbsenceScenario.StepAsync(host.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.AskStaffStep, Ct, comment: "Why?")).IsSuccess);
        }

        foreach (var host in new[] { approver, registrar })
        {
            Assert.False((await AbsenceScenario.StepAsync(host.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: "Why?")).IsSuccess);
        }

        Assert.Equal(ResourceAbsenceService.NotFound, (await AbsenceScenario.StepAsync(robins.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: "Why?")).Error);
        var robinPast = await robins.Get<WorkflowService>().TransitionEntityAsync<ResourceAbsenceState, ResourceAbsence>(id, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, comment: "Why?", cancellationToken: Ct);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.OwnQuestionReason, robinPast.Error, StringComparison.Ordinal);
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);

        Assert.True((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.AskStaffStep, Ct, comment: "Which day?")).IsSuccess);
        Assert.False((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AskStaffAgainStep, Ct, comment: "Me?")).IsSuccess);
        Assert.False((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.ContinueWithoutAnswerStep, Ct)).IsSuccess);
        Assert.False((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.AnswerStep, Ct, comment: "For Kim.")).IsSuccess);
        Assert.False((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).IsSuccess);
        Assert.False((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).IsSuccess);
        Assert.False((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.RejectStep, Ct, comment: "No.")).IsSuccess);
        Assert.Equal(ResourceAbsenceState.Question, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);
        Assert.True((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AnswerStep, Ct, comment: "Monday.")).IsSuccess);

        Assert.True((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: "Is that enough?")).IsSuccess);
        Assert.False((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AnswerStaffStep, Ct, comment: "Me again.")).IsSuccess);
        Assert.False((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.QuestionHandledStep, Ct)).IsSuccess);
        Assert.False((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.AskCoordinatorAgainStep, Ct, comment: "For Kim.")).IsSuccess);
        Assert.False((await AbsenceScenario.StepAsync(registrar.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.QuestionHandledStep, Ct)).IsSuccess);
        Assert.False((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct)).IsSuccess);
        Assert.Equal(ResourceAbsenceState.StaffQuestion, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);

        static void Stretch(ResourceAbsence absence) => absence.LastDay = Monday.AddDays(2);
        Assert.Equal(TransitionRefusals.PendingEdits, (await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AskCoordinatorAgainStep, Ct, Stretch, "More.")).Error);
        Assert.Equal(TransitionRefusals.PendingEdits, (await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.AnswerStaffStep, Ct, Stretch, "Yes.")).Error);
        Assert.Equal(TransitionRefusals.PendingEdits, (await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.QuestionHandledStep, Ct, Stretch)).Error);
        Assert.Equal(Monday, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.LastDay);

        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(scenario.Coordinator, scenario.StaffMembers.TeamId, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        }

        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);
        var theirs = (await AbsenceScenario.ReportAsync(coordinator.Get<ResourceAbsenceService>(), scenario.Coordinator, AbsenceKind.PlannedLeave, Monday, Monday, Ct)).Value;
        Assert.Contains(ResourceAbsenceWorkflowAttribute.NotOwnQuestionReason, (await AbsenceScenario.StepAsync(coordinator.Get<ResourceAbsenceService>(), theirs, ResourceAbsenceWorkflowAttribute.AskStaffStep, Ct, comment: "Myself?")).Error, StringComparison.Ordinal);
        Assert.True((await AbsenceScenario.StepAsync(coordinator.Get<ResourceAbsenceService>(), theirs, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: "Colleagues, is this fine?")).IsSuccess);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.NotOwnQuestionReason, (await AbsenceScenario.StepAsync(coordinator.Get<ResourceAbsenceService>(), theirs, ResourceAbsenceWorkflowAttribute.QuestionHandledStep, Ct)).Error, StringComparison.Ordinal);
        Assert.True((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), theirs, ResourceAbsenceWorkflowAttribute.QuestionHandledStep, Ct)).IsSuccess);
    }

    [Fact]
    public async Task Required_prompts_refuse_an_empty_answer_and_the_optional_answer_writes_nothing()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Question Prompts", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        await using var approver = scenario.HostOf(this.database, scenario.Approver);
        var own = staff.Get<ResourceAbsenceService>();
        var decider = approver.Get<ResourceAbsenceService>();
        var id = (await AbsenceScenario.ReportAsync(own, kim, AbsenceKind.PlannedLeave, Monday, Monday, Ct)).Value;

        var workflow = WorkflowRegistry.Default.Get<ResourceAbsenceState, ResourceAbsence>();
        var prompted = workflow.Transitions.Where(step => step.CommentPrompt is not null).ToDictionary(step => step.Key, step => step.CommentPrompt!);
        Assert.Equal(
            [
                ResourceAbsenceWorkflowAttribute.RejectStep, ResourceAbsenceWorkflowAttribute.AskStaffStep, ResourceAbsenceWorkflowAttribute.AskStaffAgainStep,
                ResourceAbsenceWorkflowAttribute.AnswerStaffStep, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, ResourceAbsenceWorkflowAttribute.AskCoordinatorAgainStep,
                ResourceAbsenceWorkflowAttribute.AnswerStep,
            ],
            prompted.Keys);
        Assert.All(prompted.Values, prompt => Assert.Equal(CommentAudience.Staff, prompt.Audience));
        Assert.Equal([ResourceAbsenceWorkflowAttribute.AnswerStep], prompted.Where(pair => !pair.Value.Required).Select(pair => pair.Key));
        Assert.Equal(
            ("Write a question to the coordinator", "The coordinator sees your question on the absence.", "What do you want to ask about?"),
            (ResourceAbsenceWorkflowAttribute.AskCoordinatorPrompt.Title, ResourceAbsenceWorkflowAttribute.AskCoordinatorPrompt.Message, ResourceAbsenceWorkflowAttribute.AskCoordinatorPrompt.Placeholder));
        Assert.DoesNotContain(prompted.Values, prompt => $"{prompt.Title}{prompt.Message}{prompt.Placeholder}{prompt.RequiredMessage}".Contains(';', StringComparison.Ordinal));

        foreach (var blank in new[] { null, "", "   " })
        {
            Assert.Contains("asks a comment", (await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.AskStaffStep, Ct, comment: blank)).Error, StringComparison.Ordinal);
            Assert.Contains("asks a comment", (await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: blank)).Error, StringComparison.Ordinal);
        }

        Assert.Contains("longer than", (await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: new string('x', Comment.MaxTextLength + 1))).Error, StringComparison.Ordinal);
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);

        Assert.True((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: "May I?")).IsSuccess);
        Assert.Contains("asks a comment", (await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.AnswerStaffStep, Ct)).Error, StringComparison.Ordinal);
        Assert.Contains("asks a comment", (await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AskCoordinatorAgainStep, Ct)).Error, StringComparison.Ordinal);
        Assert.True((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.QuestionHandledStep, Ct)).IsSuccess);
        Assert.True((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.AskStaffStep, Ct, comment: "Which day?")).IsSuccess);
        Assert.Contains("asks a comment", (await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.AskStaffAgainStep, Ct, comment: " ")).Error, StringComparison.Ordinal);

        Assert.True((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AnswerStep, Ct)).IsSuccess);
        Assert.Equal(["May I?", "Which day?"], (await own.GetResourceAbsenceCommentsAsync(id, Ct)).Value!.Single().Comments.Select(comment => comment.Text));
    }

    [Fact]
    public async Task The_words_are_written_in_the_steps_own_transaction_and_the_thread_has_no_composer()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Question Thread", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        await using var approver = scenario.HostOf(this.database, scenario.Approver);
        await using var registrar = scenario.HostOf(this.database, scenario.Registrar);
        await using var viewer = scenario.HostOf(this.database, scenario.Viewer);
        var own = staff.Get<ResourceAbsenceService>();
        var decider = approver.Get<ResourceAbsenceService>();
        var id = (await AbsenceScenario.ReportAsync(own, kim, AbsenceKind.PlannedLeave, Monday, Monday, Ct)).Value;

        Assert.Empty((await own.GetResourceAbsenceCommentsAsync(id, Ct)).Value!);
        Assert.Empty((await decider.GetResourceAbsenceCommentsAsync(id, Ct)).Value!);

        var opened = (await decider.LoadResourceAbsenceUpdaterAsync(id, Ct)).Value!;
        Assert.True((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.SaveStep, Ct, edit => edit.LastDay = Monday.AddDays(1))).IsSuccess);
        Assert.Equal(TransitionRefusals.ChangedSinceOpened(nameof(ResourceAbsence), "Send a question"), (await decider.UpdateResourceAbsenceAndTransitionAsync(opened, ResourceAbsenceWorkflowAttribute.AskStaffStep, "About the Monday?", Ct)).Error);
        Assert.Equal(0, await CommentsOfAsync(id));
        Assert.Empty(await scenario.NoticesOfAsync(this.database, kim, Ct));

        Assert.True((await AbsenceScenario.StepAsync(decider, id, ResourceAbsenceWorkflowAttribute.AskStaffStep, Ct, comment: "About the two days?")).IsSuccess);
        await using (var context = this.database.CreateSystemContext())
        {
            var note = await context.Comments.AsNoTracking().SingleAsync(comment => comment.CommentContainer!.EntityId == id, Ct);
            Assert.Equal((CommentAudience.Staff, CommentKind.TransitionNote, ResourceAbsenceWorkflowAttribute.AskStaffStep, "About the two days?"), ((await context.CommentContainers.AsNoTracking().SingleAsync(container => container.EntityId == id, Ct)).Audience, note.Kind, note.TransitionKey, note.Text));
            Assert.Contains(ResourceAbsenceWorkflowAttribute.AskStaffStep, await AbsenceScenario.HistoryAsync(this.database, id, Ct));
        }

        foreach (var reader in new[] { own, decider })
        {
            var thread = Assert.Single((await reader.GetResourceAbsenceCommentsAsync(id, Ct)).Value!);
            Assert.Equal((CommentAudience.Staff, false, (string?)null), (thread.Audience, thread.CanPost, thread.ClosedReason));
        }

        Assert.Empty((await registrar.Get<ResourceAbsenceService>().GetResourceAbsenceCommentsAsync(id, Ct)).Value!);
        Assert.Empty((await viewer.Get<ResourceAbsenceService>().GetResourceAbsenceCommentsAsync(id, Ct)).Value!);

        Assert.Equal(ResourceAbsenceService.WrittenThroughSteps, (await staff.Get<CommentService>().AddCommentAsync<ResourceAbsence>(id, ResourceAbsenceService.OwnThread, "A side note.", Ct)).Error);
        Assert.Equal(ResourceAbsenceService.WrittenThroughSteps, (await approver.Get<CommentService>().AddCommentAsync<ResourceAbsence>(id, ResourceAbsenceService.ApproverThread, "A side note.", Ct)).Error);
        Assert.Equal(1, await CommentsOfAsync(id));
    }

    [Fact]
    public async Task A_sick_leave_blocks_in_both_question_states_and_only_the_staff_member_cancels()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Question Blocks", Ct);
        var kim = await scenario.AddWorkingStaffAsync(this.database, "Kim", Monday, Ct);
        var resource = await ScheduleScenario.ResourceOfAsync(this.database, kim, Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);
        var own = staff.Get<ResourceAbsenceService>();
        var manager = coordinator.Get<ResourceAbsenceService>();

        var sick = (await AbsenceScenario.ReportAsync(own, kim, AbsenceKind.SickLeave, Monday, Monday, Ct)).Value;
        var leave = (await AbsenceScenario.ReportAsync(own, kim, AbsenceKind.PlannedLeave, Monday.AddDays(2), Monday.AddDays(2), Ct)).Value;
        Assert.True((await AbsenceScenario.StepAsync(manager, sick, ResourceAbsenceWorkflowAttribute.AskStaffStep, Ct, comment: "Do you have a note?")).IsSuccess);
        Assert.True((await AbsenceScenario.StepAsync(own, leave, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: "Is the Wednesday fine?")).IsSuccess);
        Assert.Equal([sick], await BlocksAsync(coordinator, resource));
        Assert.True((await AbsenceScenario.StepAsync(own, sick, ResourceAbsenceWorkflowAttribute.AnswerStep, Ct, comment: "Yes.")).IsSuccess);
        Assert.True((await AbsenceScenario.StepAsync(own, sick, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: "Do you need it?")).IsSuccess);
        Assert.Equal(ResourceAbsenceState.StaffQuestion, (await AbsenceScenario.StoredAsync(this.database, sick, Ct))!.State);
        Assert.Equal([sick], await BlocksAsync(coordinator, resource));
        Assert.True(AbsenceRules.Blocks(AbsenceKind.SickLeave, ResourceAbsenceState.Question));
        Assert.False(AbsenceRules.Blocks(AbsenceKind.PlannedLeave, ResourceAbsenceState.StaffQuestion));

        Assert.All(new[] { ResourceAbsenceState.Question, ResourceAbsenceState.StaffQuestion }, state => Assert.True(AbsenceRules.IsUndecided(state) && AbsenceRules.ScheduledStates.Contains(state)));

        Assert.True((await AbsenceScenario.StepAsync(own, sick, ResourceAbsenceWorkflowAttribute.CancelStaffQuestionStep, Ct)).IsSuccess);
        Assert.Equal(
            ("An absence was canceled", "Kim Example canceled their sick leave on 2026-11-09. It no longer keeps them from being staffed.", $"?dlg=absence-{sick}"),
            (await scenario.NoticesOfAsync(this.database, scenario.Approver, Ct))[0]);
        Assert.False((await AbsenceScenario.StepAsync(manager, leave, ResourceAbsenceWorkflowAttribute.AskStaffStep, Ct, comment: "Never mind.")).IsSuccess);
        Assert.True((await AbsenceScenario.StepAsync(manager, leave, ResourceAbsenceWorkflowAttribute.QuestionHandledStep, Ct)).IsSuccess);
        Assert.True((await AbsenceScenario.StepAsync(manager, leave, ResourceAbsenceWorkflowAttribute.AskStaffStep, Ct, comment: "Can it move?")).IsSuccess);
        Assert.Contains(ResourceAbsenceWorkflowAttribute.OwnCancelReason, (await AbsenceScenario.StepAsync(manager, leave, ResourceAbsenceWorkflowAttribute.CancelQuestionStep, Ct)).Error, StringComparison.Ordinal);
        Assert.True((await AbsenceScenario.StepAsync(own, leave, ResourceAbsenceWorkflowAttribute.CancelQuestionStep, Ct)).IsSuccess);
        Assert.Empty(await BlocksAsync(coordinator, resource));
        Assert.Equal(ResourceAbsenceState.Canceled, (await AbsenceScenario.StoredAsync(this.database, leave, Ct))!.State);
    }

    [Fact]
    public async Task Mark_as_handled_settles_a_staff_members_question_for_every_coordinator_without_telling_the_staff_member()
    {
        var scenario = await AbsenceScenario.CreateAsync(this.database, "Question Settled", Ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", Ct);
        var dana = await scenario.AddCoordinatorAsync(this.database, "Dana", Ct);
        await using var staff = scenario.HostOf(this.database, kim);
        var own = staff.Get<ResourceAbsenceService>();
        var id = (await AbsenceScenario.ReportAsync(own, kim, AbsenceKind.PlannedLeave, Monday.AddDays(7), Monday.AddDays(8), Ct)).Value;
        Assert.True((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: "Can I swap the Tuesday?")).IsSuccess);
        var coordinators = new[] { scenario.Coordinator, dana };
        foreach (var coordinator in coordinators.Append(scenario.Approver))
        {
            Assert.Equal(1, await UnreadQuestionsAsync(coordinator, id));
        }

        foreach (var coordinator in coordinators)
        {
            Assert.Contains($"absence-{id}", await scenario.StaffQuestionKeysAsync(this.database, coordinator, Ct));
        }

        var step = WorkflowRegistry.Default.Get<ResourceAbsenceState, ResourceAbsence>().GetTransition(ResourceAbsenceWorkflowAttribute.QuestionHandledStep);
        Assert.Equal(("absence-question-handled", "Mark as handled", "done_all", (TransitionCommentPrompt?)null), (step.Key, step.Label, step.Icon, step.CommentPrompt));
        await using (var registrar = scenario.HostOf(this.database, scenario.Registrar))
        {
            Assert.False((await AbsenceScenario.StepAsync(registrar.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.QuestionHandledStep, Ct)).IsSuccess);
        }

        Assert.False((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.QuestionHandledStep, Ct)).IsSuccess);
        var kimBefore = await scenario.NoticesOfAsync(this.database, kim, Ct);
        var bus = new NoticeBus(NullLogger<NoticeBus>.Instance);
        var told = new ConcurrentQueue<Guid>();
        var subscriptions = coordinators.Append(scenario.Approver).Append(kim)
            .Select(user => bus.Subscribe(scenario.TenantId, user, notice =>
            {
                told.Enqueue(notice.MarkedRead ? user : Guid.Empty);
                return Task.CompletedTask;
            }))
            .ToList();
        await using (var handling = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(dana), services => services.AddSingleton(bus)))
        {
            var handled = await AbsenceScenario.StepAsync(handling.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.QuestionHandledStep, Ct);
            Assert.True(handled.IsSuccess, handled.Error);
        }

        subscriptions.ForEach(subscription => subscription.Dispose());
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);
        Assert.Equal(dana, (await AbsenceScenario.LastStepByAsync(this.database, id, ResourceAbsenceWorkflowAttribute.QuestionHandledStep, Ct)).ActorId);
        Assert.Equal(kimBefore, await scenario.NoticesOfAsync(this.database, kim, Ct));
        Assert.Equal(["Can I swap the Tuesday?"], (await own.GetResourceAbsenceCommentsAsync(id, Ct)).Value!.Single().Comments.Select(comment => comment.Text));
        foreach (var coordinator in coordinators.Append(scenario.Approver))
        {
            Assert.Equal(0, await UnreadQuestionsAsync(coordinator, id));
        }

        foreach (var coordinator in coordinators)
        {
            Assert.DoesNotContain($"absence-{id}", await scenario.StaffQuestionKeysAsync(this.database, coordinator, Ct));
        }

        Assert.Equal(coordinators.Append(scenario.Approver).Order(), told.Order());

        Assert.True((await AbsenceScenario.StepAsync(own, id, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: "And the Wednesday?")).IsSuccess);
        Assert.Equal(1, await UnreadQuestionsAsync(dana, id));
        await using (var approver = scenario.HostOf(this.database, scenario.Approver))
        {
            Assert.True((await AbsenceScenario.StepAsync(approver.Get<ResourceAbsenceService>(), id, ResourceAbsenceWorkflowAttribute.AnswerStaffStep, Ct, comment: "Yes, both.")).IsSuccess);
        }

        foreach (var coordinator in coordinators.Append(scenario.Approver))
        {
            Assert.Equal(0, await UnreadQuestionsAsync(coordinator, id));
        }

        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);
        Assert.DoesNotContain($"absence-{id}", await scenario.StaffQuestionKeysAsync(this.database, dana, Ct));
    }

    private async Task<int> UnreadQuestionsAsync(Guid userId, Guid absenceId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.MessageRecipients.AsNoTracking().CountAsync(copy => copy.UserId == userId && !copy.IsRead && copy.StoredMessage!.EntityId == absenceId && copy.StoredMessage.MessageType == MessageTypes.AbsenceQuestionFromStaff.Key, Ct);
    }

    private async Task<int> CommentsOfAsync(Guid absenceId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Comments.AsNoTracking().CountAsync(comment => comment.CommentContainer!.EntityId == absenceId, Ct);
    }

    private static async Task<List<Guid>> BlocksAsync(ServiceHost coordinator, Guid resource)
    {
        var answer = await coordinator.Get<IStaffAvailability>().ReadAsync([resource], Monday, Monday.AddDays(6), TestContext.Current.CancellationToken);
        Assert.True(answer.IsSuccess, answer.Error);
        return [.. answer.Value!.StaffMembers[resource].Blocks.Select(block => block.SourceId!.Value)];
    }
}
