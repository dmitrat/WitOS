using System;
using System.Diagnostics;
using System.IO;

namespace OutWit.OS.Tests.FileSystems;

/// <summary>
/// e2fsprogs as the reference that judges what the ext provider wrote (storage document §73): e2fsck checks a volume
/// without changing it, or replays its journal; debugfs reads a file out of it.
/// </summary>
internal static class Ext4Tools
{
    #region Functions

    /// <summary>
    /// Checks an image with <c>e2fsck -fn</c>, changing nothing.
    /// </summary>
    /// <param name="image">The image's bytes.</param>
    /// <returns>e2fsck's exit code (zero for a clean volume) and its report.</returns>
    public static (int Exit, string Report) Check(byte[] image) => OnImage(image, (tools, path) => Run(Path.Combine(tools, "e2fsck"), "-fn", path));

    /// <summary>
    /// Lets e2fsck replay an image's journal and fix what it finds (<c>e2fsck -fy</c>).
    /// </summary>
    /// <param name="image">The image's bytes, changed in place.</param>
    /// <returns>e2fsck's exit code and its report.</returns>
    public static (int Exit, string Report) Repair(byte[] image) => OnImage(image, (tools, path) => Run(Path.Combine(tools, "e2fsck"), "-fy", path), true);

    /// <summary>
    /// Reads a file out of an image with debugfs.
    /// </summary>
    /// <param name="image">The image's bytes.</param>
    /// <param name="file">The file's path in the volume.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Dump(byte[] image, string file)
    {
        var output = Path.Combine(Path.GetTempPath(), $"witos-dump-{Guid.NewGuid():N}");
        try
        {
            var (exit, report) = OnImage(image, (tools, path) => Run(Path.Combine(tools, "debugfs"), "-R", $"dump {file} {output}", path));
            if (exit != 0 || !File.Exists(output))
                throw new InvalidOperationException($"debugfs could not dump {file}: {report}");
            return File.ReadAllBytes(output);
        }
        finally
        {
            File.Delete(output);
        }
    }

    /// <summary>
    /// Runs debugfs commands on an image, writable (<c>debugfs -w -f</c>).
    /// </summary>
    /// <param name="image">The image's bytes, changed in place.</param>
    /// <param name="commands">The commands.</param>
    /// <returns>debugfs's report.</returns>
    public static string Script(byte[] image, params string[] commands)
    {
        var script = Path.Combine(Path.GetTempPath(), $"witos-debugfs-{Guid.NewGuid():N}.cmd");
        try
        {
            File.WriteAllLines(script, commands);
            return OnImage(image, (tools, path) => Run(Path.Combine(tools, "debugfs"), "-w", "-f", script, path), true).Report;
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static (int Exit, string Report) OnImage(byte[] image, Func<string, string, (int, string)> tool, bool writeBack = false)
    {
        var tools = Path.GetDirectoryName(Ext4Images.RequireMkfs())!;
        var path = Path.Combine(Path.GetTempPath(), $"witos-check-{Guid.NewGuid():N}.img");
        try
        {
            File.WriteAllBytes(path, image);
            var result = tool(tools, path);
            if (writeBack)
                File.ReadAllBytes(path).CopyTo(image, 0);
            return result;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (int, string) Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    #endregion
}
