using WitOS.Dev.Host;

namespace WitOS.Dev.Kernel;

/// <summary>
/// The kernel integration scenarios: successful boots, rejected handoffs, CPU and memory faults and a timeout.
/// </summary>
internal static class KernelTestSuite
{
    #region Constants

    // Firmware start under TCG dominates a foundation run of about six seconds.
    private const int FOUNDATION_TIMEOUT = 60;

    // A hanging kernel must reach Hello before QEMU is stopped.
    private const int FOUNDATION_HANG = 30;

    // The x64 hang runs every self-test before Hello: about 14 s in virtual time here, several times that on a slow
    // runner. The limit only stops QEMU; guest time does not depend on it.
    private const int SELFTEST_HANG = 60;

    // The riscv64 firmware is EDK II's DEBUG build over OpenSBI (V0), which prints its way through the boot under TCG
    // before the kernel runs; the limits only stop a hung QEMU, since guest time is virtual.
    private const int RISCV64_TIMEOUT = 120;

    private const int RISCV64_HANG = 60;

    #endregion

    #region Fields

    private static readonly (string Name, A64FaultExpectation Fault)[] A64_FAULT_SCENARIOS =
    [
        ("breakpoint", new(0x3C, "Cpu.Breakpoint", "Breakpoint")),
        ("undefined-instruction", new(0x00, "Cpu.UndefinedInstruction", "Undefined instruction")),
        ("data-abort", new(0x25, "Cpu.DataAbort", "Data abort", FaultAddress: true)),
        ("write-code", new(0x25, "Memory.WriteCode", "Data abort", Probe: true)),
        ("execute-data", new(0x21, "Memory.ExecuteData", "Instruction abort", Probe: true)),
        ("guard-low", new(0x25, "Memory.GuardLow", "Data abort", Probe: true)),
        ("guard-high", new(0x25, "Memory.GuardHigh", "Data abort", Probe: true)),
        ("readonly-alias", new(0x25, "Memory.ReadOnlyAlias", "Data abort", Probe: true)),
        ("unmapped-alias", new(0x25, "Memory.UnmappedAlias", "Data abort", Probe: true))
    ];

    // The RISC-V causes (plan step V1.2): a breakpoint, an illegal instruction, a load page fault for the unmapped probe
    // and for the unmapped alias, a store page fault for code, the guards and the read-only alias, an instruction page
    // fault for data.
    private static readonly (string Name, Rv64FaultExpectation Fault)[] RV64_FAULT_SCENARIOS =
    [
        ("breakpoint", new(3, "Cpu.Breakpoint", "Breakpoint")),
        ("undefined-instruction", new(2, "Cpu.IllegalInstruction", "Illegal instruction")),
        ("data-abort", new(13, "Cpu.LoadPageFault", "Load page fault", FaultAddress: true)),
        ("write-code", new(15, "Memory.WriteCode", "Store page fault", Probe: true)),
        ("execute-data", new(12, "Memory.ExecuteData", "Instruction page fault", Probe: true)),
        ("guard-low", new(15, "Memory.GuardLow", "Store page fault", Probe: true)),
        ("guard-high", new(15, "Memory.GuardHigh", "Store page fault", Probe: true)),
        ("readonly-alias", new(15, "Memory.ReadOnlyAlias", "Store page fault", Probe: true)),
        ("unmapped-alias", new(13, "Memory.UnmappedAlias", "Load page fault", Probe: true))
    ];

    private static readonly (string Name, FaultExpectation Fault)[] FAULT_SCENARIOS =
    [
        ("breakpoint", new(3, 0, "Cpu.Breakpoint", "Breakpoint")),
        ("divide-error", new(0, 0, "Cpu.DivideError", "Divide error")),
        ("invalid-opcode", new(6, 0, "Cpu.InvalidOpcode", "Invalid opcode")),
        ("general-protection", new(13, 0xFFF8, "Cpu.GeneralProtection", "General protection")),
        ("page-fault", new(14, 0, "Cpu.PageFault", "Page fault")),
        ("double-fault", new(8, 0, "Cpu.DoubleFault", "Double fault")),
        ("write-code", new(14, 3, "Memory.WriteCode", "Page fault", true)),
        ("execute-data", new(14, 17, "Memory.ExecuteData", "Page fault", true)),
        ("guard-low", new(14, 2, "Memory.GuardLow", "Page fault", true)),
        ("guard-high", new(14, 2, "Memory.GuardHigh", "Page fault", true)),
        ("readonly-alias", new(14, 3, "Memory.ReadOnlyAlias", "Page fault", true)),
        ("unmapped-alias", new(14, 0, "Memory.UnmappedAlias", "Page fault", true))
    ];

