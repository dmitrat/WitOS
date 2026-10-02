using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WitOS.NativeAotTarget;

/// <summary>
/// Native exports of the NativeAOT target library that the native host calls.
/// </summary>
/// <remarks>
/// Ordinary upstream CoreLib, GC and exception machinery. No substitute runtime.
/// </remarks>
public static class Exports
{
    #region Fields

    private static readonly Payload ROOT = new(0x57);

    [ThreadStatic] private static int m_sequence;

    #endregion

    #region Types

    private sealed class Payload(int value)
    {
        #region Fields

        public readonly int Value = value;

        public Payload? Next;

        #endregion
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reports the runtime version.
    /// </summary>
    /// <returns>Major * 10000 + Minor * 100 + Build.</returns>
    [UnmanagedCallersOnly(EntryPoint = "witos_target_version")]
    public static int Version()
    {
        var version = Environment.Version;
        return version.Major * 10000 + version.Minor * 100 + version.Build;
    }

    /// <summary>
    /// Allocates, collects and checks a small object graph and a large array.
    /// </summary>
    /// <param name="seed">Value stored in the graph.</param>
    /// <returns>A value derived from the seed, or -1 on failure.</returns>
    [UnmanagedCallersOnly(EntryPoint = "witos_target_probe")]
    public static int Probe(int seed)
    {
        if (RuntimeFeature.IsDynamicCodeSupported || ROOT.Value != 0x57)
            return -1;
        var count = ++m_sequence;
        var payload = new Payload(seed) { Next = new Payload(seed ^ 0x55) };
        var large = new byte[128 * 1024];
        large[0] = (byte)seed;
        large[^1] = (byte)(seed ^ 0x55);
        GC.Collect();
        var caught = false;
        var finallyRan = false;
        try
        { ThrowMarker(); }
        catch (InvalidOperationException) { caught = true; }
        finally { finallyRan = true; }
        var valid = payload.Value == seed && payload.Next.Value == (seed ^ 0x55) &&
            large[0] == (byte)seed && large[^1] == (byte)(seed ^ 0x55) && caught && finallyRan;
        GC.KeepAlive(payload);
        GC.KeepAlive(large);
        return valid ? 0x10000 | (count << 8) | (seed & 255) : -2;
    }

    #endregion

    #region Tools

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMarker() => throw new InvalidOperationException("target boundary");

    #endregion
}
