using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WitOS.NativeAotTarget;

// Ordinary upstream CoreLib, GC and exception machinery. No substitute runtime.
public static class Exports
{
    private sealed class Payload(int value)
    {
        public readonly int Value = value;
        public Payload? Next;
    }

    private static readonly Payload Root = new(0x57);
    [ThreadStatic] private static int sequence;

    [UnmanagedCallersOnly(EntryPoint = "witos_target_version")]
    public static int Version()
    {
        var version = Environment.Version;
        return version.Major * 10000 + version.Minor * 100 + version.Build;
    }

    [UnmanagedCallersOnly(EntryPoint = "witos_target_probe")]
    public static int Probe(int seed)
    {
        if (RuntimeFeature.IsDynamicCodeSupported || Root.Value != 0x57)
            return -1;
        var count = ++sequence;
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMarker() => throw new InvalidOperationException("target boundary");
}
