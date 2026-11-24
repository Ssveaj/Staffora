using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Coordination;
using Staffora.Services.Jobs;
using Staffora.Services.Messaging;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class AssignmentQuestionTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private readonly RealDatabase database;

    public AssignmentQuestionTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_staff_members_question_is_answered_the_request_is_theirs_again_and_they_accept_each_side_told_with_the_words()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Question Staffing", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        var coordinator = scenario.Coordinator;
        var id = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));
        var coordinatorBefore = await scenario.NoticesOfAsync(this.database, coordinator, Ct);
        var adaBefore = await scenario.NoticesOfAsync(this.database, ada, Ct);

        Assert.True((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Is there parking at the place?")).IsSuccess);
        Assert.Equal(SlotAssignmentState.StaffQuestion, (await AssignmentScenario.StoredAsync(this.database, id, Ct))!.State);
        (string Subject, string Text, string? Link) question = ("New question about a request", "Ada Example has a question about working Algebra as Lead before answering the request.\n\nIs there parking at the place?", $"?dlg=assignment-{id}");
        Assert.Equal([question, .. coordinatorBefore], await scenario.NoticesOfAsync(this.database, coordinator, Ct));
        Assert.Equal(adaBefore, await scenario.NoticesOfAsync(this.database, ada, Ct));

        Assert.Contains($"question-{id}", await StaffQuestionKeysAsync(scenario));

        Assert.True((await scenario.StepAsync(this.database, coordinator, id, SlotAssignmentWorkflowAttribute.AnswerStaffStep, Ct, "Yes, behind the building.")).IsSuccess);
        Assert.Equal(SlotAssignmentState.Requested, (await AssignmentScenario.StoredAsync(this.database, id, Ct))!.State);
        Assert.Equal(
            ("New answer about your request", "Hello Ada, a coordinator answered your question about working Algebra as Lead. Open the request to accept or decline it.\n\nYes, behind the building.", $"?dlg=assignment-{id}"),
            (await scenario.NoticesOfAsync(this.database, ada, Ct))[0]);
        Assert.DoesNotContain($"question-{id}", await StaffQuestionKeysAsync(scenario));

        Assert.True((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Thanks. And is lunch included?")).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AskCoordinatorAgainStep, Ct, "Or do I bring my own?")).IsSuccess);
        Assert.Equal(SlotAssignmentState.StaffQuestion, (await AssignmentScenario.StoredAsync(this.database, id, Ct))!.State);
        Assert.EndsWith("\n\nOr do I bring my own?", (await scenario.NoticesOfAsync(this.database, coordinator, Ct))[0].Text, StringComparison.Ordinal);

        Assert.True((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AcceptQuestionStep, Ct)).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, coordinator, id, SlotAssignmentWorkflowAttribute.PickStep, Ct)).IsSuccess);
        Assert.Equal(SlotAssignmentState.Assigned, (await AssignmentScenario.StoredAsync(this.database, id, Ct))!.State);

        Assert.Equal(
            [
                SlotAssignmentWorkflowAttribute.RequestStep, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, SlotAssignmentWorkflowAttribute.AnswerStaffStep,
                SlotAssignmentWorkflowAttribute.AskCoordinatorStep, SlotAssignmentWorkflowAttribute.AskCoordinatorAgainStep, SlotAssignmentWorkflowAttribute.AcceptQuestionStep,
                SlotAssignmentWorkflowAttribute.PickStep,
            ],
            await AssignmentScenario.HistoryAsync(this.database, id, Ct));

        var expected = new[]
        {
            ("Is there parking at the place?", "Ada Example"),
            ("Yes, behind the building.", "Casey Example"),
            ("Thanks. And is lunch included?", "Ada Example"),
            ("Or do I bring my own?", "Ada Example"),
        };
        foreach (var reader in new[] { ada, coordinator })
        {
            await using var host = scenario.HostOf(this.database, reader);
            var thread = Assert.Single((await host.Get<SlotAssignmentService>().Conversations.GetSlotAssignmentCommentsAsync(id, Ct)).Value!);
            Assert.Equal(CommentAudience.Staff, thread.Audience);
            Assert.Equal(expected, thread.Comments.Select(comment => (comment.Text, comment.Author)));
            Assert.All(thread.Comments, comment => Assert.Equal(CommentKind.TransitionNote, comment.Kind));
        }
    }

    [Fact]
    public async Task Each_question_step_is_taken_only_by_its_side_and_only_from_its_state()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Question Sides", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct);
        var coordinator = scenario.Coordinator;
        var id = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));

        Assert.False((await scenario.StepAsync(this.database, coordinator, id, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "On her behalf?")).IsSuccess);
        Assert.Equal(SlotAssignmentService.NotFound, (await scenario.StepAsync(this.database, bo, id, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Me too?")).Error);
        Assert.False((await scenario.StepAsync(this.database, coordinator, id, SlotAssignmentWorkflowAttribute.AnswerStaffStep, Ct, "An answer.")).IsSuccess);
        Assert.False((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AskCoordinatorAgainStep, Ct, "Again?")).IsSuccess);
        Assert.Equal(SlotAssignmentState.Requested, (await AssignmentScenario.StoredAsync(this.database, id, Ct))!.State);

        Assert.True((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Which floor?")).IsSuccess);
        Assert.False((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AnswerStaffStep, Ct, "Self-answered.")).IsSuccess);
        Assert.False((await scenario.StepAsync(this.database, coordinator, id, SlotAssignmentWorkflowAttribute.AskCoordinatorAgainStep, Ct, "Another?")).IsSuccess);
        Assert.False((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Twice?")).IsSuccess);
        Assert.False((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.Equal(SlotAssignmentState.StaffQuestion, (await AssignmentScenario.StoredAsync(this.database, id, Ct))!.State);
        Assert.Equal([SlotAssignmentWorkflowAttribute.RequestStep, SlotAssignmentWorkflowAttribute.AskCoordinatorStep], await AssignmentScenario.HistoryAsync(this.database, id, Ct));

        await using (var staff = scenario.HostOf(this.database, ada))
        {
            Assert.Equal(
                [SlotAssignmentWorkflowAttribute.DeclineQuestionStep, SlotAssignmentWorkflowAttribute.AskCoordinatorAgainStep, SlotAssignmentWorkflowAttribute.AcceptQuestionStep],
                await OfferedAsync(staff, id));
        }

        await using (var coordinating = scenario.HostOf(this.database, coordinator))
        {
            Assert.Equal(
                [SlotAssignmentWorkflowAttribute.CancelQuestionStep, SlotAssignmentWorkflowAttribute.QuestionHandledStep, SlotAssignmentWorkflowAttribute.AnswerStaffStep],
                await OfferedAsync(coordinating, id));
        }
    }

    [Fact]
    public async Task Every_question_prompt_is_required_and_a_blank_one_moves_nothing()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Question Prompts", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Chemistry", PeriodStart, PeriodEnd, Ct);
        var coordinator = scenario.Coordinator;
        var id = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));

        var workflow = WorkflowRegistry.Default.Get<SlotAssignmentState, SlotAssignment>();
        var asked = new[] { SlotAssignmentWorkflowAttribute.AskCoordinatorStep, SlotAssignmentWorkflowAttribute.AskCoordinatorAgainStep, SlotAssignmentWorkflowAttribute.AnswerStaffStep };
        var prompts = asked.Select(key => workflow.GetTransition(key).CommentPrompt!).ToList();
        Assert.All(prompts, prompt => Assert.Equal((CommentAudience.Staff, true), (prompt.Audience, prompt.Required)));
        Assert.Equal(
            ("Write a question to the coordinator", "The coordinator sees your question on the request.", "What do you want to ask about?"),
            (prompts[0].Title, prompts[0].Message, prompts[0].Placeholder));
        Assert.Equal("Write another question", prompts[1].Title);
        Assert.Equal("Answer the staff member", prompts[2].Title);
        Assert.DoesNotContain(prompts, prompt => $"{prompt.Title}{prompt.Message}{prompt.Placeholder}{prompt.RequiredMessage}".Contains(';', StringComparison.Ordinal));
        Assert.Equal("The request goes back to the staff member with your answer.", prompts[2].Message);

        foreach (var blank in new[] { null, "", "   " })
        {
            Assert.Contains("asks a comment", (await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, blank)).Error, StringComparison.Ordinal);
        }

        Assert.Equal(SlotAssignmentState.Requested, (await AssignmentScenario.StoredAsync(this.database, id, Ct))!.State);
        Assert.True((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "May I come later?")).IsSuccess);
        Assert.Contains("asks a comment", (await scenario.StepAsync(this.database, coordinator, id, SlotAssignmentWorkflowAttribute.AnswerStaffStep, Ct)).Error, StringComparison.Ordinal);
        Assert.Contains("asks a comment", (await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AskCoordinatorAgainStep, Ct, " ")).Error, StringComparison.Ordinal);
        Assert.Equal(["May I come later?"], await AssignmentScenario.StaffThreadAsync(this.database, id, Ct));
    }

    [Fact]
    public async Task The_words_are_written_in_the_steps_transaction_and_the_open_request_has_no_composer_until_assigned()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Question Thread", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Biology", PeriodStart, PeriodEnd, Ct);
        var coordinator = scenario.Coordinator;
        var id = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));

        Assert.True((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Do I bring tools?")).IsSuccess);
        await using (var context = this.database.CreateSystemContext())
        {
            var note = await context.Comments.AsNoTracking().SingleAsync(comment => comment.CommentContainer!.EntityId == id, Ct);
            Assert.Equal((CommentAudience.Staff, CommentKind.TransitionNote, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, "Do I bring tools?"), ((await context.CommentContainers.AsNoTracking().SingleAsync(container => container.EntityId == id, Ct)).Audience, note.Kind, note.TransitionKey, note.Text));
        }

        foreach (var reader in new[] { ada, coordinator })
        {
            await using var host = scenario.HostOf(this.database, reader);
            var conversations = host.Get<SlotAssignmentService>().Conversations;
            var thread = Assert.Single((await conversations.GetSlotAssignmentCommentsAsync(id, Ct)).Value!);
            Assert.Equal((CommentAudience.Staff, false, (string?)null), (thread.Audience, thread.CanPost, thread.ClosedReason));
            Assert.Equal(StaffConversationService.WrittenThroughSteps, (await conversations.AddSlotAssignmentCommentAsync(id, CommentAudience.Staff, "A side note.", Ct)).Error);
        }

        Assert.True((await scenario.StepAsync(this.database, coordinator, id, SlotAssignmentWorkflowAttribute.AnswerStaffStep, Ct, "Yes, bring them.")).IsSuccess);
        await using (var host = scenario.HostOf(this.database, ada))
        {
            Assert.Equal(StaffConversationService.WrittenThroughSteps, (await host.Get<SlotAssignmentService>().Conversations.AddSlotAssignmentCommentAsync(id, CommentAudience.Staff, "A side note.", Ct)).Error);
        }

        Assert.Equal(["Do I bring tools?", "Yes, bring them."], await AssignmentScenario.StaffThreadAsync(this.database, id, Ct));

        Assert.True((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, coordinator, id, SlotAssignmentWorkflowAttribute.PickStep, Ct)).IsSuccess);
        foreach (var (reader, text) in new[] { (ada, "See you Monday."), (coordinator, "Welcome.") })
        {
            await using var host = scenario.HostOf(this.database, reader);
            var conversations = host.Get<SlotAssignmentService>().Conversations;
            Assert.True(Assert.Single((await conversations.GetSlotAssignmentCommentsAsync(id, Ct)).Value!).CanPost);
            Assert.True((await conversations.AddSlotAssignmentCommentAsync(id, CommentAudience.Staff, text, Ct)).IsSuccess);
        }

        Assert.Equal(["Do I bring tools?", "Yes, bring them.", "See you Monday.", "Welcome."], await AssignmentScenario.StaffThreadAsync(this.database, id, Ct));
    }

    [Fact]
    public async Task Accept_and_decline_from_a_question_follow_the_requests_rules_a_first_to_accept_claim_included()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Question Answers", Ct);
        var (ada, bo, cy) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Geometry", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var coordinator = scenario.Coordinator;

        var adaId = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));
        Assert.True((await scenario.StepAsync(this.database, ada, adaId, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Mornings only?")).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, ada, adaId, SlotAssignmentWorkflowAttribute.AcceptQuestionStep, Ct)).IsSuccess);
        Assert.Equal(SlotAssignmentState.Accepted, (await AssignmentScenario.StoredAsync(this.database, adaId, Ct))!.State);
        Assert.Equal("Ada Example accepted Geometry", (await scenario.NoticesOfAsync(this.database, coordinator, Ct))[0].Subject);

        var boId = Made(await scenario.RequestAsync(this.database, coordinator, slots[1], bo, Ct));
        Assert.True((await scenario.StepAsync(this.database, bo, boId, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Is it paid travel?")).IsSuccess);
        Assert.Contains("asks a comment", (await scenario.StepAsync(this.database, bo, boId, SlotAssignmentWorkflowAttribute.DeclineQuestionStep, Ct)).Error, StringComparison.Ordinal);
        Assert.True((await scenario.StepAsync(this.database, bo, boId, SlotAssignmentWorkflowAttribute.DeclineQuestionStep, Ct, "Too far for me.")).IsSuccess);
        Assert.Equal(SlotAssignmentState.Declined, (await AssignmentScenario.StoredAsync(this.database, boId, Ct))!.State);
        Assert.Equal("Bo Example declined Geometry", (await scenario.NoticesOfAsync(this.database, coordinator, Ct))[0].Subject);
        Assert.Equal(["Is it paid travel?", "Too far for me."], await AssignmentScenario.StaffThreadAsync(this.database, boId, Ct));

        var roundId = await SendRoundAsync(scenario, slots[1], [await AssignmentScenario.ResourceOfAsync(this.database, cy, Ct)], firstToAccept: true);
        Guid cyId;
        await using (var context = this.database.CreateSystemContext())
        {
            cyId = await context.SlotAssignments.AsNoTracking().Where(assignment => assignment.RequestRoundId == roundId).Select(assignment => assignment.Id).SingleAsync(Ct);
        }

        Assert.True((await scenario.StepAsync(this.database, cy, cyId, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Which entrance?")).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, cy, cyId, SlotAssignmentWorkflowAttribute.AcceptQuestionStep, Ct)).IsSuccess);
        Assert.Equal(SlotAssignmentState.Assigned, (await AssignmentScenario.StoredAsync(this.database, cyId, Ct))!.State);
        Assert.Equal(SlotAssignmentWorkflowAttribute.ClaimStep, (await AssignmentScenario.HistoryAsync(this.database, cyId, Ct))[^1]);
    }

    [Fact]
    public async Task A_request_with_a_question_is_withdrawn_closed_by_another_holder_and_canceled_with_its_slot_or_round()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Question Closings", Ct);
        var (ada, bo, cy, dee) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct), await scenario.AddStaffAsync(this.database, "Dee", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "History", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant", "Helper"]);
        var coordinator = scenario.Coordinator;

        var adaId = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));
        Assert.True((await scenario.StepAsync(this.database, ada, adaId, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "How long?")).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, coordinator, adaId, SlotAssignmentWorkflowAttribute.CancelQuestionStep, Ct)).IsSuccess);
        Assert.Equal(SlotAssignmentState.Canceled, (await AssignmentScenario.StoredAsync(this.database, adaId, Ct))!.State);
        Assert.Equal("History is canceled for you", (await scenario.NoticesOfAsync(this.database, ada, Ct))[0].Subject);

        var boId = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], bo, Ct));
        Assert.True((await scenario.StepAsync(this.database, bo, boId, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Is it indoors?")).IsSuccess);
        _ = Made(await scenario.AssignAsync(this.database, coordinator, slots[0], ada, Ct));
        Assert.Equal(SlotAssignmentState.NotSelected, (await AssignmentScenario.StoredAsync(this.database, boId, Ct))!.State);
        Assert.Equal(SlotAssignmentWorkflowAttribute.CloseQuestionStep, (await AssignmentScenario.HistoryAsync(this.database, boId, Ct))[^1]);
        Assert.StartsWith("Another staff member was picked for History", (await scenario.NoticesOfAsync(this.database, bo, Ct))[0].Subject, StringComparison.Ordinal);

        var cyId = Made(await scenario.RequestAsync(this.database, coordinator, slots[1], cy, Ct));
        Assert.True((await scenario.StepAsync(this.database, cy, cyId, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Any dress code?")).IsSuccess);
        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            var canceled = await AssignmentScenario.StepSlotAsync(host.Get<JobSlotService>(), slots[1], JobSlotWorkflowAttribute.CancelStep, Ct);
            Assert.True(canceled.IsSuccess, canceled.Error);
        }

        Assert.Equal(SlotAssignmentState.Canceled, (await AssignmentScenario.StoredAsync(this.database, cyId, Ct))!.State);
        Assert.Equal(SlotAssignmentWorkflowAttribute.CancelQuestionStep, (await AssignmentScenario.HistoryAsync(this.database, cyId, Ct))[^1]);

        var roundId = await SendRoundAsync(scenario, slots[2], [await AssignmentScenario.ResourceOfAsync(this.database, dee, Ct)], firstToAccept: false);
        Guid deeId;
        await using (var context = this.database.CreateSystemContext())
        {
            deeId = await context.SlotAssignments.AsNoTracking().Where(assignment => assignment.RequestRoundId == roundId).Select(assignment => assignment.Id).SingleAsync(Ct);
        }

        Assert.True((await scenario.StepAsync(this.database, dee, deeId, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Who else is coming?")).IsSuccess);
        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            var rounds = host.Get<RequestRoundService>();
            var updater = (await rounds.LoadRequestRoundUpdaterAsync(roundId, Ct)).Value!;
            var closed = await rounds.UpdateRequestRoundAndTransitionAsync(updater, RequestRoundWorkflowAttribute.CloseStep, null, Ct);
            Assert.True(closed.IsSuccess, closed.Error);
        }

        Assert.Equal(SlotAssignmentState.Canceled, (await AssignmentScenario.StoredAsync(this.database, deeId, Ct))!.State);
        Assert.Equal(SlotAssignmentWorkflowAttribute.CancelQuestionStep, (await AssignmentScenario.HistoryAsync(this.database, deeId, Ct))[^1]);
    }

    [Fact]
    public async Task A_request_filled_by_others_leaves_the_staff_members_open_requests_from_both_states_by_a_pick_and_by_a_round()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Question Filled", Ct);
        var (ada, bo, cy, dee) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct), await scenario.AddStaffAsync(this.database, "Dee", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Geography", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var coordinator = scenario.Coordinator;

        var adaMain = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));
        var boMain = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], bo, Ct));
        Assert.True((await scenario.StepAsync(this.database, ada, adaMain, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Is it every Monday?")).IsSuccess);
        _ = Made(await scenario.AssignAsync(this.database, coordinator, slots[0], cy, Ct));

        var roundId = await SendRoundAsync(scenario, slots[1], [await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct), await AssignmentScenario.ResourceOfAsync(this.database, dee, Ct)], firstToAccept: true);
        Dictionary<Guid, Guid> requests;
        await using (var context = this.database.CreateSystemContext())
        {
            requests = await context.SlotAssignments.AsNoTracking().Where(assignment => assignment.RequestRoundId == roundId).Select(assignment => new { assignment.Resource.UserId, assignment.Id }).ToDictionaryAsync(row => row.UserId, row => row.Id, Ct);
        }

        Assert.True((await scenario.StepAsync(this.database, ada, requests[ada], SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Can I start at nine?")).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, dee, requests[dee], SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.Equal(SlotAssignmentState.Assigned, (await AssignmentScenario.StoredAsync(this.database, requests[dee], Ct))!.State);

        Assert.Equal(
            [SlotAssignmentState.NotSelected, SlotAssignmentState.NotSelected, SlotAssignmentState.NotSelected, SlotAssignmentState.NotSelected],
            await AssignmentScenario.StatesAsync(this.database, Ct, adaMain, boMain, requests[ada], requests[bo]));
        Assert.Equal(SlotAssignmentWorkflowAttribute.CloseQuestionStep, (await AssignmentScenario.HistoryAsync(this.database, adaMain, Ct))[^1]);
        Assert.Equal(SlotAssignmentWorkflowAttribute.CloseRequestStep, (await AssignmentScenario.HistoryAsync(this.database, boMain, Ct))[^1]);
        Assert.Equal(SlotAssignmentWorkflowAttribute.CloseQuestionStep, (await AssignmentScenario.HistoryAsync(this.database, requests[ada], Ct))[^1]);
        Assert.Equal(SlotAssignmentWorkflowAttribute.CloseRequestStep, (await AssignmentScenario.HistoryAsync(this.database, requests[bo], Ct))[^1]);

        foreach (var user in new[] { ada, bo })
        {
            await using var host = scenario.HostOf(this.database, user);
            var assignments = host.Get<SlotAssignmentService>();
            Assert.Empty((await assignments.QueryOwnAssignmentsAsync(new ListQuery(), Ct)).Value!.Rows);
            Assert.Empty((await assignments.QueryOwnAssignmentsAsync(new ListQuery().WithFilter(OwnAssignmentList.Status, "requested"), Ct)).Value!.Rows);
            Assert.Empty((await assignments.QueryOwnAssignmentsAsync(new ListQuery().WithFilter(OwnAssignmentList.Status, "staffquestion"), Ct)).Value!.Rows);
            var work = (await assignments.ReadOwnWorkAsync(Ct)).Value!;
            Assert.Equal((0, 0), (work.RequestCount, work.Requests.Count));
            Assert.All((await assignments.QueryOwnAssignmentsAsync(new ListQuery().WithToggle(OwnAssignmentList.ShowClosed, true), Ct)).Value!.Rows, row => Assert.Equal(SlotAssignmentState.NotSelected, row.State));
        }

        Assert.Equal(SlotAssignmentService.TakenBeforeYourYes, (await scenario.StepAsync(this.database, ada, requests[ada], SlotAssignmentWorkflowAttribute.AcceptQuestionStep, Ct)).Error);
    }

    [Fact]
    public async Task The_job_clock_closes_a_request_with_a_question_as_the_job_completes_telling_nobody()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Question Clock", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Music", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var coordinator = scenario.Coordinator;
        _ = Made(await scenario.AssignAsync(this.database, coordinator, slots[0], ada, Ct));
        var boId = Made(await scenario.RequestAsync(this.database, coordinator, slots[1], bo, Ct));
        Assert.True((await scenario.StepAsync(this.database, bo, boId, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Can I bring my own instrument?")).IsSuccess);
        await using (var confirming = scenario.HostOf(this.database, coordinator))
        {
            Assert.True((await AssignmentScenario.StepJobAsync(confirming.Get<JobService>(), job, JobWorkflowAttribute.ConfirmStep, Ct)).IsSuccess);
        }

        var before = (await scenario.NoticesOfAsync(this.database, bo, Ct)).Count;
        await using (var clock = ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(new FakeClock(DateTimeOffset.Parse("2027-06-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
        }))
        {
            await clock.Get<JobClock>().RunOnceAsync(Ct);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            Assert.Equal(JobState.Completed, (await system.Jobs.AsNoTracking().SingleAsync(row => row.Id == job, Ct)).State);
        }

        Assert.Equal(SlotAssignmentState.NotSelected, (await AssignmentScenario.StoredAsync(this.database, boId, Ct))!.State);
        Assert.Equal(SlotAssignmentWorkflowAttribute.CloseQuestionStep, (await AssignmentScenario.HistoryAsync(this.database, boId, Ct))[^1]);
        Assert.Equal(before, (await scenario.NoticesOfAsync(this.database, bo, Ct)).Count);

        Assert.False((await scenario.StepAsync(this.database, coordinator, boId, SlotAssignmentWorkflowAttribute.AnswerStaffStep, Ct, "Too late.")).IsSuccess);
        Assert.Equal(["Can I bring my own instrument?"], await AssignmentScenario.StaffThreadAsync(this.database, boId, Ct));
    }

    [Fact]
    public async Task The_staff_members_dialog_reads_their_request_in_words_and_what_the_slot_asks_for()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Question Dialog", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Drawing", PeriodStart, PeriodEnd, Ct);
        var coordinator = scenario.Coordinator;
        var id = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));

        await using (var staff = scenario.HostOf(this.database, ada))
        {
            var options = (await staff.Get<SlotAssignmentService>().GetSlotAssignmentEditorOptionsAsync(id, Ct)).Value!;
            Assert.Equal((true, "Your answer is needed", (string?)null), (options.ForStaff, options.Status, options.Service));
            Assert.StartsWith("Read what the job contains.", options.StatusHint, StringComparison.Ordinal);
            Assert.Empty(options.Qualifications);
        }

        Assert.True((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Pencils provided?")).IsSuccess);
        await using (var staff = scenario.HostOf(this.database, ada))
        {
            var options = (await staff.Get<SlotAssignmentService>().GetSlotAssignmentEditorOptionsAsync(id, Ct)).Value!;
            Assert.Equal("Your question waits for the coordinator", options.Status);
        }

        await using (var coordinating = scenario.HostOf(this.database, coordinator))
        {
            var options = (await coordinating.Get<SlotAssignmentService>().GetSlotAssignmentEditorOptionsAsync(id, Ct)).Value!;
            Assert.Equal((false, (string?)null, (string?)null), (options.ForStaff, options.Status, options.StatusHint));
        }
    }

    [Fact]
    public async Task A_coordinator_marks_a_question_handled_without_words_and_it_settles_for_every_coordinator_while_the_staff_member_is_told_nothing()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Question Handled", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var dana = await scenario.AddCoordinatorAsync(this.database, "Dana", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Poetry", PeriodStart, PeriodEnd, Ct);
        var casey = scenario.Coordinator;
        var id = Made(await scenario.RequestAsync(this.database, dana, slots[0], ada, Ct));
        Assert.True((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Where do I park?")).IsSuccess);
        foreach (var coordinator in new[] { casey, dana })
        {
            Assert.Equal(1, await AbsenceScenario.UnreadAboutAsync(this.database, coordinator, id, Ct));
            Assert.Contains($"question-{id}", await StaffQuestionKeysAsync(scenario, coordinator));
        }

        var step = WorkflowRegistry.Default.Get<SlotAssignmentState, SlotAssignment>().GetTransition(SlotAssignmentWorkflowAttribute.QuestionHandledStep);
        Assert.Equal(("Mark as handled", "done_all", (TransitionCommentPrompt?)null, (string?)null), (step.Label, step.Icon, step.CommentPrompt, step.ConfirmationMessage));
        Assert.False((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.QuestionHandledStep, Ct)).IsSuccess);
        Assert.Equal(SlotAssignmentService.NotFound, (await scenario.StepAsync(this.database, bo, id, SlotAssignmentWorkflowAttribute.QuestionHandledStep, Ct)).Error);
        Assert.Equal(SlotAssignmentState.StaffQuestion, (await AssignmentScenario.StoredAsync(this.database, id, Ct))!.State);

        var adaBefore = await scenario.NoticesOfAsync(this.database, ada, Ct);
        var bus = new NoticeBus(NullLogger<NoticeBus>.Instance);
        var told = new ConcurrentQueue<(Guid User, bool MarkedRead)>();
        using var caseyBell = bus.Subscribe(scenario.TenantId, casey, notice => Heard(told, casey, notice));
        using var danaBell = bus.Subscribe(scenario.TenantId, dana, notice => Heard(told, dana, notice));
        using var adaBell = bus.Subscribe(scenario.TenantId, ada, notice => Heard(told, ada, notice));
        await using (var handling = scenario.HostOf(this.database, casey, bus))
        {
            var handled = await AssignmentScenario.StepAsync(handling.Get<SlotAssignmentService>(), id, SlotAssignmentWorkflowAttribute.QuestionHandledStep, Ct);
            Assert.True(handled.IsSuccess, handled.Error);
        }

        Assert.Equal(SlotAssignmentState.Requested, (await AssignmentScenario.StoredAsync(this.database, id, Ct))!.State);
        Assert.Equal(SlotAssignmentWorkflowAttribute.QuestionHandledStep, (await AssignmentScenario.HistoryAsync(this.database, id, Ct))[^1]);
        Assert.Equal(casey, (await AbsenceScenario.LastStepByAsync(this.database, id, SlotAssignmentWorkflowAttribute.QuestionHandledStep, Ct)).ActorId);
        Assert.Equal(["Where do I park?"], await AssignmentScenario.StaffThreadAsync(this.database, id, Ct));
        Assert.Equal(adaBefore, await scenario.NoticesOfAsync(this.database, ada, Ct));
        foreach (var coordinator in new[] { casey, dana })
        {
            Assert.Equal(0, await AbsenceScenario.UnreadAboutAsync(this.database, coordinator, id, Ct));
            Assert.DoesNotContain($"question-{id}", await StaffQuestionKeysAsync(scenario, coordinator));
        }

        Assert.Equal([(casey, true), (dana, true)], told.OrderBy(heard => heard.User == dana));
        await using (var context = this.database.CreateSystemContext())
        {
            Assert.All(await context.MessageRecipients.AsNoTracking().Where(copy => copy.StoredMessage!.EntityId == id && copy.StoredMessage.MessageType == MessageTypes.AssignmentQuestionFromStaff.Key).ToListAsync(Ct), copy => Assert.NotNull(copy.ReadAt));
        }

        Assert.True((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "And lunch?")).IsSuccess);
        Assert.Equal(1, await AbsenceScenario.UnreadAboutAsync(this.database, dana, id, Ct));
        Assert.True((await scenario.StepAsync(this.database, casey, id, SlotAssignmentWorkflowAttribute.AnswerStaffStep, Ct, "Bring your own.")).IsSuccess);
        foreach (var coordinator in new[] { casey, dana })
        {
            Assert.Equal(0, await AbsenceScenario.UnreadAboutAsync(this.database, coordinator, id, Ct));
        }

        Assert.Equal(1, await AbsenceScenario.UnreadAboutAsync(this.database, ada, id, Ct, MessageTypes.AssignmentAnswerToStaff));
        Assert.True((await scenario.StepAsync(this.database, ada, id, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
    }

    private static Task Heard(ConcurrentQueue<(Guid User, bool MarkedRead)> told, Guid user, NoticeArrived notice)
    {
        told.Enqueue((user, notice.MarkedRead));
        return Task.CompletedTask;
    }

    private static async Task<List<string>> OfferedAsync(ServiceHost host, Guid id)
    {
        var answers = await host.Get<Staffora.Services.Workflow.WorkflowService>().GetAvailableTransitionsAsync<SlotAssignmentState, SlotAssignment>(id, SlotAssignmentWorkflowQueries.WithWorkflowGraph, TestContext.Current.CancellationToken);
        Assert.True(answers.IsSuccess, answers.Error);
        return [.. answers.Value!
            .Where(answer => answer.CanRun && !answer.Transition.IsSystemDriven)
            .OrderByDescending(answer => answer.Transition.Placement)
            .ThenBy(answer => answer.Transition.SortOrder)
            .Select(answer => answer.Transition.Key)];
    }

    private Task<List<string>> StaffQuestionKeysAsync(AssignmentScenario scenario) => StaffQuestionKeysAsync(scenario, scenario.Coordinator);

    private async Task<List<string>> StaffQuestionKeysAsync(AssignmentScenario scenario, Guid reader)
    {
        await using var host = scenario.HostOf(this.database, reader);
        var workspace = host.Get<CoordinationService>();
        var snapshot = await workspace.ReadAsync(Ct);
        Assert.True(snapshot.IsSuccess, snapshot.Error);
        var dock = await workspace.ReadPrioritiesAsync(snapshot.Value!, new PriorityQuery(), Ct);
        Assert.True(dock.IsSuccess, dock.Error);
        return [.. dock.Value!.Items.Where(item => item.Kind == PriorityItemKind.StaffQuestions).Select(item => item.Key)];
    }

    private async Task<Guid> SendRoundAsync(AssignmentScenario scenario, Guid slot, IReadOnlyList<Guid> resources, bool firstToAccept)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var sent = await host.Get<RequestRoundService>().SendRoundAsync(slot, resources, firstToAccept, Ct);
        Assert.True(sent.IsSuccess, sent.Error);
        return sent.Value;
    }

    private static Guid Made(Staffora.Core.Models.Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }
}
