using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The display scenario (plan step C2.1): the device services boot with the board's ramfb, whose framebuffer the
/// firmware set up and the loader recorded in the boot contract; the platform publishes it as a device of the firmware
/// bus, the device manager discovers it and its probe draws the test pattern into it through DEVICE_MEMORY's windows,
/// after which the runner takes QEMU's screendump and the host checks the pattern by the same function; with a virtio
/// keyboard, whose driver reports the key the runner presses through QMP. tests/User/display_init.c runs as /bin/init.
/// </summary>
internal static class RuntimeDisplay
{
    #region Constants

    /// <summary>
    /// The scenario's /bin/init.
    /// </summary>
    public const string INIT = "tests/User/display_init.c";

    /// <summary>
    /// The key the runner presses once the keyboard driver is ready: its QKeyCode name, and the Linux key code the
    /// driver reports (KEY_A).
    /// </summary>
    public const string KEY = "a";

    public const int KEY_CODE = 30;

    #endregion

    #region Functions

    /// <summary>
    /// Boots the scenario on the services and the install earlier runs built.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the boot passed and the screen shows the pattern.</returns>
    /// <exception cref="InvalidOperationException">The screen does not show the pattern.</exception>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_DISPLAY_SCENARIO, architecture: architecture);
        var dataDisk = Path.Combine(Path.GetDirectoryName(image)!, "data-disk.img");
        await RuntimeDataDisk.CreateAsync(dataDisk);
        var name = $"{architecture.Name}-runtime-display-512";
        var logs = Path.Combine(root, "artifacts", "logs");
        var screen = BootScenarioRunner.ScreenPath(logs, name);
        if (File.Exists(screen))
            File.Delete(screen);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest(name, 512, 900, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = RequiredLines(architecture),
            DataDisk = dataDisk,
            Display = true,
            Keyboard = true,
            Actions =
            [
                new BootAction("[DEVICES] display probe: ", BootActionKind.Screendump),
                new BootAction("[VIRTIO-INPUT] driver of pci/", BootActionKind.Key, KEY)
            ]
        });
        var screenshot = Screenshot.Read(screen);
        screenshot.RequireTestPattern();
        Console.WriteLine($"Display on witos-{architecture.Name}: the firmware's framebuffer ({screenshot.Width} by {screenshot.Height}) shows the " +
            $"device manager's test pattern in QEMU's screendump ({screen}), and the keyboard driver reported the key.");
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
    /// The lines the scenario requires in the serial log.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
    [
        "[BOOT] Framebuffer: ",
        "[BOOT] Framebuffer memory type ",
        "[DEVICES] Device discovered: Class: FrameBuffer, Provider: Firmware, DeviceId: firmware/framebuffer, Regions: 1, Lines: 0",
        "[DEVICES] Device discovered: Class: InputDevice, Provider: Virtio, DeviceId: pci/",
        "[VIRTIO-INPUT] driver of pci/",
        "[DEVICES] driver /system/Drivers/VirtioInput of pci/",
        "[DEVICES] display probe: ",
        ": test pattern drawn",
        $"[VIRTIO-INPUT] key {KEY_CODE} down",
        $"[VIRTIO-INPUT] key {KEY_CODE} up",
        "[ROOT-TASK] /system/DeviceManager was ready before /bin/init",
        $"[DISPLAY-INIT] display scenario on {architecture.Triple[..architecture.Triple.IndexOf('-')]}: init ran",
        "[DEVICES] driver /system/Drivers/VirtioInput of pci/",
        "[DEVICES] device manager: stopped",
        "[ROOT-TASK] /system/DeviceManager stopped",
        architecture.RootTaskPassedLine
    ];

    #endregion
}
