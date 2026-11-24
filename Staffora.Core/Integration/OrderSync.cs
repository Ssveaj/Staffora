using System.Text.Json;
using System.Text.Json.Serialization;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Portal;

namespace Staffora.Core.Integration;

public static class OrderSync
{
    public const string JobStateKind = "job-state";

    public const string CancelKind = "order-cancel";

    public const int MaxStaffMembers = 30;

    public sealed record Row(JobState Job, OrderState Request, bool? AfterConfirmation = null);

    public static readonly IReadOnlyList<Row> Table =
    [
        new(JobState.Coordinating, OrderState.Coordinating),
        new(JobState.Confirmed, OrderState.Confirmed),
        new(JobState.InProgress, OrderState.InProgress),
        new(JobState.Completed, OrderState.Completed),
        new(JobState.CanceledByCustomer, OrderState.CanceledByUs),
        new(JobState.CanceledBySupplier, OrderState.CanceledBySupplier, AfterConfirmation: true),
        new(JobState.CanceledBySupplier, OrderState.DeclinedBySupplier, AfterConfirmation: false),
        new(JobState.Deleted, OrderState.DeclinedBySupplier, AfterConfirmation: false),
    ];

    public static readonly IReadOnlyList<OrderState> Following = [OrderState.Sent, OrderState.Coordinating, OrderState.Confirmed, OrderState.InProgress];

    public static readonly IReadOnlyList<OrderState> Final = [OrderState.Completed, OrderState.CanceledByUs, OrderState.CanceledBySupplier, OrderState.DeclinedBySupplier];

    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static bool IsCustomerVisible(JobState state) => Table.Any(row => row.Job == state);

    public static bool WasConfirmed(JobState from) => from is JobState.Confirmed or JobState.InProgress;

    public static OrderState? RequestStateFor(JobState state, bool confirmed) =>
        Table.FirstOrDefault(row => row.Job == state && (row.AfterConfirmation is null || row.AfterConfirmation == confirmed))?.Request;

    public static bool MovesForward(OrderState current, OrderState target) =>
        current == OrderState.CancellationSent
            ? Final.Contains(target)
            : Following.Contains(current) && Rank(target) > Rank(current);

    public static OrderState CanceledTo(OrderState from) =>
        CancelCrosses(from) ? OrderState.CancellationSent : OrderState.CanceledByUs;

    public static bool AnswersCancellation(JobState state) => state is JobState.Completed or JobState.CanceledByCustomer or JobState.CanceledBySupplier;

    public static bool SendsStaffMembers(JobState state) => state is JobState.Confirmed or JobState.InProgress or JobState.Completed;

    public static bool ShowsStaffMembers(OrderState state) => state is OrderState.Confirmed or OrderState.InProgress or OrderState.Completed;

    public static bool OrdererCancels(OrderState state) => state is OrderState.Registration or OrderState.Sent or OrderState.Coordinating;

    public static bool CancelCrosses(OrderState state) => state is OrderState.Sent or OrderState.Coordinating;

    public static IReadOnlyList<string> StaffNames(IEnumerable<(string? FirstName, string? LastName)> staffMembers)
    {
        ArgumentNullException.ThrowIfNull(staffMembers);
        return [.. staffMembers
            .Select(staff => $"{staff.FirstName?.Trim()} {staff.LastName?.Trim()}".Trim())
            .Where(name => name.Length > 0)
            .Select(name => name.Length <= OrderStaff.NameMaxLength ? name : name[..OrderStaff.NameMaxLength])
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .Take(MaxStaffMembers)];
    }

    public static string WriteJobState(JobStatePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var text = JsonSerializer.Serialize(payload, json);
        while (text.Length > IntegrationMessage.MaxPayloadLength && payload.StaffMembers is { Count: > 0 } staffMembers)
        {
            payload = payload with { StaffMembers = [.. staffMembers.Take(staffMembers.Count - 1)] };
            text = JsonSerializer.Serialize(payload, json);
        }

        return text;
    }

    public static JobStatePayload? ReadJobState(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<JobStatePayload>(payload, json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int Rank(OrderState state) => Final.Contains(state) ? Following.Count : Following.ToList().IndexOf(state);
}

public sealed record JobStatePayload(JobState State, bool Confirmed, IReadOnlyList<string>? StaffMembers);
