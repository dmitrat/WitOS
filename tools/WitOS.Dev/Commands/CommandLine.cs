using WitOS.Dev.CoreClr;
using WitOS.Dev.Host;
using WitOS.Dev.Interfaces;
using WitOS.Dev.Kernel;
using WitOS.Dev.NativeAot;
using WitOS.Dev.NativeAot.References;

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
        new CommandAction("setup", "Download and verify pinned QEMU and clang into .tools", Toolchain.SetupAsync),
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
        new CommandFormat(check: false),
        new CommandFormat(check: true),
        new CommandFingerprint(),
        new CommandScenario("coreclr-memory", "Test owned executable memory backend (not guest CoreCLR)", "coreclr-memory",
        [
            new BootRequest("coreclr-memory-128", 128, 120, ExpectedOutcome.Success) { Suite = BootSuite.CoreClrMemory },
            new BootRequest("coreclr-memory-512", 512, 120, ExpectedOutcome.Success) { Suite = BootSuite.CoreClrMemory, CpuModel = "max" }
        ]),
        new CommandScenario("coreclr-storage",
            "Test unchanged assembly delivery and readonly guest IO (not guest CoreCLR)", "coreclr-storage",
        [
            new BootRequest("coreclr-storage-128", 128, 120, ExpectedOutcome.Success) { Suite = BootSuite.CoreClrStorage },
            new BootRequest("coreclr-storage-512", 512, 120, ExpectedOutcome.Success) { Suite = BootSuite.CoreClrStorage }
        ]),
        new CommandAction("coreclr-source", "Build pinned CoreCLR/JIT Windows reference and inventory platform imports",
            CoreClrExperiment.RunAsync),
        new CommandAction("coreclr-host", "Build upstream Windows host and verify standard runtimeconfig/deps binding",
            CoreClrHostReference.RunAsync),
        new CommandAction("coreclr-host-files", "Verify pinned hosting PAL file contracts with a hosted syscall model",
            CoreClrHostFilePal.RunAsync),
        new CommandAction("coreclr-host-guest",
            "Build upstream hostfxr and hostpolicy for the guest and check their unresolved externals",
            CoreClrHostGuest.RunAsync),
        new CommandAction("coreclr-guest",
            "Link upstream CoreCLR for the guest from the reference build's objects and check its unresolved externals",
            CoreClrGuest.RunAsync),
        new CommandAction("coreclr-functions",
            "Compare dynamic function-table registration and target unwind with Windows (hosted)",
            CoreClrFunctionTableReference.RunAsync),
        new CommandAction("runtime-audit", "Verify pinned NativeAOT sources and package provenance", RuntimeExperiment.AuditAsync),
        new CommandAction("runtime-probe", "Publish and execute a hosted NativeAOT dependency probe", RuntimeExperiment.ProbeAsync),
        new CommandAction("runtime-target",
            "Inspect NativeAOT objects and test native-host bootstrap / strict link boundaries", RuntimeTargetExperiment.RunAsync),
        new CommandScenario("runtime-port", "Build pinned GC memory adapter and execute guest checks in QEMU", "runtime-port",
            [new BootRequest("runtime-port-256", 256, 60, ExpectedOutcome.Success)]),
        new CommandAction("runtime-source", "Build full upstream native libraries and verify the WitOS source overlay",
            RuntimeSourceBuild.RunAsync),
        new CommandRuntimeBoot(rebuildRuntime: false),
        new CommandRuntimeBoot(rebuildRuntime: true),
        new CommandAction("runtime-readiness", "Build source runtime and audit minimal standard-CoreLib executable startup",
            RuntimeSourceBuild.RunAsync),
        new CommandAction("runtime-unwind", "Compare the pinned AMD64 unwinder with Windows (hosted)", async root =>
        {
            await RuntimeExperiment.AuditAsync(root);
            await RuntimeUnwindReference.RunAsync(root, await Toolchain.FindMsvcAsync(root));
        }),
        new CommandAction("runtime-exception", "Verify Windows exception/VEH reference semantics (hosted)",
            async root => await RuntimeExceptionReference.RunAsync(root, await Toolchain.FindMsvcAsync(root))),
        new CommandAction("runtime-gp", "Verify Windows x64 general-protection translation (hosted)",
            async root => await RuntimeGpReference.RunAsync(root, await Toolchain.FindMsvcAsync(root))),
        new CommandAction("runtime-failfast", "Verify Windows fail-fast debugger record/context (hosted)",
            async root => await RuntimeFailFastReference.RunAsync(root, await Toolchain.FindMsvcAsync(root))),
        new CommandAction("runtime-seh", "Verify compiler scope tables and real filter/finally ABI (hosted)",
            async root => await RuntimeSehReference.RunAsync(root, await Toolchain.FindMsvcAsync(root))),
        new CommandAction("runtime-gc-policy", "Audit write-watch exclusion in existing source-built GC objects",
            RuntimeGcPolicy.ExistingAsync),
        new CommandAction("runtime-encoding", "Compare UTF conversions with Windows APIs (hosted)",
            async root => await RuntimeEncodingReference.RunAsync(root, await Toolchain.FindMsvcAsync(root))),
        new CommandScenario("runtime-config",
            "Build upstream configuration/startup sources and execute their guest probe", "runtime-config",
        [
            new BootRequest("runtime-config-128", 128, 120, ExpectedOutcome.Success) { Suite = BootSuite.RuntimeConfig },
            new BootRequest("runtime-config-512", 512, 120, ExpectedOutcome.Success) { Suite = BootSuite.RuntimeConfig },
            new BootRequest("runtime-config-intel", 256, 120, ExpectedOutcome.Success)
            {
                Suite = BootSuite.RuntimeConfig,
                CpuModel = "Nehalem"
            },
            new BootRequest("runtime-config-avx", 256, 120, ExpectedOutcome.Success)
            {
                Suite = BootSuite.RuntimeConfig,
                CpuModel = "max"
            }
        ], RuntimeSourceBuild.RunAsync)
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
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "The current development host is Windows x64 with Visual Studio C++ tools. The guest does not use Windows.");
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
