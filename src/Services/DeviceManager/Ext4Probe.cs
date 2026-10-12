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
/// The device manager's proof of the ext4 provider (plan step D4.2), until the namespace service of D5 mounts volumes:
/// the data disk's ext4 partition, which the host's e2fsprogs made from a known tree, mounted read-only through the
/// block channel and read back — its root, a text file, a sparse file over more extents than an inode holds, an htree
/// directory, a link and a mode — then checked. The host requires the partition unchanged after the boot.
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

    #endregion

    #region Functions

    /// <summary>
    /// Reads the ext4 partition.
    /// </summary>
    /// <param name="partition">The partition.</param>
    /// <param name="id">The device, for the lines.</param>
    /// <returns>When the volume is unmounted again.</returns>
    public static async Task ReadAsync(IBlockStorage partition, string id)
    {
        await using var volume = await Ext4FileSystem.MountAsync(partition);
        var root = await volume.EnumerateAsync("/");
        Console.WriteLine($"{PREFIX}ext4 {id}: partition \"{EXT4_PARTITION}\": {volume.Volume.Format} volume \"{volume.Volume.Label}\" of " +
            $"{volume.Volume.Length} bytes in blocks of {volume.Volume.AllocationUnit}, read-only, / holds " +
            string.Join(", ", root.Select(entry => entry.Name).Order(StringComparer.Ordinal)));
        var hello = (await volume.GetEntryAsync("/hello.txt"))!;
        Console.WriteLine($"{PREFIX}ext4 {id}: /hello.txt (mode {Convert.ToString(hello.Unix!.Mode, 8)}) says \"{await ReadTextAsync(volume, "/hello.txt")}\"");
        var big = await ReadAllAsync(volume, "/big.bin");
        Console.WriteLine($"{PREFIX}ext4 {id}: /big.bin holds {big.Length} bytes, {BIG_CHUNKS} chunks between holes, " +
            (IsBig(big) ? "as the host wrote them" : "other bytes than the host wrote"));
        var many = await volume.EnumerateAsync("/many");
        Console.WriteLine($"{PREFIX}ext4 {id}: /many holds {many.Count} entries, /link points to {await volume.ReadLinkAsync("/link")}");
        var report = await volume.CheckAsync();
        Console.WriteLine($"{PREFIX}ext4 {id}: " + (report.IsClean
            ? $"check clean ({report.Files} files, {report.Directories} directories)"
            : $"check found {report.Problems.Count} problems: {string.Join("; ", report.Problems.Take(3))}"));
    }

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
