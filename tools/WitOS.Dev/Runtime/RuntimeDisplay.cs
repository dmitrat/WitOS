using System.Text;
using OutWit.OS.Display;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The display scenario (plan steps C2.1–C2.3): the device services boot with the board's ramfb, a virtio keyboard and a
/// virtio console whose character device the runner owns. The device manager draws the test pattern into the framebuffer
/// (C2.1), then starts the display service with the framebuffer and the keyboard driver's channel, and the terminal
/// service with the display's console as its main one and the virtio console as its mirror (C2.2); apps_init runs
/// tests/User/display_check.c and then upstream PowerShell from the data volume on that terminal (C2.3: C1's interactive
/// session on the screen), the runner types on the keyboard through QMP what the check and PowerShell's prompt ask for,
/// takes QEMU's screendump at the last prompt, and the host renders the frame it expects from the mirror's transcript
/// with the same emulator, renderer and font, which the screendump must equal pixel for pixel.
/// </summary>
internal static class RuntimeDisplay
{
    #region Constants

    /// <summary>
    /// The check of the terminal on the screen, the first command.
    /// </summary>
    public const string CHECK = "tests/User/display_check.c";

    /// <summary>
    /// The line the runner types on the keyboard once the terminal probe asked for one.
    /// </summary>
    public const string PROBE_TEXT = "hello";

    /// <summary>
    /// The line the runner types on the keyboard once the check asked for one.
    /// </summary>
    public const string CHECK_TEXT = "screen";

    /// <summary>
    /// The Linux key code of the first key typed (KEY_H), which the keyboard driver reports to the log.
    /// </summary>
    public const int FIRST_KEY_CODE = 35;

    /// <summary>
    /// The checks display_check counts.
    /// </summary>
    public const int CHECKS = 4;

    /// <summary>
    /// The first bytes the terminal writes to its consoles: the terminal probe's prompt. The firmware writes its own
    /// console output to the virtio console before the terminal exists, which the display never sees, so the frame the
    /// host renders starts here.
    /// </summary>
    public const string FIRST_LINE = "[TERMINAL] type a line";

    /// <summary>
    /// The guest's memory: PowerShell's.
    /// </summary>
    public const int MEMORY_MIB = RuntimePowerShell.MEMORY_MIB;

    /// <summary>
    /// The commands the runner types into PowerShell on the keyboard, each after the prompt: C1.3's interactive session.
    /// </summary>
    public static readonly string[] COMMANDS = ["$PSVersionTable", "1 + 2", "Get-Date", "Get-ChildItem /data"];

