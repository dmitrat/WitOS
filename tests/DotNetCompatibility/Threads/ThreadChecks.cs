using System.Diagnostics;

namespace DotNetCompatibility;

/// <summary>
/// Threads: start and join, thread-static state, the thread pool, identities, sleeping and names.
/// </summary>
internal static class ThreadChecks
{
    [ThreadStatic]
    private static int t_value;

    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("Threads", "StartJoin", () =>
        {
            var value = 0;
            var thread = new Thread(() => value = 42);
            thread.Start();
            var joined = thread.Join(TimeSpan.FromSeconds(30));
            return CheckResult.Now(joined && value == 42, $"joined {joined}, value {value}");
        });
        yield return new Check("Threads", "ThreadStatic", () =>
        {
            t_value = 1;
            var other = -1;
            var thread = new Thread(() =>
            {
                other = t_value;
                t_value = 2;
            });
            thread.Start();
            thread.Join();
            return CheckResult.Now(t_value == 1 && other == 0, $"this thread {t_value}, the other saw {other}");
        });
        yield return new Check("Threads", "ThreadPool", () =>
        {
            using var done = new ManualResetEventSlim();
            var pooled = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                pooled = Thread.CurrentThread.IsThreadPoolThread;
                done.Set();
            });
            var ran = done.Wait(TimeSpan.FromSeconds(30));
            return CheckResult.Now(ran && pooled, $"ran {ran} on a pool thread {pooled}");
        });
        yield return new Check("Threads", "DistinctIds", () =>
        {
            var ids = new int[8];
            var threads = Enumerable.Range(0, ids.Length).Select(i => new Thread(() => ids[i] = Environment.CurrentManagedThreadId)).ToArray();
            foreach (var thread in threads)
                thread.Start();
            foreach (var thread in threads)
                thread.Join();
            var distinct = ids.Distinct().Count();
            return CheckResult.Now(distinct == ids.Length, $"{distinct} distinct ids of {ids.Length} threads");
        });
        yield return new Check("Threads", "Sleep", () =>
        {
            var clock = Stopwatch.StartNew();
            Thread.Sleep(50);
            var elapsed = clock.Elapsed.TotalMilliseconds;
            return CheckResult.Now(elapsed >= 45, $"{elapsed:F0} ms for a sleep of 50");
        });
        yield return new Check("Threads", "Name", () =>
        {
            var name = "";
            var thread = new Thread(() => name = Thread.CurrentThread.Name ?? "") { Name = "compatibility worker" };
            thread.Start();
            thread.Join();
            return CheckResult.Now(name == "compatibility worker", $"'{name}'");
        });
    }
}
