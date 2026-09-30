using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace WitOS.NativeAotBoot;

internal static class Program
{
    private sealed class Box(int value) { internal readonly int Value = value; }
    private static readonly Box Root = Make(87);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Box Make(int value) => new(value);

    private static Box? WorkerRoot;
    private static int WorkerCollections;
    private static int SpinReady, SpinStop, SpinSucceeded;

    [UnmanagedCallersOnly]
    private static int Worker(int value)
    {
        if ((value == 200 || value == 201) && (!FaultProbe.Run() || !ExceptionProbe.Run())) return -1;
        if (value == 218) return FallbackSpinner();
        if (value == 219) return FallbackCollector();
        if (value == 217) return StackOverflowProbe.Run();
        if (value == 212) return MemoryFailureProbe.Run() ? 212 : -1;
        if (value == 213) return MemoryFailureProbe.FailUnderPressure() ? 213 : -1;
        if (value == 214) return MemoryFailureProbe.RecoverAfterPressure() ? 214 : -1;
        if (value == 208) return SpinWithRoots();
        if (value == 209) return CollectSpinningWorker();
        if (value == 211) return CollectExitingWorker();
        WorkerRoot = Make(value);
        if (value == 207)
        {
            var local = Make(1207);
            var bytes = new byte[2048];
            bytes[0] = 31;
            bytes[^1] = 53;
            int before = GC.CollectionCount(0);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (GC.CollectionCount(0) <= before || local.Value != 1207 || WorkerRoot.Value != value || bytes[0] != 31 || bytes[^1] != 53)
                return -1;
            GC.KeepAlive(local);
            GC.KeepAlive(bytes);
            WorkerCollections++;
            // Keep the shutdown acceptance context populated after the collection.
            WorkerRoot = Make(value);
        }
        return WorkerRoot.Value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SpinWithRoots()
    {
        var local = Make(3208);
        var bytes = new byte[2048];
        bytes[0] = 67;
        bytes[^1] = 89;
        Volatile.Write(ref SpinReady, 1);
        while (Volatile.Read(ref SpinStop) == 0) { }
        bool valid = local.Value == 3208 && bytes[0] == 67 && bytes[^1] == 89;
        GC.KeepAlive(local);
        GC.KeepAlive(bytes);
        GC.KeepAlive(Make(208)); // Refill the allocation context after the collection.
        Volatile.Write(ref SpinSucceeded, valid ? 1 : -1);
        return valid ? 208 : -1;
    }

    private static int CollectSpinningWorker()
    {
        while (Volatile.Read(ref SpinReady) == 0) { }
        int before = GC.CollectionCount(0);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        if (GC.CollectionCount(0) <= before) return -1;
        WorkerRoot = Make(209);
        WorkerCollections++;
        Volatile.Write(ref SpinStop, 1);
        return 209;
    }

    private static int FallbackReady, FallbackStop, FallbackResult;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Box FallbackReturn(Box root)
    {
        // Repeated real returns let the upstream return-address hijack run.
        // Keep this method call/GC-poll free while the timer captures its frame.
        for (int i = 0; i < 1000000 && Volatile.Read(ref FallbackStop) == 0; ++i) { }
        return root;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int FallbackSpinner()
    {
        var root = Make(3218);
        var bytes = new byte[2048]; bytes[0] = 71; bytes[^1] = 93;
        Volatile.Write(ref FallbackReady, 1);
        while (Volatile.Read(ref FallbackStop) == 0)
            if (FallbackReturn(root).Value != 3218) return -1;
        bool valid = root.Value == 3218 && bytes[0] == 71 && bytes[^1] == 93;
        GC.KeepAlive(root); GC.KeepAlive(bytes); GC.KeepAlive(Make(218));
        Volatile.Write(ref FallbackResult, valid ? 1 : -1);
        return valid ? 218 : -1;
    }

    private static int FallbackCollector()
    {
        while (Volatile.Read(ref FallbackReady) == 0) { }
        int before = GC.CollectionCount(0);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        if (GC.CollectionCount(0) <= before) return -1;
        WorkerRoot = Make(219); WorkerCollections++;
        Volatile.Write(ref FallbackStop, 1);
        return 219;
    }

    private static int CollectExitingWorker()
    {
        int before = GC.CollectionCount(0);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        if (GC.CollectionCount(0) <= before || WorkerRoot?.Value != 210) return -1;
        WorkerRoot = Make(211);
        WorkerCollections++;
        return 211;
    }

    // A real executable with standard CoreLib and normal NativeAOT bootstrap.
    // Optional private native worker fixture; no custom CoreLib or managed thread API.
    private static unsafe int Main(string[] args)
    {
        if (RuntimeFeature.IsDynamicCodeSupported) return 101;
        var local = Make(42);
        var bytes = new byte[4096];
        bytes[0] = 17;
        bytes[^1] = 29;
        delegate* unmanaged<delegate* unmanaged<int, int>, int> lifecycle = null;
        if (args.Length != 0)
        {
            // Private bring-up fixture passed by the native driver, not an app API.
            if (args.Length != 1 || args[0].Length != 16) return 103;
            nuint address = 0;
            foreach (char c in args[0])
            {
                int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (digit < 0) return 104;
                address = (address << 4) | (nuint)digit;
            }
            lifecycle = (delegate* unmanaged<delegate* unmanaged<int, int>, int>)address;
            if (address == 0 || lifecycle(&Worker) != 42 || WorkerRoot?.Value != 211 || WorkerCollections != 4 || Volatile.Read(ref SpinSucceeded) != 1 || Volatile.Read(ref FallbackResult) != 1) return 105;
        }
        for (int cycle = 0; cycle < 4; ++cycle)
        {
            if (!FaultProbe.Run()) return 106;
            if (!ExceptionProbe.Run()) return 107;
            if (!FinalizationProbe.Run()) return 108;
            if (!ManagedThreadProbe.Run()) return 110;
            if (!ThreadQuotaProbe.Run(args.Length != 0)) return 112;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (lifecycle != null && lifecycle(null) != 42) return 114;
            if (!GuestReport.Write("[RUNTIME] integration cycle passed\n"u8)) return 115;
        }
        if (!GuestReport.Write("[RUNTIME] managed finalization passed: 48 releases + 8 resurrection passes + suppression\n"u8)) return 109;
        if (!GuestReport.Write("[RUNTIME] managed threads passed: 28 (Thread/Join/Monitor/TLS/GC)\n"u8)) return 111;
        if (!GuestReport.Write(args.Length != 0
            ? "[RUNTIME] managed thread quota recovery passed: 4\n"u8
            : "[RUNTIME] managed thread capacity reference passed: 4\n"u8)) return 113;
        int before = GC.CollectionCount(0);
        GC.Collect();
        var valid = GC.CollectionCount(0) > before && Root.Value == 87 && local.Value == 42 && bytes[0] == 17 && bytes[^1] == 29 && (args.Length == 0 || WorkerRoot?.Value == 211);
        GC.KeepAlive(local);
        GC.KeepAlive(bytes);
        return valid ? 42 : 102;
    }
}
