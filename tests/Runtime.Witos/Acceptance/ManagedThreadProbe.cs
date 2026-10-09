// Carried from experiments/NativeAotBoot, the M3 acceptance of the frozen line, to NativeAOT's Unix form (plan step R2.2).
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace WitOS.Acceptance;

/// <summary>
/// Managed threads: start, join, Monitor waits, thread-static data and GC with parked threads.
/// </summary>
internal static class ManagedThreadProbe
{
    #region Fields

    [ThreadStatic] private static int m_local;

    private static Thread? m_previous;

    private static readonly object GATE = new();

    private static int m_ready, m_release, m_completed, m_failed, m_workerId;

    #endregion

    #region Types

    private sealed class Root(int value) { internal readonly int Value = value; }

    #endregion

    #region Functions

    /// <summary>
    /// Runs the managed thread cases.
    /// </summary>
    /// <returns>True when every case passed.</returns>
    internal static bool Run()
    {
        int mainId = Environment.CurrentManagedThreadId;
        m_local = 911;
        m_previous = null;
        lock (GATE)
        {
            lock (GATE)
                if (Monitor.Wait(GATE, 1) || !Monitor.IsEntered(GATE))
                    return false;
        }
        try
        { Monitor.Wait(new object(), 0); return false; }
        catch (SynchronizationLockException) { }
        for (int round = 0; round < 4; ++round)
        {
            if (!Round(round, mainId))
                return false;
            // Prior rounds become collectible; the latest exited observer stays live for reuse checks.
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        m_previous = null;
        return m_local == 911;
    }

    #endregion

    #region Tools

    private static bool WaitReady()
    {
        long start = Stopwatch.GetTimestamp();
        while (Volatile.Read(ref m_ready) == 0)
        {
            if (Volatile.Read(ref m_failed) != 0 || Stopwatch.GetElapsedTime(start).TotalSeconds >= 10)
                return false;
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
            if (m_local != 0)
            { Volatile.Write(ref m_failed, 1); return; }
            m_local = 731 + round;
            m_workerId = Environment.CurrentManagedThreadId;
            var root = new Root(m_local);
            var bytes = new byte[2048];
            bytes[0] = 17;
            bytes[^1] = 29;
            if ((round & 1) == 0)
            {
                Volatile.Write(ref m_ready, 1);
                // Actual managed execution must be suspended by GC, not voluntarily parked.
                while (Volatile.Read(ref m_release) == 0)
                { }
            }
            else
            {
                lock (GATE)
                {
                    lock (GATE) // Monitor recursion depth must survive Wait's full release/reacquire.
                    {
                        Volatile.Write(ref m_ready, 1);
                        while (Volatile.Read(ref m_release) == 0)
                            if (!Monitor.Wait(GATE, 10000))
                                throw new TimeoutException();
                        if (!Monitor.IsEntered(GATE))
                            throw new InvalidOperationException();
                    }
                }
            }
            if (root.Value != m_local || m_local != 731 + round || bytes[0] != 17 || bytes[^1] != 29 ||
                !ExceptionProbe.Run())
                Volatile.Write(ref m_failed, 1);
            GC.KeepAlive(root);
            GC.KeepAlive(bytes);
            Interlocked.Increment(ref m_completed);
        }
        catch { Volatile.Write(ref m_failed, 1); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Round(int round, int mainId)
    {
        m_ready = m_release = m_completed = m_failed = m_workerId = 0;
        var thread = new Thread(Work);
        thread.Start(round);
        bool valid = false;
        try
        {
            if (WaitReady() && !thread.Join(0) && m_workerId != mainId && m_local == 911)
            {
                if (m_previous is { } old && (old.IsAlive || !old.Join(0)))
                    return false;
                if ((round & 1) != 0)
                {
                    long start = Stopwatch.GetTimestamp();
                    while ((thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0 && Volatile.Read(ref m_failed) == 0)
                    {
                        if (Stopwatch.GetElapsedTime(start).TotalSeconds >= 10)
                            return false;
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
            Volatile.Write(ref m_release, 1);
            lock (GATE)
                Monitor.PulseAll(GATE);
            valid &= thread.Join(10000);
        }
        bool passed = valid && !thread.IsAlive && thread.Join(0) && m_completed == 1 && m_failed == 0 && m_local == 911;
        m_previous = thread; // Keep the exited observer live through the next slot reuse.
        return passed;
    }

    #endregion
}
