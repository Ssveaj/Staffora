using System.Globalization;

namespace Staffora.Blazor.Components.Common.Comments;

public static class CommentDays
{
    private static readonly CultureInfo english = CultureInfo.GetCultureInfo("en-GB");

    public static string DayLabel(DateOnly day, DateOnly today) =>
        day == today ? "Today"
        : day == today.AddDays(-1) ? "Yesterday"
        : day.ToString(day.Year == today.Year ? "dddd d MMMM" : "d MMMM yyyy", english);

    public static string WhenLabel(DateTimeOffset local, DateOnly today)
    {
        var day = DateOnly.FromDateTime(local.DateTime);
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return day == today ? $"today {time}"
            : day == today.AddDays(-1) ? $"yesterday {time}"
            : $"{local.ToString(day.Year == today.Year ? "d MMM" : "d MMM yyyy", english)} {time}";
    }

    public static string TimeLabel(DateTimeOffset local) => local.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string FullLabel(DateTimeOffset local) => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static DateOnly Today(DateTimeOffset now, string? timeZoneId)
    {
        if (!string.IsNullOrEmpty(timeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone))
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        }

        return DateOnly.FromDateTime(now.DateTime);
    }
}
