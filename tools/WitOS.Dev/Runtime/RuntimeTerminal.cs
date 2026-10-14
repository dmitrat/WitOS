using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The terminal scenario (plan step N4.3): the device services boot with a virtio console whose character device the
/// runner owns; the device manager starts the console driver and the terminal service, and its probe asks for a line,
/// which the runner types, and echoes it to the terminal and to the log (N4.3a); then tests/User/terminal_init.c runs
/// as /bin/init with the terminal as its standard streams and checks the libc's side with what the runner types
/// (N4.3b). The required lines are in the serial log or in the terminal's transcript.
/// </summary>
internal static class RuntimeTerminal
{
    #region Constants

    /// <summary>
    /// The line the runner types once the probe's prompt appeared.
    /// </summary>
    public const string PROBE_LINE = "hello terminal";

    /// <summary>
    /// The line the runner types once /bin/init asked for one.
    /// </summary>
    public const string INIT_LINE = "hello from init";

    /// <summary>
    /// The scenario's /bin/init.
    /// </summary>
    public const string INIT = "tests/User/terminal_init.c";

    #endregion

    #region Functions

    /// <summary>
    /// Boots the scenario on the services and the install earlier runs built.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the boot passed.</returns>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_TERMINAL_SCENARIO, architecture: architecture);
        var dataDisk = Path.Combine(Path.GetDirectoryName(image)!, "data-disk.img");
        await RuntimeDataDisk.CreateAsync(dataDisk);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest($"{architecture.Name}-runtime-terminal-512", 512, 900, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = RequiredLines(architecture),
            DataDisk = dataDisk,
            Terminal = Script()
        });
        Console.WriteLine($"Terminal on witos-{architecture.Name}: the console driver, the terminal service and the libc's streams over it " +
            "passed in the guest.");
    }

    /// <summary>
    /// The scenario's package: the device services and terminal_init as /bin/init.
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
        var init = Path.Combine(output, "terminal_init.o");
        await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, INIT), init, LibWitos.Options(root));
        var program = await MuslLibc.LinkStartedProgramAsync(root, architecture, output, "terminal_init", [init]);
        return [("bin/init", program), .. services];
    }

    /// <summary>
    /// What the runner types: the probe's line, then what /bin/init asks for, each once its prompt appeared.
    /// </summary>
    /// <returns>The script.</returns>
    public static IReadOnlyList<TerminalInput> Script() =>
    [
        new TerminalInput("[TERMINAL] type a line", PROBE_LINE + "\n"),
        new TerminalInput("[TERMINAL-INIT] type a line", INIT_LINE + "\n"),
        new TerminalInput("[TERMINAL-INIT] press a key", "k"),
        new TerminalInput("[TERMINAL-INIT] press Ctrl-C", "\x03"),
        new TerminalInput("[TERMINAL-INIT] press Ctrl-D", "\x04"),
        new TerminalInput("[TERMINAL-INIT] type x", "x\n")
    ];

    /// <summary>
    /// The lines the scenario requires, in the serial log or in the terminal's transcript.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
    [
        "[VIRTIO-CONSOLE] driver of pci/",
        "serving the serial protocol",
        "[DEVICES] driver /system/Drivers/VirtioConsole of pci/",
        $"[TERMINAL] terminal service on {(architecture == KernelArchitecture.X64 ? "X64" : "Arm64")}: 80 by 24, canonical with echo, serving the terminal protocol",
        "[DEVICES] terminal service /system/Terminal on pci/",
        "[DEVICES] terminal probe: 80 by 24, canonical with echo",
        $"[DEVICES] terminal probe: echo: {PROBE_LINE}",
        $"[TERMINAL] echo: {PROBE_LINE}",
        "[ROOT-TASK] /system/DeviceManager was ready before /bin/init",
        $"[TERMINAL-INIT] echo: {INIT_LINE}",
        "[TERMINAL-INIT] standard error",
        $"[TERMINAL-INIT] terminal on {architecture.Triple[..architecture.Triple.IndexOf('-')]}: ",
        " checks passed",
        "[DEVICES] terminal service /system/Terminal: stopped",
        "[TERMINAL] terminal service: 0 sessions stopped, the console released",
        "[DEVICES] device manager: stopped",
        "[ROOT-TASK] /system/DeviceManager stopped",
        architecture.RootTaskPassedLine
    ];

    #endregion
}
