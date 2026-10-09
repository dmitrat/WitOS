using System;
using System.Runtime.InteropServices;

namespace WitOS.Acceptance;

/// <summary>
/// The libc functions the acceptance calls: output to the log, a page of native memory and the thread's id.
/// </summary>
/// <remarks>
/// runtime-witos binds them as direct P/Invokes (--directpinvoke:libc), so the linker resolves them in the system
/// layer's libc; the class libraries above CoreLib are not built for witos yet.
/// </remarks>
internal static unsafe class Libc
{
    #region Constants

    private const int PROT_READ_WRITE = 3;

    private const int MAP_PRIVATE_ANONYMOUS = 0x22;

    #endregion

    #region Functions

    /// <summary>
    /// Writes one line to standard output, which the system layer gives the kernel log.
    /// </summary>
    /// <param name="text">ASCII text without the newline.</param>
    /// <returns>True when every byte was written.</returns>
    internal static bool Line(string text)
    {
        var bytes = new byte[text.Length + 1];
        for (int i = 0; i < text.Length; ++i)
            bytes[i] = text[i] < 128 ? (byte)text[i] : (byte)'?';
        bytes[^1] = (byte)'\n';
        fixed (byte* pointer = bytes)
            return Write(1, pointer, bytes.Length) == bytes.Length;
    }

    /// <summary>
    /// Maps anonymous readable and writable memory.
    /// </summary>
    /// <param name="bytes">Size of the mapping.</param>
    /// <returns>The mapping, or null when there is none.</returns>
    internal static byte* Map(nuint bytes)
    {
        void* memory = MemoryMap(null, bytes, PROT_READ_WRITE, MAP_PRIVATE_ANONYMOUS, -1, 0);
        return memory == (void*)-1 ? null : (byte*)memory;
    }

    /// <summary>
    /// Unmaps memory Map returned.
    /// </summary>
    /// <param name="memory">The mapping.</param>
    /// <param name="bytes">Its size.</param>
    /// <returns>True when the mapping is gone.</returns>
    internal static bool Unmap(byte* memory, nuint bytes) => MemoryUnmap(memory, bytes) == 0;

    /// <summary>
    /// The calling thread's id in the libc.
    /// </summary>
    /// <returns>A nonzero id, distinct for every live thread.</returns>
    internal static int ThreadId() => GetThreadId();

    #endregion

    #region Tools

    [DllImport("libc", EntryPoint = "write", ExactSpelling = true)]
    private static extern nint Write(int descriptor, byte* bytes, nint count);

    [DllImport("libc", EntryPoint = "mmap", ExactSpelling = true)]
    private static extern void* MemoryMap(void* address, nuint bytes, int protection, int flags, int descriptor, nint offset);

    [DllImport("libc", EntryPoint = "munmap", ExactSpelling = true)]
    private static extern int MemoryUnmap(void* address, nuint bytes);

    [DllImport("libc", EntryPoint = "gettid", ExactSpelling = true)]
    private static extern int GetThreadId();

    #endregion
}
