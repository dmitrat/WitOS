namespace WitOS.Dev.Kernel;

/// <summary>
/// The RISC-V port's own scenario while it has no suite (plan step V1.1): the converted image boots under EDK II over
/// OpenSBI, the loader reads its own headers, publishes the device tree and leaves the firmware, and the bring-up ends
/// the machine through the test finisher with the success code. V1.2 replaces it with the kernel suite of riscv64.
/// </summary>
internal static class RiscV64Port
{
    #region Constants

    /// <summary>
    /// The lines the loader boot requires.
    /// </summary>
    public static readonly string[] REQUIRED_LINES =
    [
        "[BOOT] UEFI riscv64 adapter", "[BOOT] Device tree published", "[BOOT] ExitBootServices OK",
        "[RV64] entered the kernel after ExitBootServices: "
    ];

    #endregion

    #region Functions

    /// <summary>
    /// Builds the riscv64 image and boots it (the riscv64-loader command).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>When the boot passed.</returns>
    public static async Task BootLoaderAsync(string root)
    {
        var architecture = KernelArchitecture.Riscv64;
        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RELEASE_SCENARIO, architecture: architecture);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest("riscv64-loader-256", 256, 300, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Loader,
            RequiredLines = REQUIRED_LINES
        });
        Console.WriteLine("RISC-V (V1.1): the converted image booted under EDK II over OpenSBI, the loader left the firmware and the bring-up ended the machine.");
    }

    #endregion
}
