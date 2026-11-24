using Staffora.Core.Models;

namespace Staffora.Blazor.Pages.SuperAdmin;

public static class ErrorLogText
{
    public static string ShortName(string? fullName) =>
        string.IsNullOrEmpty(fullName) ? string.Empty : fullName[(fullName.LastIndexOf('.') + 1)..];

    public static string WhoText(ErrorLogEntryDto entry) => (entry.UserName, entry.ImpersonatorId) switch
    {
        ({ } user, not null) => $"{user}, impersonated by {entry.ImpersonatorName ?? "a removed user"}",
        ({ } user, null) => user,
        (null, _) when entry.UserId is not null => "A removed user",
        _ => "No user",
    };

    public static string TenantText(ErrorLogEntryDto entry) =>
        entry.TenantName ?? (entry.TenantId is null ? "No tenant" : "Removed tenant");
}
