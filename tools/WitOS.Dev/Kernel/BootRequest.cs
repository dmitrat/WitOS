namespace WitOS.Dev.Kernel;

/// <summary>
/// One QEMU boot of a built image together with the outcome it must produce.
/// </summary>
/// <param name="Name">Profile name used for log file names.</param>
/// <param name="MemoryMiB">Guest RAM size.</param>
/// <param name="TimeoutSeconds">Wall-clock limit before the runner stops QEMU.</param>
/// <param name="Expected">Required outcome.</param>
internal sealed record BootRequest(string Name, int MemoryMiB, int TimeoutSeconds, ExpectedOutcome Expected)
{
    #region Properties

    /// <summary>
    /// Kernel exception required by an <see cref="ExpectedOutcome.Exception"/> boot.
    /// </summary>
    public FaultExpectation? Fault { get; init; }

    /// <summary>
    /// QEMU CPU model.
    /// </summary>
    public string CpuModel { get; init; } = "qemu64";

    /// <summary>
    /// Guest suite whose markers a successful boot must report.
    /// </summary>
    public BootSuite Suite { get; init; } = BootSuite.Kernel;

    /// <summary>
    /// Log directory; the shared artifacts/logs directory when null.
    /// </summary>
    public string? LogDirectory { get; init; }

    #endregion
}
