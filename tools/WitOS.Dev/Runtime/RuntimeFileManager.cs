using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Storage;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The file manager of the developer scenario (plan step E4.2, goal Ц6): Arlecchino.Commander from the data volume's
/// layer on the screen, with the keyboard and a USB drive on QEMU's xHCI controller. The drive holds an interactive
/// program published by the stock SDK without a RID, as a developer brings one from Windows. The runner types through
/// QMP:
/// 1. Ctrl-F1 opens the drive picker for the left panel, which lists the volumes of the catalog (E1:
///    `DriveInfo.GetDrives` reads /etc/mtab); the drive's label typed narrows the list to it.
/// 2. F5 copies the program's folder to the right panel, /data.
/// 3. Tab, then F7 makes a folder there.
/// 4. The command line runs the program with `dotnet` under the pseudo-terminal of E4.1: it asks a name, then holds the
///    whole screen until a key, and Commander hands it the terminal and takes it back.
/// The screendump while the program holds the screen must equal the frame the host renders from the transcript, and
/// the clean volume must hold the copy.
/// </summary>
internal static class RuntimeFileManager
{
    #region Constants

    /// <summary>
    /// The interactive program.
    /// </summary>
    public const string PROJECT = "tests/Runtime.Witos/Interactive";

    /// <summary>
    /// The drive's label, its alias below /media.
    /// </summary>
    public const string LABEL = "STICK";

    /// <summary>
    /// The program's folder on the drive and, copied, on /data.
    /// </summary>
    public const string FOLDER = "interactive";

    /// <summary>
    /// The folder F7 makes on /data.
    /// </summary>
    public const string MADE = "made-here";

    /// <summary>
    /// The name typed at the program's question.
    /// </summary>
    public const string NAME = "WitOS";

    private const string APPLICATION = "interactive";

    private const int MEMORY_MIB = 1024;

    private const long DISK_BYTES = 1536L << 20; // the layer with the .NET SDK (C2.5), as runtime-commander's

    private const long DRIVE_BYTES = 64L << 20;

    private const long FIRST_BLOCK = 2048;

    private const int BLOCK = 512;

    private const int TIMEOUT_SECONDS = 3600;

    // The hint bar's word for F10, which the commander's first frame draws.
    private const string QUIT_HINT = "Quit";

    // The drive picker's title; the picker narrows its list to what is typed.
    private const string DRIVE_TITLE = "Drive";

    // The copy dialog's hint for one folder carried.
    private const string CARRYING = "a folder";

    // What the commander's notification says once a copy ended.
    private const string COPIED = "Copied";

    // The make-folder dialog's title.
    private const string NEW_FOLDER = "New folder";

    private const string QUESTION = "What is your name?";

    private const string HOLDING = "[INTERACTIVE] the screen is the program's: press a key";

    // The commander's word once a command ended and the terminal came back: before it, keys go to the program's terminal.
    private const string COMMAND_DONE = "reads what it";

    #endregion

    #region Functions

