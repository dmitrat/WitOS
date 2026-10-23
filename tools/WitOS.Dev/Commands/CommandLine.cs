using WitOS.Dev.Host;
using WitOS.Dev.Interfaces;
using WitOS.Dev.Kernel;
using WitOS.Dev.Runtime;

namespace WitOS.Dev.Commands;

/// <summary>
/// Entry point of the development tool: selects a command by name and reports failures as exit code 1.
/// </summary>
internal static class CommandLine
{
    #region Constants

    private const string HELP_COMMAND = "help";

    private const string HELP_HEADER = "WitOS development tool\nUsage: dotnet run --project tools/WitOS.Dev -- <command> [options]";

    #endregion

    #region Fields

    // Help lists the commands in this order.
    private static readonly IReadOnlyList<ICommand> COMMANDS =
    [
        new CommandDoctor(),
        new CommandAction("setup", "Download and verify pinned QEMU, clang, musl and libc-test into .tools", Toolchain.SetupAsync),
        new CommandArchitecture("build", "Build the UEFI image (no VM)",
            (root, architecture) => KernelImageBuilder.BuildAsync(root, "boot", architecture: architecture)),
        new CommandScenario("run", "Build and boot headlessly in QEMU", "boot",
            [new BootRequest("boot-256", 256, 60, ExpectedOutcome.Success)]),
        new CommandArchitecture("test",
            "Test boot, physical pages, CPU exceptions and timeout handling; arm64 and riscv64: the kernel foundation and the user-mode isolation, with the layer-2 scenarios and the second processor where the port has them",
            (root, architecture) => architecture == KernelArchitecture.X64
                ? KernelTestSuite.RunAsync(root)
                : KernelTestSuite.RunFoundationAsync(root, architecture)),
        new CommandScenario("release", "Build and boot the release kernel without self-tests, once with the board's display (C2.1) and, on x64, once on QEMU's pc machine without a virtio device (H1.1)", KernelImageBuilder.RELEASE_SCENARIO,
        architecture => [
            new BootRequest("release-128", 128, 60, ExpectedOutcome.Success) { Suite = BootSuite.Release },
            new BootRequest("release-512", 512, 60, ExpectedOutcome.Success) { Suite = BootSuite.Release },
            // The firmware's framebuffer in the boot contract (C2.1): the loader records ramfb's, the kernel takes it and the
            // platform publishes it as a device.
            new BootRequest("release-display-256", 256, 60, ExpectedOutcome.Success)
            {
                Suite = BootSuite.Release, Display = true, RequiredLines = ["[BOOT] Framebuffer: ", "[BOOT] Framebuffer memory type "]
            },
            // The pc board (H1.1): QEMU's i440FX and PIIX3 without a virtio device, the boot disk on IDE and the default
            // VGA card's GOP as the framebuffer, as VMware's 440BX and PIIX4 boot the same kernel; on two processors, so that
            // the PIC's line reaches the boot processor through LINT0 on PIIX too. The self-test kernel stays on q35: its
            // fixtures know the virtio block function by its ECAM configuration space, which an i440FX board has not.
            .. architecture == KernelArchitecture.X64
                ? [new BootRequest("release-pc-256", 256, 60, ExpectedOutcome.Success)
                {
                    Suite = BootSuite.Release, Pc = true, IdeBootDisk = true, Processors = 2,
                    RequiredLines = ["[BOOT] Framebuffer: ", "Processors present/online: 2/2", "PCI: 5 functions, 5 described, ECAM 0x"]
                }]
                : Array.Empty<BootRequest>()
        ]),
        new CommandScenario("libc", "Build the pinned musl over ABI-1 and boot the first libc program as the root task (S1.1)",
            KernelImageBuilder.LIBC_SCENARIO,
            architecture => [new BootRequest("libc-256", 256, 60, ExpectedOutcome.Success) { Suite = BootSuite.Release, RequiredLines = [architecture.LibcPassedLine] }]),
        new CommandScenario("libc-test", "Build the selected musl libc-test programs, static and dynamic, and boot them a process each under the root task (S1.3, S7.1)",
            KernelImageBuilder.LIBC_TEST_SCENARIO,
            architecture => [new BootRequest("libc-test-256", 256, 120, ExpectedOutcome.Success) { Suite = BootSuite.Release, RequiredLines = [architecture.LibcTestPassedLine] }]),
        new CommandScenario("cxx", "Build the pinned LLVM C++ runtime over the libc and boot the C++ scenarios as the root task (S4)",
            KernelImageBuilder.CXX_SCENARIO,
            architecture => [new BootRequest("cxx-256", 256, 120, ExpectedOutcome.Success) { Suite = BootSuite.Release, RequiredLines = [architecture.CxxPassedLine] }]),
        new CommandScenario("process", "Boot the system layer's root task, whose process manager serves /bin/init's posix_spawn (S6.1)",
            KernelImageBuilder.PROCESS_SCENARIO,
            architecture => [new BootRequest("process-256", 256, 60, ExpectedOutcome.Success)
            {
                Suite = BootSuite.Release,
                RequiredLines = [architecture.InitPassedLine, architecture.RootTaskPassedLine]
            }]),
        new CommandScenario("spawn", "Build libwitos and libc.so and boot a root task that starts static and dynamic programs of the boot package (S5.2, S5.3)",
            KernelImageBuilder.SPAWN_SCENARIO,
            architecture => [new BootRequest("spawn-256", 256, 60, ExpectedOutcome.Success)
            {
                Suite = BootSuite.Release,
                RequiredLines =
                [
                    architecture.SpawnChildLine, architecture.DynamicPassedLine, architecture.PhaseSPassedLine,
                    architecture.CxxPassedLine, architecture.SpawnPassedLine
                ]
            }]),
        new CommandScenario("sysroot", "Build layer 2's sysroot and boot a C++ program clang's driver built against it as /bin/init (R1.2a)",
            KernelImageBuilder.SYSROOT_SCENARIO,
            architecture => [new BootRequest("sysroot-256", 256, 60, ExpectedOutcome.Success)
            {
                Suite = BootSuite.Release,
                RequiredLines = [architecture.SysrootPassedLine, architecture.RootTaskPassedLine]
            }]),
        new CommandScenario("runtime-coreclr",
            "Boot CoreCLR's runtime and JIT that runtime-witos built: /bin/init loads both with musl's dynamic linker (R3.1)",
            KernelImageBuilder.RUNTIME_CORECLR_SCENARIO,
            architecture => [new BootRequest("runtime-coreclr-256", 256, 300, ExpectedOutcome.Success)
            {
                Suite = BootSuite.Release,
                RequiredLines = [KernelArchitecture.CoreClrLoadedLine, architecture.RootTaskPassedLine]
            }]),
        new CommandScenario("runtime-corerun",
            "Boot upstream's corerun on a managed program with the CoreCLR runtime-witos built (R3.2)",
            KernelImageBuilder.RUNTIME_CORERUN_SCENARIO,
            architecture => [new BootRequest("runtime-corerun-256", 256, 600, ExpectedOutcome.Success)
            {
                Suite = BootSuite.Release,
                RequiredLines = [architecture.CoreRunLine, architecture.RootTaskPassedLine]
            }]),
        new CommandScenario("runtime-coreclr-acceptance",
            "Boot CoreCLR's M3 acceptance under corerun with the CoreCLR runtime-witos built (R3.3)",
            KernelImageBuilder.RUNTIME_CORECLR_ACCEPTANCE_SCENARIO,
            architecture => [new BootRequest("runtime-coreclr-acceptance-256", 256, 1800, ExpectedOutcome.Success)
            {
                Suite = BootSuite.Release,
                RequiredLines = [architecture.CoreClrAcceptancePassedLine, architecture.RootTaskPassedLine]
            }]),
        new CommandScenario("runtime-system-native",
            "Boot System.Native's check under corerun with the CoreCLR runtime-witos built (R4)",
            KernelImageBuilder.RUNTIME_SYSTEM_NATIVE_SCENARIO,
            architecture => [new BootRequest("runtime-system-native-256", 256, 900, ExpectedOutcome.Success)
            {
                Suite = BootSuite.Release,
                RequiredLines = [architecture.SystemNativePassedLine, architecture.RootTaskPassedLine]
            }]),
        new CommandScenario("runtime-dotnet",
            "Boot dotnet App.dll through hostfxr and hostpolicy from the shared framework runtime-witos built (R5)",
            KernelImageBuilder.RUNTIME_DOTNET_SCENARIO,
            architecture => [new BootRequest("runtime-dotnet-512", 512, 900, ExpectedOutcome.Success)
            {
                Suite = BootSuite.Release,
                RequiredLines = [architecture.DotnetLine, architecture.RootTaskPassedLine]
            }]),
        new CommandScenario("runtime-portable-app",
            "Boot an ordinary application the stock SDK published without a RID, through dotnet (R6)",
            KernelImageBuilder.RUNTIME_PORTABLE_APP_SCENARIO,
            architecture => [new BootRequest("runtime-portable-app-512", 512, 900, ExpectedOutcome.Success)
            {
                Suite = BootSuite.Release,
                RequiredLines = [KernelArchitecture.PortableAppPassedLine, architecture.RootTaskPassedLine]
            }]),
        new CommandScenario("runtime-palsuite",
            "Boot CoreCLR's PAL suite for witos, a process a test of upstream's paltestlist.txt less build/runtime/palsuite.json (R7.1)",
            KernelImageBuilder.RUNTIME_PALSUITE_SCENARIO,
            architecture => [new BootRequest("runtime-palsuite-256", 256, 3600, ExpectedOutcome.Success)
            {
                Suite = BootSuite.Release,
                RequiredLines = [architecture.PalSuitePassedLine, architecture.RootTaskPassedLine]
            }]),
        new CommandScenario("runtime-coreclr-tests",
            "Boot slices of CoreCLR's src/tests for witos, upstream's merged test runners of build/runtime/coreclr-tests.json under corerun (R7.2b)",
            KernelImageBuilder.RUNTIME_CORECLR_TESTS_SCENARIO,
            architecture => [new BootRequest("runtime-coreclr-tests-512", 512, 3600, ExpectedOutcome.Success)
            {
                Suite = BootSuite.Release,
                RequiredLines = [architecture.CoreClrTestsPassedLine, architecture.RootTaskPassedLine]
            }]),
        new CommandScenario("runtime-library-tests",
            "Boot slices of the .NET libraries' tests for witos, upstream's test projects of build/runtime/library-tests.json under xunit's console runner (R7.3)",
            KernelImageBuilder.RUNTIME_LIBRARY_TESTS_SCENARIO,
            architecture => [new BootRequest("runtime-library-tests-512", 512, 3600, ExpectedOutcome.Success)
            {
                Suite = BootSuite.Release,
                RequiredLines = [architecture.LibraryTestsPassedLine, architecture.RootTaskPassedLine]
            }]),
        new CommandRebase(),
        new CommandArchitecture("runtime-services",
            "Publish WitOS's NativeAOT services with the packages runtime-witos built and boot their scenarios, without the runtime's build (D1.1)",
            RuntimeDevices.ServicesAsync),
        new CommandArchitecture("runtime-terminal",
            "Boot the device services with a virtio console the runner types into: the console driver, the terminal service and its echo (N4.3a)",
            RuntimeTerminal.RunAsync),
        new CommandArchitecture("runtime-display",
            "Boot the device services with the board's framebuffer and a virtio keyboard: the terminal on the screen with upstream PowerShell, typed through QMP and judged against the frame the host renders from the mirror's transcript (C2.1-C2.3)",
            RuntimeDisplay.RunAsync),
        new CommandArchitecture("runtime-powershell",
            "Boot upstream PowerShell from the data volume on the terminal beside the services and the .NET install, non-interactively and under PSReadLine (C1.3)",
            RuntimePowerShell.RunAsync),
        new CommandArchitecture("runtime-commander",
            "Boot the file manager Arlecchino.Commander from the data volume's layer on the screen and drive it through the keyboard (C4.2)",
            RuntimeCommander.RunAsync),
        new CommandArchitecture("runtime-installer",
            "Boot the installer of base applications on the screen: an update of the file manager from a folder feed on /fat beside the old version, the rollback and the update again through the keyboard (C4.3)",
            RuntimeInstaller.RunAsync),
        new CommandArchitecture("runtime-csharprepl",
            "Boot CSharpRepl from the data volume's layer on the terminal beside the services and the .NET install and type C# into it (C2.5)",
            RuntimeCSharpRepl.RunAsync),
        new CommandArchitecture("runtime-compatibility",
            "Boot the .NET compatibility suite through dotnet beside the device services, on the services and the install earlier runs built (N1)",
            RuntimeCompatibility.RunAsync),
        new CommandArchitecture("runtime-aspnetcore",
            "Boot an ordinary ASP.NET Core application on Kestrel through dotnet beside the device services and request it through the port forward (W3.2)",
            RuntimeAspNetCore.RunAsync),
        new CommandPublishApps(),
        new CommandPublished(),
        new CommandShell(),
        new CommandApps(),
        new CommandAction("compatibility-matrix",
            "Write the public compatibility matrix from the suite's checks, expectations.json and build/compatibility-matrix.json (N4.5)",
            CompatibilityMatrix.WriteAsync),
        new CommandArchitecture("runtime-libraries",
            "Boot NUnit's and xUnit's test applications of representative NuGet libraries through dotnet beside the device services (N2.1)",
            RuntimeLibraries.RunAsync),
        new CommandArchitecture("runtime-processors",
            "Boot an ordinary application on four processors through dotnet beside the device services, under the workstation and the server collector (P2)",
            RuntimeProcessors.RunAsync),
        new CommandNuGetSuites(),
        new CommandArchitecture("runtime-side-by-side",
            "Backport the witos target to dotnet/runtime's release/8.0, build .NET 8's shared framework for witos and boot it beside .NET 10's install, which runtime-witos built (N3)",
            RuntimeSideBySide.BuildAsync),
        new CommandArchitecture("runtime-witos",
            "Apply the witos patch set to the pinned dotnet/runtime on a Linux host; build CoreLib, then NativeAOT's native part and CoreLib against the sysroot, measure the configure's try_run answers in the guest, compile programs for witos with ILC and run the first program and the M3 acceptance in the guest (R1.1, R1.2b, R1.3, R2.1, R2.2)",
            RuntimeWitos.BuildAsync),
        new CommandFormat(check: false),
        new CommandFormat(check: true),
        new CommandFingerprint(),
        new CommandGuestThreads()
    ];

