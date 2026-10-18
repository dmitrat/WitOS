using System.Text;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Storage;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The writable data disk of the runtime-devices scenario (plan steps D3.2, D4.1 and D4.2), written anew for every boot
/// with OutWit.OS's own GPT writer and FAT provider and the host's e2fsprogs, and read again after the boot to require
/// what the guest wrote: the test partition's raw pattern (the device manager's storage probe), the FAT partition's
/// files (its filesystem probe), and the ext4 partition the guest wrote through the journal, judged by e2fsprogs.
/// </summary>
internal static class RuntimeDataDisk
{
    #region Constants

    /// <summary>
    /// The disk's bytes.
    /// </summary>
    public const long BYTES = 32 * 1024 * 1024;

    /// <summary>
    /// The name of the test partition, which the storage probe writes raw.
    /// </summary>
    public const string TEST_PARTITION = "WitOS test";

    /// <summary>
    /// The bytes the storage probe writes at the start of the test partition.
    /// </summary>
    public const int TEST_PATTERN_BYTES = 320 * 1024;

    /// <summary>
    /// The first block of the test partition, at 1 MiB.
    /// </summary>
    public const long TEST_PARTITION_FIRST_BLOCK = 2048;

    /// <summary>
    /// The last block of the test partition, 1 MiB long.
    /// </summary>
    public const long TEST_PARTITION_LAST_BLOCK = 4095;

    /// <summary>
    /// The name of the FAT partition, which the filesystem probe writes through the FAT provider.
    /// </summary>
    public const string FAT_PARTITION = "WitOS FAT";

    /// <summary>
    /// The first block of the FAT partition, after the test partition.
    /// </summary>
    public const long FAT_PARTITION_FIRST_BLOCK = 4096;

    /// <summary>
    /// The last block of the FAT partition, 8 MiB long.
    /// </summary>
    public const long FAT_PARTITION_LAST_BLOCK = 20479;

    /// <summary>
    /// The name of the ext4 partition, which the guest mounts read-only.
    /// </summary>
    public const string EXT4_PARTITION = "WitOS ext4";

    /// <summary>
    /// The first block of the ext4 partition, after the FAT partition.
    /// </summary>
    public const long EXT4_PARTITION_FIRST_BLOCK = 20480;

    /// <summary>
    /// The last block of the ext4 partition, 1 MiB before the end of the disk.
    /// </summary>
    public const long EXT4_PARTITION_LAST_BLOCK = BYTES / BLOCK - 2048 - 1;

    /// <summary>
    /// The image of the ext4 volume beside the disk's, kept to compare after the boot.
    /// </summary>
    public const string EXT4_IMAGE = "ext4-volume.img";

    /// <summary>
    /// The FAT partition's label.
    /// </summary>
    public const string FAT_LABEL = "WITOS DATA";

    /// <summary>
    /// The file the host writes on the FAT partition.
    /// </summary>
    public const string HOST_FILE = "/hello.txt";

    /// <summary>
    /// What the host's file says.
    /// </summary>
    public const string HOST_TEXT = "Written on the host by OutWit.OS's FAT provider over OutWit.Common.Fat.";

    /// <summary>
    /// The file of several clusters the guest writes (the filesystem probe's GUEST_FILE).
    /// </summary>
    public const string GUEST_FILE = "/guest/written.bin";

    /// <summary>
    /// The guest's note (the filesystem probe's GUEST_NOTE).
    /// </summary>
    public const string GUEST_NOTE = "/guest/note.txt";

    /// <summary>
    /// The bytes of the guest's file.
    /// </summary>
    public const int GUEST_FILE_BYTES = 100_000;

    /// <summary>
    /// What the guest's note says (the filesystem probe's Note()).
    /// </summary>
    public const string GUEST_TEXT = "Written by WitOS through its virtio driver, the block protocol and OutWit.Common.Fat.";

    private const int BLOCK = 512;

