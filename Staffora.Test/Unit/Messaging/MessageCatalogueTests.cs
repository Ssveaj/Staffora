using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Messaging;
using Staffora.Services.Messaging;
using Staffora.Services.Workflow;

namespace Staffora.Test.Unit.Messaging;

public class MessageCatalogueTests
{
    private static readonly MessageType type = new("sample-notice", MessageCategory.Account, "Sample", "Name", "Id");

    [Fact]
    public void Values_fill_in_as_text_and_as_one_part_of_the_link()
    {
        var rendered = MessageCatalogue.Render(type, new MessageTemplate("{{Name}} is here", "Hello {{Name}}.", "/people/{{Id}}?q={{Name}}"), new Dictionary<string, string?>
        {
            ["Name"] = " <b>Ann</b> & co/x?y ",
            ["Id"] = "42",
        });

        Assert.Equal("<b>Ann</b> & co/x?y is here", rendered.Subject);
        Assert.Equal("Hello <b>Ann</b> & co/x?y.", rendered.Text);
        Assert.Equal("/people/42?q=%3Cb%3EAnn%3C%2Fb%3E%20%26%20co%2Fx%3Fy", rendered.Link);
    }

    [Fact]
    public void Words_a_step_carried_are_appended_as_a_paragraph_of_their_own_and_cut_with_the_text()
    {
        var rendered = MessageCatalogue.Render(type, new MessageTemplate("{{Name}} asks", "{{Name}} has a question.", null), new Dictionary<string, string?> { ["Name"] = "Kim" });
        Assert.Equal("Kim has a question.\n\nWhich day?", MessageCatalogue.WithWords(rendered, "  Which day?  ").Text);
        Assert.Same(rendered, MessageCatalogue.WithWords(rendered, null));
        Assert.Same(rendered, MessageCatalogue.WithWords(rendered, "   "));
        var cut = MessageCatalogue.WithWords(rendered, new string('x', MessageCatalogue.MaxTextLength));
        Assert.Equal(MessageCatalogue.MaxTextLength, cut.Text.Length);
        Assert.EndsWith("…", cut.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_reads_as_a_placeholder_is_filled_in_as_written_never_filled_again()
    {
        var rendered = MessageCatalogue.Render(type, new MessageTemplate("{{Name}}", "Hello {{Name}}.", "?dlg=sample-{{Id}}"), new Dictionary<string, string?>
        {
            ["Name"] = "{{Id}}",
            ["Id"] = "{{Name}}",
        });

        Assert.Equal(("{{Id}}", "Hello {{Id}}."), (rendered.Subject, rendered.Text));
        Assert.Equal("?dlg=sample-%7B%7BName%7D%7D", rendered.Link);
    }

    [Theory]
    [InlineData(OutgoingMessageStatus.Delivered, 1, "delivered to 1 person")]
    [InlineData(OutgoingMessageStatus.Delivered, 3, "delivered to 3 people")]
    [InlineData(OutgoingMessageStatus.Delivered, 0, "nobody to deliver to")]
    [InlineData(OutgoingMessageStatus.Failed, 0, "not delivered")]
    [InlineData(OutgoingMessageStatus.Pending, 0, "delivery pending")]
    public void A_steps_history_says_how_its_notice_was_delivered_naming_no_one(OutgoingMessageStatus status, int count, string line)
    {
        Assert.Equal(line, new StateHistoryNotice("Account reactivated", StateHistoryService.InAppChannel, status, count).Outcome);
    }

    [Fact]
    public void A_missing_value_fills_in_empty()
    {
        Assert.Equal("Hello .", MessageCatalogue.Render(type, new MessageTemplate("Hi", "Hello {{Name}}.", null), new Dictionary<string, string?>()).Text);
    }

    [Fact]
    public void A_placeholder_or_a_value_that_is_not_the_types_own_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => MessageCatalogue.Render(type, new MessageTemplate("Hi", "Hello {{Email}}.", null), new Dictionary<string, string?>()));
        Assert.Throws<InvalidOperationException>(() => MessageCatalogue.Render(type, new MessageTemplate("Hi", "Hello.", null), new Dictionary<string, string?> { ["Email"] = "x" }));
    }

    [Theory]
    [InlineData("//evil.example/{{Id}}")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("javascript:alert(1)")]
    public void A_link_out_of_the_app_is_refused(string link)
    {
        Assert.Throws<InvalidOperationException>(() => MessageCatalogue.Render(type, new MessageTemplate("Hi", "Hello.", link), new Dictionary<string, string?> { ["Id"] = "1" }));
    }

    [Fact]
    public void A_filled_value_cannot_take_the_link_out_of_the_app()
    {
        var rendered = MessageCatalogue.Render(type, new MessageTemplate("Hi", "Hello.", "/{{Id}}"), new Dictionary<string, string?> { ["Id"] = "/evil.example" });

        Assert.Equal("/%2Fevil.example", rendered.Link);
        Assert.True(MessageCatalogue.IsLocalLink(rendered.Link));
    }

    [Fact]
    public void Subject_and_text_are_cut_to_their_columns()
    {
        var rendered = MessageCatalogue.Render(type, new MessageTemplate("{{Name}}", "{{Name}}{{Name}}", null), new Dictionary<string, string?> { ["Name"] = new string('x', 1500) });

        Assert.Equal(MessageCatalogue.MaxSubjectLength, rendered.Subject.Length);
        Assert.Equal(MessageCatalogue.MaxTextLength, rendered.Text.Length);
        Assert.EndsWith("…", rendered.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Not-a-key")]
    [InlineData("-leading")]
    [InlineData("")]
    public void A_type_key_is_lowercase_ascii(string key)
    {
        Assert.Throws<ArgumentException>(() => new MessageType(key, MessageCategory.Account, "Sample"));
    }

    [Fact]
    public async Task The_bus_tells_only_the_persons_own_subscriptions_until_each_is_disposed()
    {
        var bus = new NoticeBus(NullLogger<NoticeBus>.Instance);
        var (tenant, person, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var heard = new List<string>();
        var first = bus.Subscribe(tenant, person, notice => Record(heard, "first"));
        using var second = bus.Subscribe(tenant, person, notice => Record(heard, "second"));
        using var others = bus.Subscribe(tenant, other, notice => Record(heard, "other"));
        using var failing = bus.Subscribe(tenant, person, _ => throw new InvalidOperationException("A circuit that is gone."));

        bus.Publish(tenant, person, new NoticeArrived(Guid.NewGuid()));
        bus.Publish(Guid.NewGuid(), person, new NoticeArrived(Guid.NewGuid()));
        first.Dispose();
        bus.Publish(tenant, person, new NoticeArrived(Guid.NewGuid()));
        await Task.Yield();

        Assert.Equal(["first", "second", "second"], heard);
        Assert.Equal(3, bus.SubscriptionCount);
    }

    private static Task Record(List<string> heard, string who)
    {
        lock (heard)
        {
            heard.Add(who);
        }

        return Task.CompletedTask;
    }
}
