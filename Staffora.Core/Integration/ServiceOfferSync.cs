using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Integration;

namespace Staffora.Core.Integration;

public static class ServiceOfferSync
{
    public const string OfferedKind = "service-offered";

    public const string ChangedKind = "service-changed";

    public const string WithdrawnKind = "service-withdrawn";

    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool IsOffered(ServiceState state, bool offeredInPortal) => state == ServiceState.Active && offeredInPortal;

    public static string? KindFor(bool wasOffered, bool isOffered, bool snapshotChanged) => (wasOffered, isOffered) switch
    {
        (false, true) => OfferedKind,
        (true, true) when snapshotChanged => ChangedKind,
        (true, false) => WithdrawnKind,
        _ => null,
    };

    public static Guid CorrelationFor(Guid serviceLink, Guid targetTenantId)
    {
        Span<byte> input = stackalloc byte[32];
        serviceLink.TryWriteBytes(input[..16], bigEndian: true, out _);
        targetTenantId.TryWriteBytes(input[16..], bigEndian: true, out _);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    public static string WriteOffer(ServiceOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var text = JsonSerializer.Serialize(offer, json);
        while (text.Length > IntegrationMessage.MaxPayloadLength && offer.Description is { Length: > 0 } description)
        {
            var excess = text.Length - IntegrationMessage.MaxPayloadLength;
            var keep = Math.Max(0, description.Length - ((excess + 5) / 6));
            if (keep > 0 && char.IsHighSurrogate(description[keep - 1]))
            {
                keep--;
            }

            offer = offer with { Description = keep > 0 ? description[..keep] : null };
            text = JsonSerializer.Serialize(offer, json);
        }

        return text.Length <= IntegrationMessage.MaxPayloadLength
            ? text
            : throw new InvalidOperationException("A service's offer is larger than an integration message holds.");
    }

    public static string WriteWithdrawal(Guid serviceLink) => JsonSerializer.Serialize(new ServiceWithdrawal(serviceLink), json);

    public static ServiceOffer? ReadOffer(string payload) => Read<ServiceOffer>(payload) is { Service: var link, Name.Length: > 0 } offer && link != Guid.Empty ? offer : null;

    public static Guid? ReadWithdrawal(string payload) => Read<ServiceWithdrawal>(payload) is { Service: var link } && link != Guid.Empty ? link : null;

    private static T? Read<T>(string payload)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record ServiceOffer(Guid Service, string Name, string? Description, int DefaultSlotCount);

public sealed record ServiceWithdrawal(Guid Service);
