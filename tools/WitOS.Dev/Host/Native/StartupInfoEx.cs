using System.Runtime.InteropServices;

namespace WitOS.Dev.Host.Native;

/// <summary>
/// STARTUPINFOEXW.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfoEx
{
    #region Fields

    public StartupInfo Startup;

    public IntPtr Attributes;

    #endregion
}
