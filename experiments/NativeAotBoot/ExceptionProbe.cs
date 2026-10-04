using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WitOS.NativeAotBoot;

/// <summary>
/// Managed exception dispatch, filters, finally ordering and GC during unwinding.
/// </summary>
/// <remarks>
/// Same managed cases run in the Windows reference and the guest. The explicit
/// native allocation exercises existing platform bindings, not a replacement BCL.
/// </remarks>
internal static unsafe class ExceptionProbe
{
    #region Types

    private sealed class Payload(int value) { internal readonly int Value = value; }

    private sealed class ProbeException(Payload payload) : Exception
    {
        internal readonly Payload Payload = payload;
    }

    private sealed class Trace
    {
        #region Fields

        internal ulong Steps;

        internal int Collections, Releases;

        internal bool Valid = true;

        #endregion

        #region Functions

        /// <summary>
        /// Appends one step to the recorded order as a hexadecimal digit.
        /// </summary>
        /// <param name="value">Step number, 0 to 15.</param>
        internal void Step(uint value) => Steps = Steps * 16 + value;

        #endregion
    }

    #endregion

    #region Functions

    /// <summary>
    /// Runs four rounds of the exception cases.
    /// </summary>
    /// <returns>True when every round passed.</returns>
    internal static bool Run()
    {
        for (int round = 0; round < 4; ++round)
            if (!Round())
                return false;
        return true;
    }

    #endregion

    #region Tools

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void* VirtualAlloc(void* address, nuint bytes, uint kind, uint protection);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int VirtualFree(void* address, nuint bytes, uint kind);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect(Trace trace, Payload root, ProbeException error)
    {
        int before = GC.CollectionCount(0);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        trace.Valid &= GC.CollectionCount(0) > before && root.Value == 731 &&
            ReferenceEquals(root, error.Payload);
        ++trace.Collections;
        GC.KeepAlive(root);
        GC.KeepAlive(error);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Filter(Trace trace, Payload root, ProbeException error, bool throws)
    {
        trace.Step(throws ? 2U : 3U);
        Collect(trace, root, error);
        if (throws)
            throw new InvalidOperationException(); // CLR must treat this filter as false.
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowWithResource(Trace trace, Payload root, ProbeException error)
    {
        byte* memory = (byte*)VirtualAlloc(null, 4096, 0x3000, 4);
        if (memory == null)
        { trace.Valid = false; throw error; }
        memory[0] = 17;
        memory[4095] = 29;
        try
        {
            trace.Step(1);
            throw error;
        }
        finally
        {
            // Filters ran in the first pass; release occurs in the unwind pass.
            trace.Step(4);
            Collect(trace, root, error);
            trace.Valid &= memory[0] == 17 && memory[4095] == 29;
            if (VirtualFree(memory, 0, 0x8000) != 0)
                ++trace.Releases;
            else
                trace.Valid = false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Round()
    {
        var root = new Payload(731);
        var error = new ProbeException(root);
        var trace = new Trace();
        try
        {
            try
            {
                try
                { ThrowWithResource(trace, root, error); }
                catch (ProbeException caught) when (Filter(trace, root, caught, true))
                { trace.Valid = false; }
                catch (ProbeException caught) when (Filter(trace, root, caught, false))
                {
                    trace.Step(5);
                    trace.Valid &= ReferenceEquals(caught, error);
                    Collect(trace, root, caught);
                    throw; // Preserve exception identity through a second unwind.
                }
                finally
                {
                    trace.Step(6);
                    try
                    { throw new ArgumentException(); }
                    catch (ArgumentException) { trace.Step(7); Collect(trace, root, error); }
                    finally { trace.Step(8); }
                }
            }
            catch (ProbeException caught)
            {
                trace.Step(9);
                trace.Valid &= ReferenceEquals(caught, error);
                Collect(trace, root, caught);
            }
        }
        finally { trace.Step(10); }
        GC.KeepAlive(root);
        GC.KeepAlive(error);
        return trace.Valid && trace.Steps == 0x123456789AUL && trace.Collections == 6 && trace.Releases == 1;
    }

    #endregion
}
