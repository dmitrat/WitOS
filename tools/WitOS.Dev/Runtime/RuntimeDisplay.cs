using System.Text;
using OutWit.OS.Display;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The display scenario (plan steps C2.1 and C2.2): the device services boot with the board's ramfb, a virtio keyboard
/// and a virtio console whose character device the runner owns. The device manager draws the test pattern into the
/// framebuffer (C2.1), then starts the display service with the framebuffer and the keyboard driver's channel, and the
/// terminal service with the display's console as its main one and the virtio console as its mirror (C2.2); the runner
/// types on the keyboard through QMP what the terminal probe and tests/User/display_init.c ask for, takes QEMU's
/// screendump once init reported, and the host renders the frame it expects from the mirror's transcript with the same
/// emulator, renderer and font, which the screendump must equal pixel for pixel.
/// </summary>
internal static class RuntimeDisplay
{
    #region Constants

    /// <summary>
    /// The scenario's /bin/init.
    /// </summary>
    public const string INIT = "tests/User/display_init.c";

    /// <summary>
    /// The line the runner types on the keyboard once the terminal probe asked for one.
    /// </summary>
    public const string PROBE_TEXT = "hello";

    /// <summary>
    /// The line the runner types on the keyboard once /bin/init asked for one.
    /// </summary>
    public const string INIT_TEXT = "screen";

    /// <summary>
    /// The Linux key code of the first key typed (KEY_H), which the keyboard driver reports to the log.
    /// </summary>
    public const int FIRST_KEY_CODE = 35;

    /// <summary>
    /// The checks /bin/init counts.
    /// </summary>
    public const int INIT_CHECKS = 4;

    /// <summary>
    /// The first bytes the terminal writes to its consoles: the terminal probe's prompt. The firmware writes its own
    /// console output to the virtio console before the terminal exists, which the display never sees, so the frame the
    /// host renders starts here.
    /// </summary>
    public const string FIRST_LINE = "[TERMINAL] type a line";

    // The QKeyCode names of the lines typed, each key pressed and released in turn.
    private const string PROBE_KEYS = "h e l l o ret";
    private const string INIT_KEYS = "s c r e e n ret";
    private const string INIT_PROMPT = "[DISPLAY-INIT] type a line";

    #endregion

    #region Functions

