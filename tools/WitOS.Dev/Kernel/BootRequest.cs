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
    /// A raw image QEMU gives the board as a third, writable virtio block device (E1: a drive whose volumes the namespace
    /// mounts below /media), in the slot whose INTx line is the entropy source's, which a keyboard or a network adapter
    /// would take otherwise; none when null.
    /// </summary>
    public string? ExtraDisk { get; init; }

    /// <summary>
    /// The controller the data disk hangs on: virtio-blk, an AHCI controller's first port (H3.1) — q35's own ICH9 SATA
    /// controller, an ich9-ahci on the boards without one — or an NVMe controller's namespace 1 (H3.2).
    /// </summary>
    public DiskController DataDiskController { get; init; } = DiskController.Virtio;

    /// <summary>
    /// The controller the boot disk hangs on (H3.3): a read-only virtio block device, or, for the PC's one disk that the
    /// guest also writes, an AHCI controller's first port or an NVMe namespace, writable, the firmware booting from its EFI
    /// system partition. ARM64's firmware drives no AHCI controller.
    /// </summary>
    public DiskController BootDiskController { get; init; } = DiskController.Virtio;

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
    /// The keyboard as USB (plan step H2.1, ADR 0029: USB on every target): QEMU's usb-kbd on the first port of a
    /// qemu-xhci controller in the virtio keyboard's slot, which the xHCI driver serves; with <see cref="Keyboard"/>. The
    /// controller's bus takes the hubs and keyboards that device_add actions plug in during the boot (H2.2).
    /// </summary>
    public bool UsbKeyboard { get; init; }

    /// <summary>
    /// The board's own PS/2 keyboard as the keyboard (plan step H5.2, ADR 0031): the PC's i8042, served by the PS/2
    /// keyboard's driver. q35 carries it only then (the runner turns it off otherwise, <c>i8042=off</c>, since a keyboard
    /// makes the display the console); the runner adds no keyboard device, so that QMP's keys reach the PS/2 keyboard, and
    /// waits for each key's report as it does for <see cref="Keyboard"/>'s. x64 alone, and not beside <see cref="Keyboard"/>.
    /// </summary>
    public bool Ps2Keyboard { get; init; }

    /// <summary>
    /// A network adapter (plan step W1.1): virtio-net-pci, or the adapter <see cref="NetworkAdapter"/> names, on QEMU's
    /// user networking (slirp), whose gateway, DHCP server and DNS forwarder the guest reaches at 10.0.2.2 and 10.0.2.3.
    /// </summary>
    public bool Network { get; init; }

    /// <summary>
    /// The adapter <see cref="Network"/> puts on the board (H4): virtio-net-pci, or QEMU's e1000e, an Intel 82574L as a
    /// PC and VMware have it, in the same slot.
    /// </summary>
    public NetworkAdapter NetworkAdapter { get; init; } = NetworkAdapter.Virtio;

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
    /// The runner's HTTPS server beside the network adapter (plan step W3.1), serving with this authority's certificate at
    /// <see cref="Host.NetworkPeer.ECHO_ADDRESS"/>:<see cref="Host.NetworkPeer.HTTPS_PORT"/> through QEMU's guestfwd; the
    /// scenario lays the authority's certificate in the guest's root store. None for no server.
    /// </summary>
    public Host.RunnerAuthority? NetworkHttps { get; init; }

    /// <summary>
    /// A share the runner serves over SMB 3 at 10.0.2.100:445 (E2.2: Samba's smbd behind QEMU's guestfwd); none when null.
    /// </summary>
    public Host.NetworkShare? NetworkShare { get; init; }

    /// <summary>
    /// Whether the display's console is the terminal's main console (plan step C2.3): the device manager starts the
    /// display service when the board has a framebuffer and a keyboard driver, and the terminal takes the display's size
    /// and lets the virtio console mirror it. The runner's side of the mirror then answers no query of the terminal, since
    /// the display does and two answers garble the input.
    /// </summary>
    public bool DisplayIsMainConsole => Display && (Keyboard || Ps2Keyboard);

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

    /// <summary>
    /// QEMU's pc machine in place of q35 (plan step H1.1): i440FX with PIIX3, the HPET, no MCFG and no PIRQ E–H, the QEMU
    /// board nearest VMware's 440BX with PIIX4; the firmware's entropy comes from RDRAND instead of a virtio entropy
    /// source, and the default VGA card's GOP is the framebuffer. x64 alone, without the devices of the services.
    /// </summary>
    public bool Pc { get; init; }

    /// <summary>
    /// The boot disk on PIIX's IDE channel instead of virtio-blk (plan step H1.1), so that a <see cref="Pc"/> board has no
    /// virtio device at all; QEMU writes a snapshot, since an IDE disk cannot be read-only.
    /// </summary>
    public bool IdeBootDisk { get; init; }

    /// <summary>
    /// The Linux arm64 boot protocol in place of the firmware (plan step H6, ADR 0030): QEMU loads the kernel's flat image
    /// and its initial ramdisk beside the boot disk (<see cref="KernelImageBuilder.LinuxImagePath"/>) and enters the image
    /// with the device tree it writes; no firmware, no boot disk.
    /// </summary>
    public bool LinuxBoot { get; init; }

    /// <summary>
    /// The board's EL2 (QEMU's virtualization=on), at which a <see cref="LinuxBoot"/> enters the image (H6).
    /// </summary>
    public bool Virtualization { get; init; }

    /// <summary>
    /// The ARM64 board with a GICv2 (QEMU's virt with gic-version=2) in place of its GICv3 (plan step H7.1): the GIC a
    /// Raspberry Pi 4's GIC-400 is, which the kernel takes from the device tree as it takes the GICv3.
    /// </summary>
    public bool Gic2 { get; init; }

    /// <summary>
    /// QEMU's raspi4b, the Raspberry Pi 4 Model B (plan step H7.2): four Cortex-A72 entered at EL2, 2 GiB, a GIC-400, the
    /// mini UART as the console and no PCI bus; booted by the Linux protocol (<see cref="LinuxBoot"/>) with the Raspberry Pi
    /// firmware's pinned tree (<see cref="RaspberryPiTree"/>), into which the runner writes an entropy seed.
    /// </summary>
    public bool Raspi4 { get; init; }

    /// <summary>
    /// The board without its test exit (plan step H1.2a), as a machine has none: the kernel's end then turns the board off,
    /// which <see cref="ExpectedOutcome.PoweredOff"/> judges.
    /// </summary>
    public bool WithoutTestExit { get; init; }

    /// <summary>
    /// The user faults a layer-2 scenario on the release kernel provokes on purpose (plan step P5: a program's unhandled
    /// fault and the libc's report of it): the release judge requires exactly this many of the kernel's fault lines,
    /// where it otherwise forbids every one.
    /// </summary>
    public int UserFaults { get; init; }

    #endregion
}

/// <summary>
/// The controllers a data disk may hang on (<see cref="BootRequest.DataDiskController"/>).
/// </summary>
internal enum DiskController
{
    /// <summary>
    /// virtio-blk-pci.
    /// </summary>
    Virtio,

    /// <summary>
    /// ide-hd on an AHCI controller's first port (H3.1).
    /// </summary>
    Ahci,

    /// <summary>
    /// QEMU's nvme, whose drive is its namespace 1 (H3.2).
    /// </summary>
    Nvme
}

/// <summary>
/// The adapters a board's network may have (<see cref="BootRequest.NetworkAdapter"/>).
/// </summary>
internal enum NetworkAdapter
{
    /// <summary>
    /// virtio-net-pci.
    /// </summary>
    Virtio,

    /// <summary>
    /// QEMU's e1000e, an Intel 82574L (H4).
    /// </summary>
    E1000e
}
