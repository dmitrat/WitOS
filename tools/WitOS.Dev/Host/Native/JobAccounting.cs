using System.Runtime.InteropServices;

namespace WitOS.Dev.Host.Native;

/// <summary>
/// JOBOBJECT_BASIC_ACCOUNTING_INFORMATION.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobAccounting
{
    #region Fields

#pragma warning disable CS0649 // Populated by Win32.
    public long User, Kernel, PeriodUser, PeriodKernel;

    public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;

    #endregion
}
