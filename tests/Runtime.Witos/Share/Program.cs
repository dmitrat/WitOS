// A share of an SMB server as an ordinary .NET program sees it (plan step E2.2): witos-volumes mounted it at
// /media/witos before the program starts; the program reads what the server's host laid there, writes, copies, makes
// directories, moves and deletes through System.IO, and the host checks what it left. With "unmounted" it checks that
// the share is gone. Nothing here knows WitOS but the paths the scenario lays out.
using System.Runtime.InteropServices;

const string SHARE = "/media/witos";
const string FROM_HOST = SHARE + "/from-host.txt";
const string FROM_HOST_TEXT = "Laid in the share by the runner's host, read by a program in WitOS.";
const int WRITTEN_BYTES = 300_000;

if (args is ["unmounted"])
{
    var gone = !Directory.Exists(SHARE) && !File.ReadAllLines("/etc/mtab").Any(line => line.Split(' ') is [_, SHARE, ..]);
    Console.WriteLine(gone ? "[SHARE] unmounted: /media/witos is gone" : "[SHARE] unmounted: FAILED, /media/witos is still there");
    return gone ? 0 : 1;
}

var checks = new (string Name, Func<string?> Run)[]
{
    // Each check answers null when it holds, or what it saw.
    ("mounted", () =>
    {
        var line = File.ReadAllLines("/etc/mtab").FirstOrDefault(entry => entry.Split(' ') is [_, SHARE, ..]);
        return line is not null && line.Split(' ') is [_, _, "smb3", "rw", ..] ? null : line ?? "no line for " + SHARE;
    }),
    ("network drive", () =>
    {
        var drive = new DriveInfo(SHARE);
        return drive.DriveType == DriveType.Network && drive.TotalSize > 0 && drive.AvailableFreeSpace > 0
            ? null : $"{drive.DriveType}, {drive.DriveFormat}, total {drive.TotalSize}, free {drive.AvailableFreeSpace}";
    }),
    ("read", () => File.ReadAllText(FROM_HOST) == FROM_HOST_TEXT ? null : File.ReadAllText(FROM_HOST)),
    ("list", () =>
    {
        var names = Directory.GetFileSystemEntries(SHARE).Select(Path.GetFileName).ToList();
        return names.Contains("from-host.txt") ? null : string.Join(", ", names);
    }),
    ("copy to data", () =>
    {
        File.Copy(FROM_HOST, "/data/from-share.txt", overwrite: true);
        return File.ReadAllText("/data/from-share.txt") == FROM_HOST_TEXT ? null : "a different copy";
    }),
    ("write and read back", () =>
    {
        var bytes = Enumerable.Range(0, WRITTEN_BYTES).Select(i => (byte)(i * 7)).ToArray();
        File.WriteAllBytes(SHARE + "/from-witos.bin", bytes);
        return File.ReadAllBytes(SHARE + "/from-witos.bin").SequenceEqual(bytes) ? null : "other bytes back";
    }),
    ("directories and a move", () =>
    {
        Directory.CreateDirectory(SHARE + "/made-in-witos/inner");
        File.WriteAllText(SHARE + "/made-in-witos/note.txt", "moved by WitOS");
        File.Move(SHARE + "/made-in-witos/note.txt", SHARE + "/made-in-witos/inner/note.txt");
        return File.Exists(SHARE + "/made-in-witos/inner/note.txt") && !File.Exists(SHARE + "/made-in-witos/note.txt") ? null : "the move";
    }),
    ("delete", () =>
    {
        File.WriteAllText(SHARE + "/doomed.txt", "x");
        File.Delete(SHARE + "/doomed.txt");
        Directory.CreateDirectory(SHARE + "/doomed-directory");
        Directory.Delete(SHARE + "/doomed-directory");
        return !File.Exists(SHARE + "/doomed.txt") && !Directory.Exists(SHARE + "/doomed-directory") ? null : "something is left";
    }),
    ("errors", () =>
    {
        try
        {
            File.ReadAllText(SHARE + "/no-such-file.txt");
            return "a missing file read";
        }
        catch (FileNotFoundException)
        {
        }
        try
        {
            Directory.Delete(SHARE + "/made-in-witos");
            return "a directory with files deleted";
        }
        catch (IOException)
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
    Console.WriteLine(failure is null ? $"[SHARE] {name}: ok" : $"[SHARE] {name}: FAILED ({failure})");
    passed += failure is null ? 1 : 0;
}
Console.WriteLine($"[SHARE] on {RuntimeInformation.OSArchitecture}: {passed} of {checks.Length} checks passed");
return passed == checks.Length ? 0 : 1;
