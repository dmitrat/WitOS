// Carried from experiments/NativeAotBoot, the M3 acceptance of the frozen line, to NativeAOT's Unix form (plan step R2.2).
using System.Runtime.CompilerServices;
using System.Threading;

namespace WitOS.Acceptance;

/// <summary>
/// Finalization on the finalizer thread: native resource release, resurrection, re-registration and suppression.
/// </summary>
/// <remarks>
/// The native page is the libc's mmap and the thread identity its gettid, where the frozen line's were kernel32's.
/// </remarks>
internal static unsafe class FinalizationProbe
{
    #region Fields

    private static int m_finalized, m_released, m_suppressed, m_resurrections, m_failed;

    private static uint m_callerThread, m_finalizerThread;

    private static Resurrecting? m_survivor;

    #endregion

    #region Types

    private sealed class Payload { internal readonly int Value = 731; }

    private sealed class Resource
    {
        #region Fields

        private readonly Payload m_root = new();

        private byte* m_memory;

        #endregion

        #region Constructors

        internal Resource()
        {
            m_memory = Libc.Map(4096);
            if (m_memory == null)
                throw new OutOfMemoryException();
            m_memory[0] = 17;
            m_memory[4095] = 29;
        }

        ~Resource()
        {
            CheckThread();
            Release();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (m_root.Value != 731)
                Volatile.Write(ref m_failed, 1);
            GC.KeepAlive(m_root);
            Interlocked.Increment(ref m_finalized);
        }

        #endregion

        #region Functions

        /// <summary>
        /// Checks and frees the native page; a second release is a failure.
        /// </summary>
        internal void Release()
        {
            byte* memory = m_memory;
            m_memory = null;
            if (memory == null || memory[0] != 17 || memory[4095] != 29 ||
                !Libc.Unmap(memory, 4096))
                Volatile.Write(ref m_failed, 1);
            else
                Interlocked.Increment(ref m_released);
        }

        #endregion
    }

    private sealed class SuppressedObject
    {
        ~SuppressedObject() { Interlocked.Increment(ref m_suppressed); }
    }

    private sealed class Resurrecting
    {
        #region Fields

        private int m_pass;

        internal readonly Payload Root = new();

        #endregion

        #region Constructors

        ~Resurrecting()
        {
            CheckThread();
            if (Root.Value != 731)
                Volatile.Write(ref m_failed, 1);
            Interlocked.Increment(ref m_resurrections);
            if (++m_pass == 1)
            {
                m_survivor = this;
                GC.ReRegisterForFinalize(this);
            }
            else if (m_pass != 2)
                Volatile.Write(ref m_failed, 1);
        }

        #endregion
    }

    #endregion

    #region Functions

    /// <summary>
    /// Creates finalizable waves, collects them and checks counters and finalizer thread identity.
    /// </summary>
    /// <returns>True when every finalizer ran as expected.</returns>
    internal static bool Run()
    {
        m_finalized = m_released = m_suppressed = m_resurrections = m_failed = 0;
        m_finalizerThread = 0;
        m_callerThread = (uint)Libc.ThreadId();
        CreateSuppressed();
        for (int wave = 1; wave <= 3; ++wave)
        {
            CreateWave();
            Drain();
            if (Volatile.Read(ref m_finalized) != wave * 4 || Volatile.Read(ref m_released) != wave * 4 ||
                Volatile.Read(ref m_failed) != 0 || Volatile.Read(ref m_suppressed) != 0)
                return false;
        }
        CreateResurrecting();
        Drain();
        if (Volatile.Read(ref m_resurrections) != 1 || !HasSurvivor())
            return false;
        Drain(); // A resurrected, strongly rooted object must not finalize again.
        if (Volatile.Read(ref m_resurrections) != 1)
            return false;
        ReleaseSurvivor();
        Drain();
        if (Volatile.Read(ref m_resurrections) != 2)
            return false;
        Drain();
        GC.WaitForPendingFinalizers(); // Empty-queue handshake must also complete.
        return m_finalizerThread != 0 && Volatile.Read(ref m_failed) == 0 && Volatile.Read(ref m_suppressed) == 0 &&
            Volatile.Read(ref m_finalized) == 12 && Volatile.Read(ref m_released) == 12 &&
            Volatile.Read(ref m_resurrections) == 2 && !HasSurvivor();
    }

    #endregion

    #region Tools

    private static void CheckThread()
    {
        uint id = (uint)Libc.ThreadId();
        if (id == 0 || id == m_callerThread || (m_finalizerThread != 0 && id != m_finalizerThread))
            Volatile.Write(ref m_failed, 1);
        m_finalizerThread = id;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateWave() { for (int n = 0; n < 4; ++n) _ = new Resource(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateSuppressed() { var item = new SuppressedObject(); GC.SuppressFinalize(item); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateResurrecting() { _ = new Resurrecting(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasSurvivor() => m_survivor?.Root.Value == 731;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReleaseSurvivor() { m_survivor = null; }

    private static void Drain()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    #endregion
}