    /// <summary>
    /// Boots the scenario on the services and the install earlier runs built.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the boot passed and the screen shows the terminal's text.</returns>
    /// <exception cref="InvalidOperationException">The screen differs from the frame the host rendered.</exception>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_DISPLAY_SCENARIO, architecture: architecture);
        var dataDisk = Path.Combine(Path.GetDirectoryName(image)!, "data-disk.img");
        await RuntimeDataDisk.CreateAsync(dataDisk);
        var name = $"{architecture.Name}-runtime-display-512";
        var logs = Path.Combine(root, "artifacts", "logs");
        var screen = BootScenarioRunner.ScreenPath(logs, name);
        foreach (var stale in new[] { screen, BootScenarioRunner.TranscriptPath(screen) }.Where(File.Exists))
            File.Delete(stale);
        var final = FinalLine(architecture);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest(name, 512, 900, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = RequiredLines(architecture),
            DataDisk = dataDisk,
            Display = true,
            Keyboard = true,
            Terminal = [], // the virtio console mirrors the display: its transcript is what the display drew
            Actions =
            [
                new BootAction("[DEVICES] terminal probe: ", BootActionKind.Key, PROBE_KEYS),
                new BootAction(INIT_PROMPT, BootActionKind.Key, INIT_KEYS),
                new BootAction(final, BootActionKind.Screendump),
                new BootAction(final, BootActionKind.Key, "ret") // init ends once the screendump is taken
            ]
        });
        var screenshot = Screenshot.Read(screen);
        var transcript = await File.ReadAllTextAsync(BootScenarioRunner.TranscriptPath(screen));
        var written = TerminalBytes(transcript);
        var expected = Render(root, screenshot.Width, screenshot.Height, written);
        screenshot.RequireFrame(expected.Frame);
        Console.WriteLine($"Display on witos-{architecture.Name}: the display service's screen ({screenshot.Width} by {screenshot.Height}, " +
            $"{expected.Screen.Columns} by {expected.Screen.Rows} cells) equals the frame the host rendered from the terminal's transcript " +
            $"({written.Length} bytes, {expected.Screen.Text.Split('\n').Length} lines), and the keys typed through QMP reached the terminal.");
    }

    /// <summary>
    /// What the terminal wrote to its consoles, out of the mirror's transcript: from the terminal probe's prompt, the
    /// terminal's first bytes, on; the firmware's console output before it never reached the display.
    /// </summary>
    /// <param name="transcript">The virtio console's transcript.</param>
    /// <returns>The terminal's bytes.</returns>
    /// <exception cref="InvalidOperationException">The terminal wrote nothing to the mirror.</exception>
    public static byte[] TerminalBytes(string transcript)
    {
        var first = transcript.IndexOf(FIRST_LINE, StringComparison.Ordinal);
        if (first < 0)
            throw new InvalidOperationException($"The terminal's transcript holds no \"{FIRST_LINE}\": the virtio console did not mirror the display.");
        return Encoding.UTF8.GetBytes(transcript[first..]);
    }

    /// <summary>
    /// Renders the frame the display shows for what the terminal wrote: the same emulator, renderer and font as the
    /// display service's, on a framebuffer of the screen's size in the firmware's format.
    /// </summary>
    /// <param name="root">Repository root, for the pinned font.</param>
    /// <param name="width">The screen's width.</param>
    /// <param name="height">The screen's height.</param>
    /// <param name="written">The bytes the terminal wrote to its consoles.</param>
    /// <returns>The frame and the screen it shows.</returns>
    public static (MemoryFrame Frame, TerminalScreen Screen) Render(string root, int width, int height, ReadOnlySpan<byte> written)
    {
        var font = BitmapFont.ParseBdf(File.ReadAllText(SpleenFont.BdfPath(root)));
        var geometry = new FramebufferGeometry(width, height, width, FramebufferGeometry.BGRX);
        var frame = new MemoryFrame(geometry);
        var renderer = new FrameRenderer(geometry, font, frame);
        var screen = new TerminalScreen(renderer.Columns, renderer.Rows);
        renderer.Clear();
        screen.Feed(written);
        renderer.Render(screen);
        return (frame, screen);
    }

    /// <summary>
    /// The scenario's package: the device services and display_init as /bin/init.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var services = RuntimeDevices.ServiceFiles(root, architecture);
        await Sysroot.BuildAsync(root, architecture);
        var init = Path.Combine(output, "display_init.o");
        await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, INIT), init, LibWitos.Options(root));
        var program = await MuslLibc.LinkStartedProgramAsync(root, architecture, output, "display_init", [init]);
        return [("bin/init", program), .. services];
    }

    /// <summary>
    /// The last line /bin/init writes before it waits for the runner's Enter, after which the screendump is taken.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    public static string FinalLine(KernelArchitecture architecture) =>
        $"[DISPLAY-INIT] display on {architecture.Triple[..architecture.Triple.IndexOf('-')]}: {INIT_CHECKS} checks passed";

    /// <summary>
    /// The lines the scenario requires in the serial log or the terminal's transcript.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
    [
        "[BOOT] Framebuffer: ",
        "[DEVICES] Device discovered: Class: FrameBuffer, Provider: Firmware, DeviceId: firmware/framebuffer, Regions: 1, Lines: 0",
        "[DEVICES] Device discovered: Class: InputDevice, Provider: Virtio, DeviceId: pci/",
        "[VIRTIO-INPUT] driver of pci/",
        ": reporting events, serving the input protocol",
        "[DEVICES] display probe: ",
        ": test pattern drawn",
        "[DISPLAY] display service on ",
        " by 37 cells of 8 by 16 (",
        "the keyboard's keys: serving the serial protocol",
        "[DEVICES] display service /system/Display on firmware/framebuffer with the keyboard pci/",
        "[TERMINAL] terminal service on ",
        "100 by 37, 2 consoles, canonical with echo",
        "[DEVICES] terminal service /system/Terminal on firmware/framebuffer, mirrored on pci/",
        "[DEVICES] terminal probe: 100 by 37, canonical with echo",
        $"[VIRTIO-INPUT] key {FIRST_KEY_CODE} down",
        $"[DEVICES] terminal probe: echo: {PROBE_TEXT}",
        "[ROOT-TASK] /system/DeviceManager was ready before /bin/init",
        $"[DISPLAY-INIT] echo: {INIT_TEXT}",
        FinalLine(architecture),
        "[DEVICES] terminal service /system/Terminal: stopped",
        "[DISPLAY] display service for firmware/framebuffer: stopped after ",
        "[DEVICES] display service /system/Display: stopped",
        "[DEVICES] device manager: stopped",
        "[ROOT-TASK] /system/DeviceManager stopped",
        architecture.RootTaskPassedLine
    ];

    #endregion
}
