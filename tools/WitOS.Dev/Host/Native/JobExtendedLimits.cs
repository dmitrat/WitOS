using System.Runtime.InteropServices;

namespace WitOS.Dev.Host.Native;

/// <summary>
/// JOBOBJECT_EXTENDED_LIMIT_INFORMATION.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobExtendedLimits
{
#pragma warning disable CS0649 // Populated by Win32.
    public JobBasicLimits Basic;
    public JobIoCounters Io;
    public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
#pragma warning restore CS0649
}
