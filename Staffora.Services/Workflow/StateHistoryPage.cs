using Staffora.Core.Entities.Messaging;

namespace Staffora.Services.Workflow;

public sealed record StateHistoryQuery(int Take, StateHistoryCursor? OlderThan = null, StateHistoryCursor? NewerThan = null)
{
    public static StateHistoryQuery Newest(int take) => new(take);
}

public sealed record StateHistoryCursor(DateTime Created, Guid Id);

public sealed record StateHistoryPage(IReadOnlyList<StateHistoryStep> Steps, int TotalCount, int NewerCount, string TimeZoneId, bool InReadersZone = true, DateOnly? Today = null)
{
    public static StateHistoryPage Empty { get; } = new([], 0, 0, string.Empty);

    public bool HasNewer => Steps.Count > 0 && NewerCount > 0;

    public bool HasOlder => Steps.Count > 0 && NewerCount + Steps.Count < TotalCount;

    public StateHistoryQuery? Older(int take) => HasOlder ? new StateHistoryQuery(take, OlderThan: Steps[^1].Cursor) : null;

    public StateHistoryQuery? Newer(int take) => HasNewer ? new StateHistoryQuery(take, NewerThan: Steps[0].Cursor) : null;
}

public sealed record StateHistoryStep(Guid Id, string TransitionKey, string StepName, Enum From, Enum To, DateTimeOffset At, string Actor, IReadOnlyList<string> ActorNotes, StateHistoryCursor Cursor, IReadOnlyList<StateHistoryNotice>? Notices = null);

public sealed record StateHistoryNotice(string Name, string Channel, OutgoingMessageStatus Status, int RecipientCount)
{
    public string Outcome => Status switch
    {
        OutgoingMessageStatus.Delivered when RecipientCount == 0 => "nobody to deliver to",
        OutgoingMessageStatus.Delivered => $"delivered to {RecipientCount} {(RecipientCount == 1 ? "person" : "people")}",
        OutgoingMessageStatus.Failed => "not delivered",
        _ => "delivery pending",
    };
}
