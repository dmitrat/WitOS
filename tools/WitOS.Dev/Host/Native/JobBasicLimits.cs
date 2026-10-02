using System.Runtime.InteropServices;

namespace WitOS.Dev.Host.Native;

/// <summary>
/// JOBOBJECT_BASIC_LIMIT_INFORMATION.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobBasicLimits
{
    #region Fields

#pragma warning disable CS0649 // Populated by Win32.
    public long ProcessTime, JobTime;

    public uint LimitFlags;

    public nuint MinWorkingSet, MaxWorkingSet;

    public uint ActiveLimit;

    public nuint Affinity;

    public uint Priority, Scheduling;

    #endregion
}
