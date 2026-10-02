using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace WitOS.NativeAotBoot;

internal static unsafe class FinalizationProbe
{
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void* VirtualAlloc(void* address, nuint bytes, uint kind, uint protection);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int VirtualFree(void* address, nuint bytes, uint kind);

    private static int Finalized, Released, Suppressed, Resurrections, Failed;
    private static uint CallerThread, FinalizerThread;
    private static Resurrecting? Survivor;
    private sealed class Payload { internal readonly int Value = 731; }

    private static void CheckThread()
    {
        uint id = GetCurrentThreadId();
        if (id == 0 || id == CallerThread || (FinalizerThread != 0 && id != FinalizerThread))
            Volatile.Write(ref Failed, 1);
        FinalizerThread = id;
    }

    private sealed class Resource
    {
        private readonly Payload Root = new();
        private byte* Memory;
        internal Resource()
        {
            Memory = (byte*)VirtualAlloc(null, 4096, 0x3000, 4);
            if (Memory == null)
                throw new OutOfMemoryException();
            Memory[0] = 17;
            Memory[4095] = 29;
        }
        internal void Release()
        {
            byte* memory = Memory;
            Memory = null;
            if (memory == null || memory[0] != 17 || memory[4095] != 29 ||
                VirtualFree(memory, 0, 0x8000) == 0)
                Volatile.Write(ref Failed, 1);
            else
                Interlocked.Increment(ref Released);
        }
        ~Resource()
        {
            CheckThread();
            Release();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (Root.Value != 731)
                Volatile.Write(ref Failed, 1);
            GC.KeepAlive(Root);
            Interlocked.Increment(ref Finalized);
        }
    }
    private sealed class SuppressedObject
    {
        ~SuppressedObject() { Interlocked.Increment(ref Suppressed); }
    }
    private sealed class Resurrecting
    {
        private int Pass;
        internal readonly Payload Root = new();
        ~Resurrecting()
        {
            CheckThread();
            if (Root.Value != 731)
                Volatile.Write(ref Failed, 1);
            Interlocked.Increment(ref Resurrections);
            if (++Pass == 1)
            {
                Survivor = this;
                GC.ReRegisterForFinalize(this);
            }
            else if (Pass != 2)
                Volatile.Write(ref Failed, 1);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateWave() { for (int n = 0; n < 4; ++n) _ = new Resource(); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateSuppressed() { var item = new SuppressedObject(); GC.SuppressFinalize(item); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateResurrecting() { _ = new Resurrecting(); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasSurvivor() => Survivor?.Root.Value == 731;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReleaseSurvivor() { Survivor = null; }
    private static void Drain()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    internal static bool Run()
    {
        Finalized = Released = Suppressed = Resurrections = Failed = 0;
        FinalizerThread = 0;
        CallerThread = GetCurrentThreadId();
        CreateSuppressed();
        for (int wave = 1; wave <= 3; ++wave)
        {
            CreateWave();
            Drain();
            if (Volatile.Read(ref Finalized) != wave * 4 || Volatile.Read(ref Released) != wave * 4 ||
                Volatile.Read(ref Failed) != 0 || Volatile.Read(ref Suppressed) != 0)
                return false;
        }
        CreateResurrecting();
        Drain();
        if (Volatile.Read(ref Resurrections) != 1 || !HasSurvivor())
            return false;
        Drain(); // A resurrected, strongly rooted object must not finalize again.
        if (Volatile.Read(ref Resurrections) != 1)
            return false;
        ReleaseSurvivor();
        Drain();
        if (Volatile.Read(ref Resurrections) != 2)
            return false;
        Drain();
        GC.WaitForPendingFinalizers(); // Empty-queue handshake must also complete.
        return FinalizerThread != 0 && Volatile.Read(ref Failed) == 0 && Volatile.Read(ref Suppressed) == 0 &&
            Volatile.Read(ref Finalized) == 12 && Volatile.Read(ref Released) == 12 &&
            Volatile.Read(ref Resurrections) == 2 && !HasSurvivor();
    }
}