    #endregion

    #region Functions

    /// <summary>
    /// Runs the command named by the first argument.
    /// </summary>
    /// <param name="args">Command name followed by its options.</param>
    /// <returns>0 on success; 1 after printing the failure to standard error.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var name = args.Length == 0 ? HELP_COMMAND : args[0];
            var command = COMMANDS.FirstOrDefault(candidate => candidate.Name == name);
            if (args.Length > 1 && (command is null || command.Arguments.Length == 0))
            {
                throw new ArgumentException("This command takes no options. Run help for the command list and their options.");
            }
            var root = FindRoot();
            if (name == HELP_COMMAND)
            {
                Console.WriteLine(HelpText());
                return 0;
            }
            if (command is null)
            {
                throw new ArgumentException($"Unknown command: {name}. Use help.");
            }
            await command.RunAsync(root, args[1..]);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}");
            return 1;
        }
    }

    #endregion

    #region Tools

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WitOS.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Run this command inside the WitOS repository.");
    }

    private static string HelpText()
    {
        var width = COMMANDS.Max(command => command.Name.Length);
        var lines = new List<string>();
        foreach (var command in COMMANDS)
        {
            lines.Add($"  {command.Name.PadRight(width)}  {command.Description}");
            if (command.Arguments.Length > 0)
            {
                lines.Add($"  {"".PadRight(width)}  Options: {command.Arguments}");
            }
        }
        return HELP_HEADER + "\n\n" + string.Join("\n", lines);
    }

    #endregion

    #region Properties

    /// <summary>
    /// Names of all commands, in help order.
    /// </summary>
    public static IEnumerable<string> CommandNames => COMMANDS.Select(command => command.Name);

    #endregion
}
