using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OutWit.OS.Communication;
using OutWit.OS.DeviceManager.Drivers;
using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Resources;
using OutWit.OS.Storage;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The device manager's proof of a block driver's service (plan step D3.2), until the namespace service of D5 holds the
/// block channels: the disk as a block storage resource on the driver's block channel, its GUID partition table read
/// through it, a write refused by a read-only disk, and on a writable disk's test partition a durable write of more
/// than one window read back; then the FAT volumes of D4.1 (<see cref="FileSystemProbe"/>): the read-only boot disk's
/// — or, on the PC's one disk (H3.3), its EFI system partition's — and the data disk's FAT partition; and the data disk's
/// ext4 partition of D4.2 (<see cref="Ext4Probe"/>).
/// </summary>
internal static class StorageProbe
{
    #region Constants

    /// <summary>
    /// The name of the partition the probe writes, which runtime-devices' data disk carries.
    /// </summary>
    public const string TEST_PARTITION = "WitOS test";

    /// <summary>
    /// The bytes the probe writes at the start of the test partition, which runtime-devices reads back from the disk's
    /// image: more than one window, so that the write takes two transfers.
    /// </summary>
    public const int PATTERN_BYTES = 320 * 1024;

    private const string PREFIX = "[DEVICES] ";

    #endregion

    #region Functions

    /// <summary>
    /// Probes the disk a ready driver serves on its block channel.
    /// </summary>
    /// <param name="driver">The driver's activation, with its block channel.</param>
    /// <returns>The disk, open for the device manager's life; null when the probe failed.</returns>
    public static async Task<ChannelBlockStorage?> RunAsync(DriverActivation driver)
    {
        var id = driver.Record.Id;
        ChannelBlockStorage? storage = null;
        try
        {
            var session = ChannelBlockSession.Attach(new Channel(driver.Service!));
            storage = ChannelBlockStorage.Open(session);
            var info = storage.Info;
            Console.WriteLine($"{PREFIX}storage {id}: {info.Length} bytes in blocks of {info.LogicalBlockSize}, " +
                $"{(info.IsReadOnly ? "read-only" : "writable")}, through the block channel and a window of {session.Window.Length} bytes");
            if (info.IsReadOnly)
            {
                // The driver's own check, past the resource's: a write to a read-only device.
                var reply = session.Call(new BlockRequest(BlockMessages.WRITE, 0, 0, info.LogicalBlockSize));
                Console.WriteLine($"{PREFIX}storage {id}: the driver refuses a write with errno {reply.Error}");
            }
            GptPartitionTable table;
            try
            {
                table = await GptPartitionTable.ReadAsync(storage);
            }
            catch (InvalidDataException)
            {
                Console.WriteLine($"{PREFIX}storage {id}: no GUID partition table");
                if (await FatFileSystem.DetectAsync(storage))
                    await FileSystemProbe.ReadBootVolumeAsync(storage, id);
                return storage;
            }
            Console.WriteLine($"{PREFIX}storage {id}: GUID partition table of {table.Partitions.Count} partitions: " +
                string.Join(", ", table.Partitions.Select(partition => $"\"{partition.Name}\" on blocks {partition.FirstBlock}-{partition.LastBlock}")));
            var system = table.Partitions.FirstOrDefault(partition => partition.Type == GptPartition.EFI_SYSTEM);
            if (system is not null)
            {
                await using var partition = system.Open(storage, isReadOnly: true);
                if (await FatFileSystem.DetectAsync(partition))
                    await FileSystemProbe.ReadBootVolumeAsync(partition, id);
            }
            var test = table.Partitions.FirstOrDefault(partition => partition.Name == TEST_PARTITION);
            if (test is not null && !info.IsReadOnly)
                await WriteAndReadAsync(storage, test, id);
            var fat = table.Partitions.FirstOrDefault(partition => partition.Name == FileSystemProbe.FAT_PARTITION);
            if (fat is not null && !info.IsReadOnly)
            {
                await using var partition = fat.Open(storage);
                await FileSystemProbe.WriteDataVolumeAsync(partition, id);
            }
            var ext4 = table.Partitions.FirstOrDefault(partition => partition.Name == Ext4Probe.EXT4_PARTITION);
            if (ext4 is not null)
            {
                await using var partition = ext4.Open(storage, isReadOnly: info.IsReadOnly);
                await Ext4Probe.ProbeAsync(partition, id);
            }
            return storage;
        }
        catch (Exception exception) when (exception is CapabilityException or IOException or FormatException)
        {
            Console.WriteLine($"{PREFIX}storage {id}: probe failed: {exception.Message}");
            if (storage is not null)
                await storage.DisposeAsync();
            return null;
        }
    }

    /// <summary>
    /// The byte the probe writes at an offset of the test partition: no block repeats another.
    /// </summary>
    /// <param name="offset">The offset in the partition.</param>
    /// <returns>The byte.</returns>
    public static byte Pattern(int offset) => (byte)(offset % 509 ^ offset / 4096);

    // A durable write of the pattern at the start of the partition, then a read of it; first, on a partition a previous
    // boot wrote (D6.1), a read of what it left.
    private static async Task WriteAndReadAsync(ChannelBlockStorage disk, GptPartition test, string id)
    {
        await using var partition = test.Open(disk);
        var pattern = new byte[PATTERN_BYTES];
        for (var i = 0; i < pattern.Length; i++)
            pattern[i] = Pattern(i);
        var before = new byte[PATTERN_BYTES];
        await partition.ReadAsync(0, before);
        if (before.AsSpan().SequenceEqual(pattern))
            Console.WriteLine($"{PREFIX}storage {id}: partition \"{TEST_PARTITION}\": holds the {PATTERN_BYTES} bytes a previous boot wrote");
        await partition.WriteAsync(0, pattern, WriteOptions.Durable);
        var back = new byte[PATTERN_BYTES];
        await partition.ReadAsync(0, back);
        Console.WriteLine(back.AsSpan().SequenceEqual(pattern)
            ? $"{PREFIX}storage {id}: partition \"{TEST_PARTITION}\": wrote {PATTERN_BYTES} bytes durably and read them back"
            : $"{PREFIX}storage {id}: partition \"{TEST_PARTITION}\": read back other bytes than it wrote");
    }

    #endregion
}
