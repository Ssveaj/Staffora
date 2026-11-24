using Staffora.Core.Jobs;
using Staffora.Core.Scheduling;

namespace Staffora.Core.Staffing;

public static class AbsenceImpactRules
{
    public static IReadOnlyList<OccurrenceInstant> AffectedOccurrences(ScheduledAbsence absence, JobTimes times, DateTime nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(absence);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(zone);
        if (AbsenceRules.Problems(absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime).Count > 0)
        {
            return [];
        }

        var (startUtc, endUtc) = AbsenceRules.Instants(absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime, zone);
        var from = absence.FirstDay > DateOnly.MinValue.AddDays(1) ? absence.FirstDay.AddDays(-1) : absence.FirstDay;
        return [.. JobRules.Occurrences(times, from, absence.LastDay, zone)
            .Where(occurrence => occurrence.StartUtc < endUtc && occurrence.EndUtc > startUtc && occurrence.EndUtc > nowUtc)
            .OrderBy(occurrence => occurrence.StartUtc)];
    }

    public static int OpenAgain(IEnumerable<DateTime> takenOffAt, IEnumerable<DateTime> heldSince, int openSlots)
    {
        ArgumentNullException.ThrowIfNull(takenOffAt);
        ArgumentNullException.ThrowIfNull(heldSince);
        if (openSlots <= 0)
        {
            return 0;
        }

        var events = takenOffAt.Select(at => (At: at, TakenOff: true))
            .Concat(heldSince.Select(at => (At: at, TakenOff: false)))
            .OrderBy(change => change.At)
            .ThenBy(change => change.TakenOff);
        var waiting = 0;
        foreach (var change in events)
        {
            waiting = change.TakenOff ? waiting + 1 : Math.Max(0, waiting - 1);
        }

        return Math.Min(waiting, openSlots);
    }

    public static string OpenAgainText(int openAgain, int open) =>
        openAgain <= 0 ? string.Empty
        : openAgain >= open ? "open again"
        : $"{openAgain} open again";
}
