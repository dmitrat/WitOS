using System.Runtime.CompilerServices;
namespace WitOS.NativeAotBoot;

internal static unsafe class StackOverflowProbe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadEnds(byte* memory) => memory[0] + memory[4095];
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Recurse(int depth)
    {
        if (depth >= 256)
            return -1; // A missing stack guard must fail, not spin forever.
        byte* memory = stackalloc byte[4096];
        memory[0] = 17;
        memory[4095] = 29;
        if (!GuestReport.Write("[RUNTIME] managed stack frame\n"u8))
            return -2;
        int result = Recurse(depth + 1);
        return result + ReadEnds(memory); // Escape the span and prevent a tail call.
    }
    internal static int Run()
    {
        try
        { return Recurse(1); }
        finally { _ = GuestReport.Write("[RUNTIME] unexpected stack-finally cleanup\n"u8); }
    }
}
