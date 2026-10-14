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
    /// ARM64 kernel exception required by an <see cref="ExpectedOutcome.Exception"/> boot of that architecture.
    /// </summary>
    public A64FaultExpectation? A64Fault { get; init; }

    /// <summary>
    /// Architecture whose QEMU board boots the image.
    /// </summary>
    public KernelArchitecture Architecture { get; init; } = KernelArchitecture.X64;

    /// <summary>
    /// QEMU CPU model; the architecture's base profile when null.
    /// </summary>
    public string? CpuModel { get; init; }

    /// <summary>
    /// Guest suite whose markers a successful boot must report.
    /// </summary>
    public BootSuite Suite { get; init; } = BootSuite.Kernel;

    /// <summary>
    /// Processors QEMU gives the board; one unless a scenario asks for more.
    /// </summary>
    public int Processors { get; init; } = 1;

    /// <summary>
    /// Lines the serial output must contain for a successful boot, beyond the suite's markers.
    /// </summary>
    public string[] RequiredLines { get; init; } = [];

    /// <summary>
    /// Log directory; the shared artifacts/logs directory when null.
    /// </summary>
    public string? LogDirectory { get; init; }

    /// <summary>
    /// A raw image QEMU gives the board as a second, writable virtio block device after the entropy source (D3.2);
    /// none when null.
    /// </summary>
    public string? DataDisk { get; init; }

    /// <summary>
    /// A line after which the runner kills QEMU where it stands, without asking it to quit, as a power cut ends a machine
    /// (D6.2): the boot an <see cref="ExpectedOutcome.Cut"/> request judges by what it printed before; none when null.
    /// </summary>
    public string? CutAfter { get; init; }

    /// <summary>
    /// The longest wall-clock wait between <see cref="CutAfter"/>'s line and the cut, chosen at random up to it, so that
    /// cuts fall anywhere in the guest's work rather than right after the line (D6.2).
    /// </summary>
    public int CutJitterMilliseconds { get; init; }

    /// <summary>
    /// Sectors of the data disk whose reads the device fails (D6.3): QEMU's blkdebug fails every read that covers one with
    /// EIO, which virtio-blk reports to the guest as VIRTIO_BLK_S_IOERR; none when empty.
    /// </summary>
    public long[] DataDiskReadErrors { get; init; } = [];

    /// <summary>
    /// The guest's terminal (plan step N4.3a): a virtio console on a character device the runner owns, with the
    /// inputs to type into it, each once the line it waits for appeared; null for a guest without one. The required
    /// lines are looked for in the terminal's transcript too.
    /// </summary>
    public IReadOnlyList<Host.TerminalInput>? Terminal { get; init; }

    #endregion
}
