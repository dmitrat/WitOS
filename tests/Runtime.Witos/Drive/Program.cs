// A USB drive as an ordinary .NET program sees it (plan step E3): the namespace mounts its FAT32 volume at /media/STICK
// when the xHCI driver finds it; the program waits for it, reads what the host laid there, writes, then asks the runner to
// take the drive out and sees /media/STICK go, asks for it back and sees it come with what it wrote. Nothing here knows
// WitOS but the paths and the lines the scenario reads.
using System.Runtime.InteropServices;

const string DRIVE = "/media/STICK";
const string README = DRIVE + "/readme.txt";
const string README_TEXT = "Laid on the USB drive by the host, read by a program in WitOS.";
const string WRITTEN = DRIVE + "/from-witos/written.bin";
const int WRITTEN_BYTES = 200_000;
// The guest's clock runs ahead while it idles (QEMU's -icount with sleep=off), so a wait for the runner is long.
var wait = TimeSpan.FromSeconds(1800);

bool Mounted() => Directory.Exists(DRIVE) && File.ReadAllLines("/etc/mtab").Any(line => line.Split(' ') is [_, DRIVE, ..]);

bool Await(Func<bool> condition)
{
    var deadline = DateTime.UtcNow + wait;
    while (!condition())
    {
        if (DateTime.UtcNow > deadline)
            return false;
        Thread.Sleep(250);
    }
    return true;
}

byte[] Pattern() => Enumerable.Range(0, WRITTEN_BYTES).Select(i => (byte)(i * 13)).ToArray();

var checks = new List<(string Name, Func<string?> Run)>
{
    // Each check answers null when it holds, or what it saw.
    ("mounted", () => Await(Mounted) ? null : "no /media/STICK within " + wait),
    ("mtab", () =>
    {
        var line = File.ReadAllLines("/etc/mtab").FirstOrDefault(entry => entry.Split(' ') is [_, DRIVE, ..]);
        return line is not null && line.Split(' ') is ["STICK", _, "vfat", "rw", ..] ? null : line ?? "no line";
    }),
    ("read", () => File.ReadAllText(README) == README_TEXT ? null : File.ReadAllText(README)),
    ("space", () =>
    {
        var drive = new DriveInfo(DRIVE);
        return drive.TotalSize > 0 && drive.AvailableFreeSpace > 0 ? null : $"total {drive.TotalSize}, free {drive.AvailableFreeSpace}";
    }),
    ("write and read back", () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(WRITTEN)!);
        File.WriteAllBytes(WRITTEN, Pattern());
        return File.ReadAllBytes(WRITTEN).SequenceEqual(Pattern()) ? null : "other bytes back";
    }),
    ("taken out", () =>
    {
        Console.WriteLine("[DRIVE] take the drive out");
        return Await(() => !Mounted()) ? null : "/media/STICK still there";
    }),
    ("put back", () =>
    {
        Console.WriteLine("[DRIVE] put the drive back");
        return Await(Mounted) ? null : "no /media/STICK again";
    }),
    ("kept what it wrote", () => File.Exists(WRITTEN) && File.ReadAllBytes(WRITTEN).SequenceEqual(Pattern()) ? null : "the file the program wrote")
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
    Console.WriteLine(failure is null ? $"[DRIVE] {name}: ok" : $"[DRIVE] {name}: FAILED ({failure})");
    passed += failure is null ? 1 : 0;
}
Console.WriteLine($"[DRIVE] on {RuntimeInformation.OSArchitecture}: {passed} of {checks.Count} checks passed");
return passed == checks.Count ? 0 : 1;
