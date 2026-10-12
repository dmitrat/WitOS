using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Ext4;
using OutWit.OS.Storage;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The device manager's proof of the ext4 provider (plan steps D4.2 and D4.3), until the namespace service of D5 mounts
/// volumes: the data disk's ext4 partition, which the host's e2fsprogs made from a known tree, mounted through the block
/// channel and read back — its root, a text file, a sparse file over more extents than an inode holds, an htree
/// directory, a link and a mode — then written through the journal — a directory, a file written around a hole, a note,
/// entries added to the htree, a file removed, a link moved — and read back after a second mount. After the boot the
/// host's e2fsck must find the partition clean and debugfs must read what the guest wrote. A partition a previous boot
/// wrote (D6.1: it holds /guest) is not written again: the probe reports how the previous boot left the volume, cleanly
/// unmounted or with transactions to replay, and reads back what it wrote.
/// </summary>
internal static class Ext4Probe
{
    #region Constants

    /// <summary>
    /// The name of the data disk's ext4 partition.
    /// </summary>
    public const string EXT4_PARTITION = "WitOS ext4";

    private const string PREFIX = "[DEVICES] ";
    private const int BIG_CHUNKS = 6;
    private const int BIG_CHUNK_BYTES = 20_000;
    private const int BIG_STRIDE = 65_536;
    private const int WRITTEN_BYTES = 200_000;
    private const int ADDED = 50;
    private const string NOTE = "Written by WitOS through the journal of its managed ext4 provider.";

    #endregion

    #region Functions

    /// <summary>
    /// Reads the ext4 partition, writes it, and reads it back after a second mount.
    /// </summary>
    /// <param name="partition">The partition.</param>
    /// <param name="id">The device, for the lines.</param>
    /// <returns>When the volume is unmounted again.</returns>
    public static async Task ProbeAsync(IBlockStorage partition, string id)
    {
        if (await WrittenBeforeAsync(partition, id))
        {
            await ReadBackAsync(partition, id, previous: true);
            return;
        }
        await ReadTreeAsync(partition, id);
        await WriteAsync(partition, id);
        await ReadBackAsync(partition, id, previous: false);
    }

    /// <summary>
    /// The byte the guest writes at an offset of /guest/logs/written.bin (RuntimeExt4Volume.GuestPattern).
    /// </summary>
    /// <param name="offset">The offset.</param>
    /// <returns>The byte.</returns>
    public static byte Pattern(int offset) => (byte)(offset % 241 ^ offset / 8192);

    // Whether a previous boot wrote the volume, and then how it left it (D6.1, D6.2).
    private static async Task<bool> WrittenBeforeAsync(IBlockStorage partition, string id)
    {
        await using var volume = await Ext4FileSystem.MountAsync(partition);
        if (await volume.GetEntryAsync("/guest") is null)
            return false;
        Console.WriteLine($"{PREFIX}ext4 {id}: a previous boot wrote the volume and " + (volume.WasCleanlyUnmounted
            ? "unmounted it cleanly: no transaction to replay"
            : $"left it mounted: the journal replayed {volume.ReplayedTransactions} transactions"));
        return true;
    }

    private static async Task ReadTreeAsync(IBlockStorage partition, string id)
    {
        await using var volume = await Ext4FileSystem.MountAsync(partition);
        var root = await volume.EnumerateAsync("/");
        Console.WriteLine($"{PREFIX}ext4 {id}: partition \"{EXT4_PARTITION}\": {volume.Volume.Format} volume \"{volume.Volume.Label}\" of " +
            $"{volume.Volume.Length} bytes in blocks of {volume.Volume.AllocationUnit}, {(volume.Volume.IsReadOnly ? "read-only" : "writable")}, / holds " +
            string.Join(", ", root.Select(entry => entry.Name).Order(StringComparer.Ordinal)));
        var hello = (await volume.GetEntryAsync("/hello.txt"))!;
        Console.WriteLine($"{PREFIX}ext4 {id}: /hello.txt (mode {Convert.ToString(hello.Unix!.Mode, 8)}) says \"{await ReadTextAsync(volume, "/hello.txt")}\"");
        var big = await ReadAllAsync(volume, "/big.bin");
        Console.WriteLine($"{PREFIX}ext4 {id}: /big.bin holds {big.Length} bytes, {BIG_CHUNKS} chunks between holes, " +
            (IsBig(big) ? "as the host wrote them" : "other bytes than the host wrote"));
        var many = await volume.EnumerateAsync("/many");
        Console.WriteLine($"{PREFIX}ext4 {id}: /many holds {many.Count} entries, /link points to {await volume.ReadLinkAsync("/link")}");
        Console.WriteLine($"{PREFIX}ext4 {id}: {Check(await volume.CheckAsync())}");
    }

