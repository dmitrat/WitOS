using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WitOS.Acceptance;

/// <summary>
/// The M3 acceptance of NativeAOT's Unix form on WitOS (plan step R2.2), against the NativeAOT CoreLib for witos alone.
/// </summary>
/// <remarks>
/// Four cycles run the probes of the frozen line's acceptance (hardware faults, exception dispatch, finalization,
/// managed threads, thread creation beside parked threads) and three of the Unix form's (an allocation the GC cannot
/// serve, the thread pool, waits). Each run prints one line through the libc's write; the last line counts the runs,
/// and Main returns 0 only when every run passed and the program's roots survived.
/// </remarks>
internal static class Program
{
    #region Constants

    private const int CYCLES = 4;

    #endregion

    #region Fields

    private static readonly Box ROOT = Make(87);

    private static int m_passed, m_failed;

    #endregion

    #region Types

    private sealed class Box(int value) { internal readonly int Value = value; }

    #endregion

    #region Functions

    private static int Main()
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
            return 101;
        var local = Make(42);
        var bytes = new byte[4096];
        bytes[0] = 17;
        bytes[^1] = 29;
        for (int cycle = 1; cycle <= CYCLES; ++cycle)
        {
            Run(cycle, "faults", FaultProbe.Run);
            Run(cycle, "exceptions", ExceptionProbe.Run);
            Run(cycle, "finalization", FinalizationProbe.Run);
            Run(cycle, "managed threads", ManagedThreadProbe.Run);
            Run(cycle, "thread creation", () => ThreadQuotaProbe.Run(false));
            Run(cycle, "out of memory", OutOfMemoryProbe.Run);
            Run(cycle, "thread pool", ThreadPoolProbe.Run);
            Run(cycle, "waits", WaitProbe.Run);
        }
        int before = GC.CollectionCount(0);
        GC.Collect();
        bool roots = GC.CollectionCount(0) > before && ROOT.Value == 87 && local.Value == 42 && bytes[0] == 17 && bytes[^1] == 29;
        GC.KeepAlive(local);
        GC.KeepAlive(bytes);
        if (!roots)
            ++m_failed;
        Libc.Line($"[M3] NativeAOT on {Isa()}: {m_passed} runs passed, {m_failed} failed");
        return m_failed == 0 ? 0 : 1;
    }

    #endregion

    #region Tools

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Box Make(int value) => new(value);

    private static void Run(int cycle, string name, Func<bool> probe)
    {
        string result;
        try
        {
            result = probe() ? "ok" : "FAILED";
        }
        catch (Exception exception)
        {
            result = $"threw {exception.GetType().Name}";
        }
        if (result == "ok")
            ++m_passed;
        else
            ++m_failed;
        Libc.Line($"[M3] cycle {cycle} {name}: {result}");
    }

    private static string Isa() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        var other => other.ToString()
    };

    #endregion
}
