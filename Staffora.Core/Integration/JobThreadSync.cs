using System.Text.Encodings.Web;
using System.Text.Json;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Portal;

namespace Staffora.Core.Integration;

public static class JobThreadSync
{
    public const string ToPortalKind = "job-comment";

    public const string ToSupplierKind = "order-comment";

    public const string TooLongToSend = "This message is too long to send. Shorten it and try again.";

    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool OpenOnJob(JobState state) =>
        state is not (JobState.Completed or JobState.CanceledByCustomer or JobState.CanceledBySupplier or JobState.Deleted);

    public static bool ShowsOnRequest(OrderState state) => state is not (OrderState.Create or OrderState.Registration);

    public static bool OpenOnRequest(OrderState state) => ShowsOnRequest(state) && !OrderSync.Final.Contains(state);

    public static string? WriteToPortal(ToPortalComment payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var text = JsonSerializer.Serialize(payload, jsonOptions);
        return text.Length <= IntegrationMessage.MaxPayloadLength ? text : null;
    }

    public static string? WriteToSupplier(ToSupplierComment payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var text = JsonSerializer.Serialize(payload, jsonOptions);
        return text.Length <= IntegrationMessage.MaxPayloadLength ? text : null;
    }

    public static ToPortalComment? ReadToPortal(string payload) => Read<ToPortalComment>(payload) is { Comment: var id, Text: { Length: > 0 } } read && id != Guid.Empty ? read : null;

    public static ToSupplierComment? ReadToSupplier(string payload) =>
        Read<ToSupplierComment>(payload) is { Comment: var id, Text: { Length: > 0 }, Writer: { UserId: var writer } } read && id != Guid.Empty && writer != Guid.Empty ? read : null;

    private static T? Read<T>(string payload)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record ToPortalComment(Guid Comment, string Text);

public sealed record ToSupplierComment(Guid Comment, string Text, CommentWriter Writer);

public sealed record CommentWriter(Guid UserId, string FirstName, string LastName, string? Email);
