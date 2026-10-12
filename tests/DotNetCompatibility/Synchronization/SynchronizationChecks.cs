namespace DotNetCompatibility;

/// <summary>
/// Synchronization: monitors under contention, semaphores, events, interlocked operations, reader-writer locks,
/// barriers, countdowns and a named mutex.
/// </summary>
internal static class SynchronizationChecks
{
    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("Synchronization", "Monitor", () =>
        {
            var gate = new object();
            var count = 0;
            Run(4, () =>
            {
                for (var i = 0; i < 10_000; i++)
                {
                    lock (gate)
                        count++;
                }
            });
            return CheckResult.Now(count == 40_000, $"{count}");
        });
        yield return new Check("Synchronization", "SemaphoreSlim", () =>
        {
            using var semaphore = new SemaphoreSlim(2);
            int inside = 0, most = 0;
            Run(6, () =>
            {
                semaphore.Wait();
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref most, now);
                Thread.Sleep(10);
                Interlocked.Decrement(ref inside);
                semaphore.Release();
            });
            return CheckResult.Now(most is >= 1 and <= 2, $"at most {most} inside");
        });
        yield return new Check("Synchronization", "ManualResetEventSlim", () =>
        {
            using var signal = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                Thread.Sleep(20);
                signal.Set();
            });
            thread.Start();
            var set = signal.Wait(TimeSpan.FromSeconds(30));
            thread.Join();
            return CheckResult.Now(set, $"set {set}");
        });
        yield return new Check("Synchronization", "Interlocked", () =>
        {
            long value = 0;
            Run(4, () =>
            {
                for (var i = 0; i < 25_000; i++)
                    Interlocked.Increment(ref value);
            });
            var exchanged = Interlocked.CompareExchange(ref value, 1, 100_000);
            return CheckResult.Now(exchanged == 100_000 && value == 1, $"{exchanged}, then {value}");
        });
        yield return new Check("Synchronization", "ReaderWriterLock", () =>
        {
            using var rw = new ReaderWriterLockSlim();
            var shared = 0;
            Run(4, () =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    rw.EnterWriteLock();
                    shared++;
                    rw.ExitWriteLock();
                    rw.EnterReadLock();
                    _ = shared;
                    rw.ExitReadLock();
                }
            });
            return CheckResult.Now(shared == 4_000, $"{shared}");
        });
        yield return new Check("Synchronization", "Barrier", () =>
        {
            var phases = 0;
            using var barrier = new Barrier(3, _ => Interlocked.Increment(ref phases));
            Run(3, () =>
            {
                barrier.SignalAndWait();
                barrier.SignalAndWait();
            });
            return CheckResult.Now(phases == 2, $"{phases} phases");
        });
        yield return new Check("Synchronization", "CountdownEvent", () =>
        {
            using var countdown = new CountdownEvent(5);
            for (var i = 0; i < 5; i++)
                ThreadPool.QueueUserWorkItem(_ => countdown.Signal());
            var done = countdown.Wait(TimeSpan.FromSeconds(30));
            return CheckResult.Now(done, $"counted down {done}");
        });
        yield return new Check("Synchronization", "NamedMutex", () =>
        {
            using var mutex = new Mutex(false, $"witos-compatibility-{Environment.ProcessId}", out var created);
            var taken = mutex.WaitOne(TimeSpan.FromSeconds(10));
            if (taken)
                mutex.ReleaseMutex();
            return CheckResult.Now(created && taken, $"created {created}, taken {taken}");
        });
    }

    // Runs the work on several threads at once and waits for all of them.
    private static void Run(int threads, Action work)
    {
        var started = Enumerable.Range(0, threads).Select(_ => new Thread(() => work())).ToArray();
        foreach (var thread in started)
            thread.Start();
        foreach (var thread in started)
            thread.Join();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
