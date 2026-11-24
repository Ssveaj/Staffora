using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.Jobs;

public enum Weekday
{
    [Display(Name = "Monday")]
    Monday = 1,

    [Display(Name = "Tuesday")]
    Tuesday = 2,

    [Display(Name = "Wednesday")]
    Wednesday = 3,

    [Display(Name = "Thursday")]
    Thursday = 4,

    [Display(Name = "Friday")]
    Friday = 5,

    [Display(Name = "Saturday")]
    Saturday = 6,

    [Display(Name = "Sunday")]
    Sunday = 7,
}

public static class WeekdayExtensions
{
    public static IReadOnlyList<Weekday> Week { get; } = [Weekday.Monday, Weekday.Tuesday, Weekday.Wednesday, Weekday.Thursday, Weekday.Friday, Weekday.Saturday, Weekday.Sunday];

    public static bool IsDefined(this Weekday day) => day is >= Weekday.Monday and <= Weekday.Sunday;

    public static DayOfWeek ToDayOfWeek(this Weekday day) => (DayOfWeek)((int)day % 7);

    public static Weekday WeekdayOf(DateOnly date) => date.DayOfWeek == DayOfWeek.Sunday ? Weekday.Sunday : (Weekday)(int)date.DayOfWeek;
}
