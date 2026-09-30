using System.Runtime.InteropServices;

namespace WitOS.NativeAotBoot;

// Explicit system-component console interop, shared with the Windows reference.
// The guest binding writes through the kernel-validated startup capability.
internal static unsafe class GuestReport
{
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetStdHandle(int selector);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int WriteFile(nint handle, byte* bytes, uint count, uint* written, nint overlapped);

    internal static bool Write(ReadOnlySpan<byte> message)
    {
        uint written = 0;
        fixed (byte* bytes = message)
            return WriteFile(GetStdHandle(-11), bytes, (uint)message.Length, &written, 0) != 0 &&
                written == message.Length;
    }
}
