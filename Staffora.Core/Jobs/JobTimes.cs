using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Portal;

namespace Staffora.Core.Jobs;

public sealed record JobTimes(JobSchedule Schedule, DateOnly PeriodStart, DateOnly PeriodEnd, TimeOnly? StartTime, TimeOnly? EndTime, IReadOnlyList<OccurrenceTimes> Weekly)
{
    public bool IsSingle => Schedule == JobSchedule.Single;

    public static JobTimes Of(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(job.Schedule, job.PeriodStart, job.PeriodEnd, job.StartTime, job.EndTime, [.. JobRules.OccurrencesOf(job)]);
    }

    public static JobTimes Of(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new(order.Schedule, order.PeriodStart, order.PeriodEnd, order.StartTime, order.EndTime, []);
    }

    public static JobTimes Recurring(DateOnly periodStart, DateOnly periodEnd, IEnumerable<OccurrenceTimes> weekly)
    {
        ArgumentNullException.ThrowIfNull(weekly);
        return new(JobSchedule.Recurring, periodStart, periodEnd, null, null, [.. weekly]);
    }

    public static JobTimes Single(DateOnly day, TimeOnly start, TimeOnly end) =>
        new(JobSchedule.Single, day, JobRules.SingleLastDay(day, start, end), start, end, []);
}
