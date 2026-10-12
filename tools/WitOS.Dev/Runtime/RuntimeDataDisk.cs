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
    public static async Task CreateAsync(string path, bool ext4 = true, IReadOnlyList<(string Path, string Source)>? programs = null)
    {
        await using var disk = new MemoryBlockStorage(BYTES, BLOCK);
        var table = await GptPartitionTable.WriteAsync(disk, DISK_ID,
        [
            new GptPartition(0, TEST_PARTITION_TYPE, TEST_PARTITION_ID, TEST_PARTITION_FIRST_BLOCK, TEST_PARTITION_LAST_BLOCK, 0, TEST_PARTITION),
            new GptPartition(1, BASIC_DATA, FAT_PARTITION_ID, FAT_PARTITION_FIRST_BLOCK, FAT_PARTITION_LAST_BLOCK, 0, FAT_PARTITION),
            new GptPartition(2, LINUX_DATA, EXT4_PARTITION_ID, EXT4_PARTITION_FIRST_BLOCK, EXT4_PARTITION_LAST_BLOCK, 0, EXT4_PARTITION)
        ]);
        if (ext4)
        {
            var volume = Ext4Image(path);
            await RuntimeExt4Volume.CreateAsync(volume, (EXT4_PARTITION_LAST_BLOCK - EXT4_PARTITION_FIRST_BLOCK + 1) * BLOCK, programs);
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
