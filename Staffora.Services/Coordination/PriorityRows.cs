using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Staffora.Services.Coordination;

public enum PriorityTab
{
    [Display(Name = "All")]
    All = 0,

    [Display(Name = "Orders")]
    Orders = 1,

    [Display(Name = "Slots")]
    Slots = 2,

    [Display(Name = "Questions")]
    Questions = 3,

    [Display(Name = "Absence")]
    Absence = 4,
}

public enum PriorityItemKind
{
    [Display(Name = "Absent")]
    AbsenceImpact = 0,

    [Display(Name = "Order")]
    OrdersToAnswer = 1,

    [Display(Name = "At risk")]
    AtRisk = 2,

    [Display(Name = "Uncovered")]
    ToRequestOrAssign = 3,

    [Display(Name = "Unanswered")]
    UnansweredRequests = 4,

    [Display(Name = "Waiting for a pick")]
    WaitingForPick = 5,

    [Display(Name = "Question")]
    StaffQuestions = 6,

    [Display(Name = "Message")]
    CustomerMessages = 7,

    [Display(Name = "Absence")]
    AbsenceToApprove = 8,

    [Display(Name = "Behind")]
    JobsBehind = 9,
}

public enum PriorityTarget
{
    Absence = 1,

    Job = 2,

    AbsenceImpact = 3,
}

public static class PriorityList
{
    public const int PageSize = 50;

    public const int MaxShown = 500;

    public const int MaxSearchLength = 100;

    public static readonly TimeSpan ReplyWithin = TimeSpan.FromDays(1);

    public static string Label(PriorityItemKind kind) =>
        typeof(PriorityItemKind).GetField(kind.ToString())?.GetCustomAttribute<DisplayAttribute>()?.Name ?? kind.ToString();

    public static string TabTitle(PriorityTab tab) =>
        typeof(PriorityTab).GetField(tab.ToString())?.GetCustomAttribute<DisplayAttribute>()?.Name ?? tab.ToString();

    public static PriorityTab TabOf(PriorityItemKind kind) => kind switch
    {
        PriorityItemKind.OrdersToAnswer or PriorityItemKind.JobsBehind => PriorityTab.Orders,
        PriorityItemKind.AtRisk or PriorityItemKind.ToRequestOrAssign or PriorityItemKind.UnansweredRequests or PriorityItemKind.WaitingForPick => PriorityTab.Slots,
        PriorityItemKind.StaffQuestions or PriorityItemKind.CustomerMessages => PriorityTab.Questions,
        _ => PriorityTab.Absence,
    };

    public static bool InTab(PriorityItemKind kind, PriorityTab tab) => tab == PriorityTab.All || TabOf(kind) == tab;

    public static int Rank(PriorityItemKind kind) => kind switch
    {
        PriorityItemKind.AtRisk => 0,
        PriorityItemKind.AbsenceImpact => 1,
        PriorityItemKind.ToRequestOrAssign => 2,
        PriorityItemKind.OrdersToAnswer => 3,
        PriorityItemKind.UnansweredRequests => 4,
        PriorityItemKind.StaffQuestions => 5,
        PriorityItemKind.CustomerMessages => 6,
        PriorityItemKind.WaitingForPick => 7,
        PriorityItemKind.AbsenceToApprove => 8,
        _ => 9,
    };

    public static DateTime DueUtc(DateTime? startUtc, DateTime? waitingSinceUtc)
    {
        var reply = waitingSinceUtc + ReplyWithin;
        return (startUtc, reply) switch
        {
            ({ } start, { } due) => start < due ? start : due,
            ({ } start, null) => start,
            (null, { } due) => due,
            _ => throw new ArgumentException("An item is due by its start or by the reply it waits for."),
        };
    }

    public static IReadOnlyList<PriorityItem> Order(IEnumerable<PriorityItem> items, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(items);
        return [.. items
            .OrderBy(item => item.DueUtc > nowUtc ? item.DueUtc : nowUtc)
            .ThenBy(item => Rank(item.Kind))
            .ThenBy(item => item.DueUtc)
            .ThenBy(item => item.Key, StringComparer.Ordinal)];
    }
}

public sealed record PriorityQuery(PriorityTab Tab = PriorityTab.All, string? Search = null, DateOnly? Day = null, int Take = PriorityList.PageSize)
{
    public string SearchText
    {
        get
        {
            var text = (Search ?? string.Empty).Trim();
            return text.Length > PriorityList.MaxSearchLength ? text[..PriorityList.MaxSearchLength] : text;
        }
    }

    public int Shown => Math.Clamp(Take, 1, PriorityList.MaxShown);
}

public sealed record PriorityItem(
    PriorityItemKind Kind,
    string Key,
    DateTime DueUtc,
    string When,
    string Title,
    string Text,
    string Meta,
    PriorityTarget Target,
    Guid TargetId,
    Guid? AbsenceId = null)
{
    public bool IsSoon { get; init; }

    public bool IsOpenAgain { get; init; }

    public string Label => PriorityList.Label(Kind);
}

public sealed record PriorityBoard(PriorityTab Tab, IReadOnlyList<PriorityItem> Items, IReadOnlyDictionary<PriorityTab, int> TabCounts)
{
    public int Total => CountOf(PriorityTab.All);

    public int CountOf(PriorityTab tab) => TabCounts.TryGetValue(tab, out var count) ? count : 0;

    public bool HasMore => Items.Count < CountOf(Tab) && Items.Count < PriorityList.MaxShown;
}
