using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The terminal scenario (plan step N4.3a): the device services boot with a virtio console whose character device the
/// runner owns; the device manager starts the console driver and the terminal service, and its probe asks for a line,
/// which the runner types, and echoes it to the terminal and to the log. The step's N4.3b part runs the libc's checks
/// as /bin/init here.
/// </summary>
internal static class RuntimeTerminal
{
    #region Constants

    /// <summary>
    /// The line the runner types once the probe's prompt appeared.
    /// </summary>
    public const string TYPED_LINE = "hello terminal";

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
        await RuntimeApps.BootAsync(root, architecture, KernelImageBuilder.RUNTIME_TERMINAL_SCENARIO, RequiredLines(architecture), 900,
            terminal: Script());
        Console.WriteLine($"Terminal on witos-{architecture.Name}: the console driver, the terminal service and the typed line passed in the guest.");
    }

    /// <summary>
    /// The scenario's package: the device services and apps_init with no commands.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output, KernelArchitecture architecture) =>
        RuntimeApps.BuildPackageAsync(root, output, architecture, [], []);

    /// <summary>
    /// What the runner types: the line, once the probe's prompt appeared.
    /// </summary>
    /// <returns>The script.</returns>
    public static IReadOnlyList<TerminalInput> Script() => [new TerminalInput("[TERMINAL] type a line", TYPED_LINE + "\n")];

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
        $"[DEVICES] terminal probe: echo: {TYPED_LINE}",
        $"[TERMINAL] echo: {TYPED_LINE}",
        "[DEVICES] terminal service /system/Terminal: stopped",
        "[TERMINAL] terminal service: 0 sessions stopped, the console released"
    ];

    #endregion
}