    // The disk's identities, fixed so that every run builds the same table.
    private static readonly Guid DISK_ID = new("3f2b8c1e-6a4d-4e7b-9b52-1d0c7e5a9f31");
    private static readonly Guid TEST_PARTITION_TYPE = new("c7d4a1f0-2b6e-4f39-8a15-5749544f5354");
    private static readonly Guid TEST_PARTITION_ID = new("9e1b5d27-4c83-4a6f-b0d2-6f3e8a1c4b75");
    private static readonly Guid BASIC_DATA = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    private static readonly Guid FAT_PARTITION_ID = new("5b8e0a43-1f6c-4d27-a9e3-2c7d14f6b980");
    private static readonly Guid LINUX_DATA = new("0fc63daf-8483-4772-8e79-3d69d8477de4");
    private static readonly Guid EXT4_PARTITION_ID = new("a1d7c3e5-62b4-4f09-8d1e-3b6f5c2a7e48");

    #endregion

    #region Functions

    /// <summary>
    /// Writes the disk anew: a GUID partition table of the test partition, the FAT partition, which is formatted with the
    /// host's file on it, and the ext4 partition, which e2fsprogs makes (RuntimeExt4Volume).
    /// </summary>
    /// <param name="path">The image's path.</param>
    /// <param name="ext4">Whether to make the ext4 volume; a host without e2fsprogs leaves its partition empty.</param>
    /// <param name="programs">Files the ext4 volume holds under /programs (D5.3); none by default.</param>
    /// <param name="files">Files the ext4 volume holds at their paths from its root (N2.2); none by default.</param>
    /// <param name="bytes">The disk's size, a whole number of MiB; the ext4 partition takes what the others leave.</param>
    public static async Task CreateAsync(string path, bool ext4 = true, IReadOnlyList<(string Path, string Source)>? programs = null,
        IReadOnlyList<(string Path, string Source)>? files = null, long bytes = BYTES)
    {
        if (bytes < BYTES || bytes % (1024 * 1024) != 0)
            throw new ArgumentOutOfRangeException(nameof(bytes), $"A data disk has a whole number of MiB, at least {BYTES} bytes.");
        var ext4Last = bytes / BLOCK - 2048 - 1;
        await using var disk = new MemoryBlockStorage(bytes, BLOCK);
        var table = await GptPartitionTable.WriteAsync(disk, DISK_ID,
        [
            new GptPartition(0, TEST_PARTITION_TYPE, TEST_PARTITION_ID, TEST_PARTITION_FIRST_BLOCK, TEST_PARTITION_LAST_BLOCK, 0, TEST_PARTITION),
            new GptPartition(1, BASIC_DATA, FAT_PARTITION_ID, FAT_PARTITION_FIRST_BLOCK, FAT_PARTITION_LAST_BLOCK, 0, FAT_PARTITION),
            new GptPartition(2, LINUX_DATA, EXT4_PARTITION_ID, EXT4_PARTITION_FIRST_BLOCK, ext4Last, 0, EXT4_PARTITION)
        ]);
        if (ext4)
        {
            var volume = Ext4Image(path);
            await RuntimeExt4Volume.CreateAsync(volume, (ext4Last - EXT4_PARTITION_FIRST_BLOCK + 1) * BLOCK, programs, files);
            await disk.WriteAsync(EXT4_PARTITION_FIRST_BLOCK * BLOCK, await File.ReadAllBytesAsync(volume));
        }
        await using (var partition = table.Partitions[1].Open(disk))
        await using (var volume = await FatFileSystem.FormatAsync(partition, FAT_LABEL))
        await using (var file = await volume.OpenAsync(HOST_FILE, FileMode.CreateNew, FileAccess.Write))
            await file.WriteAsync(0, Encoding.UTF8.GetBytes(HOST_TEXT));
        await File.WriteAllBytesAsync(path, disk.Bytes.ToArray());
    }

