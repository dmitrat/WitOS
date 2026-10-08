using WitOS.Dev.Host;

namespace WitOS.Dev.Kernel;

/// <summary>
/// The 20 kernel integration scenarios: successful boots, rejected handoffs, CPU and memory faults and a timeout.
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
        await BootScenarioRunner.RunAsync(root, image, new BootRequest("boot-128", 128, 60, ExpectedOutcome.Success));
        await BootScenarioRunner.RunAsync(root, image, new BootRequest("boot-512", 512, 60, ExpectedOutcome.Success));
        // Two processors: the table names both, the boot processor alone is online (K7.1).
        await BootScenarioRunner.RunAsync(root, image, new BootRequest("boot-smp2", 256, 60, ExpectedOutcome.Success)
        {
            Processors = 2,
            RequiredLines = ["Processors present/online: 2/1"]
        });
        await BootScenarioRunner.RunAsync(root, image,
            new BootRequest("boot-intel", 256, 60, ExpectedOutcome.Success) { CpuModel = "Nehalem" });
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
        Console.WriteLine("PASS: all 21 kernel integration scenarios.");
    }

    /// <summary>
    /// Boots the kernel foundation of an architecture without user mode yet and requires its outcomes: success at
    /// 128 and 512 MiB, a rejected boot contract, a rejected memory map, each fatal CPU and memory permission
    /// exception and a timeout after a successful boot.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Architecture whose target has the kernel foundation but no user mode.</param>
    /// <exception cref="InvalidOperationException">A scenario did not produce its outcome.</exception>
    public static async Task RunFoundationAsync(string root, KernelArchitecture architecture)
    {
        architecture.RequireQemu(root);
        var prefix = architecture.Name + "-";
        BootRequest Request(string name, int memory, int timeout, ExpectedOutcome expected) =>
            new(prefix + name, memory, timeout, expected) { Architecture = architecture, Suite = BootSuite.Foundation };

        var image = await KernelImageBuilder.BuildAsync(root, "boot", architecture: architecture);
        await BootScenarioRunner.RunAsync(root, image, Request("boot-128", 128, FOUNDATION_TIMEOUT, ExpectedOutcome.Success));
        await BootScenarioRunner.RunAsync(root, image, Request("boot-512", 512, FOUNDATION_TIMEOUT, ExpectedOutcome.Success));
        await BootScenarioRunner.RunAsync(root, image,
            Request("boot-smp2", 256, FOUNDATION_TIMEOUT, ExpectedOutcome.Success) with
            {
                Processors = 2,
                RequiredLines = ["Processors present/online: 2/1"]
            });
        var panic = await KernelImageBuilder.BuildAsync(root, "invalid-boot-info", architecture: architecture);
        await BootScenarioRunner.RunAsync(root, panic,
            Request("invalid-boot-info", 256, FOUNDATION_TIMEOUT, ExpectedOutcome.InvalidBootInfo));
        var overlap = await KernelImageBuilder.BuildAsync(root, "overlapping-map", architecture: architecture);
        await BootScenarioRunner.RunAsync(root, overlap,
            Request("overlapping-map", 256, FOUNDATION_TIMEOUT, ExpectedOutcome.InvalidMap));
        foreach (var (name, fault) in A64_FAULT_SCENARIOS)
        {
            var faultImage = await KernelImageBuilder.BuildAsync(root, name, architecture: architecture);
            await BootScenarioRunner.RunAsync(root, faultImage,
                Request(name, 256, FOUNDATION_TIMEOUT, ExpectedOutcome.Exception) with { A64Fault = fault });
        }
        var timeout = await KernelImageBuilder.BuildAsync(root, "timeout", architecture: architecture);
        await BootScenarioRunner.RunAsync(root, timeout, Request("timeout", 256, FOUNDATION_HANG, ExpectedOutcome.Timeout));
        Console.WriteLine($"PASS: all {6 + A64_FAULT_SCENARIOS.Length} {architecture.Name} foundation scenarios.");
    }

    #endregion
}
