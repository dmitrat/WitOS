using System.Runtime.CompilerServices;

namespace WitOS.Acceptance;

/// <summary>
/// An allocation the GC cannot serve throws OutOfMemoryException, and the process allocates on after it.
/// </summary>
/// <remarks>
/// The frozen line's MemoryFailureProbe relied on a 4 MiB hard limit its native driver set; a witos program runs with
/// the GC's defaults, so the request is larger than the system layer's whole data arena.
/// </remarks>
internal static class OutOfMemoryProbe
{
    #region Functions

    /// <summary>
    /// Fails three impossible allocations, then allocates and collects.
    /// </summary>
    /// <returns>True when every failure was OutOfMemoryException and the process recovered.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool Run()
    {
        var live = Allocate(512);
        live[0] = 71;
        live[^1] = 93;
        int failures = 0;
        for (int i = 0; i < 3; ++i)
        {
            try
            {
                GC.KeepAlive(Allocate(int.MaxValue / 2));
                return false;
            }
            catch (OutOfMemoryException)
            {
                ++failures;
            }
        }
        int before = GC.CollectionCount(0);
        GC.Collect();
        var after = Allocate(1024);
        return failures == 3 && GC.CollectionCount(0) > before && after.Length == 1024 && live[0] == 71 && live[^1] == 93;
    }

    #endregion

    #region Tools

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long[] Allocate(int count) => new long[count];

    #endregion
}
