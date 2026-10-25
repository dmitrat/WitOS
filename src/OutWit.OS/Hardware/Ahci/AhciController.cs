using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware.Ahci;

/// <summary>
/// An AHCI host bus adapter (AHCI 1.3.1, plan step H3.1) the driver holds: its registers through ABAR, the BAR 5 the PCI
/// function names, its structures in one pinned arena, its interrupt bound to an event — an MSI message where the kernel
/// serves messages and the function has the capability (VMware's and QEMU's ICH9 controllers do), its INTx line otherwise.
/// It takes the controller from the firmware (BIOS/OS Handoff), resets it, waits for the ports' links, starts every port
/// whose device signs as an ATA disk and serves each such disk's commands one at a time through command slot 0, a port
/// whose command fails stopped and started again before the failure is reported (§6.2.2.1).
/// </summary>
public sealed class AhciController : IDisposable
{
    #region Constants

    /// <summary>
    /// The PRDT entries of a command table: more than the largest window's pages, so that a transfer is the window.
    /// </summary>
    public const int PRDT_ENTRIES = 128;

    /// <summary>
    /// The pages of the arena: each disk's command list, received FIS, command table and own sector.
    /// </summary>
    private const int ARENA_PAGES = 16;

    /// <summary>
    /// The seconds a command, the reset or a port's stop may take.
    /// </summary>
    private const int TIMEOUT_SECONDS = 10;

    /// <summary>
    /// The milliseconds the ports' links are given after the reset while a port sees a device without communication.
    /// </summary>
    private const int LINK_MILLISECONDS = 1000;

    /// <summary>
    /// The milliseconds every port's link is given after the reset, whatever it sees.
    /// </summary>
    private const int SETTLE_MILLISECONDS = 20;

    /// <summary>
    /// The signature of an ATA disk in PxSIG.
    /// </summary>
    public const uint SIGNATURE_ATA = 0x00000101;

    /// <summary>
    /// The signature of an ATAPI device.
    /// </summary>
    public const uint SIGNATURE_ATAPI = 0xEB140101;

    // Generic host control.
    private const int CAP = 0x00;
    private const int GHC = 0x04;
    private const int IS = 0x08;
    private const int PI = 0x0C;
    private const int VS = 0x10;
    private const int CAP2 = 0x24;
    private const int BOHC = 0x28;
    private const uint GHC_AHCI_ENABLE = 1U << 31;
    private const uint GHC_INTERRUPT_ENABLE = 1 << 1;
    private const uint GHC_RESET = 1;
    private const uint CAP_64_BIT = 1U << 31;
    private const uint CAP_STAGGERED_SPIN_UP = 1 << 27;
    private const uint CAP2_HANDOFF = 1;
    private const uint BOHC_BIOS_OWNED = 1;
    private const uint BOHC_OS_OWNED = 1 << 1;
    private const uint BOHC_BIOS_BUSY = 1 << 4;

    // A port's registers, at 0x100 + 0x80 × the port.
    private const int PORTS = 0x100;
    private const int PORT_BYTES = 0x80;
    private const int PxCLB = 0x00;
    private const int PxFB = 0x08;
    private const int PxIS = 0x10;
    private const int PxIE = 0x14;
    private const int PxCMD = 0x18;
    private const int PxTFD = 0x20;
    private const int PxSIG = 0x24;
    private const int PxSSTS = 0x28;
    private const int PxSCTL = 0x2C;
    private const int PxSERR = 0x30;
    private const int PxCI = 0x38;
    private const uint CMD_START = 1;
    private const uint CMD_SPIN_UP = 1 << 1;
    private const uint CMD_FIS_RECEIVE = 1 << 4;
    private const uint CMD_FIS_RUNNING = 1 << 14;
    private const uint CMD_LIST_RUNNING = 1 << 15;
    private const uint TFD_BUSY = 0x80;
    private const uint TFD_DATA_REQUEST = 0x08;
    private const uint TFD_ERROR = 0x01;

    // PxIS and PxIE: task file, host bus fatal, host bus data and interface fatal errors, which end a command.
    private const uint IS_ERRORS = 1U << 30 | 1 << 29 | 1 << 28 | 1 << 27;

    // PxIE: the D2H, PIO setup, DMA setup and Set Device Bits FISes, the descriptor processed, the port's connect and PHY
    // ready changes, overflow, the interface's errors, the host bus's and the task file's.
    private const uint PORT_INTERRUPTS = 0x7D40006F;

    #endregion

    #region Fields

