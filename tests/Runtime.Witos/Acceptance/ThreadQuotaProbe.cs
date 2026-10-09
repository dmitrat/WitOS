// Carried from experiments/NativeAotBoot, the M3 acceptance of the frozen line, to NativeAOT's Unix form (plan step R2.2).
using System.Runtime.CompilerServices;
using System.Threading;
namespace WitOS.Acceptance;

/// <summary>
/// Managed thread creation at the thread quota: refusal, recovery and reuse after workers exit.
/// </summary>
internal static class ThreadQuotaProbe
{
    #region Fields

    private static readonly object GATE = new();

    private static int m_ready, m_completed, m_failed, m_late;

    private static bool m_release;

    #endregion

    #region Types

    private sealed class Root(int value) { internal readonly int Value = value; }

    #endregion

    #region Functions

    /// <summary>
    /// Runs the thread quota case.
    /// </summary>
    /// <param name="boundedGuest">Whether the guest thread quota applies; false for the Windows reference.</param>
    /// <returns>True when refusal and recovery behaved as expected.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool Run(bool boundedGuest)
    {
        m_ready = m_completed = m_failed = m_late = 0;
        m_release = false;
        var first = new Thread(Parked);
        var second = new Thread(Parked);
        var retry = new Thread(AfterRecovery);
        first.Start(731);
        second.Start(913);
        bool valid = false;
        try
        {
            lock (GATE)
            {
                while (m_ready != 2)
                    if (!Monitor.Wait(GATE, 10000))
                        return false;
            }
            if (boundedGuest)
            {
                try
                { retry.Start(); return false; }
                catch (OutOfMemoryException)
                {
                    if (retry.IsAlive || (retry.ThreadState & ThreadState.Unstarted) == 0 || Volatile.Read(ref m_late) != 0)
                        return false;
                }
            }
            else
            {
                retry.Start();
                if (!retry.Join(10000) || Volatile.Read(ref m_late) != 1)
                    return false;
            }
            int before = GC.CollectionCount(0);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            valid = GC.CollectionCount(0) > before;
        }
        finally
        {
            lock (GATE)
            { m_release = true; Monitor.PulseAll(GATE); }
            valid &= first.Join(10000) && second.Join(10000);
        }
        if (boundedGuest)
        { retry.Start(); if (!retry.Join(10000)) return false; }
        return valid && m_completed == 2 && m_failed == 0 && m_late == 1 && !first.IsAlive && !second.IsAlive && !retry.IsAlive;
    }

    #endregion

    #region Tools

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Parked(object? argument)
    {
        try
        {
            var root = new Root((int)argument!);
            var bytes = new byte[2048];
            bytes[0] = 17;
            bytes[^1] = 29;
            lock (GATE)
            {
                ++m_ready;
                Monitor.PulseAll(GATE);
                while (!m_release)
                    if (!Monitor.Wait(GATE, 10000))
                        throw new TimeoutException();
            }
            if (root.Value != (int)argument! || bytes[0] != 17 || bytes[^1] != 29)
                Interlocked.Exchange(ref m_failed, 1);
            GC.KeepAlive(root);
            GC.KeepAlive(bytes);
            Interlocked.Increment(ref m_completed);
        }
        catch { Interlocked.Exchange(ref m_failed, 1); }
    }

    private static void AfterRecovery() { GC.Collect(); Interlocked.Increment(ref m_late); }

    #endregion
}
