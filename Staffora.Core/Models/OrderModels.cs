using Staffora.Core.Jobs;
using Staffora.Core.Entities.Portal;

namespace Staffora.Core.Models;

public sealed record OrderListItem(Guid Id, string Name, DateOnly PeriodStart, DateOnly PeriodEnd, OrderState State, string Orderer, bool IsOwn, DateTime Written)
{
    public string PeriodLabel => Times is { } times ? JobRules.WhenText(times) : JobRules.PeriodText(PeriodStart, PeriodEnd);

    public JobTimes? Times { get; init; }
}

public sealed record OrderDetails(
    Guid Id,
    string Name,
    string? Description,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    OrderState State,
    string Supplier,
    string Orderer,
    bool IsOwn,
    DateTime Written,
    DateTime? Sent,
    IReadOnlyList<string>? StaffMembers = null)
{
    public string PeriodLabel => Times is { } times ? JobRules.WhenText(times) : JobRules.PeriodText(PeriodStart, PeriodEnd);

    public JobTimes? Times { get; init; }

    public JobPlace Place { get; init; }
}

public sealed record OrderEditorOptions(bool FieldsLocked, string? Supplier, string? Orderer)
{
    public IReadOnlyList<OfferedServiceChoice> Services { get; init; } = [];
}

public sealed record OfferedServiceChoice(Guid Id, string Name, string? Description, int DefaultSlotCount, bool Offered)
{
    public string Label => Offered ? Name : $"{Name} (no longer offered)";
}
