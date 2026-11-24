using System.Globalization;
using Staffora.Core.Scheduling;

namespace Staffora.Core.Models;

public sealed record OwnScheduleMonth(DateOnly First, DateOnly Today, string TimeZoneId, IReadOnlyList<OwnMonthWeek> Weeks)
{
    public Guid ResourceId { get; init; }

    public Guid UserId { get; init; }

    public OwnPatternPeriod? Pattern { get; init; }

    public OwnPatternPeriod? NextPattern { get; init; }

    public OwnPatternPeriod? PreviousPattern { get; init; }

    public DateOnly Last => First.AddMonths(1).AddDays(-1);

    public IEnumerable<OwnScheduleDay> Days => Weeks.SelectMany(week => week.Days);

    public string Title => First.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    public string Key => KeyOf(First);

    public string WeeksLabel => Weeks.Count == 0 ? string.Empty : $"Weeks {Weeks[0].Number} to {Weeks[^1].Number}";

    public bool IsCurrent => First <= Today && Today <= Last;

    public OwnHours Hours => OwnHours.Of(Days);

    public static DateOnly FirstOf(DateOnly day) => new(day.Year, day.Month, 1);

    public static string KeyOf(DateOnly month) => month.ToString("yyyy'-'MM", CultureInfo.InvariantCulture);

    public static string ShortDate(DateOnly date) => date.ToString("d MMM", CultureInfo.InvariantCulture);

    public static string LongDate(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static bool IsReadable(DateOnly first) => first.Year > 1 && first.Year < 9999;
}

public sealed record OwnMonthWeek(DateOnly Monday, IReadOnlyList<OwnScheduleDay> Days)
{
    public int? PatternWeek { get; init; }

    public int Number => ISOWeek.GetWeekOfYear(Monday.ToDateTime(TimeOnly.MinValue));

    public string RangeLabel => Days.Count switch
    {
        0 => string.Empty,
        1 => OwnScheduleMonth.ShortDate(Days[0].Date),
        _ => $"{OwnScheduleMonth.ShortDate(Days[0].Date)} to {OwnScheduleMonth.ShortDate(Days[^1].Date)}",
    };

    public OwnHours Hours => OwnHours.Of(Days);
}

public sealed record OwnPatternPeriod(Guid AssignmentId, string Name, DateOnly From, DateOnly? To, int CycleWeeks, DateOnly Anchor, bool Confirmed, IReadOnlyList<PatternSegment> Segments)
{
    public string PeriodLabel => To is { } to
        ? $"{OwnScheduleMonth.LongDate(From)} to {OwnScheduleMonth.LongDate(to)}"
        : $"From {OwnScheduleMonth.LongDate(From)} until further notice";

    public bool Covers(DateOnly date) => date >= From && (To is not { } to || date <= to);

    public int? DaysLeft(DateOnly today) => To is { } to && today <= to ? to.DayNumber - today.DayNumber : null;

    public int CycleWeekOn(DateOnly date)
    {
        var cycle = Math.Max(CycleWeeks, 1);
        var weeks = (int)Math.Floor((date.DayNumber - Anchor.DayNumber) / (double)WorkPatternRules.DaysPerWeek);
        return (((weeks % cycle) + cycle) % cycle) + 1;
    }

    public IReadOnlyList<OwnPatternWeekRow> CycleRows => [.. Enumerable.Range(0, Math.Max(CycleWeeks, 1)).Select(week =>
    {
        var days = Enumerable.Range(0, WorkPatternRules.DaysPerWeek)
            .Select(day => (IReadOnlyList<PatternSegment>)[.. Segments.Where(segment => segment.DayIndex == (week * WorkPatternRules.DaysPerWeek) + day).OrderBy(segment => segment.Start)])
            .ToList();
        var minutes = days.SelectMany(day => day).Sum(segment => (int)(segment.End - segment.Start).TotalMinutes);
        return new OwnPatternWeekRow(week + 1, days, minutes);
    })];
}

public sealed record OwnPatternWeekRow(int Week, IReadOnlyList<IReadOnlyList<PatternSegment>> Days, int Minutes)
{
    public static IReadOnlyList<string> TimesOf(IReadOnlyList<PatternSegment> day) =>
        [.. day.Select(segment => $"{WorkPatternRules.TimeText(segment.Start)}–{WorkPatternRules.TimeText(segment.End)}")];
}

public sealed record OwnHours(int WorkingMinutes, int AssignmentMinutes, int AbsenceMinutes)
{
    public int OpenMinutes => WorkingMinutes - AssignmentMinutes - AbsenceMinutes;

    public string WorkingLabel => Text(WorkingMinutes);

    public string AssignmentLabel => Text(AssignmentMinutes);

    public string AbsenceLabel => Text(AbsenceMinutes);

    public string OpenLabel => Text(OpenMinutes);

    public static string Text(int minutes) => $"{(minutes / 60d).ToString("0.##", CultureInfo.InvariantCulture)} h";

    public static OwnHours Of(IEnumerable<OwnScheduleDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        int working = 0, assigned = 0, absent = 0;
        foreach (var day in days)
        {
            var absences = day.Absences.Select(OwnDayMinutes.Of).ToList();
            var occurrences = day.Occurrences.Select(OwnDayMinutes.Of).ToList();
            foreach (var (start, end) in day.Working.Select(OwnDayMinutes.Of))
            {
                for (var minute = start; minute < end; minute++)
                {
                    working++;
                    if (absences.Any(span => span.Start <= minute && minute < span.End))
                    {
                        absent++;
                    }
                    else if (occurrences.Any(span => span.Start <= minute && minute < span.End))
                    {
                        assigned++;
                    }
                }
            }
        }

        return new OwnHours(working, assigned, absent);
    }
}

public static class OwnDayMinutes
{
    public const int Day = 24 * 60;

