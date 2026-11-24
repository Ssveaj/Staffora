using Staffora.Core.Entities.Jobs;
using Staffora.Core.Jobs;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Models;

public sealed record RequestRoundRow(Guid Id, RequestRoundState State, bool FirstToAccept, DateTime Created, string SentBy, int Requested, int Accepted, int Assigned, int Closed)
{
    public string RuleLabel => FirstToAccept ? "First to accept" : "Coordinator picks";

    public string CountsLabel => RequestRoundSummary.Counts(Requested, Accepted, Assigned, Closed);
}

public sealed record RequestRoundSummary(string Job, string Slot, int SlotCount, int Held, bool FirstToAccept, string SentBy, DateTime SentAt, int Requested, int Accepted, int Assigned, int Closed)
{
    public string HeldLabel => SlotGroups.HeldText(Held, SlotCount);

    public string RuleLabel => RequestRoundWorkflowAttribute.RuleOf(FirstToAccept);

    public string CountsLabel => Counts(Requested, Accepted, Assigned, Closed);

    public bool NothingWaits => Requested == 0 && Accepted == 0;

    public static string Counts(int requested, int accepted, int assigned, int closed) =>
        string.Join(", ", new[]
        {
            requested > 0 ? $"{requested} asked" : null,
            accepted > 0 ? $"{accepted} said yes" : null,
            assigned > 0 ? $"{assigned} assigned" : null,
            closed > 0 ? $"{closed} closed" : null,
        }.OfType<string>()) is { Length: > 0 } text ? text : "No request";
}
