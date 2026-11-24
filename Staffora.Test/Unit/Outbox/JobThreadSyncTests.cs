using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Integration;

namespace Staffora.Test.Unit.Outbox;

public class JobThreadSyncTests
{
    [Fact]
    public void The_job_side_is_open_until_the_job_ends_and_the_request_side_from_its_send_until_it_is_final()
    {
        Assert.Equal(
            [JobState.Create, JobState.Registration, JobState.AwaitingCoordination, JobState.Coordinating, JobState.Confirmed, JobState.InProgress],
            Enum.GetValues<JobState>().Where(JobThreadSync.OpenOnJob));
        Assert.Equal(
            Enum.GetValues<OrderState>().Except([OrderState.Create, OrderState.Registration]),
            Enum.GetValues<OrderState>().Where(JobThreadSync.ShowsOnRequest));
        Assert.Equal(
            [OrderState.Sent, OrderState.Coordinating, OrderState.Confirmed, OrderState.InProgress, OrderState.CancellationSent],
            Enum.GetValues<OrderState>().Where(JobThreadSync.OpenOnRequest));
    }

    [Fact]
    public void A_message_carries_the_comments_id_and_words_and_a_portal_users_name_and_address_alone()
    {
        var id = Guid.NewGuid();
        var toPortal = JobThreadSync.WriteToPortal(new ToPortalComment(id, "Yes, she will."))!;
        Assert.Equal(new ToPortalComment(id, "Yes, she will."), JobThreadSync.ReadToPortal(toPortal));
        Assert.Equal(["comment", "text"], System.Text.Json.JsonDocument.Parse(toPortal).RootElement.EnumerateObject().Select(property => property.Name));

        var writer = new CommentWriter(Guid.NewGuid(), "Åsa", "Öberg", "asa@portal.example");
        var toSupplier = JobThreadSync.WriteToSupplier(new ToSupplierComment(id, "Får läraren ta med rockar?", writer))!;
        Assert.Contains("Får läraren", toSupplier, StringComparison.Ordinal);
        var read = JobThreadSync.ReadToSupplier(toSupplier)!;
        Assert.Equal((id, "Får läraren ta med rockar?", writer), (read.Comment, read.Text, read.Writer));
    }

    [Fact]
    public void A_whole_comment_of_any_language_fits_and_a_text_whose_escapes_overflow_is_refused()
    {
        var writer = new CommentWriter(Guid.NewGuid(), new string('n', 100), new string('n', 100), new string('e', 256));
        Assert.NotNull(JobThreadSync.WriteToSupplier(new ToSupplierComment(Guid.NewGuid(), new string('å', Comment.MaxTextLength), writer)));
        Assert.NotNull(JobThreadSync.WriteToPortal(new ToPortalComment(Guid.NewGuid(), new string('ö', Comment.MaxTextLength))));

        var escaped = string.Concat(Enumerable.Repeat("\"\n", Comment.MaxTextLength / 2));
        Assert.Equal(Comment.MaxTextLength, escaped.Length);
        Assert.True(IntegrationMessage.MaxPayloadLength < 2 * Comment.MaxTextLength + 50);
        Assert.Null(JobThreadSync.WriteToPortal(new ToPortalComment(Guid.NewGuid(), escaped)));
        Assert.Null(JobThreadSync.WriteToSupplier(new ToSupplierComment(Guid.NewGuid(), escaped, writer)));
    }

    [Fact]
    public void A_payload_that_is_none_of_its_kind_reads_as_nothing()
    {
        Assert.Null(JobThreadSync.ReadToPortal("not json"));
        Assert.Null(JobThreadSync.ReadToPortal("""{"comment":"00000000-0000-0000-0000-000000000000","text":"x"}"""));
        Assert.Null(JobThreadSync.ReadToPortal($$"""{"comment":"{{Guid.NewGuid()}}","text":""}"""));
        Assert.Null(JobThreadSync.ReadToSupplier($$"""{"comment":"{{Guid.NewGuid()}}","text":"x"}"""));
    }
}
