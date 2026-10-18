using System.Text;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The file manager scenario (plan step C4.2, ADR 0027): Arlecchino.Commander, a base application of the data volume's
/// layer built from its source, started by its launcher /data/apps/bin/arlc on the display's console beside the device
/// services and the .NET install — the screen of C2, 100 by 37 cells, its minimum being 100 by 20 — and driven through the
/// keyboard by QMP: it lists /data in its panels, opens a file in its viewer (Enter), goes back (F3), copies the file to
/// /data/tmp (F5) and quits (F10). The runner takes QEMU's screendump in the viewer, and the host renders the frame it
/// expects from the mirror's transcript, which the screendump must equal pixel for pixel, as runtime-display judges
/// PowerShell; after the boot the host requires the copy on the clean volume.
/// </summary>
internal static class RuntimeCommander
{
    #region Constants

    /// <summary>
    /// How /bin/init starts the file manager: the layer's launcher.
    /// </summary>
    public const string ARLC = "/data/apps/bin/arlc";

    /// <summary>
    /// The file the viewer opens, at the end of /data's listing.
    /// </summary>
    public const string NOTE = "zz-note.txt";

    /// <summary>
    /// The note's first line, which the viewer shows.
    /// </summary>
    public const string NOTE_TEXT = "A note on the data volume, viewed in Arlecchino.Commander on WitOS.";

    private const int MEMORY_MIB = 1024;

    private const long DISK_BYTES = 1024L << 20;

    private const int TIMEOUT_SECONDS = 3600;

    /// <summary>
    /// Where the copy goes on the data volume.
    /// </summary>
    public const string COPY = "/tmp/" + NOTE;

    // The hint bar's word for F10, which the commander's first frame draws.
    private const string QUIT_HINT = "Quit";

    // The panel's line of the entry under the cursor, drawn once the panels read /data again after the viewer: before
    // it, F5 finds no entry and copies nothing.
    private const string NOTE_ENTRY = NOTE + " · 68 B";

    // What the commander's notification says once the copy ended.
    private const string COPIED = "Copied 1 file";

    #endregion

    #region Functions

    /// <summary>
    /// Boots the scenario on the services and the install earlier runs built.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the boot passed and the screen shows the viewer.</returns>
    /// <exception cref="InvalidOperationException">The screen differs from the frame the host rendered.</exception>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var scenario = KernelImageBuilder.RUNTIME_COMMANDER_SCENARIO;
        var output = Path.Combine(root, "artifacts", architecture.Name, scenario);
        Directory.CreateDirectory(output);
        var package = await RuntimeApps.PackageAsync(root, output, architecture, [], ["cd /data", ARLC]);
        var image = await KernelImageBuilder.BuildAsync(root, scenario, outputDirectory: output, architecture: architecture, package: package);
        var volume = await AppsDisk.VolumeFilesAsync(root, architecture, output);
        var note = Path.Combine(output, NOTE);
        await File.WriteAllTextAsync(note, NOTE_TEXT + "\n");
        volume.Add((NOTE, note));
        var dataDisk = Path.Combine(output, "data-disk.img");
        await RuntimeDataDisk.CreateAsync(dataDisk, files: volume, bytes: DISK_BYTES);
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
            Terminal = [], // the virtio console mirrors the display: its transcript is what the display drew
            Actions = Actions()
        });
        var screenshot = Screenshot.Read(screen);
        var transcript = await File.ReadAllTextAsync(BootScenarioRunner.TranscriptPath(screen));
        var written = RuntimeDisplay.TerminalBytes(transcript);
        var expected = RuntimeDisplay.Render(root, screenshot.Width, screenshot.Height, written);
        screenshot.RequireFrame(expected.Frame);
        if (!expected.Screen.Text.Contains(NOTE_TEXT, StringComparison.Ordinal))
            throw new InvalidOperationException($"The screen at the screendump shows no viewer of {NOTE}: {expected.Screen.Text}");
        var copy = Encoding.UTF8.GetString(await RuntimeDataDisk.ReadExt4Async(dataDisk, COPY));
        if (copy != NOTE_TEXT + "\n")
            throw new InvalidOperationException($"The data volume's /data{COPY} holds '{copy}', not the note the commander copied.");
        Console.WriteLine($"Commander on witos-{architecture.Name}: Arlecchino.Commander listed /data, viewed {NOTE}, copied it to /data{COPY} and " +
            $"quit on the screen through the keyboard; the display's screen in the viewer equals the frame the host rendered from the " +
            $"transcript ({written.Length} bytes), and the clean volume holds the copy.");
    }

    /// <summary>
    /// What the runner does once each line appeared: the probe's line, then, once the commander drew its panels, End to
    /// the last entry of /data (the note), Enter to open it in the viewer (Arlecchino's F3 is Edit, which runs the
    /// configured editor), the screendump, F3 back to the panels (the viewer's own binding, which needs no lone Escape
    /// told apart from a sequence), F5 to copy the note once the panels show it again, "/tmp" after the field's /data
    /// (the other panel's folder, the caret at its end) and Enter, and F10 once the commander reported the copy.
    /// </summary>
    /// <returns>The actions.</returns>
    public static IReadOnlyList<BootAction> Actions() =>
    [
        new("[DEVICES] terminal probe: ", BootActionKind.Key, KeyTyping.Keys(RuntimeDisplay.PROBE_TEXT + "\r")),
        new(QUIT_HINT, BootActionKind.Key, "end"),
        new("", BootActionKind.Key, "ret"),
        new(NOTE_TEXT, BootActionKind.Screendump),
        new("", BootActionKind.Key, "f3"),
        new(NOTE_ENTRY, BootActionKind.Key, "f5"),
        new("", BootActionKind.Key, KeyTyping.Keys("/tmp\r")),
        new(COPIED, BootActionKind.Key, "f10")
    ];

    /// <summary>
    /// The lines the scenario requires in the serial log or the terminal's transcript: the services' start, the
    /// commander's panels and its viewer, its exit with 0 and the services' stop.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
    [
        "[DEVICES] display service /system/Display on firmware/framebuffer with the keyboard pci/",
        "[DEVICES] terminal service /system/Terminal on firmware/framebuffer, mirrored on pci/",
        QUIT_HINT,
        NOTE_TEXT,
        COPIED,
        $"[APPS-INIT] {ARLC}  exited with 0",
        "[APPS-INIT] 1 commands, 0 failed",
        "[DEVICES] device manager: stopped",
        architecture.RootTaskPassedLine
    ];

    #endregion
}
