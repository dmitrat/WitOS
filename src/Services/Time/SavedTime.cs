using System;
using System.Globalization;
using System.IO;

namespace OutWit.OS.Time;

/// <summary>
/// The time the system saved on its data volume (plan step H7.4, as Raspberry Pi OS's fake-hwclock saves it): one line,
/// UTC in ISO 8601 to the tenth of a microsecond, written to a file beside it, flushed and renamed over the last, so
/// that a cut leaves the old time or the new one whole.
/// </summary>
internal static class SavedTime
{
    #region Constants

    /// <summary>
    /// The file.
    /// </summary>
    public const string PATH = "/data/system/time";

    private const string FORMAT = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    #endregion

    #region Functions

    /// <summary>
    /// The line a time is saved as.
    /// </summary>
    /// <param name="nanoseconds">UTC in nanoseconds since 1970.</param>
    /// <returns>The text, without its line end.</returns>
    public static string Format(ulong nanoseconds) => Utc(nanoseconds).ToString(FORMAT, CultureInfo.InvariantCulture);

    /// <summary>
    /// UTC in nanoseconds since 1970 of a saved line, or false for a line that is no such time.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <param name="nanoseconds">The time.</param>
    /// <returns>Whether the text held a time.</returns>
    public static bool TryParse(string text, out ulong nanoseconds)
    {
        nanoseconds = 0;
        if (!DateTime.TryParseExact(text.Trim(), FORMAT, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time) || time < DateTime.UnixEpoch)
            return false;
        nanoseconds = (ulong)(time - DateTime.UnixEpoch).Ticks * 100;
        return true;
    }

    /// <summary>
    /// A time as a DateTime of kind UTC, without reading the system's clock.
    /// </summary>
    /// <param name="nanoseconds">UTC in nanoseconds since 1970.</param>
    /// <returns>The time.</returns>
    public static DateTime Utc(ulong nanoseconds) => DateTime.UnixEpoch.AddTicks((long)(nanoseconds / 100));

    /// <summary>
    /// Saves a time at the path: a file beside it, flushed to the volume, renamed over it.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="nanoseconds">UTC in nanoseconds since 1970.</param>
    /// <exception cref="IOException">The volume could not be written.</exception>
    public static void Save(string path, ulong nanoseconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var next = path + ".next";
        using (var stream = new FileStream(next, FileMode.Create, FileAccess.Write))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(Format(nanoseconds) + "\n");
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(next, path, overwrite: true);
    }

    #endregion
}
