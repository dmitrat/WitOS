using System.Runtime.CompilerServices;
namespace WitOS.NativeAotBoot;

/// <summary>
/// Recurses with large stack frames until the fixed stack guard faults; the fault must be contained.
/// </summary>
internal static unsafe class StackOverflowProbe
{
    #region Functions

    /// <summary>
    /// Starts the recursion; returning at all means the stack guard did not fault.
    /// </summary>
    /// <returns>-1 when the depth limit was reached without a fault.</returns>
    internal static int Run()
    {
        try
        { return Recurse(1); }
        finally { _ = GuestReport.Write("[RUNTIME] unexpected stack-finally cleanup\n"u8); }
    }

    #endregion

    #region Tools

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

    #endregion
}
