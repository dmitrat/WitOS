using System.Runtime.InteropServices;

namespace WitOS.Dev.Host.Native;

/// <summary>
/// STARTUPINFOW.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct StartupInfo
{
#pragma warning disable CS0649 // Partly populated by Win32.
    public int Size;
    public IntPtr Reserved, Desktop, Title;
    public uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
    public ushort Show, ReservedBytes;
    public IntPtr ReservedData, Input, Output, Error;
#pragma warning restore CS0649
}
