namespace Staffora.Services.Demo;

internal sealed record DemoBooking(DateTime From, DateTime To)
{
    public static DemoBooking Of(DateOnly day, DemoShift shift) =>
        new(day.ToDateTime(shift.Start), (shift.Overnight ? day.AddDays(1) : day).ToDateTime(shift.End));

    public static DemoBooking WholeDays(DateOnly first, DateOnly last) =>
        new(first.ToDateTime(TimeOnly.MinValue), last.AddDays(1).ToDateTime(TimeOnly.MinValue));

    public bool Overlaps(DemoBooking other) => this.From < other.To && other.From < this.To;

    public bool Touches(DateOnly first, DateOnly last) => this.Overlaps(WholeDays(first, last));
}

internal sealed class DemoBookings
{
    private readonly Dictionary<Guid, List<DemoBooking>> booked = [];

    public bool IsFree(Guid resource, IReadOnlyList<DemoBooking> occurrences) =>
        !this.booked.TryGetValue(resource, out var taken) || !occurrences.Any(occurrence => taken.Any(occurrence.Overlaps));

    public bool IsBooked(Guid resource, DateOnly first, DateOnly last) =>
        this.booked.TryGetValue(resource, out var taken) && taken.Any(booking => booking.Touches(first, last));

    public void Book(Guid resource, IEnumerable<DemoBooking> occurrences)
    {
        if (!this.booked.TryGetValue(resource, out var taken))
        {
            taken = [];
            this.booked[resource] = taken;
        }

        taken.AddRange(occurrences);
    }

    public void BlockDays(Guid resource, DateOnly first, DateOnly last) => this.Book(resource, [DemoBooking.WholeDays(first, last)]);

    public static bool Covers(DemoWorkPattern pattern, IReadOnlyList<DemoBooking> occurrences)
    {
        var segments = pattern.Days.SelectMany(day => pattern.Overnight
            ? new[] { (Day: day, From: pattern.Start.ToTimeSpan(), To: TimeSpan.FromDays(1)), (Day: (DayOfWeek)(((int)day + 1) % 7), From: TimeSpan.Zero, To: pattern.End.ToTimeSpan()) }
            : [(Day: day, From: pattern.Start.ToTimeSpan(), To: pattern.End.ToTimeSpan())]).ToList();
        return occurrences.SelectMany(PartsOf).All(part => segments.Any(segment => segment.Day == part.Day && segment.From <= part.From && part.To <= segment.To));
    }

    private static IEnumerable<(DayOfWeek Day, TimeSpan From, TimeSpan To)> PartsOf(DemoBooking occurrence)
    {
        for (var start = occurrence.From; start < occurrence.To; start = start.Date.AddDays(1))
        {
            var end = occurrence.To < start.Date.AddDays(1) ? occurrence.To : start.Date.AddDays(1);
            yield return (start.DayOfWeek, start.TimeOfDay, end - start.Date);
        }
    }
}

internal sealed class DemoSentEarlier : TimeProvider
{
    private readonly TimeProvider clock;
    private readonly TimeSpan earlier;

    public DemoSentEarlier(TimeProvider clock, TimeSpan earlier)
    {
        this.clock = clock;
        this.earlier = earlier;
    }

    public override TimeZoneInfo LocalTimeZone => this.clock.LocalTimeZone;

    public override DateTimeOffset GetUtcNow() => this.clock.GetUtcNow() - this.earlier;
}
