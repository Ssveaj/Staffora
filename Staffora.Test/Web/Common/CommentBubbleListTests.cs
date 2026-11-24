using Bunit;
using Staffora.Blazor.Components.Common.Comments;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Models;
using Staffora.Services.Comments;

namespace Staffora.Test.Web.Common;

public class CommentBubbleListTests : WebTestContext
{
    private static readonly DateTimeOffset at = new(2026, 11, 8, 14, 5, 0, TimeSpan.FromHours(1));

    private static readonly CommentEntry[] comments =
    [
        new(Guid.NewGuid(), CommentKind.Comment, CommentAuthorType.User, "Alex Morgan", IsOwn: false, at, "Who takes the morning group?"),
        new(Guid.NewGuid(), CommentKind.Comment, CommentAuthorType.User, "Sam Lee", IsOwn: true, at.AddMinutes(4), "I do."),
        new(Guid.NewGuid(), CommentKind.TransitionNote, CommentAuthorType.User, "Alex Morgan", IsOwn: false, at.AddMinutes(9), "The period was called off."),
    ];

    [Fact]
    public void Comments_are_an_ordered_list_with_author_time_and_text_the_readers_own_marked_in_words()
    {
        var block = Render<CommentBubbleList>(parameters => parameters
            .Add(component => component.Comments, comments)
            .Add(component => component.TotalCount, 3)
            .Add(component => component.Label, "Internal comments")
            .Add(component => component.InputName, "comment-internal"));

        var list = block.Find("ol.comment-bubbles__list");
        Assert.Equal("Internal comments", list.GetAttribute("aria-label"));
        var items = list.QuerySelectorAll("li");
        Assert.Equal(3, items.Length);
        Assert.Equal(["Alex Morgan", "Sam Lee", "Alex Morgan"], items.Select(item => item.QuerySelector(".comment-bubble__author")!.TextContent));
        Assert.Equal(["Who takes the morning group?", "I do.", "The period was called off."], items.Select(item => item.QuerySelector(".comment-bubble__text")!.TextContent));

        var time = items[0].QuerySelector("time")!;
        Assert.Equal(("14:05", "2026-11-08T14:05:00.0000000+01:00", "2026-11-08 14:05"), (time.TextContent, time.GetAttribute("datetime"), time.GetAttribute("title")));
        Assert.Equal(["8 November 2026"], list.QuerySelectorAll(".comment-bubbles__divider--day").Select(day => day.TextContent));
        Assert.Equal("conversation", block.Find(".comment-bubbles").GetAttribute("data-surface"));

        Assert.Contains("comment-bubble--own", items[1].QuerySelector("article")!.ClassName, StringComparison.Ordinal);
        Assert.Contains("comment-bubble--other", items[0].QuerySelector("article")!.ClassName, StringComparison.Ordinal);
        Assert.Equal(" (you)", items[1].QuerySelector(".visually-hidden")!.TextContent);
        Assert.Null(items[0].QuerySelector(".visually-hidden"));
        Assert.Null(items[2].QuerySelector(".comment-bubble__step-name"));
        Assert.Equal("transitionnote", items[2].GetAttribute("data-kind"));

        Assert.Empty(block.FindAll("textarea"));
    }

    [Fact]
    public void An_empty_thread_says_so_and_a_cut_thread_says_how_much_it_shows()
    {
        var empty = Render<CommentBubbleList>(parameters => parameters
            .Add(component => component.Comments, Array.Empty<CommentEntry>())
            .Add(component => component.Label, "Internal comments")
            .Add(component => component.InputName, "comment-internal"));
        Assert.Equal("No comments yet.", empty.Find(".comment-bubbles__empty").TextContent);
        Assert.Empty(empty.FindAll("ol"));

        var cut = Render<CommentBubbleList>(parameters => parameters
            .Add(component => component.Comments, comments)
            .Add(component => component.TotalCount, 250)
            .Add(component => component.Label, "Internal comments")
            .Add(component => component.InputName, "comment-internal"));
        Assert.Equal("Showing the newest 3 of 250 comments.", cut.Find(".comment-bubbles__more").TextContent);
    }

