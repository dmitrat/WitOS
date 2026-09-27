using System.Runtime.CompilerServices;

namespace WitOS.NativeAotBoot;

internal static class Program
{
    private sealed class Box(int value) { internal readonly int Value = value; }
    private static readonly Box Root = Make(87);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Box Make(int value) => new(value);

    // A real executable with standard CoreLib and normal NativeAOT bootstrap.
    // No console, file, network, task or application-created thread dependency.
    private static int Main()
    {
        if (RuntimeFeature.IsDynamicCodeSupported) return 101;
        var local = Make(42);
        var bytes = new byte[4096];
        bytes[0] = 17;
        bytes[^1] = 29;
        GC.Collect();
        var valid = Root.Value == 87 && local.Value == 42 && bytes[0] == 17 && bytes[^1] == 29;
        GC.KeepAlive(local);
        GC.KeepAlive(bytes);
        return valid ? 42 : 102;
    }
}
