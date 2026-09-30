using System.Runtime.CompilerServices;

namespace WitOS.NativeAotBoot;

internal static class MemoryFailureProbe
{
    private static byte[]? PressureRoot;
    private static int PressureFailures;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] Allocate(int bytes) => new byte[bytes];

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool Run()
    {
        // The guest driver sets the actual upstream hard limit to 4 MiB.
        // 16 MiB is a valid array size, so failure must reach real allocation policy.
        var live = Allocate(4096);
        live[0] = 71; live[^1] = 93;
        int failures = 0;
        for (int i = 0; i < 3; ++i)
        {
            try
            {
                var impossible = Allocate(16 * 1024 * 1024);
                GC.KeepAlive(impossible);
                return false;
            }
            catch (OutOfMemoryException error)
            {
                if (error is null || live[0] != 71 || live[^1] != 93) return false;
                ++failures;
            }
            var recovered = Allocate(8192);
            recovered[0] = 17; recovered[^1] = 29;
            int before = GC.CollectionCount(0);
            GC.Collect();
            if (GC.CollectionCount(0) <= before || recovered[0] != 17 || recovered[^1] != 29 || live[0] != 71 || live[^1] != 93)
                return false;
            GC.KeepAlive(recovered);
        }
        PressureRoot = live;
        GC.KeepAlive(live);
        return failures == 3;
    }

    internal static bool FailUnderPressure()
    {
        try { GC.KeepAlive(Allocate(2 * 1024 * 1024)); return false; }
        catch (OutOfMemoryException)
        {
            ++PressureFailures;
            return PressureRoot is not null && PressureRoot[0] == 71 && PressureRoot[^1] == 93;
        }
    }

    internal static bool RecoverAfterPressure()
    {
        var recovered = Allocate(2 * 1024 * 1024);
        recovered[0] = 37; recovered[^1] = 59;
        int before = GC.CollectionCount(0);
        GC.Collect();
        bool valid = PressureFailures == 1 && GC.CollectionCount(0) > before && recovered[0] == 37 && recovered[^1] == 59 &&
            PressureRoot is not null && PressureRoot[0] == 71 && PressureRoot[^1] == 93;
        GC.KeepAlive(recovered);
        return valid;
    }
}
