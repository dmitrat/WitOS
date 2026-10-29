using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Ext4;
using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Namespace.Server;
using OutWit.OS.Namespaces;
using OutWit.OS.Storage;

namespace OutWit.OS.Namespace.Volumes;

/// <summary>
/// The namespace's mount policy (plan steps D5.1 and E1, ADR 0032), explicit until configuration exists. The system's
/// volumes keep their places: a GUID partition named "WitOS ext4" at /data, one named "WitOS FAT" at /fat, and the boot
/// volume at /boot — a GUID disk's EFI system partition (H3.3: the PC's one disk), read-only whatever the disk, since the
/// firmware boots from it, or a disk without a partition table that holds FAT. Every other volume a format WitOS reads
/// holds — another GUID partition, a partition of an MBR (a USB drive Windows formats), a FAT disk without a table once
/// /boot is taken — mounts at /media/&lt;alias&gt; through the catalog. A read-only disk mounts read-only. Each volume reads
/// and writes through a cache of its own (plan step H5.3: every lookup and read went to the disk through its driver):
/// blocks of 4 KiB, 32 MiB for an ext4 volume and 4 MiB for a FAT one, written through, so that a write is on the disk
/// when it completes as before (D6).
/// </summary>
internal static class VolumeMounter
{
    #region Constants

    private const string PREFIX = "[NAMESPACE] ";

    private static readonly (string Partition, string Alias)[] PARTITIONS = [("WitOS ext4", "Data"), ("WitOS FAT", "Fat")];

    private const string BOOT = "Boot";

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
    /// <param name="mounts">The namespace's volumes and their catalog.</param>
    /// <param name="disk">The disk.</param>
    /// <param name="device">Its device, for the lines.</param>
    /// <returns>The resources the mounts keep open, the partitions among them.</returns>
    public static async Task<List<IAsyncDisposable>> MountAsync(MountTable mounts, IBlockStorage disk, string device)
    {
        var kept = new List<IAsyncDisposable>();
        GptPartitionTable? gpt = null;
        try
        {
            gpt = await GptPartitionTable.ReadAsync(disk);
        }
        catch (InvalidDataException)
        {
        }
        if (gpt is not null)
        {
            foreach (var partition in gpt.Partitions)
            {
                var system = partition.Type == GptPartition.EFI_SYSTEM;
                var alias = system ? BOOT : PARTITIONS.FirstOrDefault(entry => entry.Partition == partition.Name).Alias;
                var storage = partition.Open(disk, system || disk.Info.IsReadOnly);
                kept.Add(storage);
                // A GUID partition's own GUID is a stronger identity than a FAT serial number.
                await MountVolumeAsync(mounts, storage, $"{device}, partition \"{partition.Name}\"", alias, partition.Id.ToString(), kept);
            }
            return kept;
        }
        MbrPartitionTable? mbr = null;
        try
        {
            mbr = await MbrPartitionTable.ReadAsync(disk);
        }
        catch (InvalidDataException)
        {
        }
        if (mbr is not null)
        {
            foreach (var partition in mbr.Partitions)
            {
                var storage = partition.Open(disk, disk.Info.IsReadOnly);
                kept.Add(storage);
                await MountVolumeAsync(mounts, storage, $"{device}, partition {partition.Index} of type 0x{partition.Type:X2}", null, null, kept);
            }
            return kept;
        }
        await MountVolumeAsync(mounts, disk, $"{device}, the whole disk", BOOT, null, kept);
        return kept;
    }

    // A volume of a resource, if a format WitOS reads holds it: mounted through its cache, attached to the catalog under
    // the system's alias while that is free and below /media otherwise.
    private static async Task MountVolumeAsync(MountTable mounts, IBlockStorage storage, string source, string? systemAlias, string? partitionId,
        List<IAsyncDisposable> kept)
    {
        var ext4 = await Ext4FileSystem.DetectAsync(storage);
        if (!ext4 && !await FatFileSystem.DetectAsync(storage))
        {
            Console.WriteLine($"{PREFIX}{source}: no volume of a format the service reads");
            return;
        }
        if (systemAlias is not null && mounts.Mounts.Any(mount => mount.Point == "/" + systemAlias.ToLowerInvariant()))
            systemAlias = null; // the system's place is taken: this one goes below /media
        var cache = Cached(storage, ext4 ? EXT4_CACHE_BLOCKS : FAT_CACHE_BLOCKS, kept);
        IFileSystem volume;
        try
        {
            volume = ext4 ? await Ext4FileSystem.MountAsync(cache) : await FatFileSystem.MountAsync(cache);
        }
        catch (FileSystemException exception) when (systemAlias is null)
        {
            // A volume of /media that the provider refuses (a format feature it does not serve, a damaged volume, bytes
            // that only look like a superblock) stays unmounted; the system's volumes fail the service as before.
            Console.WriteLine($"{PREFIX}{source}: not mounted: {exception.Message}");
            return;
        }
        var (identity, weak) = ext4 || partitionId is null ? (volume.Volume.Identity, !ext4 && volume.Volume.Identity is not null) : (partitionId, false);
        var attached = mounts.Catalog.Attach(volume, NamespaceMessages.ORIGIN_INTERNAL, systemAlias, identity, weak);
        kept.Add(volume);
        kept.Add(new CacheReport(attached.Point, cache)); // the last kept, so the first to go at the stop
        Console.WriteLine($"{PREFIX}mounted {attached.Point}: {volume.Volume.Format} volume{(volume.Volume.Label is { } label ? $" \"{label}\"" : "")} of " +
            $"{volume.Volume.Length} bytes, {(volume.Volume.IsReadOnly ? "read-only" : "writable")}, from {source}, " +
            $"through a cache of {cache.CapacityBytes >> 20} MiB in blocks of {cache.BlockSize} bytes" +
            (attached.Collision ? $"; its identity {identity} is another attached volume's too" : ""));
        if (attached.Point == HOME_VOLUME && !volume.Volume.IsReadOnly)
            await EnsureDirectoryAsync(volume, HOME_DIRECTORY, attached.Point);
    }

    private static async Task EnsureDirectoryAsync(IFileSystem volume, string path, string point)
    {
        if (await volume.GetEntryAsync(path) is not null)
            return;
        await volume.CreateDirectoryAsync(path);
        Console.WriteLine($"{PREFIX}created {point}{path}");
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