    #endregion

    #region Functions

    /// <summary>
    /// Builds and boots every kernel scenario; throws on the first unexpected outcome.
    /// </summary>
    /// <param name="root">Repository root.</param>
    public static async Task RunAsync(string root)
    {
        Toolchain.RequireQemu(root);
        var image = await KernelImageBuilder.BuildAsync(root, "boot");
        // The root task is the clang and lld build of layer 2 (T1); its first log line names the compiler and the ISA.
        await BootScenarioRunner.RunAsync(root, image, new BootRequest("boot-128", 128, 60, ExpectedOutcome.Success)
        {
            RequiredLines = [KernelArchitecture.X64.RootStartedLine, KernelArchitecture.X64.RootMechanismsLine]
        });
        await BootScenarioRunner.RunAsync(root, image, new BootRequest("boot-512", 512, 60, ExpectedOutcome.Success));
        // Two processors: the table names both and both come online, the second idling for inter-processor interrupts (K7.2).
        await BootScenarioRunner.RunAsync(root, image, new BootRequest("boot-smp2", 256, 60, ExpectedOutcome.Success)
        {
            Processors = 2,
            RequiredLines = ["Processors present/online: 2/2"]
        });
        await BootScenarioRunner.RunAsync(root, image,
            new BootRequest("boot-intel", 256, 60, ExpectedOutcome.Success) { CpuModel = "Nehalem" });
        // The first libc program (S1.1): unchanged musl over the system layer's dispatch, as the root task.
        var libc = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.LIBC_SCENARIO);
        await RunOnOneAndTwoAsync(root, libc, new BootRequest("libc", 256, 60, ExpectedOutcome.Success)
        {
            Suite = BootSuite.Release,
            RequiredLines = [KernelArchitecture.X64.LibcPassedLine]
        });
        // musl's own tests (S1.3): the selected functional and regression tests as one program.
        var libcTest = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.LIBC_TEST_SCENARIO);
        await RunOnOneAndTwoAsync(root, libcTest, new BootRequest("libc-test", 256, 120, ExpectedOutcome.Success)
        {
            Suite = BootSuite.Release,
            RequiredLines = [KernelArchitecture.X64.LibcTestPassedLine]
        });
        // The C++ runtime (S4): the exception scenarios in their Itanium form and the libc++ checks.
        var cxx = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.CXX_SCENARIO);
        await RunOnOneAndTwoAsync(root, cxx, new BootRequest("cxx", 256, 120, ExpectedOutcome.Success)
        {
            Suite = BootSuite.Release,
            RequiredLines = [KernelArchitecture.X64.CxxPassedLine]
        });
        // The static ELF loader (S5.2): programs of the package started in processes of their own.
        var spawn = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.SPAWN_SCENARIO);
        await RunOnOneAndTwoAsync(root, spawn, new BootRequest("spawn", 256, 60, ExpectedOutcome.Success)
        {
            Suite = BootSuite.Release,
            RequiredLines =
            [
                KernelArchitecture.X64.SpawnChildLine, KernelArchitecture.X64.DynamicPassedLine, KernelArchitecture.X64.PhaseSPassedLine,
                KernelArchitecture.X64.CxxPassedLine, KernelArchitecture.X64.SpawnPassedLine
            ]
        });
        // The process manager (S6.1): the system layer's root task serves /bin/init's posix_spawn.
        var process = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.PROCESS_SCENARIO);
        await RunOnOneAndTwoAsync(root, process, new BootRequest("process", 256, 60, ExpectedOutcome.Success)
        {
            Suite = BootSuite.Release,
            RequiredLines = [KernelArchitecture.X64.InitPassedLine, KernelArchitecture.X64.RootTaskPassedLine]
        });
        // The sysroot (R1.2a): a C++ program clang's own driver built against layer 2's sysroot, as /bin/init.
        var sysroot = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.SYSROOT_SCENARIO);
        await RunOnOneAndTwoAsync(root, sysroot, new BootRequest("sysroot", 256, 60, ExpectedOutcome.Success)
        {
            Suite = BootSuite.Release,
            RequiredLines = [KernelArchitecture.X64.SysrootPassedLine, KernelArchitecture.X64.RootTaskPassedLine]
        });
        await BootScenarioRunner.RunAsync(root, image, new BootRequest("no-rng", 256, 60, ExpectedOutcome.EntropyUnavailable));
        await BootScenarioRunner.RunAsync(root, image, new BootRequest("no-hpet", 256, 60, ExpectedOutcome.ClockUnavailable));
        var panic = await KernelImageBuilder.BuildAsync(root, "invalid-boot-info");
        await BootScenarioRunner.RunAsync(root, panic,
            new BootRequest("invalid-boot-info", 256, 60, ExpectedOutcome.InvalidBootInfo));
        var overlap = await KernelImageBuilder.BuildAsync(root, "overlapping-map");
        await BootScenarioRunner.RunAsync(root, overlap, new BootRequest("overlapping-map", 256, 60, ExpectedOutcome.InvalidMap));

        foreach (var (name, fault) in FAULT_SCENARIOS)
        {
            var faultImage = await KernelImageBuilder.BuildAsync(root, name);
            await BootScenarioRunner.RunAsync(root, faultImage,
                new BootRequest(name, 256, 60, ExpectedOutcome.Exception) { Fault = fault });
        }
        var timeout = await KernelImageBuilder.BuildAsync(root, "timeout");
        await BootScenarioRunner.RunAsync(root, timeout,
            new BootRequest("timeout", 256, SELFTEST_HANG, ExpectedOutcome.Timeout));
        Console.WriteLine("PASS: all 33 kernel integration scenarios.");
    }

    /// <summary>
    /// Boots the kernel foundation of an architecture other than x64 and requires its outcomes: success at 128 and
    /// 512 MiB with the root task's lines, the two-processor boot where the port starts secondary processors, the
    /// layer-2 scenarios where the port has a system layer, a rejected boot contract, a rejected memory map, each fatal
    /// CPU and memory permission exception and a timeout after a successful boot.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Architecture whose target has the kernel foundation and user mode.</param>
    /// <exception cref="InvalidOperationException">A scenario did not produce its outcome.</exception>
    public static async Task RunFoundationAsync(string root, KernelArchitecture architecture)
    {
        architecture.RequireQemu(root);
        var prefix = architecture.Name + "-";
        var limit = architecture == KernelArchitecture.Riscv64 ? RISCV64_TIMEOUT : FOUNDATION_TIMEOUT;
        var hang = architecture == KernelArchitecture.Riscv64 ? RISCV64_HANG : FOUNDATION_HANG;
        BootRequest Request(string name, int memory, int timeout, ExpectedOutcome expected) =>
            new(prefix + name, memory, timeout, expected) { Architecture = architecture, Suite = BootSuite.Foundation };
        var scenarios = 0;

        var image = await KernelImageBuilder.BuildAsync(root, "boot", architecture: architecture);
        await BootScenarioRunner.RunAsync(root, image,
            Request("boot-128", 128, limit, ExpectedOutcome.Success) with
            {
                RequiredLines = [architecture.RootStartedLine, architecture.RootMechanismsLine]
            });
        await BootScenarioRunner.RunAsync(root, image, Request("boot-512", 512, limit, ExpectedOutcome.Success));
        scenarios += 2;
        if (architecture.SecondaryProcessors)
        {
            await BootScenarioRunner.RunAsync(root, image,
                Request("boot-smp2", 256, limit, ExpectedOutcome.Success) with
                {
                    Processors = 2,
                    RequiredLines = ["Processors present/online: 2/2"]
                });
            scenarios++;
        }
        if (architecture.Libc)
        {
            var libc = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.LIBC_SCENARIO, architecture: architecture);
            await RunOnOneAndTwoAsync(root, libc,
                Request("libc", 256, limit, ExpectedOutcome.Success) with { Suite = BootSuite.Release, RequiredLines = [architecture.LibcPassedLine] });
            scenarios += 2;
        }
        if (architecture.CxxRuntime)
        {
            var cxx = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.CXX_SCENARIO, architecture: architecture);
            await RunOnOneAndTwoAsync(root, cxx,
                Request("cxx", 256, 120, ExpectedOutcome.Success) with { Suite = BootSuite.Release, RequiredLines = [architecture.CxxPassedLine] });
            scenarios += 2;
        }
        if (architecture.SystemLayer)
        {
            var libcTest = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.LIBC_TEST_SCENARIO, architecture: architecture);
            await RunOnOneAndTwoAsync(root, libcTest,
                Request("libc-test", 256, 120, ExpectedOutcome.Success) with { Suite = BootSuite.Release, RequiredLines = [architecture.LibcTestPassedLine] });
            var spawn = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.SPAWN_SCENARIO, architecture: architecture);
            await RunOnOneAndTwoAsync(root, spawn,
                Request("spawn", 256, limit, ExpectedOutcome.Success) with
                {
                    Suite = BootSuite.Release,
                    RequiredLines =
                    [
                        architecture.SpawnChildLine, architecture.DynamicPassedLine, architecture.PhaseSPassedLine,
                        architecture.CxxPassedLine, architecture.SpawnPassedLine
                    ]
                });
            var process = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.PROCESS_SCENARIO, architecture: architecture);
            await RunOnOneAndTwoAsync(root, process,
                Request("process", 256, limit, ExpectedOutcome.Success) with
                {
                    Suite = BootSuite.Release,
                    RequiredLines = [architecture.InitPassedLine, architecture.RootTaskPassedLine]
                });
            var sysroot = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.SYSROOT_SCENARIO, architecture: architecture);
            await RunOnOneAndTwoAsync(root, sysroot,
                Request("sysroot", 256, limit, ExpectedOutcome.Success) with
                {
                    Suite = BootSuite.Release,
                    RequiredLines = [architecture.SysrootPassedLine, architecture.RootTaskPassedLine]
                });
            scenarios += 8;
        }
        var panic = await KernelImageBuilder.BuildAsync(root, "invalid-boot-info", architecture: architecture);
        await BootScenarioRunner.RunAsync(root, panic,
            Request("invalid-boot-info", 256, limit, ExpectedOutcome.InvalidBootInfo));
        var overlap = await KernelImageBuilder.BuildAsync(root, "overlapping-map", architecture: architecture);
        await BootScenarioRunner.RunAsync(root, overlap,
            Request("overlapping-map", 256, limit, ExpectedOutcome.InvalidMap));
        scenarios += 2;
        if (architecture == KernelArchitecture.Riscv64)
        {
            foreach (var (name, fault) in RV64_FAULT_SCENARIOS)
            {
                var faultImage = await KernelImageBuilder.BuildAsync(root, name, architecture: architecture);
                await BootScenarioRunner.RunAsync(root, faultImage,
                    Request(name, 256, limit, ExpectedOutcome.Exception) with { Rv64Fault = fault });
            }
            scenarios += RV64_FAULT_SCENARIOS.Length;
        }
        else
        {
            foreach (var (name, fault) in A64_FAULT_SCENARIOS)
            {
                var faultImage = await KernelImageBuilder.BuildAsync(root, name, architecture: architecture);
                await BootScenarioRunner.RunAsync(root, faultImage,
                    Request(name, 256, limit, ExpectedOutcome.Exception) with { A64Fault = fault });
            }
            scenarios += A64_FAULT_SCENARIOS.Length;
        }
        var timeout = await KernelImageBuilder.BuildAsync(root, "timeout", architecture: architecture);
        await BootScenarioRunner.RunAsync(root, timeout, Request("timeout", 256, hang, ExpectedOutcome.Timeout));
        scenarios++;
        Console.WriteLine($"PASS: all {scenarios} {architecture.Name} foundation scenarios.");
    }

    /// <summary>
    /// Boots a layer-2 scenario on one processor and again on two (plan step P1.3c), the second named with the suffix
    /// "-smp2": the system layer's threads and processes run side by side there.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="image">The scenario's image.</param>
    /// <param name="request">The one-processor boot.</param>
    private static async Task RunOnOneAndTwoAsync(string root, string image, BootRequest request)
    {
        await BootScenarioRunner.RunAsync(root, image, request);
        await BootScenarioRunner.RunAsync(root, image, request with { Name = request.Name + "-smp2", Processors = 2 });
    }

    #endregion
}
