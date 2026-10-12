using System.Runtime;
using System.Runtime.CompilerServices;

namespace DotNetCompatibility;

/// <summary>
/// The garbage collector: weak references, finalizers, a large object, the generations and the mode.
/// </summary>
internal static class GcChecks
{
    private static volatile bool s_finalized;

    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("GC", "WeakReference", () =>
        {
            var weak = Abandoned();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return CheckResult.Now(!weak.IsAlive, $"alive {weak.IsAlive}");
        });
        yield return new Check("GC", "Finalizer", () =>
        {
            Finalizable();
            for (var i = 0; i < 10 && !s_finalized; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return CheckResult.Now(s_finalized, $"finalized {s_finalized}");
        });
        yield return new Check("GC", "LargeObject", () =>
        {
            var bytes = new byte[32 * 1024 * 1024];
            bytes[^1] = 1;
            var total = GC.GetTotalMemory(false);
            var generation = GC.GetGeneration(bytes);
            GC.KeepAlive(bytes);
            return CheckResult.Now(total >= bytes.Length && generation == GC.MaxGeneration,
                $"{total / (1024 * 1024)} MiB in use, the array in generation {generation}");
        });
        yield return new Check("GC", "Generations", () =>
            CheckResult.Now(GC.MaxGeneration == 2, $"{GC.MaxGeneration}"));
        yield return new Check("GC", "Mode", () =>
            CheckResult.Now(!GCSettings.IsServerGC, $"server {GCSettings.IsServerGC}, latency {GCSettings.LatencyMode}"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandoned() => new(new object());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Finalizable() => _ = new Finalized();

    private sealed class Finalized
    {
        ~Finalized() => s_finalized = true;
    }
}
