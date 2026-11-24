namespace Staffora.Core.Models;

public enum SearchKind
{
    StaffMembers = 0,
    Jobs = 1,
    Customers = 2,
    Tenants = 3,
}

public enum SearchOpens
{
    StaffMember = 0,
    Job = 1,
    Assignment = 2,
    Customer = 3,
    TenantConsole = 4,
}

public sealed record SearchHit(SearchKind Kind, SearchOpens Opens, Guid Id, string Title, string? Subtitle, Enum? State);

public sealed record SearchGroup(SearchKind Kind, IReadOnlyList<SearchHit> Hits);

public sealed record SearchAnswer(string Text, IReadOnlyList<SearchGroup> Groups)
{
    public static SearchAnswer None(string text) => new(text, []);

    public IReadOnlyList<SearchHit> Hits => [.. Groups.SelectMany(group => group.Hits)];
}

public static class SearchText
{
    public const int MinLength = 2;

    public const int MaxLength = 100;

    public static string? Normalize(string? text)
    {
        var trimmed = text?.Trim();
        if (trimmed is null || trimmed.Length < MinLength)
        {
            return null;
        }

        return trimmed.Length > MaxLength ? trimmed[..MaxLength].TrimEnd() : trimmed;
    }
}
