using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Ext4;
using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Namespace.Server;
using OutWit.OS.Storage;

namespace OutWit.OS.Namespace.Volumes;

/// <summary>
/// The namespace's mount policy (plan step D5.1), explicit until configuration exists: a GUID partition named
/// "WitOS ext4" mounts at /data and one named "WitOS FAT" at /fat, each through the provider its volume needs; a disk
/// without a partition table that holds a FAT volume mounts at /boot, and so does a GUID disk's EFI system partition
/// (H3.3: the PC's one disk), read-only whatever the disk, since the firmware boots from it. A read-only disk mounts
/// read-only. Each volume reads and writes through a cache of its own (plan step H5.3: every lookup and read went to the
/// disk through its driver): blocks of 4 KiB, 32 MiB for an ext4 volume and 4 MiB for a FAT one, written through, so
/// that a write is on the disk when it completes as before (D6).
/// </summary>
internal static class VolumeMounter
{
    #region Constants

    private const string PREFIX = "[NAMESPACE] ";

    private static readonly (string Partition, string Point)[] PARTITIONS = [("WitOS ext4", "/data"), ("WitOS FAT", "/fat")];

    /// <summary>
    /// The volume that holds the home of the one account and the directory on it (plan step C1.2): the package is
    /// read-only, and programs such as PowerShell create directories under HOME at startup, so the install's
    /// environment names /data/home and the service makes it when it mounts the volume writable.
    /// </summary>
    private const string HOME_VOLUME = "/data";

    private const string HOME_DIRECTORY = "/home";

    private const int CACHE_BLOCK = 4096;

    private const int EXT4_CACHE_BLOCKS = 8192;

    private const int FAT_CACHE_BLOCKS = 1024;

    #endregion

    #region Functions

    /// <summary>
    /// Mounts what a disk holds.
    /// </summary>
    /// <param name="mounts">The namespace's volumes.</param>
    /// <param name="disk">The disk.</param>
    /// <param name="device">Its device, for the lines.</param>
    /// <returns>The resources the mounts keep open, the partitions among them.</returns>
    public static async Task<List<IAsyncDisposable>> MountAsync(MountTable mounts, IBlockStorage disk, string device)
    {
        var kept = new List<IAsyncDisposable>();
        GptPartitionTable table;
        try
        {
            table = await GptPartitionTable.ReadAsync(disk);
        }
        catch (InvalidDataException)
        {
            var whole = Cached(disk, FAT_CACHE_BLOCKS, kept);
            if (await FatFileSystem.DetectAsync(whole))
                await AddAsync(mounts, "/boot", await FatFileSystem.MountAsync(whole), $"{device}, the whole disk", whole, kept);
            return kept;
        }
        if (table.Partitions.FirstOrDefault(entry => entry.Type == GptPartition.EFI_SYSTEM) is { } system)
        {
            var storage = system.Open(disk, isReadOnly: true);
            kept.Add(storage);
            var cached = Cached(storage, FAT_CACHE_BLOCKS, kept);
            if (await FatFileSystem.DetectAsync(cached))
                await AddAsync(mounts, "/boot", await FatFileSystem.MountAsync(cached), $"{device}, partition \"{system.Name}\"", cached, kept);
        }
        foreach (var (name, point) in PARTITIONS)
        {
            var partition = table.Partitions.FirstOrDefault(entry => entry.Name == name);
            if (partition is null)
                continue;
            var storage = partition.Open(disk, disk.Info.IsReadOnly);
            kept.Add(storage);
            var ext4 = await Ext4FileSystem.DetectAsync(storage);
            var cached = Cached(storage, ext4 ? EXT4_CACHE_BLOCKS : FAT_CACHE_BLOCKS, kept);
            IFileSystem volume = ext4 ? await Ext4FileSystem.MountAsync(cached) : await FatFileSystem.MountAsync(cached);
            await AddAsync(mounts, point, volume, $"{device}, partition \"{name}\"", cached, kept);
            if (point == HOME_VOLUME && !volume.Volume.IsReadOnly)
                await EnsureDirectoryAsync(volume, HOME_DIRECTORY, point);
        }
        return kept;
    }

    private static async Task EnsureDirectoryAsync(IFileSystem volume, string path, string point)
    {
        if (await volume.GetEntryAsync(path) is not null)
            return;
        await volume.CreateDirectoryAsync(path);
        Console.WriteLine($"{PREFIX}created {point}{path}");
    }

    private static Task AddAsync(MountTable mounts, string point, IFileSystem volume, string source, CachedBlockStorage cache,
        List<IAsyncDisposable> kept)
    {
        mounts.Add(point, volume);
        kept.Add(volume);
        kept.Add(new CacheReport(point, cache)); // the last kept, so the first to go at the stop
        Console.WriteLine($"{PREFIX}mounted {point}: {volume.Volume.Format} volume{(volume.Volume.Label is { } label ? $" \"{label}\"" : "")} of " +
            $"{volume.Volume.Length} bytes, {(volume.Volume.IsReadOnly ? "read-only" : "writable")}, from {source}, " +
            $"through a cache of {cache.CapacityBytes >> 20} MiB in blocks of {cache.BlockSize} bytes");
        return Task.CompletedTask;
    }

    // A cache over the resource, written through, kept beside it; the resource stays open for its own disposal after.
    private static CachedBlockStorage Cached(IBlockStorage storage, int blocks, List<IAsyncDisposable> kept)
    {
        var cache = new CachedBlockStorage(storage, blocks, leaveOpen: true, blockSize: CACHE_BLOCK);
        kept.Add(cache);
        return cache;
    }

    #endregion

    #region Types

    // The cache's account at the stop (H5.3), before its volume unmounts.
    private sealed class CacheReport(string point, CachedBlockStorage cache) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            var read = cache.Hits + cache.Misses;
            Console.WriteLine($"{PREFIX}{point}: {cache.Hits} of {read} blocks read from the cache" +
                (read == 0 ? "" : $" ({cache.Hits * 100 / read}%)") + $", {cache.Misses} from the disk");
            return ValueTask.CompletedTask;
        }
    }

    #endregion
}
