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
            "Test boot, physical pages, CPU exceptions and timeout handling; arm64: the kernel foundation and EL0 isolation",
            (root, architecture) => architecture == KernelArchitecture.X64
                ? KernelTestSuite.RunAsync(root)
                : KernelTestSuite.RunFoundationAsync(root, architecture)),
        new CommandScenario("release", "Build and boot the release kernel without self-tests", KernelImageBuilder.RELEASE_SCENARIO,
        [
            new BootRequest("release-128", 128, 60, ExpectedOutcome.Success) { Suite = BootSuite.Release },
            new BootRequest("release-512", 512, 60, ExpectedOutcome.Success) { Suite = BootSuite.Release }
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
        new CommandArchitecture("runtime-witos",
            "Apply the witos patch set to the pinned dotnet/runtime on a Linux host; build CoreLib, then NativeAOT's native part and CoreLib against the sysroot, measure the configure's try_run answers in the guest, compile programs for witos with ILC and run the first program and the M3 acceptance in the guest (R1.1, R1.2b, R1.3, R2.1, R2.2)",
            RuntimeWitos.BuildAsync),
        new CommandFormat(check: false),
        new CommandFormat(check: true),
        new CommandFingerprint()
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
