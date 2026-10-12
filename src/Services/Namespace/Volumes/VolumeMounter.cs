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
/// without a partition table that holds a FAT volume mounts at /boot. A read-only disk mounts read-only.
/// </summary>
internal static class VolumeMounter
{
    #region Constants

    private const string PREFIX = "[NAMESPACE] ";

    private static readonly (string Partition, string Point)[] PARTITIONS = [("WitOS ext4", "/data"), ("WitOS FAT", "/fat")];

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
            if (await FatFileSystem.DetectAsync(disk))
                await AddAsync(mounts, "/boot", await FatFileSystem.MountAsync(disk), $"{device}, the whole disk", kept);
            return kept;
        }
        foreach (var (name, point) in PARTITIONS)
        {
            var partition = table.Partitions.FirstOrDefault(entry => entry.Name == name);
            if (partition is null)
                continue;
            var storage = partition.Open(disk, disk.Info.IsReadOnly);
            kept.Add(storage);
            IFileSystem volume = await Ext4FileSystem.DetectAsync(storage)
                ? await Ext4FileSystem.MountAsync(storage)
                : await FatFileSystem.MountAsync(storage);
            await AddAsync(mounts, point, volume, $"{device}, partition \"{name}\"", kept);
        }
        return kept;
    }

    private static Task AddAsync(MountTable mounts, string point, IFileSystem volume, string source, List<IAsyncDisposable> kept)
    {
        mounts.Add(point, volume);
        kept.Add(volume);
        Console.WriteLine($"{PREFIX}mounted {point}: {volume.Volume.Format} volume{(volume.Volume.Label is { } label ? $" \"{label}\"" : "")} of " +
            $"{volume.Volume.Length} bytes, {(volume.Volume.IsReadOnly ? "read-only" : "writable")}, from {source}");
        return Task.CompletedTask;
    }

    #endregion
}
