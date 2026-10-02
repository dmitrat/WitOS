using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WitOS.Dev.Host.Native;

/// <summary>
/// kernel32 job, process and attribute-list functions used by the owned child process runner.
/// </summary>
internal static class Kernel32
{
    #region Constants

    private const string LIBRARY = "kernel32.dll";

    #endregion

    #region Functions

    /// <summary>
    /// Wraps the calling thread's last Win32 error.
    /// </summary>
    /// <returns>The exception describing the last error.</returns>
    public static Win32Exception LastError() => new(Marshal.GetLastWin32Error());

    /// <summary>
    /// Win32 CreateJobObjectW from kernel32.dll.
    /// </summary>
    [DllImport(LIBRARY, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    /// <summary>
    /// Win32 SetInformationJobObject from kernel32.dll.
    /// </summary>
    [DllImport(LIBRARY, SetLastError = true)]
    public static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobExtendedLimits info, uint length);

    /// <summary>
    /// Win32 QueryInformationJobObject from kernel32.dll.
    /// </summary>
    [DllImport(LIBRARY, SetLastError = true)]
    public static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out JobAccounting info, uint length,
        IntPtr returned);

    /// <summary>
    /// Win32 TerminateProcess from kernel32.dll.
    /// </summary>
    [DllImport(LIBRARY, SetLastError = true)]
    public static extern bool TerminateProcess(SafeFileHandle process, uint code);

    /// <summary>
    /// Win32 TerminateJobObject from kernel32.dll.
    /// </summary>
    [DllImport(LIBRARY, SetLastError = true)]
    public static extern bool TerminateJobObject(SafeFileHandle job, uint code);

    /// <summary>
    /// Win32 SetHandleInformation from kernel32.dll.
    /// </summary>
    [DllImport(LIBRARY, SetLastError = true)]
    public static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    /// <summary>
    /// Win32 InitializeProcThreadAttributeList from kernel32.dll.
    /// </summary>
    [DllImport(LIBRARY, SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);

    /// <summary>
    /// Win32 UpdateProcThreadAttribute from kernel32.dll.
    /// </summary>
    [DllImport(LIBRARY, SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint key, IntPtr value, nuint size,
        IntPtr previous, IntPtr returned);

    /// <summary>
    /// Win32 DeleteProcThreadAttributeList from kernel32.dll.
    /// </summary>
    [DllImport(LIBRARY)]
    public static extern void DeleteProcThreadAttributeList(IntPtr list);

    /// <summary>
    /// Win32 CreateProcessW from kernel32.dll.
    /// </summary>
    [DllImport(LIBRARY, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(string? application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup,
        out ProcessInformation info);

    /// <summary>
    /// Win32 WaitForSingleObject from kernel32.dll.
    /// </summary>
    [DllImport(LIBRARY, SetLastError = true)]
    public static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);

    /// <summary>
    /// Win32 GetExitCodeProcess from kernel32.dll.
    /// </summary>
    [DllImport(LIBRARY, SetLastError = true)]
    public static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);

    #endregion
}
