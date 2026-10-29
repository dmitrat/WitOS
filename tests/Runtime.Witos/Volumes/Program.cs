// The volumes as an ordinary .NET program sees them (plan step E1, ADR 0032), run from the drive it checks: a FAT
// volume of a disk with an MBR, which the namespace service mounts at /media/TRANSFER. Nothing here knows WitOS but the
// paths the scenario lays out.
using System.Runtime.InteropServices;

const string DRIVE = "/media/TRANSFER";
const string README = DRIVE + "/readme.txt";
const string README_TEXT = "Laid on the drive by the host, read by a program on the same drive.";

var checks = new (string Name, Func<string?> Run)[]
{
    // Each check answers null when it holds, or what it saw.
    ("media", () => Directory.GetDirectories("/media").Contains(DRIVE) ? null : string.Join(", ", Directory.GetDirectories("/media"))),
    ("running from the drive", () => AppContext.BaseDirectory.StartsWith(DRIVE + "/", StringComparison.Ordinal) ? null : AppContext.BaseDirectory),
    ("drives", () =>
    {
        var names = DriveInfo.GetDrives().Select(drive => drive.Name.TrimEnd('/')).ToList();
        return names.Contains(DRIVE) && names.Contains("/data") ? null : string.Join(", ", names);
    }),
    ("drive space", () =>
    {
        var drive = new DriveInfo(DRIVE);
        return drive.IsReady && drive.TotalSize > 0 && drive.AvailableFreeSpace > 0 && drive.AvailableFreeSpace <= drive.TotalSize
            ? null : $"ready {drive.IsReady}, total {drive.TotalSize}, free {drive.AvailableFreeSpace}";
    }),
    ("data space", () =>
    {
        var data = new DriveInfo("/data");
        return data.TotalSize > 0 && data.AvailableFreeSpace > 0 ? null : $"total {data.TotalSize}, free {data.AvailableFreeSpace}";
    }),
    ("mtab", () =>
    {
        var line = File.ReadAllLines("/etc/mtab").FirstOrDefault(entry => entry.Split(' ') is [_, DRIVE, ..]);
        return line is not null && line.Split(' ') is [_, _, "vfat", "rw", ..] ? null : line ?? "no line for " + DRIVE;
    }),
    ("read", () => File.ReadAllText(README) == README_TEXT ? null : File.ReadAllText(README)),
    ("copy to data", () =>
    {
        File.Copy(README, "/data/from-transfer.txt", overwrite: true);
        return File.ReadAllText("/data/from-transfer.txt") == README_TEXT ? null : "a different copy";
    }),
    ("write to the drive", () =>
    {
        var before = new DriveInfo(DRIVE).AvailableFreeSpace;
        Directory.CreateDirectory(DRIVE + "/from-guest");
        File.WriteAllBytes(DRIVE + "/from-guest/note.bin", Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray());
        var after = new DriveInfo(DRIVE).AvailableFreeSpace;
        return before - after >= 100_000 ? null : $"free {before} before, {after} after";
    }),
    ("media is the system's", () =>
    {
        try
        {
            Directory.CreateDirectory("/media/made-by-a-program");
            return "a directory was made in /media";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    })
};

var passed = 0;
foreach (var (name, run) in checks)
{
    string? failure;
    try
    {
        failure = run();
    }
    catch (Exception exception)
    {
        failure = $"{exception.GetType().Name}: {exception.Message}";
    }
    Console.WriteLine(failure is null ? $"[VOLUMES] {name}: ok" : $"[VOLUMES] {name}: FAILED ({failure})");
    passed += failure is null ? 1 : 0;
}
Console.WriteLine($"[VOLUMES] on {RuntimeInformation.OSArchitecture}: {passed} of {checks.Length} checks passed");
return passed == checks.Length ? 0 : 1;
