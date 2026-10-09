using System.Runtime.InteropServices;

namespace WitOS.Dev.Host.Native;

/// <summary>
/// The C library's process-group signal, used by the child process runner on a Unix host (plan step T2.1a).
/// </summary>
internal static class LibC
{
    #region Constants

    private const string LIBRARY = "libc";

    /// <summary>
    /// SIGKILL: the signal no process can catch.
    /// </summary>
    public const int SIGKILL = 9;

    /// <summary>
    /// ESRCH: no process matches the identifier.
    /// </summary>
    public const int ESRCH = 3;

    #endregion

    #region Functions

    /// <summary>
    /// kill(2): sends <paramref name="signal"/> to a process, or with a negative identifier to every process of that
    /// process group; signal 0 checks that one exists.
    /// </summary>
    [DllImport(LIBRARY, SetLastError = true)]
    public static extern int kill(int pid, int signal);

    #endregion
}
