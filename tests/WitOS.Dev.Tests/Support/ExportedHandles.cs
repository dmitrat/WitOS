using System.Globalization;
using System.Runtime.InteropServices;

namespace WitOS.Dev.Tests.Support;

/// <summary>
/// Closes the output pipe handles a child process duplicated into the test process.
/// </summary>
internal static class ExportedHandles
{
    #region Functions

    /// <summary>
    /// Closes every handle listed in the child's report file, if the file exists.
    /// </summary>
    /// <param name="report">File with one decimal handle value per line.</param>
    public static void Close(string report)
    {
        if (!File.Exists(report))
        {
            return;
        }
        foreach (var value in File.ReadAllLines(report))
        {
            CloseHandle(new IntPtr(long.Parse(value, CultureInfo.InvariantCulture)));
        }
    }

    #endregion

    #region Tools

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    #endregion
}
