using System.Runtime.InteropServices;

namespace WitOS.Dev.Host.Native;

/// <summary>
/// IO_COUNTERS.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobIoCounters
{
#pragma warning disable CS0649 // Populated by Win32.
    public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
#pragma warning restore CS0649
}