    // The changes, each a transaction through the journal.
    private static async Task WriteAsync(IBlockStorage partition, string id)
    {
        var bytes = Written();
        await using var volume = await Ext4FileSystem.MountAsync(partition);
        await volume.CreateDirectoryAsync("/guest/logs");
        await using (var file = await volume.OpenAsync("/guest/logs/written.bin", FileMode.CreateNew, FileAccess.Write))
        {
            await file.WriteAsync(120_000, bytes.AsMemory(120_000)); // a hole first, filled after
            await file.WriteAsync(0, bytes.AsMemory(0, 120_000));
        }
        await using (var file = await volume.OpenAsync("/guest/note.txt", FileMode.CreateNew, FileAccess.Write))
            await file.WriteAsync(0, Encoding.UTF8.GetBytes(NOTE));
        for (var i = 0; i < ADDED; i++)
            await using (var file = await volume.OpenAsync($"/many/added-by-the-guest-{i:00}", FileMode.CreateNew, FileAccess.Write))
                await file.WriteAsync(0, Encoding.UTF8.GetBytes($"{i}"));
        await volume.DeleteAsync("/docs/readme.txt");
        await volume.RenameAsync("/link", "/guest/link");
        Console.WriteLine($"{PREFIX}ext4 {id}: wrote /guest/logs/written.bin ({WRITTEN_BYTES} bytes around a hole) and /guest/note.txt, " +
            $"added {ADDED} entries to /many, removed /docs/readme.txt, moved /link to /guest/link, through the journal");
    }

    // The changes read back after a second mount, or after a reboot when a previous boot made them.
    private static async Task ReadBackAsync(IBlockStorage partition, string id, bool previous)
    {
        await using var volume = await Ext4FileSystem.MountAsync(partition);
        var same = (await ReadAllAsync(volume, "/guest/logs/written.bin")).AsSpan().SequenceEqual(Written()) &&
            await ReadTextAsync(volume, "/guest/note.txt") == NOTE && await volume.ReadLinkAsync("/guest/link") == "hello.txt" &&
            await volume.GetEntryAsync("/docs/readme.txt") is null;
        var guest = await volume.EnumerateAsync("/guest");
        var many = await volume.EnumerateAsync("/many");
        Console.WriteLine($"{PREFIX}ext4 {id}: {(previous ? "after the reboot" : "mounted again")}, /guest holds " +
            string.Join(", ", guest.Select(entry => entry.Name).Order(StringComparer.Ordinal)) + $", /many holds {many.Count} entries, " +
            (same ? "read back what it wrote" : "read back other bytes than it wrote") + $", {Check(await volume.CheckAsync())}");
    }

    private static byte[] Written()
    {
        var bytes = new byte[WRITTEN_BYTES];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Pattern(i);
        return bytes;
    }

    private static string Check(FileSystemCheckReport report) => report.IsClean
        ? $"check clean ({report.Files} files, {report.Directories} directories)"
        : $"check found {report.Problems.Count} problems: {string.Join("; ", report.Problems.Take(3))}";

    // The sparse file: each chunk's bytes, zeros between (RuntimeExt4Volume.BigPattern).
    private static bool IsBig(byte[] bytes)
    {
        if (bytes.Length != (BIG_CHUNKS - 1) * BIG_STRIDE + BIG_CHUNK_BYTES)
            return false;
        for (var i = 0; i < bytes.Length; i++)
        {
            var chunk = i / BIG_STRIDE;
            var offset = i % BIG_STRIDE;
            if (bytes[i] != (offset < BIG_CHUNK_BYTES ? (byte)(1 + (chunk * 11 + offset) % 251) : 0))
                return false;
        }
        return true;
    }

    private static async Task<string> ReadTextAsync(IFileSystem volume, string path) => Encoding.UTF8.GetString(await ReadAllAsync(volume, path));

    private static async Task<byte[]> ReadAllAsync(IFileSystem volume, string path)
    {
        await using var file = await volume.OpenAsync(path, FileMode.Open, FileAccess.Read);
        var bytes = new byte[file.Length];
        await file.ReadAsync(0, bytes);
        return bytes;
    }

    #endregion
}