    private readonly KernelHandle m_device;
    private readonly KernelHandle m_configurationObject;
    private readonly MmioRegion m_configuration;
    private readonly KernelHandle m_barObject;
    private readonly MmioRegion m_bar;
    private readonly KernelHandle m_signaled;
    private readonly Msi? m_msi;
    private readonly InterruptBinding m_binding;
    private readonly DmaArena m_arena;
    private readonly List<AhciPort> m_ports = [];
    private readonly List<AhciDisk> m_disks = [];
    private readonly uint[] m_status = new uint[32];
    private readonly IDmaBuffer?[] m_lists = new IDmaBuffer?[32];
    private bool m_running;

    #endregion

    #region Constructors

    private AhciController(KernelHandle device, KernelHandle configurationObject, MmioRegion configuration, KernelHandle barObject, MmioRegion bar,
        KernelHandle signaled, Msi? msi, InterruptBinding binding, DmaArena arena, string interruptDescription)
    {
        m_device = device;
        m_configurationObject = configurationObject;
        m_configuration = configuration;
        m_barObject = barObject;
        m_bar = bar;
        m_signaled = signaled;
        m_msi = msi;
        m_binding = binding;
        m_arena = arena;
        InterruptDescription = interruptDescription;
        var capabilities = bar.Read32(CAP);
        PortCount = (int)(capabilities & 0x1F) + 1;
        CommandSlots = (int)(capabilities >> 8 & 0x1F) + 1;
        Is64Bit = (capabilities & CAP_64_BIT) != 0;
        Version = bar.Read32(VS);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Opens the controller of the function the driver holds, resets it and starts its disks.
    /// </summary>
    /// <param name="device">The device, with BIND.</param>
    /// <param name="descriptor">Its descriptor: region 0 its configuration space, the others its BARs.</param>
    /// <returns>The running controller.</returns>
    /// <exception cref="IOException">The function is no AHCI controller the driver serves, or the controller did not start.</exception>
    /// <exception cref="CapabilityException">A region, the arena or the interrupt could not be had.</exception>
    public static AhciController Open(KernelHandle device, DeviceDescriptor descriptor)
    {
        var configurationObject = Device.Memory(device, 0);
        MmioRegion? configuration = null;
        KernelHandle? barObject = null;
        MmioRegion? bar = null;
        KernelHandle? signaled = null;
        Msi? msi = null;
        InterruptBinding? binding = null;
        DmaArena? arena = null;
        AhciController? controller = null;
        try
        {
            configuration = MmioRegion.Map(configurationObject, MemoryObject.PAGE);
            configuration.Write16(PciConfiguration.COMMAND, (ushort)(configuration.Read16(PciConfiguration.COMMAND) | 6)); // memory decoding, bus mastering
            var region = PciConfiguration.RegionOfBar(configuration, descriptor, 5);
            barObject = Device.Memory(device, region);
            bar = MmioRegion.Map(barObject, (int)Math.Min(descriptor.Regions[region].Size, (ulong)(Device.WINDOW_PAGES * MemoryObject.PAGE)));
            signaled = Event.Create();
            string description;
            if (InterruptBinding.MessagesSupported && Msi.Find(configuration) is { } found)
            {
                binding = InterruptBinding.BindMessage(device, signaled, out var address, out var data);
                found.Enable(address, data);
                msi = found;
                description = $"MSI, message {data:x2} at {address:x}";
            }
            else
            {
                binding = InterruptBinding.Bind(device, 0, signaled);
                description = "its INTx line";
            }
            arena = DmaArena.Create(device, ARENA_PAGES);
            controller = new AhciController(device, configurationObject, configuration, barObject, bar, signaled, msi, binding, arena, description);
            controller.Start();
            return controller;
        }
        catch
        {
            if (controller is not null)
            {
                controller.Dispose(); // its ports stopped, if they ran, before its arena goes
                throw;
            }
            binding?.Dispose();
            msi?.Disable();
            signaled?.Dispose();
            arena?.Dispose();
            bar?.Dispose();
            barObject?.Dispose();
            configuration?.Dispose();
            configurationObject.Dispose();
            throw;
        }
    }

    // The controller from the firmware, reset, every implemented port's link awaited and every ATA disk's port started.
    private void Start()
    {
        if ((m_bar.Read32(CAP2) & CAP2_HANDOFF) != 0)
            TakeFromFirmware();
        m_bar.Write32(GHC, m_bar.Read32(GHC) | GHC_AHCI_ENABLE);
        m_bar.Write32(GHC, GHC_AHCI_ENABLE | GHC_RESET);
        var deadline = Clock.MonotonicAfter(TIMEOUT_SECONDS);
        while ((m_bar.Read32(GHC) & GHC_RESET) != 0)
        {
            if (Clock.Monotonic() > deadline)
                throw new IOException($"The controller did not end its reset: GHC {m_bar.Read32(GHC):x8}.");
            Thread.Sleep(1);
        }
        m_bar.Write32(GHC, GHC_AHCI_ENABLE); // the reset cleared it on a controller that is not AHCI only
        m_bar.Write32(IS, m_bar.Read32(IS));
        m_bar.Write32(GHC, GHC_AHCI_ENABLE | GHC_INTERRUPT_ENABLE); // each port's own enables follow when it starts
        m_running = true;
        Implemented = m_bar.Read32(PI);
        var spinUp = (m_bar.Read32(CAP) & CAP_STAGGERED_SPIN_UP) != 0;
        for (var port = 0; port < 32; port++)
        {
            if ((Implemented & 1U << port) != 0 && spinUp)
                WritePort(port, PxCMD, ReadPort(port, PxCMD) | CMD_SPIN_UP);
        }
        AwaitLinks();
        for (var port = 0; port < 32; port++)
        {
            if ((Implemented & 1U << port) == 0)
                continue;
            var status = ReadPort(port, PxSSTS);
            var detection = (int)(status & 0xF);
            var speed = (int)(status >> 4 & 0xF);
            var active = (status >> 8 & 0xF) == 1;
            if (detection != 3 || !active)
            {
                m_ports.Add(new AhciPort(port, detection, speed, 0));
                continue;
            }
            var signature = StartReceiving(port);
            m_ports.Add(new AhciPort(port, detection, speed, signature));
            if (signature != SIGNATURE_ATA)
            {
                StopPort(port);
                continue;
            }
            WritePort(port, PxIE, PORT_INTERRUPTS);
            WritePort(port, PxCMD, ReadPort(port, PxCMD) | CMD_START);
            var disk = new AhciDisk(this, port, m_arena);
            m_disks.Add(disk);
            disk.Identify();
        }
    }

    // BIOS/OS Handoff (§10.6.3): the OS Owned semaphore set, the BIOS's cleared within 25 ms, or within two seconds of
    // its Busy flag.
    private void TakeFromFirmware()
    {
        m_bar.Write32(BOHC, m_bar.Read32(BOHC) | BOHC_OS_OWNED);
        var deadline = Clock.MonotonicAfter(2);
        var start = Clock.Monotonic();
        var quarter = Clock.Frequency() / 40;
        while ((m_bar.Read32(BOHC) & BOHC_BIOS_OWNED) != 0 && Clock.Monotonic() < deadline)
        {
            if ((m_bar.Read32(BOHC) & BOHC_BIOS_BUSY) == 0 && Clock.Monotonic() - start > quarter)
                break;
            Thread.Sleep(1);
        }
        FirmwareHeld = (m_bar.Read32(BOHC) & BOHC_BIOS_OWNED) != 0;
    }

    // The ports' links after the reset: a short settling for every port, then until no port sees a device it has no
    // communication with, within LINK_MILLISECONDS.
    private void AwaitLinks()
    {
        var frequency = Clock.Frequency();
        var start = Clock.Monotonic();
        for (; ; )
        {
            var elapsed = (Clock.Monotonic() - start) * 1000 / frequency;
            var pending = false;
            for (var port = 0; port < 32 && !pending; port++)
                pending = (Implemented & 1U << port) != 0 && (ReadPort(port, PxSSTS) & 0xF) == 1;
            if (elapsed >= SETTLE_MILLISECONDS && !pending || elapsed >= LINK_MILLISECONDS)
                return;
            Thread.Sleep(1);
        }
    }

    // A port with an established link (§10.1.2): its command list and received FIS given, its errors and interrupts
    // cleared, FIS reception on, and the device's first D2H FIS awaited, whose signature tells what the device is.
    private uint StartReceiving(int port)
    {
        StopPort(port);
        var list = m_arena.Allocate(AhciCommands.LIST_BYTES, AhciCommands.LIST_BYTES);
        var fis = m_arena.Allocate(AhciCommands.RECEIVED_FIS_BYTES, AhciCommands.RECEIVED_FIS_BYTES);
        WriteAddress(port, PxCLB, list.Physical);
        WriteAddress(port, PxFB, fis.Physical);
        m_lists[port] = list;
        WritePort(port, PxSERR, ReadPort(port, PxSERR));
        WritePort(port, PxIS, ReadPort(port, PxIS));
        WritePort(port, PxCMD, ReadPort(port, PxCMD) | CMD_FIS_RECEIVE);
        var deadline = Clock.MonotonicAfter(1);
        while ((ReadPort(port, PxTFD) & (TFD_BUSY | TFD_DATA_REQUEST)) != 0 && Clock.Monotonic() < deadline)
            Thread.Sleep(1);
        return ReadPort(port, PxSIG);
    }

    /// <summary>
    /// Issues the command a disk wrote into its slot 0 and waits for it: its completion, a reported error or the timeout;
    /// a port that failed is stopped and started again before the failure is reported.
    /// </summary>
    /// <param name="port">The disk's port.</param>
    /// <param name="what">The command, for a failure's message: "a read at sector 8".</param>
    /// <exception cref="IOException">The device reported an error or the command did not complete.</exception>
    internal void Issue(int port, string what)
    {
        m_status[port] = 0;
        MmioRegion.Barrier();
        WritePort(port, PxCI, 1);
        var deadline = Clock.MonotonicAfter(TIMEOUT_SECONDS);
        for (; ; )
        {
            Service();
            var status = m_status[port] | ReadPort(port, PxIS);
            if ((status & IS_ERRORS) != 0)
                Fail(port, what, $"status {status:x8}");
            if ((ReadPort(port, PxCI) & 1) == 0)
            {
                var taskFile = ReadPort(port, PxTFD);
                if ((taskFile & TFD_ERROR) != 0)
                    Fail(port, what, "the task file's error bit");
                return;
            }
            var now = Clock.Monotonic();
            if (now > deadline)
                Fail(port, what, "no completion in " + TIMEOUT_SECONDS + " s");
            try
            {
                Event.Wait(Math.Min(deadline, now + Clock.Frequency() / 100), m_signaled);
            }
            catch (CapabilityException exception) when (exception.Error == 110)
            {
                // The slice passed; the next look reads the port itself, so that a lost interrupt delays the command.
            }
        }
    }

    // The interrupt: every port's status taken and cleared, then the controller's, then the INTx line unmasked.
    private void Service()
    {
        var pending = m_bar.Read32(IS);
        for (var port = 0; port < 32; port++)
        {
            if ((pending & 1U << port) == 0)
                continue;
            var status = ReadPort(port, PxIS);
            WritePort(port, PxIS, status);
            m_status[port] |= status;
        }
        if (pending != 0)
            m_bar.Write32(IS, pending);
        if (m_msi is null)
            m_binding.Acknowledge();
    }

    // A failed command: the task file read, the port stopped, its errors cleared and started again (§6.2.2.1), then
    // the failure reported.
    private void Fail(int port, string what, string cause)
    {
        var taskFile = ReadPort(port, PxTFD);
        var errors = ReadPort(port, PxSERR);
        StopPort(port);
        WritePort(port, PxSERR, errors);
        WritePort(port, PxIS, ReadPort(port, PxIS));
        if ((ReadPort(port, PxTFD) & (TFD_BUSY | TFD_DATA_REQUEST)) != 0)
            Reset(port);
        WritePort(port, PxCMD, ReadPort(port, PxCMD) | CMD_FIS_RECEIVE);
        WritePort(port, PxCMD, ReadPort(port, PxCMD) | CMD_START);
        m_status[port] = 0;
        throw new IOException($"the device of port {port} failed {what}: {cause}, task file status {taskFile & 0xFF:x2} error {taskFile >> 8 & 0xFF:x2}" +
            (errors != 0 ? $", SError {errors:x8}" : ""));
    }

    // COMRESET (§10.4.2): SControl's detection set to 1 for a millisecond and more, then cleared, and the link awaited.
    private void Reset(int port)
    {
        WritePort(port, PxSCTL, ReadPort(port, PxSCTL) & ~0xFU | 1);
        Thread.Sleep(2);
        WritePort(port, PxSCTL, ReadPort(port, PxSCTL) & ~0xFU);
        var deadline = Clock.MonotonicAfter(1);
        while ((ReadPort(port, PxSSTS) & 0xF) != 3 && Clock.Monotonic() < deadline)
            Thread.Sleep(1);
        WritePort(port, PxSERR, ReadPort(port, PxSERR));
    }

    // A port idle (§10.1.2): its command list stopped, then its FIS reception; whether it stopped in time.
    private bool StopPort(int port)
    {
        WritePort(port, PxCMD, ReadPort(port, PxCMD) & ~CMD_START);
        var deadline = Clock.MonotonicAfter(1);
        while ((ReadPort(port, PxCMD) & CMD_LIST_RUNNING) != 0)
        {
            if (Clock.Monotonic() > deadline)
                return false;
            Thread.Sleep(1);
        }
        WritePort(port, PxCMD, ReadPort(port, PxCMD) & ~CMD_FIS_RECEIVE);
        while ((ReadPort(port, PxCMD) & CMD_FIS_RUNNING) != 0)
        {
            if (Clock.Monotonic() > deadline)
                return false;
            Thread.Sleep(1);
        }
        return true;
    }

    /// <summary>
    /// The command list of a started port.
    /// </summary>
    /// <param name="port">The port.</param>
    /// <returns>Its list.</returns>
    internal IDmaBuffer List(int port) => m_lists[port] ?? throw new InvalidOperationException($"Port {port} has no command list.");

    private uint ReadPort(int port, int register) => m_bar.Read32(PORTS + port * PORT_BYTES + register);

    private void WritePort(int port, int register, uint value) => m_bar.Write32(PORTS + port * PORT_BYTES + register, value);

    // A 64-bit address as its two registers, the high one only where the controller addresses 64 bits.
    private void WriteAddress(int port, int register, ulong address)
    {
        if (!Is64Bit && address >> 32 != 0)
            throw new IOException($"The controller addresses 32 bits and the arena lies at {address:x}.");
        WritePort(port, register, (uint)address);
        if (Is64Bit)
            WritePort(port, register + 4, (uint)(address >> 32));
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (m_running)
        {
            m_bar.Write32(GHC, GHC_AHCI_ENABLE); // interrupts off
            var stopped = true;
            foreach (var disk in m_disks)
                stopped &= StopPort(disk.Port);
            m_running = false;
            if (!stopped)
                return; // a port that does not stop keeps its pages; the arena stays only as long as the process
        }
        m_msi?.Disable();
        m_binding.Dispose();
        m_signaled.Dispose();
        m_arena.Dispose();
        m_bar.Dispose();
        m_barObject.Dispose();
        m_configuration.Dispose();
        m_configurationObject.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// VS: the AHCI version, major in bits 31–16 and minor in bits 15–0 as binary-coded decimal.
    /// </summary>
    public uint Version { get; }

    /// <summary>
    /// CAP's ports.
    /// </summary>
    public int PortCount { get; }

    /// <summary>
    /// CAP's command slots of a port.
    /// </summary>
    public int CommandSlots { get; }

    /// <summary>
    /// Whether the controller addresses 64 bits.
    /// </summary>
    public bool Is64Bit { get; }

    /// <summary>
    /// PI: the implemented ports.
    /// </summary>
    public uint Implemented { get; private set; }

    /// <summary>
    /// Every implemented port as the reset left it.
    /// </summary>
    public IReadOnlyList<AhciPort> Ports => m_ports;

    /// <summary>
    /// The ATA disks of its ports, started and identified.
    /// </summary>
    public IReadOnlyList<AhciDisk> Disks => m_disks;

    /// <summary>
    /// Whether the firmware kept the controller after the driver asked for it (BIOS/OS Handoff).
    /// </summary>
    public bool FirmwareHeld { get; private set; }

    /// <summary>
    /// What the interrupt is, for the driver's log.
    /// </summary>
    public string InterruptDescription { get; }

    /// <summary>
    /// The bytes of the arena taken.
    /// </summary>
    public int ArenaUsed => m_arena.Used;

    #endregion
}

/// <summary>
/// An implemented port as the controller's reset left it.
/// </summary>
/// <param name="Number">The port, from 0.</param>
/// <param name="Detection">PxSSTS's DET: 0 nothing, 1 a device without communication, 3 a device with its link.</param>
/// <param name="Speed">PxSSTS's SPD: 1 for 1.5 Gb/s, 2 for 3, 3 for 6.</param>
/// <param name="Signature">PxSIG after the device's first FIS: <see cref="AhciController.SIGNATURE_ATA"/>, ATAPI or another; zero
/// without a link.</param>
public sealed record AhciPort(int Number, int Detection, int Speed, uint Signature)
{
    /// <summary>
    /// The link's speed as SATA names it.
    /// </summary>
    public string SpeedName => Speed switch
    {
        1 => "1.5 Gb/s",
        2 => "3 Gb/s",
        3 => "6 Gb/s",
        _ => "no link",
    };
}