    /// <summary>
    /// Builds the package, the data disk with the layer and the drive, boots, types and judges.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">The ISA.</param>
    /// <returns>When the scenario passed.</returns>
    /// <exception cref="InvalidOperationException">The screen or the volume is not what the scenario left.</exception>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var scenario = KernelImageBuilder.RUNTIME_FILE_MANAGER_SCENARIO;
        var output = Path.Combine(root, "artifacts", architecture.Name, scenario);
        Directory.CreateDirectory(output);
        var published = await RuntimeApps.PublishAsync(root, output, PROJECT, APPLICATION);
        var package = await RuntimeApps.PackageAsync(root, output, architecture, [], ["cd /data", RuntimeCommander.ARLC]);
        var image = await KernelImageBuilder.BuildAsync(root, scenario, outputDirectory: output, architecture: architecture, package: package);
        var volume = await AppsDisk.VolumeFilesAsync(root, architecture, output);
        var dataDisk = Path.Combine(output, "data-disk.img");
        await RuntimeDataDisk.CreateAsync(dataDisk, files: volume, bytes: DISK_BYTES);
        var drive = Path.Combine(output, "usb-drive.img");
        await WriteDriveAsync(drive, published);
        var name = $"{architecture.Name}-{scenario}-{MEMORY_MIB}";
        var logs = Path.Combine(root, "artifacts", "logs");
        var screen = BootScenarioRunner.ScreenPath(logs, name);
        foreach (var stale in new[] { screen, BootScenarioRunner.TranscriptPath(screen) }.Where(File.Exists))
            File.Delete(stale);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest(name, MEMORY_MIB, TIMEOUT_SECONDS, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = RequiredLines(architecture),
            DataDisk = dataDisk,
            Display = true,
            Keyboard = true,
            UsbKeyboard = true, // the keyboard and the drive on one xHCI controller, the same on every ISA
            UsbDrive = drive,
            Terminal = [], // the virtio console mirrors the display: its transcript is what the display drew
            Actions = Actions()
        });
        var screenshot = Screenshot.Read(screen);
        var transcript = await File.ReadAllTextAsync(BootScenarioRunner.TranscriptPath(screen));
        var written = RuntimeDisplay.TerminalBytes(transcript);
        var expected = RuntimeDisplay.Render(root, screenshot.Width, screenshot.Height, written);
        screenshot.RequireFrame(expected.Frame);
        if (!expected.Screen.Text.Contains(HOLDING, StringComparison.Ordinal))
            throw new InvalidOperationException($"The screen at the screendump shows not the program holding it: {expected.Screen.Text}");
        var program = Path.Combine(published, "Interactive.dll");
        var copied = (await RuntimeDataDisk.ReadExt4Async(dataDisk, [$"/{FOLDER}/Interactive.dll"]))[0];
        var original = await File.ReadAllBytesAsync(program);
        if (!copied.SequenceEqual(original))
            throw new InvalidOperationException($"The data volume's /data/{FOLDER}/Interactive.dll is not the program the commander copied.");
        Console.WriteLine($"File manager on witos-{architecture.Name}: Arlecchino.Commander picked the USB drive at /media/{LABEL} from the " +
            $"volumes, copied the program's folder to /data, made /data/{MADE} and ran the program under a pseudo-terminal: it answered " +
            $"the question and held the screen until a key; the display's screen then equals the frame the host rendered from the " +
            $"transcript ({written.Length} bytes), and the clean volume holds the copy.");
    }

    /// <summary>
    /// What the runner types, each once its line appeared.
    /// </summary>
    public static IReadOnlyList<BootAction> Actions() =>
    [
        new("[DEVICES] terminal probe: ", BootActionKind.Key, KeyTyping.Keys(RuntimeDisplay.PROBE_TEXT + "\r")),
        new(QUIT_HINT, BootActionKind.Key, "ctrl+f1"),
        new(DRIVE_TITLE, BootActionKind.Key, KeyTyping.Keys(LABEL + "\r")),
        new(FOLDER, BootActionKind.Key, "end"),
        new("", BootActionKind.Key, "f5"),
        new(CARRYING, BootActionKind.Key, "ret"),
        new(COPIED, BootActionKind.Key, "tab"),
        new("", BootActionKind.Key, "f7"),
        new(NEW_FOLDER, BootActionKind.Key, KeyTyping.Keys(MADE + "\r")),
        new(MADE, BootActionKind.Key, KeyTyping.Keys($":dotnet /data/{FOLDER}/Interactive.dll\r")),
        new(QUESTION, BootActionKind.Key, KeyTyping.Keys(NAME + "\r")),
        new(HOLDING, BootActionKind.Screendump),
        new("", BootActionKind.Key, "k"),
        new(COMMAND_DONE, BootActionKind.Key, "f10")
    ];

    /// <summary>
    /// The lines the scenario requires.
    /// </summary>
    /// <param name="architecture">The ISA.</param>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
    [
        "[DEVICES] terminal service /system/Terminal on firmware/framebuffer, mirrored on pci/",
        $"[NAMESPACE] mounted /media/{LABEL}: FAT32 volume \"{LABEL}\"",
        QUIT_HINT,
        $"[INTERACTIVE] Hello, {NAME}!",
        HOLDING,
        $"[INTERACTIVE] the screen went back after 'k' on witos-{architecture.Name}",
        $"[APPS-INIT] {RuntimeCommander.ARLC}  exited with 0",
        "[APPS-INIT] 1 commands, 0 failed",
        "[DEVICES] device manager: stopped",
        architecture.RootTaskPassedLine
    ];

    // The drive: an MBR with one partition holding a FAT32 volume and the program's folder.
    private static async Task WriteDriveAsync(string path, string published)
    {
        await using var disk = new MemoryBlockStorage(DRIVE_BYTES, BLOCK);
        var table = await MbrPartitionTable.WriteAsync(disk, 0x57495434, [new MbrPartition(1, 0x0C, FIRST_BLOCK, DRIVE_BYTES / BLOCK - FIRST_BLOCK, false)]);
        await using (var partition = table.Partitions[0].Open(disk))
        await using (var volume = await FatFileSystem.FormatAsync(partition, LABEL, "FAT32"))
        {
            await volume.CreateDirectoryAsync("/" + FOLDER);
            foreach (var file in Directory.EnumerateFiles(published, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(published, file).Replace('\\', '/');
                var target = $"/{FOLDER}/{relative}";
                var parent = target[..target.LastIndexOf('/')];
                if (await volume.GetEntryAsync(parent) is null)
                    await volume.CreateDirectoryAsync(parent);
                await using var handle = await volume.OpenAsync(target, FileMode.CreateNew, FileAccess.Write);
                await handle.WriteAsync(0, await File.ReadAllBytesAsync(file));
            }
            await volume.FlushAsync();
        }
        await File.WriteAllBytesAsync(path, disk.Bytes.ToArray());
    }

    #endregion
}