    private const string CHECK_PROMPT = "[DISPLAY-CHECK] type a line";
    private const int TIMEOUT_SECONDS = 3600;

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
        var pin = await PowerShell.ReadPinAsync(root);
        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_DISPLAY_SCENARIO, architecture: architecture);
        var output = Path.GetDirectoryName(image)!;
        var keep = Path.Combine(output, "keep");
        await File.WriteAllTextAsync(keep, "");
        var volume = new List<(string Path, string Source)> { ("home/.keep", keep) };
        volume.AddRange(await PowerShell.VolumeFilesAsync(root, architecture, RuntimePowerShell.VOLUME_DIRECTORY));
        var dataDisk = Path.Combine(output, "data-disk.img");
        await RuntimeDataDisk.CreateAsync(dataDisk, files: volume, bytes: RuntimePowerShell.DISK_BYTES);
        var name = $"{architecture.Name}-runtime-display-{MEMORY_MIB}";
        var logs = Path.Combine(root, "artifacts", "logs");
        var screen = BootScenarioRunner.ScreenPath(logs, name);
        foreach (var stale in new[] { screen, BootScenarioRunner.TranscriptPath(screen) }.Where(File.Exists))
            File.Delete(stale);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest(name, MEMORY_MIB, TIMEOUT_SECONDS, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = RequiredLines(architecture, pin.Version),
            DataDisk = dataDisk,
            Display = true,
            Keyboard = true,
            Terminal = [], // the virtio console mirrors the display: its transcript is what the display drew
            Actions = Actions()
        });
        var screenshot = Screenshot.Read(screen);
        var transcript = await File.ReadAllTextAsync(BootScenarioRunner.TranscriptPath(screen));
        var written = TerminalBytes(transcript);
        var expected = Render(root, screenshot.Width, screenshot.Height, written);
        screenshot.RequireFrame(expected.Frame);
        Console.WriteLine($"Display on witos-{architecture.Name}: PowerShell {pin.Version} ran on the screen through the keyboard, and the display " +
            $"service's screen ({screenshot.Width} by {screenshot.Height}, {expected.Screen.Columns} by {expected.Screen.Rows} cells) at its last " +
            $"prompt equals the frame the host rendered from the terminal's transcript ({written.Length} bytes, " +
            $"{expected.Screen.Text.Split('\n').Length} lines).");
    }

    /// <summary>
    /// What the runner does once each line appeared: types the probe's and the check's lines, then each command at
    /// PowerShell's prompt, takes the screendump at the prompt after the last command, and types exit.
    /// </summary>
    /// <returns>The actions.</returns>
    public static IReadOnlyList<BootAction> Actions() =>
    [
        new("[DEVICES] terminal probe: ", BootActionKind.Key, KeyTyping.Keys(PROBE_TEXT + "\r")),
        new(CHECK_PROMPT, BootActionKind.Key, KeyTyping.Keys(CHECK_TEXT + "\r")),
        .. COMMANDS.Select(command => new BootAction(RuntimePowerShell.PROMPT, BootActionKind.Key, KeyTyping.Keys(command + "\r"))),
        new(RuntimePowerShell.PROMPT, BootActionKind.Screendump),
        new("", BootActionKind.Key, KeyTyping.Keys("exit\r"))
    ];

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
    /// The scenario's package: PowerShell's package of applications (the device services, the .NET install, apps_init
    /// with the commands and the environment that skips the login-shell check) and display_check as /bin/display-check.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var package = (await RuntimePowerShell.PackageAsync(root, output, architecture, Commands())).ToList();
        var check = Path.Combine(output, "display_check.o");
        await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, CHECK), check, LibWitos.Options(root));
        package.Add(("bin/display-check", await MuslLibc.LinkStartedProgramAsync(root, architecture, output, "display_check", [check])));
        return package;
    }

    /// <summary>
    /// The commands apps_init runs on the terminal whose main console is the display: the check, then an interactive
    /// PowerShell with the profile that draws the console mark (C1.4).
    /// </summary>
    /// <returns>The lines of /etc/apps.</returns>
    public static string[] Commands() => ["cd /data", "/bin/display-check", RuntimePowerShell.PWSH];

    /// <summary>
    /// The check's last line.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    public static string CheckLine(KernelArchitecture architecture) =>
        $"[DISPLAY-CHECK] display on {architecture.Triple[..architecture.Triple.IndexOf('-')]}: {CHECKS} checks passed";

    /// <summary>
    /// The lines the scenario requires in the serial log or the terminal's transcript: the display's and the terminal's
    /// start, the keys typed reaching the probe and the check, C1's session on the screen — the version table with WitOS's
    /// name, the console mark with the shell's version, the prompt, the listing, PowerShell exiting with 0 — and the
    /// services' stop.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="version">PowerShell's version, which the mark's panel names.</param>
    /// <returns>The lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture, string version) =>
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
        $"[DISPLAY-CHECK] echo: {CHECK_TEXT}",
        CheckLine(architecture),
        "PSVersion",
        "WitOS",
        RuntimePowerShell.EMBLEM_ROW,
        $"PowerShell {version}",
        RuntimePowerShell.PROMPT,
        "Directory: /data",
        "[APPS-INIT] /dotnet/dotnet /data/pwsh/pwsh.dll exited with 0",
        "[APPS-INIT] 2 commands, 0 failed",
        "[DEVICES] terminal service /system/Terminal: stopped",
        "[DISPLAY] display service for firmware/framebuffer: stopped after ",
        "[DEVICES] display service /system/Display: stopped",
        "[DEVICES] device manager: stopped",
        "[ROOT-TASK] /system/DeviceManager stopped",
        architecture.RootTaskPassedLine
    ];

    #endregion
}
