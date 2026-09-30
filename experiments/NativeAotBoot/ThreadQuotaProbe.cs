using System.Runtime.CompilerServices;
using System.Threading;
namespace WitOS.NativeAotBoot;
internal static class ThreadQuotaProbe
{
    private static readonly object Gate = new();
    private static int Ready, Completed, Failed, Late;
    private static bool Release;
    private sealed class Root(int value) { internal readonly int Value = value; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Parked(object? argument)
    {
        try
        {
            var root = new Root((int)argument!);
            var bytes = new byte[2048]; bytes[0] = 17; bytes[^1] = 29;
            lock (Gate)
            {
                ++Ready; Monitor.PulseAll(Gate);
                while (!Release) if (!Monitor.Wait(Gate,10000)) throw new TimeoutException();
            }
            if (root.Value != (int)argument! || bytes[0] != 17 || bytes[^1] != 29) Interlocked.Exchange(ref Failed,1);
            GC.KeepAlive(root); GC.KeepAlive(bytes);
            Interlocked.Increment(ref Completed);
        }
        catch { Interlocked.Exchange(ref Failed,1); }
    }
    private static void AfterRecovery() { GC.Collect(); Interlocked.Increment(ref Late); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool Run(bool boundedGuest)
    {
        Ready = Completed = Failed = Late = 0; Release = false;
        var first = new Thread(Parked); var second = new Thread(Parked); var retry = new Thread(AfterRecovery);
        first.Start(731); second.Start(913);
        bool valid = false;
        try
        {
            lock (Gate)
            {
                while (Ready != 2) if (!Monitor.Wait(Gate,10000)) return false;
            }
            if (boundedGuest)
            {
                try { retry.Start(); return false; }
                catch (OutOfMemoryException)
                {
                    if (retry.IsAlive || (retry.ThreadState & ThreadState.Unstarted) == 0 || Volatile.Read(ref Late) != 0) return false;
                }
            }
            else
            {
                retry.Start();
                if (!retry.Join(10000) || Volatile.Read(ref Late) != 1) return false;
            }
            int before = GC.CollectionCount(0);
            GC.Collect(GC.MaxGeneration,GCCollectionMode.Forced,blocking:true,compacting:true);
            valid = GC.CollectionCount(0) > before;
        }
        finally
        {
            lock (Gate) { Release = true; Monitor.PulseAll(Gate); }
            valid &= first.Join(10000) && second.Join(10000);
        }
        if (boundedGuest) { retry.Start(); if (!retry.Join(10000)) return false; }
        return valid && Completed == 2 && Failed == 0 && Late == 1 && !first.IsAlive && !second.IsAlive && !retry.IsAlive;
    }
}
