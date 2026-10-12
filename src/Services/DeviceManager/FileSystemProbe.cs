using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Storage;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The device manager's proof of the FAT provider (plan step D4.1), until the namespace service of D5 mounts volumes:
/// the boot disk's FAT16 volume read through its driver's block channel and checked, and the data disk's FAT partition
/// written — a directory, a file of several clusters and a note — flushed, checked, mounted again and read back. The
/// host formats that partition with the same provider and reads what the guest wrote after the boot. On a partition a
/// previous boot wrote (D6.1), the probe first reads back what it left.
/// </summary>
internal static class FileSystemProbe
{
    #region Constants

    /// <summary>
    /// The name of the data disk's FAT partition.
    /// </summary>
    public const string FAT_PARTITION = "WitOS FAT";

    /// <summary>
    /// The file the host wrote on the FAT partition.
    /// </summary>
    public const string HOST_FILE = "/hello.txt";

    /// <summary>
    /// The file of several clusters the guest writes.
    /// </summary>
    public const string GUEST_FILE = "/guest/written.bin";

    /// <summary>
    /// The guest's note.
    /// </summary>
    public const string GUEST_NOTE = "/guest/note.txt";

    /// <summary>
    /// The bytes of the guest's file.
    /// </summary>
    public const int GUEST_FILE_BYTES = 100_000;

    private const string PREFIX = "[DEVICES] ";

    #endregion

    #region Functions

    /// <summary>
    /// Reads a read-only disk's FAT volume: its root, its loader's first bytes, and a check of its structures.
    /// </summary>
    /// <param name="disk">The disk, whose volume starts at its first block.</param>
    /// <param name="id">The device, for the lines.</param>
    /// <returns>When the volume is unmounted again.</returns>
    public static async Task ReadBootVolumeAsync(IBlockStorage disk, string id)
    {
        await using var volume = await FatFileSystem.MountAsync(disk);
        var root = await volume.EnumerateAsync("/");
        Console.WriteLine($"{PREFIX}fat {id}: {Describe(volume)}, / holds {string.Join(", ", root.Select(entry => entry.Name))}");
        var loader = (await volume.EnumerateAsync("/EFI/BOOT")).First(entry => !entry.IsDirectory);
        await using (var file = await volume.OpenAsync(loader.Path, FileMode.Open, FileAccess.Read))
        {
            var head = new byte[2];
            await file.ReadAsync(0, head);
            Console.WriteLine($"{PREFIX}fat {id}: {loader.Path} holds {loader.Length} bytes, " +
                (head[0] == (byte)'M' && head[1] == (byte)'Z' ? "a PE image (MZ)" : $"no PE image ({head[0]:x2}{head[1]:x2})"));
        }
        Console.WriteLine($"{PREFIX}fat {id}: {Check(await volume.CheckAsync())}");
    }

    /// <summary>
    /// Writes the data disk's FAT partition and reads it back after a second mount.
    /// </summary>
    /// <param name="partition">The partition.</param>
    /// <param name="id">The device, for the lines.</param>
    /// <returns>When the partition is unmounted again.</returns>
    public static async Task WriteDataVolumeAsync(IBlockStorage partition, string id)
    {
        var bytes = new byte[GUEST_FILE_BYTES];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Pattern(i);
        var note = Encoding.UTF8.GetBytes(Note());
        await using (var volume = await FatFileSystem.MountAsync(partition))
        {
            Console.WriteLine($"{PREFIX}fat {id}: partition \"{FAT_PARTITION}\": {Describe(volume)}, {HOST_FILE} says " +
                $"\"{await ReadTextAsync(volume, HOST_FILE)}\"");
            if (await volume.GetEntryAsync(GUEST_FILE) is not null)
            {
                var before = new byte[GUEST_FILE_BYTES];
                await using (var file = await volume.OpenAsync(GUEST_FILE, FileMode.Open, FileAccess.Read))
                    await file.ReadAsync(0, before);
                Console.WriteLine($"{PREFIX}fat {id}: partition \"{FAT_PARTITION}\": after the reboot, " +
                    (before.AsSpan().SequenceEqual(bytes) && await ReadTextAsync(volume, GUEST_NOTE) == Note()
                        ? "read back what a previous boot wrote"
                        : "read back other bytes than a previous boot wrote"));
            }
            await volume.CreateDirectoryAsync("/guest");
            await using (var file = await volume.OpenAsync(GUEST_FILE, FileMode.Create, FileAccess.Write))
                await file.WriteAsync(0, bytes);
            await using (var file = await volume.OpenAsync(GUEST_NOTE, FileMode.Create, FileAccess.Write))
                await file.WriteAsync(0, note);
            await volume.FlushAsync();
            Console.WriteLine($"{PREFIX}fat {id}: partition \"{FAT_PARTITION}\": wrote {GUEST_FILE} ({GUEST_FILE_BYTES} bytes) and " +
                $"{GUEST_NOTE}, {Check(await volume.CheckAsync())}");
        }
        await using (var again = await FatFileSystem.MountAsync(partition))
        {
            var back = new byte[GUEST_FILE_BYTES];
            await using (var file = await again.OpenAsync(GUEST_FILE, FileMode.Open, FileAccess.Read))
                await file.ReadAsync(0, back);
            var guest = await again.EnumerateAsync("/guest");
            var same = back.AsSpan().SequenceEqual(bytes) && await ReadTextAsync(again, GUEST_NOTE) == Note();
            Console.WriteLine($"{PREFIX}fat {id}: partition \"{FAT_PARTITION}\": mounted again, /guest holds " +
                $"{string.Join(", ", guest.Select(entry => $"{entry.Name} ({entry.Length} bytes)"))}" +
                (same ? ", read back what it wrote" : ", read back other bytes than it wrote"));
        }
    }

    /// <summary>
    /// The byte the guest writes at an offset of its file: no cluster repeats another.
    /// </summary>
    /// <param name="offset">The offset in the file.</param>
    /// <returns>The byte.</returns>
    public static byte Pattern(int offset) => (byte)(offset % 251 ^ offset / 2048);

    /// <summary>
    /// The guest's note.
    /// </summary>
    /// <returns>Its text.</returns>
    public static string Note() => "Written by WitOS through its virtio driver, the block protocol and OutWit.Common.Fat.";

    private static string Describe(IFileSystem volume) =>
        $"{volume.Volume.Format} volume of {volume.Volume.Length} bytes in units of {volume.Volume.AllocationUnit}, " +
        $"{(volume.Volume.IsReadOnly ? "read-only" : "writable")}";

    private static string Check(FileSystemCheckReport report) =>
        report.IsClean
            ? $"check clean ({report.Files} files, {report.Directories} directories)"
            : $"check found {report.Problems.Count} problems: {string.Join("; ", report.Problems.Take(3))}";

    private static async Task<string> ReadTextAsync(IFileSystem volume, string path)
    {
        await using var file = await volume.OpenAsync(path, FileMode.Open, FileAccess.Read);
        var bytes = new byte[file.Length];
        await file.ReadAsync(0, bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    #endregion
}
