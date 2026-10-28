using System.Text;
using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Storage;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The USB drive scenario (plan step E3): beside the device services and the .NET install, QEMU's usb-storage on a
/// qemu-xhci controller over a 64 MiB image with an MBR and a FAT32 volume labelled STICK, as Windows formats a USB
/// drive. The xHCI driver opens the drive as a SCSI disk over Bulk-Only Transport and serves it on a block channel the
/// device manager hands to the namespace service, which mounts it at /media/STICK; an ordinary program published
/// without a RID reads and writes it, asks for it to be taken out — the runner's device_del — and sees it go, asks for it
/// back — device_add — and finds what it wrote. After the boot the host reads the drive's image.
/// </summary>
internal static class RuntimeUsbDrive
{
    #region Constants

    /// <summary>
    /// The check's project.
    /// </summary>
    public const string PROJECT = "tests/Runtime.Witos/Drive";

    /// <summary>
    /// The drive's label, its alias below /media.
    /// </summary>
    public const string LABEL = "STICK";

    private const string APPLICATION = "drive";

    private const string README_TEXT = "Laid on the USB drive by the host, read by a program in WitOS.";

    private const string WRITTEN = "/from-witos/written.bin";

    private const int WRITTEN_BYTES = 200_000;

    private const int CHECKS = 8;

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
    /// <exception cref="InvalidOperationException">The drive does not hold what the program wrote.</exception>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var scenario = KernelImageBuilder.RUNTIME_USB_DRIVE_SCENARIO;
        var output = Path.Combine(root, "artifacts", architecture.Name, scenario);
        Directory.CreateDirectory(output);
        var published = await RuntimeApps.PublishAsync(root, output, PROJECT, APPLICATION);
        var package = await RuntimeApps.PackageAsync(root, output, architecture, [(APPLICATION, published)], [Command()]);
        var drive = Path.Combine(output, "usb-drive.img");
        await WriteDriveAsync(drive);
        BootAction[] actions =
        [
            new("[DRIVE] take the drive out", BootActionKind.DeviceDelete, BootRequest.USB_DRIVE_ID),
            new("[DRIVE] put the drive back", BootActionKind.DeviceAdd, BootRequest.USB_DRIVE_DEVICE)
        ];
        // The terminal with nothing typed: a USB controller is a keyboard's to the device manager, which then gives a board with a
        // framebuffer its display as the console, where /bin/init's lines go; the transcript holds them for the runner.
        await RuntimeApps.BootAsync(root, architecture, scenario, RequiredLines(architecture), TIMEOUT_SECONDS, package: package, terminal: [],
            actions: actions, usbDrive: drive);
        var written = await ReadWrittenAsync(drive);
        if (!written.SequenceEqual(Enumerable.Range(0, WRITTEN_BYTES).Select(i => (byte)(i * 13))))
            throw new InvalidOperationException($"The drive does not hold the {WRITTEN_BYTES} bytes of {WRITTEN} the program wrote ({written.Length} bytes).");
        Console.WriteLine($"A USB drive on witos-{architecture.Name}: the xHCI driver served QEMU's usb-storage as a SCSI disk over Bulk-Only " +
            $"Transport, the namespace mounted its FAT32 volume at /media/{LABEL}, a program published without a RID wrote to it, saw it taken out " +
            "and put back, and the drive's image holds what it wrote.");
    }

    /// <summary>
    /// What /bin/init runs: the program through dotnet.
    /// </summary>
    /// <returns>The line of /etc/apps.</returns>
    public static string Command() => $"/dotnet/dotnet /app/{APPLICATION}/Drive.dll";

    /// <summary>
    /// The lines the scenario requires: the drive found and served, forwarded and mounted, let go when taken out and
    /// mounted again, every check and the program's exit.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
    [
        // QEMU's port 2 of the controller is its root port 6, the second of its USB 2 ports; the lines name no port.
        ": a drive, interface 0, bulk endpoints",
        ": drive \"QEMU QEMU HARDDISK",
        "in blocks of 512, fixed, writable: serving the block protocol",
        $"[NAMESPACE] mounted /media/{LABEL}: FAT32 volume \"{LABEL}\"",
        ", partition 1 of type 0x0C, through a cache of",
        $"[NAMESPACE] /media/{LABEL} left with its drive",
        ": the drive went; the device manager is told",
        $"[DRIVE] on {architecture.OsArchitecture}: {CHECKS} of {CHECKS} checks passed",
        $"[APPS-INIT] /dotnet/dotnet /app/{APPLICATION}/Drive.dll exited with 0"
    ];

    // The drive: an MBR with one partition holding a FAT32 volume and the readme.
    private static async Task WriteDriveAsync(string path)
    {
        await using var disk = new MemoryBlockStorage(DRIVE_BYTES, BLOCK);
        var table = await MbrPartitionTable.WriteAsync(disk, 0x57495432, [new MbrPartition(1, 0x0C, FIRST_BLOCK, DRIVE_BYTES / BLOCK - FIRST_BLOCK, false)]);
        await using (var partition = table.Partitions[0].Open(disk))
        await using (var volume = await FatFileSystem.FormatAsync(partition, LABEL, "FAT32"))
        {
            await using (var file = await volume.OpenAsync("/readme.txt", FileMode.CreateNew, FileAccess.Write))
                await file.WriteAsync(0, Encoding.UTF8.GetBytes(README_TEXT));
            await volume.FlushAsync();
        }
        await File.WriteAllBytesAsync(path, disk.Bytes.ToArray());
    }

    // What the program wrote on the drive, read back through the same table and provider.
    private static async Task<byte[]> ReadWrittenAsync(string path)
    {
        await using var disk = new MemoryBlockStorage(await File.ReadAllBytesAsync(path), BLOCK, isReadOnly: true);
        var table = await MbrPartitionTable.ReadAsync(disk);
        await using var partition = table.Partitions[0].Open(disk, isReadOnly: true);
        await using var volume = await FatFileSystem.MountAsync(partition);
        var entry = await volume.GetEntryAsync(WRITTEN);
        if (entry is null)
            return [];
        await using var file = await volume.OpenAsync(WRITTEN, FileMode.Open, FileAccess.Read);
        var bytes = new byte[entry.Length];
        await file.ReadAsync(0, bytes);
        return bytes;
    }

    #endregion
}
