using System.Runtime.InteropServices;

namespace WitOS.Dev.Host.Native;

/// <summary>
/// PROCESS_INFORMATION.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    #region Fields

#pragma warning disable CS0649 // Populated by Win32.
    public IntPtr Process, Thread;

    public uint ProcessId, ThreadId;

    #endregion
}
