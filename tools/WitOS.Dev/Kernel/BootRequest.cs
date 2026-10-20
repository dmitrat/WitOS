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
    /// RISC-V kernel exception required by an <see cref="ExpectedOutcome.Exception"/> boot of that architecture (V1.2).
    /// </summary>
    public Rv64FaultExpectation? Rv64Fault { get; init; }

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

    /// <summary>
    /// The guest's terminal on the host's console instead of a script (C1.4, the shell command): the person types.
    /// </summary>
    public bool InteractiveTerminal { get; init; }

    /// <summary>
    /// A display (plan step C2.1): the board's ramfb, whose framebuffer the firmware sets up and the loader records in
    /// the boot contract; none when false, as the boards booted before.
    /// </summary>
    public bool Display { get; init; }

    /// <summary>
    /// A keyboard (plan step C2.1): virtio-keyboard-pci on the board's bus, which QMP's key events reach.
    /// </summary>
    public bool Keyboard { get; init; }

    /// <summary>
    /// A network adapter (plan step W1.1): virtio-net-pci on QEMU's user networking (slirp), whose gateway, DHCP server
    /// and DNS forwarder the guest reaches at 10.0.2.2 and 10.0.2.3.
    /// </summary>
    public bool Network { get; init; }

    /// <summary>
    /// The runner's echo server beside the network adapter (plan step W1.2b): the guest reaches it at
    /// <see cref="Host.NetworkPeer.ECHO_ADDRESS"/>:<see cref="Host.NetworkPeer.ECHO_PORT"/> through QEMU's guestfwd.
    /// </summary>
    public bool NetworkEcho { get; init; }

    /// <summary>
    /// A guest's port the runner reaches through QEMU's hostfwd (plan step W1.2b), with a <see cref="BootActionKind.Connect"/>
    /// action; zero for none.
    /// </summary>
    public ushort NetworkForward { get; init; }

    /// <summary>
    /// Whether the display's console is the terminal's main console (plan step C2.3): the device manager starts the
    /// display service when the board has a framebuffer and a keyboard driver, and the terminal takes the display's size
    /// and lets the virtio console mirror it. The runner's side of the mirror then answers no query of the terminal, since
    /// the display does and two answers garble the input.
    /// </summary>
    public bool DisplayIsMainConsole => Display && Keyboard;

    /// <summary>
    /// The display in a window of the host's screen instead of headless (plan step C2.4, the shell command): QEMU's GTK
    /// window shows the framebuffer and gives its keys to the keyboard, while the host's console shows the virtio
    /// console's mirror and types nothing into it, since the window's keys are the input and the host terminal's answers
    /// to the mirrored queries would garble it.
    /// </summary>
    public bool Window { get; init; }

    /// <summary>
    /// The host's hardware virtualization instead of TCG and the instruction clock (plan step C2.7, the shell command):
    /// WHPX on Windows, KVM on Linux, for a guest of the host's own ISA; the guest's time is the host's, so nothing of it
    /// is deterministic, which a session a person works in does not need.
    /// </summary>
    public bool Accelerated { get; init; }

    /// <summary>
    /// What the runner does through QMP while the guest runs, each action once its line appeared (plan step C2.1).
    /// </summary>
    public IReadOnlyList<BootAction> Actions { get; init; } = [];

    #endregion
}
