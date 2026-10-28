using System.Text;
using WitOS.Dev.Kernel;
using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Storage;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The named volumes scenario (plan step E1, ADR 0032): beside the device services and the .NET install, a third disk
/// with an MBR and one FAT32 volume labelled TRANSFER, as Windows formats a USB drive, which the namespace service mounts at
/// /media/TRANSFER; on it an ordinary application the stock SDK publishes without a RID, which /bin/init runs from there
/// and which looks at the volumes as .NET sees them (/media, DriveInfo and its sizes through statvfs, /etc/mtab, a copy
/// to /data, a write to the drive, /media refusing a directory). After the boot the host reads what the program wrote on
/// the drive.
/// </summary>
internal static class RuntimeVolumes
{
    #region Constants

    /// <summary>
    /// The check's project.
    /// </summary>
    public const string PROJECT = "tests/Runtime.Witos/Volumes";

    /// <summary>
    /// The drive's volume label, the alias it mounts at below /media.
    /// </summary>
    public const string LABEL = "TRANSFER";

    private const string APPLICATION = "volumes";

    private const string README = "readme.txt";

    // The check's own text: the program compares what it reads with it.
    private const string README_TEXT = "Laid on the drive by the host, read by a program on the same drive.";

    private const string NOTE = "/from-guest/note.bin";

    private const int NOTE_BYTES = 100_000;

    private const int CHECKS = 10;

    // A drive of 64 MiB, its one partition from block 2048 as Windows aligns it, of type 0x0C (FAT32 with LBA), FAT32 in
    // clusters of 512 bytes as Windows formats a volume of that size.
    private const long DRIVE_BYTES = 64L << 20;
    private const long FIRST_BLOCK = 2048;
    private const int BLOCK = 512;

    private const int TIMEOUT_SECONDS = 1800;

    #endregion

    #region Functions

    /// <summary>
    /// Boots the scenario on the services and the install earlier runs built.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the boot passed and the drive holds what the program wrote.</returns>
    /// <exception cref="InvalidOperationException">The drive does not hold the program's note.</exception>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var scenario = KernelImageBuilder.RUNTIME_VOLUMES_SCENARIO;
        var output = Path.Combine(root, "artifacts", architecture.Name, scenario);
        Directory.CreateDirectory(output);
        var published = await RuntimeApps.PublishAsync(root, output, PROJECT, APPLICATION);
        var package = await RuntimeApps.PackageAsync(root, output, architecture, [], [Command()]);
        var drive = Path.Combine(output, "transfer-drive.img");
        await WriteDriveAsync(drive, published);
        await RuntimeApps.BootAsync(root, architecture, scenario, RequiredLines(architecture), TIMEOUT_SECONDS, package: package, extraDisk: drive);
        var note = await ReadNoteAsync(drive);
        if (note.Length != NOTE_BYTES || note.Select((value, i) => value == (byte)i).Contains(false))
            throw new InvalidOperationException($"The drive does not hold the {NOTE_BYTES} bytes of {NOTE} the program wrote ({note.Length} bytes).");
        Console.WriteLine($"Named volumes on witos-{architecture.Name}: the namespace mounted the MBR drive's FAT volume at /media/{LABEL}, a " +
            $"program published without a RID ran from it and saw the volumes through /media, DriveInfo and /etc/mtab, and the drive holds what " +
            "it wrote.");
    }

    /// <summary>
    /// What /bin/init runs: the program from the drive through dotnet.
    /// </summary>
    /// <returns>The line of /etc/apps.</returns>
    public static string Command() => $"/dotnet/dotnet /media/{LABEL}/{APPLICATION}/Volumes.dll";

    /// <summary>
    /// The lines the scenario requires: the drive mounted below /media, every check of the program and its exit.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
    [
        $"[NAMESPACE] mounted /media/{LABEL}: FAT32 volume \"{LABEL}\"",
        $", partition 1 of type 0x0C, through a cache of",
        // /tmp, the boot disk's /boot, the data disk's /data and /fat, and the drive's.
        "[NAMESPACE] namespace service: 5 volumes mounted, serving their files",
        $"[VOLUMES] on {architecture.OsArchitecture}: {CHECKS} of {CHECKS} checks passed",
        $"[APPS-INIT] {Command()} exited with 0"
    ];

    // The drive: an MBR with one partition holding a FAT32 volume, the readme and the published program under its directory.
    private static async Task WriteDriveAsync(string path, string published)
    {
        await using var disk = new MemoryBlockStorage(DRIVE_BYTES, BLOCK);
        var table = await MbrPartitionTable.WriteAsync(disk, 0x57495431,
            [new MbrPartition(1, 0x0C, FIRST_BLOCK, DRIVE_BYTES / BLOCK - FIRST_BLOCK, false)]);
        await using (var partition = table.Partitions[0].Open(disk))
        await using (var volume = await FatFileSystem.FormatAsync(partition, LABEL, "FAT32"))
        {
            await using (var file = await volume.OpenAsync("/" + README, FileMode.CreateNew, FileAccess.Write))
                await file.WriteAsync(0, Encoding.UTF8.GetBytes(README_TEXT));
            await volume.CreateDirectoryAsync("/" + APPLICATION);
            foreach (var source in Directory.EnumerateFiles(published).Order(StringComparer.Ordinal))
            {
                await using var file = await volume.OpenAsync($"/{APPLICATION}/{Path.GetFileName(source)}", FileMode.CreateNew, FileAccess.Write);
                await file.WriteAsync(0, await File.ReadAllBytesAsync(source));
            }
            await volume.FlushAsync();
        }
        await File.WriteAllBytesAsync(path, disk.Bytes.ToArray());
    }

    // What the program wrote on the drive, read back through the same table and provider.
    private static async Task<byte[]> ReadNoteAsync(string path)
    {
        await using var disk = new MemoryBlockStorage(await File.ReadAllBytesAsync(path), BLOCK, isReadOnly: true);
        var table = await MbrPartitionTable.ReadAsync(disk);
        await using var partition = table.Partitions[0].Open(disk, isReadOnly: true);
        await using var volume = await FatFileSystem.MountAsync(partition);
        var entry = await volume.GetEntryAsync(NOTE);
        if (entry is null)
            return [];
        await using var file = await volume.OpenAsync(NOTE, FileMode.Open, FileAccess.Read);
        var bytes = new byte[entry.Length];
        await file.ReadAsync(0, bytes);
        return bytes;
    }

    #endregion
}