    public static (int Start, int End) Of(WorkingTime working) => Span(working.LocalStart, working.LocalEnd);

    public static (int Start, int End) Of(OwnOccurrenceEntry occurrence) => Span(occurrence.Start, occurrence.End);

    public static (int Start, int End) Of(OwnAbsenceEntry absence) =>
        (absence.From is { } from ? Minute(from) : 0, absence.Until is { } until ? Minute(until) : Day);

    public static int Minute(TimeOnly time) => (time.Hour * 60) + time.Minute;

    private static (int Start, int End) Span(TimeOnly start, TimeOnly end) =>
        (Minute(start), end == TimeOnly.MinValue ? Day : Minute(end));
}

public sealed record OwnAxisPiece(int Start, int End, bool Folded);

public sealed record OwnAxisTick(int Minute, string Label, double At);

public sealed class OwnDayAxis
{
    public const int FoldMinimum = 120;

    public const int FoldWidth = 30;

    private readonly double total;

    private OwnDayAxis(IReadOnlyList<OwnAxisPiece> pieces)
    {
        Pieces = pieces;
        this.total = pieces.Sum(Weight);
    }

    public IReadOnlyList<OwnAxisPiece> Pieces { get; }

    public int Start => Pieces[0].Start;

    public int End => Pieces[^1].End;

    public IEnumerable<OwnAxisPiece> Folds => Pieces.Where(piece => piece.Folded);

    public string FoldsLabel => string.Join(", ", Folds.Select(fold => $"{Text(fold.Start)}–{Text(fold.End)}")) is { Length: > 0 } folds ? $"{folds} folded" : string.Empty;

    public IReadOnlyList<OwnAxisTick> Ticks
    {
        get
        {
            var ticks = new List<OwnAxisTick>();
            foreach (var piece in Pieces)
            {
                if (piece.Folded)
                {
                    ticks.Add(new OwnAxisTick(piece.Start, Text(piece.Start), Position(piece.Start)));
                    ticks.Add(new OwnAxisTick(piece.End, Text(piece.End), Position(piece.End)));
                    continue;
                }

                for (var minute = ((piece.Start + 59) / 60) * 60; minute < piece.End; minute += 60)
                {
                    var nearFold = Folds.Any(fold => Math.Abs(fold.Start - minute) < 40 || Math.Abs(fold.End - minute) < 40);
                    if (!nearFold && minute < End)
                    {
                        ticks.Add(new OwnAxisTick(minute, Text(minute), Position(minute)));
                    }
                }
            }

            return ticks;
        }
    }

    public double Position(int minute)
    {
        var clamped = Math.Clamp(minute, Start, End);
        var before = 0d;
        foreach (var piece in Pieces)
        {
            if (clamped <= piece.End)
            {
                var length = piece.End - piece.Start;
                var into = length == 0 ? 0 : (clamped - piece.Start) / (double)length;
                return Math.Round((before + (into * Weight(piece))) / this.total, 5);
            }

            before += Weight(piece);
        }

        return 1;
    }

    public bool IsFolded(int minute) => Folds.Any(fold => fold.Start < minute && minute < fold.End);

    public static OwnDayAxis Of(IEnumerable<OwnScheduleDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var stretches = new List<(int Start, int End)>();
        foreach (var day in days)
        {
            stretches.AddRange(day.Working.Select(OwnDayMinutes.Of));
            stretches.AddRange(day.Occurrences.Select(OwnDayMinutes.Of));

            foreach (var absence in day.Absences)
            {
                if (absence.From is { } from)
                {
                    var start = OwnDayMinutes.Minute(from);
                    stretches.Add((start, absence.Until is { } until ? OwnDayMinutes.Minute(until) : start));
                }
                else if (absence.Until is { } until)
                {
                    var end = OwnDayMinutes.Minute(until);
                    stretches.Add((end, end));
                }
            }
        }

        if (stretches.Count == 0)
        {
            return new OwnDayAxis([new OwnAxisPiece(8 * 60, 17 * 60, false)]);
        }

        var merged = new List<(int Start, int End)>();
        foreach (var (start, end) in stretches.OrderBy(stretch => stretch.Start).ThenBy(stretch => stretch.End))
        {
            if (merged.Count > 0 && start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        var axisStart = (merged[0].Start / 60) * 60;
        var axisEnd = Math.Min(OwnDayMinutes.Day, ((merged[^1].End + 59) / 60) * 60);
        if (axisEnd <= axisStart)
        {
            axisEnd = Math.Min(OwnDayMinutes.Day, axisStart + 60);
        }

        var pieces = new List<OwnAxisPiece>();
        var pieceStart = axisStart;
        for (var index = 0; index < merged.Count - 1; index++)
        {
            var gapStart = merged[index].End;
            var gapEnd = merged[index + 1].Start;
            if (gapEnd - gapStart >= FoldMinimum)
            {
                pieces.Add(new OwnAxisPiece(pieceStart, gapStart, false));
                pieces.Add(new OwnAxisPiece(gapStart, gapEnd, true));
                pieceStart = gapEnd;
            }
        }

        pieces.Add(new OwnAxisPiece(pieceStart, axisEnd, false));
        return new OwnDayAxis(pieces);
    }

    private static double Weight(OwnAxisPiece piece) => piece.Folded ? FoldWidth : piece.End - piece.Start;

    private static string Text(int minute) => minute >= OwnDayMinutes.Day ? "24:00" : WorkPatternRules.TimeText(new TimeOnly(minute / 60, minute % 60));
}