    [Fact]
    public async Task The_composer_is_named_posts_keeps_the_text_on_a_refusal_and_empties_it_once_posted()
    {
        var posted = new List<string>();
        var refuse = true;
        var block = Render<CommentBubbleList>(parameters => parameters
            .Add(component => component.Comments, comments)
            .Add(component => component.Label, "Internal comments")
            .Add(component => component.InputName, "comment-internal")
            .Add(component => component.CanPost, true)
            .Add(component => component.OnPost, text =>
            {
                posted.Add(text);
                return Task.FromResult(refuse ? Result.Failure("You do not have permission to read or write these comments.") : Result.Success(message: "Comment posted."));
            }));

        var input = block.Find("textarea[name=comment-internal]");
        Assert.Equal("Write a comment", block.Find($"label[for={input.Id}]").TextContent.Trim());

        await block.Find("button[data-post-comment=comment-internal]").ClickAsync(new());
        Assert.Equal("Write a comment first.", block.Find(".comment-bubbles__error[role=alert]").TextContent);
        Assert.Empty(posted);

        block.Find("textarea").Input("Can I swap Monday?");
        await block.Find("button[data-post-comment=comment-internal]").ClickAsync(new());
        Assert.Equal(["Can I swap Monday?"], posted);
        Assert.Equal("You do not have permission to read or write these comments.", block.Find(".comment-bubbles__error[role=alert]").TextContent);
        Assert.Equal("Can I swap Monday?", block.Find("textarea").GetAttribute("value") ?? block.Find("textarea").TextContent);

        refuse = false;
        await block.Find("button[data-post-comment=comment-internal]").ClickAsync(new());
        Assert.Equal(2, posted.Count);
        Assert.Empty(block.FindAll(".comment-bubbles__error"));
        Assert.True(string.IsNullOrEmpty(block.Find("textarea").GetAttribute("value")));
        Assert.Equal("Comment posted.", block.Find("[role=status]").TextContent);
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Notes_are_cards_with_their_day_and_time_and_no_day_lines()
    {
        var block = Render<CommentBubbleList>(parameters => parameters
            .Add(component => component.Comments, comments)
            .Add(component => component.Label, "Internal comments")
            .Add(component => component.InputName, "comment-internal")
            .Add(component => component.AsNotes, true));

        Assert.Equal("notes", block.Find(".comment-bubbles").GetAttribute("data-surface"));
        Assert.Empty(block.FindAll(".comment-bubbles__divider--day"));
        Assert.Equal("8 Nov 2026 14:05", block.Find("li time").TextContent);
    }

    [Fact]
    public void The_first_comment_the_reader_has_not_seen_has_a_line_before_it()
    {
        var block = Render<CommentBubbleList>(parameters => parameters
            .Add(component => component.Comments, comments)
            .Add(component => component.Label, "Staff comments")
            .Add(component => component.InputName, "comment-staff")
            .Add(component => component.MarkedCommentId, comments[2].Id)
            .Add(component => component.MarkerLabel, "New"));

        var marker = block.Find("li[data-comment] [data-marker]");
        Assert.Equal("New", marker.TextContent);
        Assert.Equal(comments[2].Id.ToString(), marker.Closest("li")!.GetAttribute("data-comment"));
        Assert.Single(block.FindAll("[data-marker]"));
    }

    [Fact]
    public void A_draft_comes_from_the_caller_and_every_change_goes_back_to_it_until_it_is_posted()
    {
        string? kept = "Half a thought";
        var block = Render<CommentBubbleList>(parameters => parameters
            .Add(component => component.Comments, comments)
            .Add(component => component.Label, "Staff comments")
            .Add(component => component.InputName, "comment-staff")
            .Add(component => component.CanPost, true)
            .Add(component => component.Draft, kept)
            .Add(component => component.DraftChanged, draft => kept = draft)
            .Add(component => component.OnPost, _ => Task.FromResult(Result.Success())));

        Assert.Equal("Half a thought", block.Find("textarea").GetAttribute("value"));
        block.Find("textarea").Input("Half a thought, finished");
        Assert.Equal("Half a thought, finished", kept);

        block.Find("textarea").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        Assert.Null(kept);
    }

    [Fact]
    public void A_comment_someone_else_wrote_that_a_new_read_brings_is_said_in_the_status_region()
    {
        var block = Render<CommentBubbleList>(parameters => parameters
            .Add(component => component.Comments, comments[..2])
            .Add(component => component.Label, "Staff comments")
            .Add(component => component.InputName, "comment-staff"));
        Assert.Equal(string.Empty, block.Find("[role=status]").TextContent);

        block.Render(parameters => parameters.Add(component => component.Comments, comments));
        Assert.Equal("New comment from Alex Morgan.", block.Find("[role=status]").TextContent);
    }
}