    /// <summary>
    /// Requires what the guest wrote: the storage probe's pattern at the start of the test partition, and on the FAT
    /// partition the filesystem probe's files beside the host's, on a volume whose check is clean. The writes reached
    /// the medium, not a cache of the guest's.
    /// </summary>
    /// <param name="path">The image's path after the boot.</param>
    /// <param name="boots">The boots of the disk so far, which the ext4 volume's /boots.txt must count (D6.1); 0 to skip.</param>
    /// <returns>What the FAT partition's check found.</returns>
    /// <exception cref="InvalidOperationException">The image holds other bytes, or the volume is not clean.</exception>
    public static async Task<FileSystemCheckReport> CheckAsync(string path, int boots = 0)
    {
        var image = await File.ReadAllBytesAsync(path);
        var start = (int)(TEST_PARTITION_FIRST_BLOCK * BLOCK);
        for (var i = 0; i < TEST_PATTERN_BYTES; i++)
        {
            if (image[start + i] != TestPattern(i))
                throw new InvalidOperationException($"The data disk {path} holds {image[start + i]:x2} at byte {i} of the test partition, " +
                    $"where the device manager wrote {TestPattern(i):x2}.");
        }
        await using var disk = new MemoryBlockStorage(image, BLOCK, isReadOnly: true);
        var table = await GptPartitionTable.ReadAsync(disk);
        await using var partition = table.Partitions.Single(entry => entry.Name == FAT_PARTITION).Open(disk, isReadOnly: true);
        await using var volume = await FatFileSystem.MountAsync(partition);
        var written = await ReadAsync(volume, GUEST_FILE);
        for (var i = 0; i < GUEST_FILE_BYTES; i++)
        {
            if (written.Length != GUEST_FILE_BYTES || written[i] != GuestPattern(i))
                throw new InvalidOperationException($"The data disk's {GUEST_FILE} holds {written.Length} bytes that differ from the guest's at " +
                    $"byte {i}.");
        }
        if (Encoding.UTF8.GetString(await ReadAsync(volume, GUEST_NOTE)) != GUEST_TEXT ||
            Encoding.UTF8.GetString(await ReadAsync(volume, HOST_FILE)) != HOST_TEXT)
            throw new InvalidOperationException($"The data disk's {GUEST_NOTE} or {HOST_FILE} says something else.");
        var report = await volume.CheckAsync();
        if (!report.IsClean)
            throw new InvalidOperationException($"The data disk's FAT partition is not clean: {string.Join("; ", report.Problems)}");
        if (File.Exists(Ext4Image(path)))
        {
            var volumeImage = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "ext4-written.img");
            await File.WriteAllBytesAsync(volumeImage,
                image.AsMemory((int)(EXT4_PARTITION_FIRST_BLOCK * BLOCK), (int)((EXT4_PARTITION_LAST_BLOCK - EXT4_PARTITION_FIRST_BLOCK + 1) * BLOCK)));
            await RuntimeExt4Volume.CheckAsync(volumeImage, boots);
        }
        return report;
    }

    /// <summary>
    /// Changes the ext4 partition of an existing disk (plan step C4.1): the partition is copied out beside the disk,
    /// repaired by e2fsck (which replays a journal a session left), changed, repaired again so that its counts hold and
    /// required clean, then copied back in place. The rest of the disk stays as it is.
    /// </summary>
    /// <param name="path">The disk's image.</param>
    /// <param name="edit">The change, given the partition's image.</param>
    /// <returns>When the disk holds the change.</returns>
    /// <exception cref="InvalidDataException">The disk is not a data disk's size.</exception>
    public static async Task EditExt4Async(string path, Func<string, Task> edit)
    {
        var (volume, first, bytes) = await CopyOutExt4Async(path, ".edit");
        await RuntimeExt4Volume.RepairAsync(volume);
        await edit(volume);
        await RuntimeExt4Volume.RepairAsync(volume);
        await RuntimeExt4Volume.RequireCleanAsync(volume);
        await using (var disk = new FileStream(path, FileMode.Open, FileAccess.Write))
        await using (var image = File.OpenRead(volume))
        {
            disk.Position = first;
            await CopyAsync(image, disk, bytes);
        }
        File.Delete(volume);
    }

    /// <summary>
    /// Reads a file the guest left on the ext4 partition of a disk of any data disk's size (plan step C4.2): the
    /// partition, copied out beside the disk, was unmounted by the guest and is clean.
    /// </summary>
    /// <param name="path">The disk's image.</param>
    /// <param name="file">The file's path in the volume.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="InvalidDataException">The disk is not a data disk's size.</exception>
    /// <exception cref="InvalidOperationException">The volume is mounted or unclean, or holds no such file.</exception>
    public static async Task<byte[]> ReadExt4Async(string path, string file)
    {
        var (volume, _, _) = await CopyOutExt4Async(path, ".read");
        try
        {
            return await RuntimeExt4Volume.ReadCleanAsync(volume, file);
        }
        finally
        {
            File.Delete(volume);
        }
    }

    // The ext4 partition of a disk of any data disk's size copied out beside it: the copy, the partition's offset and length.
    private static async Task<(string Volume, long First, long Bytes)> CopyOutExt4Async(string path, string suffix)
    {
        var length = new FileInfo(path).Length;
        if (length < BYTES || length % (1024 * 1024) != 0)
            throw new InvalidDataException($"{path} is no data disk: {length} bytes.");
        var first = EXT4_PARTITION_FIRST_BLOCK * BLOCK;
        var bytes = (length / BLOCK - 2048 - EXT4_PARTITION_FIRST_BLOCK) * BLOCK;
        var volume = Ext4Image(path) + suffix;
        await using (var disk = File.OpenRead(path))
        await using (var image = File.Create(volume))
        {
            disk.Position = first;
            await CopyAsync(disk, image, bytes);
        }
        return (volume, first, bytes);
    }

    private static async Task CopyAsync(Stream from, Stream to, long bytes)
    {
        var buffer = new byte[1 << 20];
        while (bytes > 0)
        {
            var read = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytes)));
            if (read == 0)
                throw new EndOfStreamException("The disk ends inside its ext4 partition.");
            await to.WriteAsync(buffer.AsMemory(0, read));
            bytes -= read;
        }
    }

    /// <summary>
    /// The disk's sector that holds the start of a file of the ext4 partition (D6.3).
    /// </summary>
    /// <param name="path">The image's path.</param>
    /// <param name="file">The file's path in the volume.</param>
    /// <returns>The sector, in blocks of 512 bytes from the disk's start.</returns>
    public static async Task<long> Ext4SectorAsync(string path, string file)
    {
        var image = await File.ReadAllBytesAsync(path);
        var volumeImage = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "ext4-sectors.img");
        await File.WriteAllBytesAsync(volumeImage,
            image.AsMemory((int)(EXT4_PARTITION_FIRST_BLOCK * BLOCK), (int)((EXT4_PARTITION_LAST_BLOCK - EXT4_PARTITION_FIRST_BLOCK + 1) * BLOCK)));
        return EXT4_PARTITION_FIRST_BLOCK + await RuntimeExt4Volume.FirstBlockAsync(volumeImage, file) * (4096 / BLOCK);
    }

    /// <summary>
    /// Requires what a boot the host cut off while it wrote left on the ext4 partition (D6.2): a volume still mounted,
    /// whose journal the host's e2fsck replays into a clean volume.
    /// </summary>
    /// <param name="path">The image's path after the cut.</param>
    /// <returns>When the partition is judged.</returns>
    /// <exception cref="InvalidOperationException">The volume was unmounted, or e2fsck cannot recover it.</exception>
    public static async Task CheckCutAsync(string path)
    {
        var image = await File.ReadAllBytesAsync(path);
        var volumeImage = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "ext4-cut.img");
        await File.WriteAllBytesAsync(volumeImage,
            image.AsMemory((int)(EXT4_PARTITION_FIRST_BLOCK * BLOCK), (int)((EXT4_PARTITION_LAST_BLOCK - EXT4_PARTITION_FIRST_BLOCK + 1) * BLOCK)));
        await RuntimeExt4Volume.CheckCutAsync(volumeImage);
    }

    /// <summary>
    /// The byte the storage probe writes at an offset of the test partition (its StorageProbe.Pattern).
    /// </summary>
    /// <param name="offset">The offset in the partition.</param>
    /// <returns>The byte.</returns>
    public static byte TestPattern(int offset) => (byte)(offset % 509 ^ offset / 4096);

    /// <summary>
    /// The byte the filesystem probe writes at an offset of its file (its FileSystemProbe.Pattern).
    /// </summary>
    /// <param name="offset">The offset in the file.</param>
    /// <returns>The byte.</returns>
    public static byte GuestPattern(int offset) => (byte)(offset % 251 ^ offset / 2048);

    private static string Ext4Image(string disk) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(disk))!, EXT4_IMAGE);

    private static async Task<byte[]> ReadAsync(IFileSystem volume, string path)
    {
        await using var file = await volume.OpenAsync(path, FileMode.Open, FileAccess.Read);
        var bytes = new byte[file.Length];
        await file.ReadAsync(0, bytes);
        return bytes;
    }

    #endregion
}
