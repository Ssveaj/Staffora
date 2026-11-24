using System.Runtime.ExceptionServices;

namespace Staffora.Test.Unit.Architecture;

internal static class OwnThreads
{
    public static int Wide => Math.Max(2, Environment.ProcessorCount / 4);

    public static IReadOnlyList<TResult> Map<TItem, TResult>(IReadOnlyList<TItem> items, int threads, Func<TItem, TResult> work)
    {
        var results = new TResult[items.Count];
        var next = -1;
        Exception? failure = null;
        var workers = Enumerable.Range(0, Math.Clamp(threads, 1, Math.Max(1, items.Count)))
            .Select(_ => new Thread(() =>
            {
                try
                {
                    for (var index = Interlocked.Increment(ref next); index < items.Count && Volatile.Read(ref failure) is null; index = Interlocked.Increment(ref next))
                    {
                        results[index] = work(items[index]);
                    }
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref failure, exception, null);
                }
            })
            { IsBackground = true })
            .ToList();

        workers.ForEach(worker => worker.Start());
        workers.ForEach(worker => worker.Join());
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        return results;
    }
}
