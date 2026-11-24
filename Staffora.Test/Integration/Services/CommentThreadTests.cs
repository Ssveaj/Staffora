using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Tenancy;
using Staffora.Data;
using Staffora.Services.Comments;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class CommentThreadTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public CommentThreadTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_prompts_comment_commits_with_its_transition_into_the_entitys_thread()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, coordinator) = await ScenarioWithCoordinatorAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(coordinator));
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-cancel", TestParentWorkflowQueries.WithWorkflowGraph, "  The period was called off.  ", cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(TestParentState.Canceled, (await LoadAsync(id, cancellationToken)).State);
        var container = Assert.Single(await ContainersOfAsync(id, cancellationToken));
        Assert.Equal((scenario.A.TenantId, nameof(TestParent), CommentAudience.Internal), (container.TenantId, container.EntityType, container.Audience));
        var note = Assert.Single(await CommentsOfAsync(id, cancellationToken));
        Assert.Equal(
            (container.Id, scenario.A.TenantId, CommentKind.TransitionNote, CommentAuthorType.User, coordinator, "The period was called off.", "test-parent-cancel"),
            (note.CommentContainerId, note.TenantId, note.Kind, note.AuthorType, note.CreatedById, note.Text, note.TransitionKey));

        var history = Assert.Single(await HistoryOfAsync(id, cancellationToken));
        Assert.Equal(history.Created, note.Created);

        var threads = await host.Get<CommentService>().GetThreadsAsync<TestParent>(id, TestParentDialogService.CommentAccess, cancellationToken);
        Assert.True(threads.IsSuccess, threads.Error);
        var entry = Assert.Single(Assert.Single(threads.Value!).Comments);
        Assert.Equal((CommentKind.TransitionNote, true, "Coordinator"), (entry.Kind, entry.IsOwn, entry.Author.Split(' ')[0]));
    }

    [Fact]
    public async Task A_prompts_comment_rolls_back_with_its_transition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, coordinator) = await ScenarioWithCoordinatorAsync(cancellationToken);

        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken, notes: TestParent.FailAtTheDatabase);

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(coordinator));
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-cancel", TestParentWorkflowQueries.WithWorkflowGraph, "The period was called off.", cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("could not be completed, so nothing was changed", result.Error, StringComparison.Ordinal);
        Assert.Equal((TestParentState.Active, "Parent"), ((await LoadAsync(id, cancellationToken)).State, (await LoadAsync(id, cancellationToken)).Name));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
        Assert.Empty(await ContainersOfAsync(id, cancellationToken));
        Assert.Empty(await CommentsOfAsync(id, cancellationToken));

        await using (var system = this.database.CreateSystemContext())
        {
            var parent = await system.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            parent.Notes = null;
            system.NameEditor(parent, parent.CreatedById);
            await system.SaveChangesAsync(cancellationToken);
        }

        var again = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-cancel", TestParentWorkflowQueries.WithWorkflowGraph, "The period was called off.", cancellationToken);
        Assert.True(again.IsSuccess, again.Error);
        Assert.Single(await CommentsOfAsync(id, cancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too long")]
    public async Task A_step_that_asks_a_comment_is_refused_on_the_server_without_one(string? comment)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, coordinator) = await ScenarioWithCoordinatorAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);
        var text = comment == "too long" ? new string('x', Comment.MaxTextLength + 1) : comment;

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(coordinator));
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-cancel", TestParentWorkflowQueries.WithWorkflowGraph, text, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            comment == "too long"
                ? $"The comment for the step 'Canceled' is longer than {Comment.MaxTextLength} characters, so nothing was changed."
                : "The step 'Canceled' asks a comment (Cancel the parent), and none was given, so nothing was changed.",
            result.Error);
        Assert.Equal(TestParentState.Active, (await LoadAsync(id, cancellationToken)).State);
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
        Assert.Empty(await CommentsOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_dialogs_edits_with_a_step_that_asks_a_comment_are_refused_without_one_and_roll_back()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.ViewPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, notes: TestParent.AsksForAComment);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddScoped<TestParentDialogService>());
        var service = host.Get<TestParentDialogService>();
        var updater = (await service.LoadTestParentUpdaterAsync(id, cancellationToken)).Value!;
        updater.Entity.Name = "Renamed";

        var refused = await service.UpdateTestParentAndTransitionAsync(updater, "test-parent-annotate", comment: null, cancellationToken);
        Assert.Equal((false, "The step 'Annotate' asks a comment (Annotate the parent), and none was given, so nothing was changed."), (refused.IsSuccess, refused.Error));
        Assert.Equal("Parent", (await LoadAsync(id, cancellationToken)).Name);

        var annotated = await service.UpdateTestParentAndTransitionAsync(updater, "test-parent-annotate", "Renamed for the spring period.", cancellationToken);
        Assert.True(annotated.IsSuccess, annotated.Error);
        Assert.Equal("Renamed", (await LoadAsync(id, cancellationToken)).Name);
        Assert.Equal("Renamed for the spring period.", Assert.Single(await CommentsOfAsync(id, cancellationToken)).Text);
    }

    [Fact]
    public async Task A_thread_is_created_with_its_first_comment_and_holds_every_comment_after_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.InternalThreadPermission);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, colleague, cancellationToken, TestParentDialogService.InternalThreadPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var internalThread = TestParentDialogService.CommentAccess[0];

        await using var owner = ServiceHost.Create(this.database, scenario.A.Runtime());
        var empty = await owner.Get<CommentService>().GetThreadsAsync<TestParent>(id, [internalThread], cancellationToken);
        Assert.Equal((0, 0), (Assert.Single(empty.Value!).TotalCount, (await ContainersOfAsync(id, cancellationToken)).Count));

        var first = await owner.Get<CommentService>().AddCommentAsync<TestParent>(id, internalThread, "  Who takes the morning group?  ", cancellationToken);
        Assert.True(first.IsSuccess, first.Error);
        await using var other = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        var second = await other.Get<CommentService>().AddCommentAsync<TestParent>(id, internalThread, "I do.", cancellationToken);
        Assert.True(second.IsSuccess, second.Error);

        Assert.Single(await ContainersOfAsync(id, cancellationToken));
        var read = Assert.Single((await owner.Get<CommentService>().GetThreadsAsync<TestParent>(id, [internalThread], cancellationToken)).Value!);
        Assert.Equal(
            [("Who takes the morning group?", true, CommentKind.Comment), ("I do.", false, CommentKind.Comment)],
            read.Comments.Select(comment => (comment.Text, comment.IsOwn, comment.Kind)));
        Assert.Equal((2, true, true), (read.TotalCount, read.CanPost, read.AwaitsReply));
        Assert.StartsWith("Colleague", read.Comments[1].Author, StringComparison.Ordinal);

        var stored = (await CommentsOfAsync(id, cancellationToken)).OrderBy(comment => comment.Created).First();
        Assert.Equal(stored.Created, read.Comments[0].At.UtcDateTime);
        Assert.Equal(ServiceHost.PlatformTimeZone, read.TimeZoneId);

        var theirs = Assert.Single((await other.Get<CommentService>().GetThreadsAsync<TestParent>(id, [internalThread], cancellationToken)).Value!);
        Assert.False(theirs.AwaitsReply);
    }

    private const int Races = 12;

    [Fact]
    public async Task Two_first_comments_at_once_create_one_thread_and_both_are_stored()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.InternalThreadPermission);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, colleague, cancellationToken, TestParentDialogService.InternalThreadPermission);
        var internalThread = TestParentDialogService.CommentAccess[0];

        await using var owner = ServiceHost.Create(this.database, scenario.A.Runtime());
        await using var other = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        for (var race = 0; race < Races; race++)
        {
            var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

            var results = await Task.WhenAll(
                Task.Run(() => owner.Get<CommentService>().AddCommentAsync<TestParent>(id, internalThread, $"First {race}", cancellationToken), cancellationToken),
                Task.Run(() => other.Get<CommentService>().AddCommentAsync<TestParent>(id, internalThread, $"Second {race}", cancellationToken), cancellationToken));

            Assert.All(results, result => Assert.True(result.IsSuccess, result.Error));
            var container = Assert.Single(await ContainersOfAsync(id, cancellationToken));
            Assert.Equal(
                [$"First {race}", $"Second {race}"],
                (await CommentsOfAsync(id, cancellationToken)).Where(comment => comment.CommentContainerId == container.Id).Select(comment => comment.Text).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task A_first_comment_and_a_steps_note_at_once_create_one_thread_and_the_step_goes_through()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, coordinator) = await ScenarioWithCoordinatorAsync(cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.InternalThreadPermission);
        var internalThread = TestParentDialogService.CommentAccess[0];

        await using var owner = ServiceHost.Create(this.database, scenario.A.Runtime());
        await using var stepper = ServiceHost.Create(this.database, scenario.A.RuntimeOf(coordinator));
        for (var race = 0; race < Races; race++)
        {
            var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);

            var delay = TimeSpan.FromMilliseconds(race * 3);
            var post = Task.Run(
                async () =>
                {
                    await Task.Delay(delay, cancellationToken);
                    return await owner.Get<CommentService>().AddCommentAsync<TestParent>(id, internalThread, $"Posted {race}", cancellationToken);
                },
                cancellationToken);
            var step = Task.Run(() => stepper.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-cancel", TestParentWorkflowQueries.WithWorkflowGraph, $"Noted {race}", cancellationToken), cancellationToken);
            await Task.WhenAll(post, step);

            var (posted, stepped) = (await post, await step);
            Assert.True(posted.IsSuccess, posted.Error);
            Assert.True(stepped.IsSuccess, stepped.Error);
            Assert.Equal(TestParentState.Canceled, (await LoadAsync(id, cancellationToken)).State);
            var container = Assert.Single(await ContainersOfAsync(id, cancellationToken));
            Assert.Equal(
                [(CommentKind.TransitionNote, $"Noted {race}"), (CommentKind.Comment, $"Posted {race}")],
                (await CommentsOfAsync(id, cancellationToken)).Where(comment => comment.CommentContainerId == container.Id).Select(comment => (comment.Kind, comment.Text)).OrderBy(comment => comment.Text, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task A_first_comment_and_a_delete_at_once_never_leave_a_thread_without_its_record()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, colleague, cancellationToken, TestParentDialogService.InternalThreadPermission);
        var internalThread = TestParentDialogService.CommentAccess[0];

        await using var poster = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        await using var deleter = ServiceHost.Create(this.database, scenario.A.Runtime());
        var outcomes = new HashSet<string>(StringComparer.Ordinal);
        for (var race = 0; race < Races; race++)
        {
            var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

            var post = Task.Run(() => poster.Get<CommentService>().AddCommentAsync<TestParent>(id, internalThread, $"Keep {race}", cancellationToken), cancellationToken);
            var delete = Task.Run(() => deleter.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-delete", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken), cancellationToken);
            var (posted, deleted) = (await post, await delete);

            await using var system = this.database.CreateSystemContext();
            var kept = await system.TestParents.AnyAsync(row => row.Id == id, cancellationToken);
            var comments = await CommentsOfAsync(id, cancellationToken);
            if (posted.IsSuccess)
            {
                Assert.Equal((false, CommentThreadsBlockDelete.Reason, true, 1), (deleted.IsSuccess, deleted.Error, kept, comments.Count));
                outcomes.Add("posted");
            }
            else
            {
                Assert.Equal(("The TestParent was not found.", true, false), (posted.Error, deleted.IsSuccess, kept));
                Assert.Empty(comments);
                Assert.Empty(await ContainersOfAsync(id, cancellationToken));
                outcomes.Add("deleted");
            }
        }

        Assert.NotEmpty(outcomes);
    }

    [Fact]
    public async Task A_comment_is_written_only_inside_a_transaction_which_holds_the_records_thread_lock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using var context = this.database.CreateContext(scenario.A.Runtime());
        var parent = await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CommentThreadWriter.AddAsync(context, parent, CommentAudience.Internal, CommentKind.Comment, CommentAuthorType.User, "Outside a transaction.", null, cancellationToken));
        Assert.Empty(context.ChangeTracker.Entries<Comment>());
    }

    [Fact]
    public async Task Each_thread_needs_the_permissions_its_entitys_service_passes_and_audiences_stay_apart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var reader = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Reader", cancellationToken);
        var stranger = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Stranger", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.InternalThreadPermission, TestParentDialogService.StaffThreadPermission, TestParentDialogService.StaffPostPermission);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, reader, cancellationToken, TestParentDialogService.InternalThreadPermission, TestParentDialogService.StaffThreadPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var (internalThread, staffThread) = (TestParentDialogService.CommentAccess[0], TestParentDialogService.CommentAccess[1]);

        await using var owner = ServiceHost.Create(this.database, scenario.A.Runtime());
        Assert.True((await owner.Get<CommentService>().AddCommentAsync<TestParent>(id, internalThread, "Staff only.", cancellationToken)).IsSuccess);
        Assert.True((await owner.Get<CommentService>().AddCommentAsync<TestParent>(id, staffThread, "Can you take Monday?", cancellationToken)).IsSuccess);

        var both = (await owner.Get<CommentService>().GetThreadsAsync<TestParent>(id, TestParentDialogService.CommentAccess, cancellationToken)).Value!;
        Assert.Equal(
            [(CommentAudience.Internal, "Staff only.", true), (CommentAudience.Staff, "Can you take Monday?", true)],
            both.Select(thread => (thread.Audience, Assert.Single(thread.Comments).Text, thread.CanPost)));

        await using var readerHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(reader));
        var asReader = (await readerHost.Get<CommentService>().GetThreadsAsync<TestParent>(id, TestParentDialogService.CommentAccess, cancellationToken)).Value!;
        Assert.Equal([(CommentAudience.Internal, true), (CommentAudience.Staff, false)], asReader.Select(thread => (thread.Audience, thread.CanPost)));
        var refusedPost = await readerHost.Get<CommentService>().AddCommentAsync<TestParent>(id, staffThread, "Yes.", cancellationToken);
        Assert.Equal((false, CommentService.NoAccess), (refusedPost.IsSuccess, refusedPost.Error));

        await using var strangerHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(stranger));
        Assert.Empty((await strangerHost.Get<CommentService>().GetThreadsAsync<TestParent>(id, TestParentDialogService.CommentAccess, cancellationToken)).Value!);
        var strangersPost = await strangerHost.Get<CommentService>().AddCommentAsync<TestParent>(id, internalThread, "Hello.", cancellationToken);
        Assert.Equal((false, CommentService.NoAccess), (strangersPost.IsSuccess, strangersPost.Error));
        Assert.Equal(2, (await CommentsOfAsync(id, cancellationToken)).Count);
    }

    [Fact]
    public async Task Another_tenants_record_reads_as_missing_and_takes_no_comment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.InternalThreadPermission);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, TestParentDialogService.InternalThreadPermission);
        var theirs = await AddParentAsync(scenario.B, TestParentState.Draft, cancellationToken);
        var internalThread = TestParentDialogService.CommentAccess[0];
        await using (var b = ServiceHost.Create(this.database, scenario.B.Runtime()))
        {
            Assert.True((await b.Get<CommentService>().AddCommentAsync<TestParent>(theirs, internalThread, "Company B's own note.", cancellationToken)).IsSuccess);
        }

        await using var a = ServiceHost.Create(this.database, scenario.A.Runtime());
        var missing = Guid.NewGuid();
        foreach (var id in new[] { theirs, missing })
        {
            var read = await a.Get<CommentService>().GetThreadsAsync<TestParent>(id, [internalThread], cancellationToken);
            Assert.Equal((false, "The TestParent was not found."), (read.IsSuccess, read.Error));
            var post = await a.Get<CommentService>().AddCommentAsync<TestParent>(id, internalThread, "From A.", cancellationToken);
            Assert.Equal((false, "The TestParent was not found."), (post.IsSuccess, post.Error));
        }

        Assert.Equal(["Company B's own note."], (await CommentsOfAsync(theirs, cancellationToken)).Select(comment => comment.Text));
        Assert.Empty(await CommentsOfAsync(missing, cancellationToken));
    }

    [Fact]
    public async Task A_platform_owner_reads_and_posts_only_while_impersonating_and_then_as_the_user_they_act_as()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.InternalThreadPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var internalThread = TestParentDialogService.CommentAccess[0];
        await using (var a = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            Assert.True((await a.Get<CommentService>().AddCommentAsync<TestParent>(id, internalThread, "Company A's own note.", cancellationToken)).IsSuccess);
        }

        await using (var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var read = await console.Get<CommentService>().GetThreadsAsync<TestParent>(id, [internalThread], cancellationToken);
            Assert.False(read.IsSuccess && read.Value!.Count > 0, "A platform owner on the console read a tenant's thread.");
            var post = await console.Get<CommentService>().AddCommentAsync<TestParent>(id, internalThread, "From the console.", cancellationToken);
            Assert.False(post.IsSuccess);
        }

        Assert.Equal(["Company A's own note."], (await CommentsOfAsync(id, cancellationToken)).Select(comment => comment.Text));

        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);
        await using (var acting = ServiceHost.Create(this.database, impersonating))
        {
            var posted = await acting.Get<CommentService>().AddCommentAsync<TestParent>(id, internalThread, "Checked for the owner.", cancellationToken);
            Assert.True(posted.IsSuccess, posted.Error);
            var last = posted.Value!.Comments[^1];
            Assert.Equal(("Checked for the owner.", true), (last.Text, last.IsOwn));
            Assert.DoesNotContain(posted.Value.Comments, comment => comment.Author == CommentService.OutsideAuthor);
        }

        var stored = (await CommentsOfAsync(id, cancellationToken)).Single(comment => comment.Text == "Checked for the owner.");
        Assert.Equal((scenario.A.TenantId, scenario.A.OwnerId, (Guid?)platform.OwnerId), (stored.TenantId, stored.CreatedById, stored.ImpersonatedById));
    }

    [Fact]
    public async Task An_audience_the_entitys_service_declares_no_thread_for_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.InternalThreadPermission, TestParentDialogService.StaffThreadPermission, TestParentDialogService.StaffPostPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddScoped<TestParentDialogService>());
        var refused = await host.Get<TestParentDialogService>().AddTestParentCommentAsync(id, CommentAudience.Customer, "To the customer.", cancellationToken);
        Assert.Equal((false, CommentService.NoAccess), (refused.IsSuccess, refused.Error));
        Assert.Empty(await ContainersOfAsync(id, cancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("too long")]
    public async Task A_blank_or_too_long_comment_is_refused_and_stores_nothing(string text)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.InternalThreadPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var posted = await host.Get<CommentService>().AddCommentAsync<TestParent>(id, TestParentDialogService.CommentAccess[0], text == "too long" ? new string('x', Comment.MaxTextLength + 1) : text, cancellationToken);

        Assert.Equal((false, text == "too long" ? $"A comment holds at most {Comment.MaxTextLength} characters." : "Write a comment first."), (posted.IsSuccess, posted.Error));
        Assert.Empty(await ContainersOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_comment_and_its_thread_are_never_edited_or_deleted_outside_system_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.InternalThreadPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            Assert.True((await host.Get<CommentService>().AddCommentAsync<TestParent>(id, TestParentDialogService.CommentAccess[0], "As written.", cancellationToken)).IsSuccess);
        }

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var comment = await context.Comments.SingleAsync(row => row.CommentContainer!.EntityId == id, cancellationToken);
            comment.Text = "Rewritten.";
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
        }

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.Comments.Remove(await context.Comments.SingleAsync(row => row.CommentContainer!.EntityId == id, cancellationToken));
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
        }

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var thread = await context.CommentContainers.SingleAsync(row => row.EntityId == id, cancellationToken);
            thread.Audience = CommentAudience.Customer;
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
        }

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.Comments.Where(row => row.CommentContainer!.EntityId == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Text, "Bulk."), cancellationToken));
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.CommentContainers.Where(row => row.EntityId == id).ExecuteDeleteAsync(cancellationToken));
        }

        var stored = Assert.Single(await CommentsOfAsync(id, cancellationToken));
        Assert.Equal("As written.", stored.Text);
        Assert.Equal(CommentAudience.Internal, Assert.Single(await ContainersOfAsync(id, cancellationToken)).Audience);
    }

    [Fact]
    public async Task A_record_whose_thread_holds_a_comment_is_not_deleted_and_says_why()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.InternalThreadPermission);
        var commented = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        var quiet = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        Assert.True((await host.Get<CommentService>().AddCommentAsync<TestParent>(commented, TestParentDialogService.CommentAccess[0], "Keep this.", cancellationToken)).IsSuccess);
        var workflows = host.Get<WorkflowService>();

        var offered = Assert.Single((await workflows.GetAvailableTransitionsAsync<TestParentState, TestParent>(commented, TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken)).Value!, answer => answer.Transition.Key == "test-parent-delete");
        Assert.Equal([CommentThreadsBlockDelete.Reason], offered.BlockingReasons);
        var refused = await workflows.TransitionEntityAsync<TestParentState, TestParent>(commented, "test-parent-delete", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.Equal((false, CommentThreadsBlockDelete.Reason), (refused.IsSuccess, refused.Error));
        Assert.Equal((TestParentState.Draft, false), ((await LoadAsync(commented, cancellationToken)).State, (await LoadAsync(commented, cancellationToken)).IsInTransition));
        Assert.Single(await CommentsOfAsync(commented, cancellationToken));

        var deleted = await workflows.TransitionEntityAsync<TestParentState, TestParent>(quiet, "test-parent-delete", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.True(deleted.IsSuccess, deleted.Error);
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.TestParents.AnyAsync(row => row.Id == quiet, cancellationToken));
    }

    [Fact]
    public async Task A_platform_owners_step_on_the_console_writes_its_note_on_the_tenants_behalf()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, cancellationToken);

        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-cancel", TestParentWorkflowQueries.WithWorkflowGraph, "Closed by the platform.", cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        var note = Assert.Single(await CommentsOfAsync(id, cancellationToken));
        var container = Assert.Single(await ContainersOfAsync(id, cancellationToken));
        Assert.Equal((scenario.A.TenantId, scenario.A.SystemAccountId), (note.TenantId, note.CreatedById));
        Assert.Equal((scenario.A.TenantId, scenario.A.SystemAccountId), (container.TenantId, container.CreatedById));
    }

    private async Task<(TwoTenantScenario Scenario, Guid Coordinator)> ScenarioWithCoordinatorAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var coordinator = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Coordinator", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, coordinator, cancellationToken, RolePermission.SettingsEdit, TestParentDialogService.InternalThreadPermission);
        return (scenario, coordinator);
    }

    private async Task<Guid> AddParentAsync(ScenarioTenant tenant, TestParentState state, CancellationToken cancellationToken, string? notes = null)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        using var lease = writer.BeginSystemScope("test: insert a parent as a transition left it");
        var parent = new TestParent { Name = "Parent", Notes = notes, State = state };
        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task<TestParent> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestParents.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken);
    }

    private async Task<List<Staffora.Core.Entities.Workflow.StateHistoryEntry>> HistoryOfAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read a parent's history");
        return await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == id).ToListAsync(cancellationToken);
    }

    private async Task<List<CommentContainer>> ContainersOfAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read a record's threads");
        return await system.CommentContainers.AsNoTracking().Where(container => container.EntityId == id).ToListAsync(cancellationToken);
    }

    private async Task<List<Comment>> CommentsOfAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read a record's comments");
        return await system.Comments.AsNoTracking().Where(comment => comment.CommentContainer!.EntityId == id).ToListAsync(cancellationToken);
    }
}
