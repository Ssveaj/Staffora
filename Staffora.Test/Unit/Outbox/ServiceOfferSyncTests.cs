using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Integration;

namespace Staffora.Test.Unit.Outbox;

public class ServiceOfferSyncTests
{
    [Theory]
    [InlineData(false, true, false, ServiceOfferSync.OfferedKind)]
    [InlineData(false, true, true, ServiceOfferSync.OfferedKind)]
    [InlineData(true, true, true, ServiceOfferSync.ChangedKind)]
    [InlineData(true, true, false, null)]
    [InlineData(true, false, false, ServiceOfferSync.WithdrawnKind)]
    [InlineData(true, false, true, ServiceOfferSync.WithdrawnKind)]
    [InlineData(false, false, true, null)]
    public void A_steps_message_follows_what_the_portals_were_offered_before_and_after(bool wasOffered, bool isOffered, bool changed, string? kind) =>
        Assert.Equal(kind, ServiceOfferSync.KindFor(wasOffered, isOffered, changed));

    [Fact]
    public void Only_an_active_service_offered_in_the_portal_is_offered()
    {
        Assert.True(ServiceOfferSync.IsOffered(ServiceState.Active, true));
        Assert.False(ServiceOfferSync.IsOffered(ServiceState.Active, false));
        Assert.False(ServiceOfferSync.IsOffered(ServiceState.Archived, true));
        Assert.False(ServiceOfferSync.IsOffered(ServiceState.Create, true));
    }

    [Fact]
    public void One_services_messages_to_one_portal_share_a_correlation_of_their_own()
    {
        var service = Guid.Parse("6b1f2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");
        var portal = Guid.Parse("0f1e2d3c-4b5a-4968-8776-655443322110");
        var other = Guid.Parse("11111111-2222-4333-8444-555555555555");

        var correlation = ServiceOfferSync.CorrelationFor(service, portal);
        Assert.Equal(correlation, ServiceOfferSync.CorrelationFor(service, portal));
        Assert.NotEqual(correlation, ServiceOfferSync.CorrelationFor(service, other));
        Assert.NotEqual(correlation, ServiceOfferSync.CorrelationFor(other, portal));
        Assert.NotEqual(service, correlation);

        var text = correlation.ToString();
        Assert.Equal('8', text[14]);
        Assert.Contains(text[19], "89ab");
    }

    [Fact]
    public void The_payloads_fit_a_message_at_the_longest_texts_and_read_back_as_written()
    {
        var longest = new ServiceOffer(Guid.NewGuid(), new string('n', Service.NameMaxLength), new string('d', Service.DescriptionMaxLength), Service.MaxSlotCount);
        var text = ServiceOfferSync.WriteOffer(longest);
        Assert.True(text.Length <= IntegrationMessage.MaxPayloadLength, $"{text.Length} characters");
        Assert.Equal(longest, ServiceOfferSync.ReadOffer(text));

        var withdrawn = Guid.NewGuid();
        Assert.Equal(withdrawn, ServiceOfferSync.ReadWithdrawal(ServiceOfferSync.WriteWithdrawal(withdrawn)));

        Assert.Equal(["defaultSlotCount", "description", "name", "service"], System.Text.Json.JsonDocument.Parse(text).RootElement.EnumerateObject().Select(property => property.Name).Order());

        Assert.Null(ServiceOfferSync.ReadOffer("{}"));
        Assert.Null(ServiceOfferSync.ReadOffer("not json"));
        Assert.Null(ServiceOfferSync.ReadWithdrawal("{\"service\":\"00000000-0000-0000-0000-000000000000\"}"));
    }

    [Fact]
    public void A_snapshot_of_any_language_at_the_longest_texts_fits_a_message_and_one_that_cannot_has_its_description_shortened()
    {
        var swedish = new ServiceOffer(Guid.NewGuid(), Fill("Städ \"&\" <å> ", Service.NameMaxLength), Fill("Lokalvård i skolans kök och matsal, \"åäö\" <ÅÄÖ> & mer. ", Service.DescriptionMaxLength), 3);
        var written = ServiceOfferSync.WriteOffer(swedish);
        Assert.True(written.Length <= IntegrationMessage.MaxPayloadLength, $"{written.Length} characters");
        Assert.Equal(swedish, ServiceOfferSync.ReadOffer(written));

        var escaped = new ServiceOffer(Guid.NewGuid(), Fill("\u0001\U0001F600", Service.NameMaxLength), Fill("\u0001\U0001F600", Service.DescriptionMaxLength), 4);
        var shortened = ServiceOfferSync.WriteOffer(escaped);
        Assert.True(shortened.Length <= IntegrationMessage.MaxPayloadLength, $"{shortened.Length} characters");
        var read = ServiceOfferSync.ReadOffer(shortened)!;
        Assert.Equal((escaped.Service, escaped.Name, escaped.DefaultSlotCount), (read.Service, read.Name, read.DefaultSlotCount));
        Assert.NotNull(read.Description);
        Assert.StartsWith(read.Description, escaped.Description!, StringComparison.Ordinal);
        Assert.True(read.Description.Length < escaped.Description!.Length);
        Assert.False(char.IsHighSurrogate(read.Description[^1]));
    }

    private static string Fill(string text, int length) => string.Concat(Enumerable.Repeat(text, (length / text.Length) + 1))[..length];
}
