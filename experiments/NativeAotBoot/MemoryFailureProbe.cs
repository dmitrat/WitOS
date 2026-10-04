using System.Runtime.CompilerServices;

namespace WitOS.NativeAotBoot;

/// <summary>
/// Managed out-of-memory recovery under the configured GC hard limit and under backing-page pressure.
/// </summary>
internal static class MemoryFailureProbe
{
    #region Fields

    private static byte[]? m_pressureRoot;

    private static int m_pressureFailures;

    #endregion

    #region Functions

    /// <summary>
    /// Fails three impossible allocations under the hard limit, collecting and recovering after each.
    /// </summary>
    /// <returns>True when all three failures were recoverable.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool Run()
    {
        // The guest driver sets the actual upstream hard limit to 4 MiB.
        // 16 MiB is a valid array size, so failure must reach real allocation policy.
        var live = Allocate(4096);
        live[0] = 71;
        live[^1] = 93;
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
                if (error is null || live[0] != 71 || live[^1] != 93)
                    return false;
                ++failures;
            }
            var recovered = Allocate(8192);
            recovered[0] = 17;
            recovered[^1] = 29;
            int before = GC.CollectionCount(0);
            GC.Collect();
            if (GC.CollectionCount(0) <= before || recovered[0] != 17 || recovered[^1] != 29 || live[0] != 71 || live[^1] != 93)
                return false;
            GC.KeepAlive(recovered);
        }
        m_pressureRoot = live;
        GC.KeepAlive(live);
        return failures == 3;
    }

    /// <summary>
    /// Requires an allocation to fail while backing pages are exhausted.
    /// </summary>
    /// <returns>True when the allocation failed and the retained root is intact.</returns>
    internal static bool FailUnderPressure()
    {
        try
        { GC.KeepAlive(Allocate(2 * 1024 * 1024)); return false; }
        catch (OutOfMemoryException)
        {
            ++m_pressureFailures;
            return m_pressureRoot is not null && m_pressureRoot[0] == 71 && m_pressureRoot[^1] == 93;
        }
    }

    /// <summary>
    /// Requires allocation and collection to work again after the pressure is gone.
    /// </summary>
    /// <returns>True when recovery succeeded.</returns>
    internal static bool RecoverAfterPressure()
    {
        var recovered = Allocate(2 * 1024 * 1024);
        recovered[0] = 37;
        recovered[^1] = 59;
        int before = GC.CollectionCount(0);
        GC.Collect();
        bool valid = m_pressureFailures == 1 && GC.CollectionCount(0) > before && recovered[0] == 37 && recovered[^1] == 59 &&
            m_pressureRoot is not null && m_pressureRoot[0] == 71 && m_pressureRoot[^1] == 93;
        GC.KeepAlive(recovered);
        return valid;
    }

    #endregion

    #region Tools

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] Allocate(int bytes) => new byte[bytes];

    #endregion
}
