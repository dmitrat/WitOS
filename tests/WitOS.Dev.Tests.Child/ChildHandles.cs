using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace WitOS.Dev.Tests.Child;

/// <summary>
/// Changes the ownership of this process's standard output and error handles for the pipe cleanup tests. On a Unix host
/// (plan step T2.1a) they are descriptors 1 and 2: close-on-exec keeps them out of the programs the process starts.
/// </summary>
internal static class ChildHandles
{
    #region Constants

    private const int STD_OUTPUT_HANDLE = -11;

    private const int STD_ERROR_HANDLE = -12;

    private const uint HANDLE_FLAG_INHERIT = 1;

    private const uint PROCESS_DUP_HANDLE = 0x40;

    private const uint DUPLICATE_SAME_ACCESS = 2;

    private const int F_SETFD = 2;

    private const int FD_CLOEXEC = 1;

    private static readonly int[] UNIX_OUTPUT = [1, 2];

    #endregion

    #region Functions

    /// <summary>
    /// Stops descendants from inheriting standard output and error.
    /// </summary>
    /// <exception cref="Win32Exception">The handle flags could not be changed.</exception>
    public static void MakeOutputNonInheritable()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var descriptor in UNIX_OUTPUT)
            {
                if (fcntl(descriptor, F_SETFD, FD_CLOEXEC) != 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            return;
        }
        foreach (var kind in new[] { STD_OUTPUT_HANDLE, STD_ERROR_HANDLE })
        {
            if (!SetHandleInformation(GetStdHandle(kind), HANDLE_FLAG_INHERIT, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    /// <summary>
    /// Closes standard output and error.
    /// </summary>
    public static void CloseOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var descriptor in UNIX_OUTPUT)
            {
                close(descriptor);
            }
            return;
        }
        CloseHandle(GetStdHandle(STD_OUTPUT_HANDLE));
        CloseHandle(GetStdHandle(STD_ERROR_HANDLE));
    }

    /// <summary>
    /// Duplicates standard output and error into the receiver process and reports the duplicated values.
    /// </summary>
    /// <remarks>
    /// The receiver is the test coordinator that explicitly supplied its own process ID.
    /// </remarks>
    /// <param name="receiver">Process ID of the test process.</param>
    /// <param name="report">File that receives one decimal handle value per line.</param>
    /// <exception cref="Win32Exception">The receiver could not be opened or a handle could not be duplicated.</exception>
    public static void ExportOutput(int receiver, string report)
    {
        var target = OpenProcess(PROCESS_DUP_HANDLE, false, (uint)receiver);
        if (target == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            var handles = new long[2];
            for (var i = 0; i < 2; ++i)
            {
                if (!DuplicateHandle(GetCurrentProcess(), GetStdHandle(STD_OUTPUT_HANDLE - i), target, out var copy, 0, false,
                        DUPLICATE_SAME_ACCESS))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                handles[i] = copy.ToInt64();
            }
            File.WriteAllLines(report, handles.Select(handle => handle.ToString(CultureInfo.InvariantCulture)));
        }
        finally
        {
            CloseHandle(target);
        }
    }

    #endregion

    #region Tools

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int descriptor, int command, int argument);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint rights, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target,
        uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int kind);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    #endregion
}
