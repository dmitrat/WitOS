using System.Runtime.InteropServices;

namespace WitOS.NativeAotBoot;

/// <summary>
/// Writes guest protocol lines to standard output through the console binding.
/// </summary>
/// <remarks>
/// Explicit system-component console interop, shared with the Windows reference.
/// The guest binding writes through the kernel-validated startup capability.
/// </remarks>
internal static unsafe class GuestReport
{
    #region Functions

    /// <summary>
    /// Writes one complete message.
    /// </summary>
    /// <param name="message">UTF-8 bytes to write.</param>
    /// <returns>True when every byte was written.</returns>
    internal static bool Write(ReadOnlySpan<byte> message)
    {
        uint written = 0;
        fixed (byte* bytes = message)
            return WriteFile(GetStdHandle(-11), bytes, (uint)message.Length, &written, 0) != 0 &&
                written == message.Length;
    }

    #endregion

    #region Tools

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetStdHandle(int selector);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int WriteFile(nint handle, byte* bytes, uint count, uint* written, nint overlapped);

    #endregion
}
