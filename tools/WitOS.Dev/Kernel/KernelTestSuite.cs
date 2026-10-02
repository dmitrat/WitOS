using WitOS.Dev.Host;

namespace WitOS.Dev.Kernel;

/// <summary>
/// The 20 kernel integration scenarios: successful boots, rejected handoffs, CPU and memory faults and a timeout.
/// </summary>
internal static class KernelTestSuite
{
    #region Fields

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

    #region Run

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
        await BootScenarioRunner.RunAsync(root, timeout, new BootRequest("timeout", 256, 15, ExpectedOutcome.Timeout));
        Console.WriteLine("PASS: all 20 kernel integration scenarios.");
    }

    #endregion
}
