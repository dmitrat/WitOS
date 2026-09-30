using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace WitOS.NativeAotBoot;

internal static class ManagedThreadProbe
{
    [ThreadStatic] private static int Local;
    private static Thread? Previous;
    private static readonly object Gate = new();
    private static int Ready, Release, Completed, Failed, WorkerId;
    private sealed class Root(int value) { internal readonly int Value = value; }
    private static bool WaitReady()
    {
        long start = Stopwatch.GetTimestamp();
        while (Volatile.Read(ref Ready) == 0)
        {
            if (Volatile.Read(ref Failed) != 0 || Stopwatch.GetElapsedTime(start).TotalSeconds >= 10) return false;
            Thread.Yield();
        }
        return true;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Work(object? argument)
    {
        try
        {
            int round = (int)argument!;
            if (Local != 0) { Volatile.Write(ref Failed, 1); return; }
            Local = 731 + round;
            WorkerId = Environment.CurrentManagedThreadId;
            var root = new Root(Local);
            var bytes = new byte[2048];
            bytes[0] = 17; bytes[^1] = 29;
            if ((round & 1) == 0)
            {
                Volatile.Write(ref Ready, 1);
                // Actual managed execution must be suspended by GC, not voluntarily parked.
                while (Volatile.Read(ref Release) == 0) { }
            }
            else
            {
                lock (Gate)
                {
                    lock (Gate) // Monitor recursion depth must survive Wait's full release/reacquire.
                    {
                        Volatile.Write(ref Ready, 1);
                        while (Volatile.Read(ref Release) == 0)
                            if (!Monitor.Wait(Gate, 10000)) throw new TimeoutException();
                        if (!Monitor.IsEntered(Gate)) throw new InvalidOperationException();
                    }
                }
            }
            if (root.Value != Local || Local != 731 + round || bytes[0] != 17 || bytes[^1] != 29 ||
                !ExceptionProbe.Run()) Volatile.Write(ref Failed, 1);
            GC.KeepAlive(root); GC.KeepAlive(bytes);
            Interlocked.Increment(ref Completed);
        }
        catch { Volatile.Write(ref Failed, 1); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Round(int round, int mainId)
    {
        Ready = Release = Completed = Failed = WorkerId = 0;
        var thread = new Thread(Work);
        thread.Start(round);
        bool valid = false;
        try
        {
            if (WaitReady() && !thread.Join(0) && WorkerId != mainId && Local == 911)
            {
                if (Previous is { } old && (old.IsAlive || !old.Join(0))) return false;
                if ((round & 1) != 0)
                {
                    long start = Stopwatch.GetTimestamp();
                    while ((thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0 && Volatile.Read(ref Failed) == 0)
                    {
                        if (Stopwatch.GetElapsedTime(start).TotalSeconds >= 10) return false;
                        Thread.Yield();
                    }
                    // Force a scheduling opportunity after the managed wait-state
                    // publication. Kernel diagnostics separately require actual parking.
                    Thread.Sleep(1);
                }
                int before = GC.CollectionCount(0);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                valid = GC.CollectionCount(0) > before;
            }
        }
        finally
        {
            Volatile.Write(ref Release, 1);
            lock (Gate) Monitor.PulseAll(Gate);
            valid &= thread.Join(10000);
        }
        bool passed = valid && !thread.IsAlive && thread.Join(0) && Completed == 1 && Failed == 0 && Local == 911;
        Previous = thread; // Keep the exited observer live through the next slot reuse.
        return passed;
    }

    internal static bool Run()
    {
        int mainId = Environment.CurrentManagedThreadId;
        Local = 911;
        Previous = null;
        lock (Gate)
        {
            lock (Gate)
                if (Monitor.Wait(Gate, 1) || !Monitor.IsEntered(Gate)) return false;
        }
        try { Monitor.Wait(new object(), 0); return false; }
        catch (SynchronizationLockException) { }
        for (int round = 0; round < 4; ++round)
        {
            if (!Round(round, mainId)) return false;
            // Prior rounds become collectible; the latest exited observer stays live for reuse checks.
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Previous = null;
        return Local == 911;
    }
}
