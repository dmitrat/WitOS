using System;
using System.IO;
using System.Threading;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware.Nvme;

/// <summary>
/// An NVMe controller (NVM Express Base 1.4, plan step H3.2) the driver holds: its registers through BAR 0, its queues in
/// one pinned arena, its interrupt bound to an event — MSI-X entry 0 where the kernel serves messages and the function has
/// the capability (VMware's and QEMU's controllers do), its INTx line otherwise. It disables and enables the controller
/// with an admin queue pair, identifies it and its first active namespace, creates one I/O queue pair and serves each
/// command one at a time: an entry at the submission queue's tail, its doorbell, the completion whose phase tag is new,
/// the completion queue's head doorbell. Its end is a normal shutdown, which empties the controller's cache, then a
/// disable, which ends its DMA.
/// </summary>
public sealed class NvmeController : IDisposable
{
    #region Constants

    /// <summary>
    /// The entries of each queue: one command at a time leaves the rest for a later queue depth.
    /// </summary>
    private const int QUEUE_ENTRIES = 16;

    /// <summary>
    /// The pages of the arena: the queues, the Identify data, the PRP list and the namespace's own block.
    /// </summary>
    private const int ARENA_PAGES = 16;

    /// <summary>
    /// The seconds an I/O command may take: Linux's NVMe io_timeout. Under QEMU's instruction clock without sleep, as the
    /// gates run, an idle guest's time runs ahead of a host's write, so a short timeout fails a command the host still
    /// serves.
    /// </summary>
    private const int IO_SECONDS = 30;

    /// <summary>
    /// The seconds an admin command, the enable and the shutdown may take at most: Linux's NVMe admin_timeout.
    /// </summary>
    private const int ADMIN_SECONDS = 60;

    // Controller registers.
    private const int CAP = 0x00;
    private const int VS = 0x08;
    private const int INTMC = 0x10;
    private const int CC = 0x14;
    private const int CSTS = 0x1C;
    private const int AQA = 0x24;
    private const int ASQ = 0x28;
    private const int ACQ = 0x30;
    private const int DOORBELLS = 0x1000;
    private const uint CC_ENABLE = 1;
    private const uint CC_QUEUE_ENTRY_SIZES = 6 << 16 | 4 << 20; // 64-byte submission and 16-byte completion entries
    private const uint CC_SHUTDOWN_NORMAL = 1 << 14;
    private const uint CSTS_READY = 1;
    private const uint CSTS_FATAL = 1 << 1;
    private const uint CSTS_SHUTDOWN_COMPLETE = 2 << 2;

    #endregion

    #region Fields

    private readonly KernelHandle m_configurationObject;
    private readonly MmioRegion m_configuration;
    private readonly KernelHandle m_barObject;
    private readonly MmioRegion m_bar;
    private readonly KernelHandle m_signaled;
    private readonly MsiX? m_msiX;
    private readonly InterruptBinding m_binding;
    private readonly DmaArena m_arena;
    private readonly int m_stride;
    private readonly ulong m_timeoutTicks;
    private Queue? m_admin;
    private Queue? m_io;
    private IDmaBuffer? m_identify;
    private ushort m_identifier;
    private bool m_enabled;

    #endregion

    #region Constructors

    private NvmeController(KernelHandle configurationObject, MmioRegion configuration, KernelHandle barObject, MmioRegion bar, KernelHandle signaled,
        MsiX? msiX, InterruptBinding binding, DmaArena arena, string interruptDescription)
    {
        m_configurationObject = configurationObject;
        m_configuration = configuration;
        m_barObject = barObject;
        m_bar = bar;
        m_signaled = signaled;
        m_msiX = msiX;
        m_binding = binding;
        m_arena = arena;
        InterruptDescription = interruptDescription;
        var capabilities = bar.Read32(CAP) | (ulong)bar.Read32(CAP + 4) << 32;
        MaxQueueEntries = (int)(capabilities & 0xFFFF) + 1;
        m_stride = 4 << (int)(capabilities >> 32 & 0xF);
        m_timeoutTicks = Math.Max(1UL, capabilities >> 24 & 0xFF) * Clock.Frequency() / 2; // CAP.TO counts 500 ms
        MinPageBytes = 4096 << (int)(capabilities >> 48 & 0xF);
        Version = bar.Read32(VS);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Opens the controller of the function the driver holds, enables it and identifies it and its first namespace.
    /// </summary>
    /// <param name="device">The device, with BIND.</param>
    /// <param name="descriptor">Its descriptor: region 0 its configuration space, the others its BARs.</param>
    /// <returns>The running controller.</returns>
    /// <exception cref="IOException">The function is no NVMe controller the driver serves, or it did not become ready.</exception>
    /// <exception cref="CapabilityException">A region, the arena or the interrupt could not be had.</exception>
    public static NvmeController Open(KernelHandle device, DeviceDescriptor descriptor)
    {
        var configurationObject = Device.Memory(device, 0);
        MmioRegion? configuration = null;
        KernelHandle? barObject = null;
        MmioRegion? bar = null;
        KernelHandle? signaled = null;
        MsiX? msiX = null;
        InterruptBinding? binding = null;
        DmaArena? arena = null;
        NvmeController? controller = null;
        try
        {
            configuration = MmioRegion.Map(configurationObject, MemoryObject.PAGE);
            configuration.Write16(PciConfiguration.COMMAND, (ushort)(configuration.Read16(PciConfiguration.COMMAND) | 6)); // memory decoding, bus mastering
            var region = PciConfiguration.RegionOfBar(configuration, descriptor, 0);
            barObject = Device.Memory(device, region);
            bar = MmioRegion.Map(barObject, (int)Math.Min(descriptor.Regions[region].Size, (ulong)(Device.WINDOW_PAGES * MemoryObject.PAGE)));
            signaled = Event.Create();
            string description;
            if (InterruptBinding.MessagesSupported && MsiX.Find(configuration, device, descriptor) is { } found)
            {
                msiX = found;
                binding = InterruptBinding.BindMessage(device, signaled, out var address, out var data);
                msiX.Program(0, address, data);
                msiX.Enable();
                description = $"MSI-X entry 0, message {data:x2} at {address:x}";
            }
            else
            {
                binding = InterruptBinding.Bind(device, 0, signaled);
                description = "its INTx line";
            }
            arena = DmaArena.Create(device, ARENA_PAGES);
            controller = new NvmeController(configurationObject, configuration, barObject, bar, signaled, msiX, binding, arena, description);
            controller.Start();
            return controller;
        }
        catch
        {
            if (controller is not null)
            {
                controller.Dispose(); // disabled, if it ran, before its arena goes
                throw;
            }
            binding?.Dispose();
            msiX?.Disable();
            msiX?.Dispose();
            signaled?.Dispose();
            arena?.Dispose();
            bar?.Dispose();
            barObject?.Dispose();
            configuration?.Dispose();
            configurationObject.Dispose();
            throw;
        }
    }

    // The controller disabled, given its admin queues, enabled, identified, and its I/O queue pair created (§7.6.1).
    private void Start()
    {
        if (MinPageBytes != NvmeCommands.PAGE)
            throw new IOException($"The controller's least page is {MinPageBytes} bytes, where the driver gives it {NvmeCommands.PAGE}.");
        if (MaxQueueEntries < QUEUE_ENTRIES)
            throw new IOException($"The controller takes queues of {MaxQueueEntries} entries, fewer than {QUEUE_ENTRIES}.");
        Disable();
        m_admin = new Queue(0, m_arena.Allocate(QUEUE_ENTRIES * NvmeCommands.SUBMISSION_BYTES, NvmeCommands.PAGE),
            m_arena.Allocate(QUEUE_ENTRIES * NvmeCommands.COMPLETION_BYTES, NvmeCommands.PAGE));
        m_bar.Write32(AQA, (uint)((QUEUE_ENTRIES - 1) << 16 | (QUEUE_ENTRIES - 1)));
        Write64(ASQ, m_admin.Submissions.Physical);
        Write64(ACQ, m_admin.Completions.Physical);
        if (m_msiX is null)
            m_bar.Write32(INTMC, 1); // vector 0 unmasked for the INTx line
        m_bar.Write32(CC, CC_QUEUE_ENTRY_SIZES | CC_ENABLE); // NVM command set, 4 KiB pages, round robin
        m_enabled = true;
        AwaitStatus(CSTS_READY, CSTS_READY, "become ready");
        m_identify = m_arena.Allocate(NvmeControllerIdentity.BYTES, NvmeCommands.PAGE);
        Admin(NvmeCommands.IDENTIFY, 0, m_identify.Physical, [NvmeCommands.IDENTIFY_CONTROLLER], "Identify Controller");
        Identity = NvmeControllerIdentity.Read(m_identify.Bytes);
        Admin(NvmeCommands.IDENTIFY, 0, m_identify.Physical, [NvmeCommands.ACTIVE_NAMESPACES], "the active namespaces");
        var first = BitConverter.ToUInt32(m_identify.Bytes[..4]);
        if (first == 0)
            throw new IOException("The controller has no active namespace.");
        Admin(NvmeCommands.IDENTIFY, first, m_identify.Physical, [NvmeCommands.IDENTIFY_NAMESPACE], $"Identify Namespace {first}");
        var identity = NvmeNamespaceIdentity.Read(m_identify.Bytes);
        if (identity.MetadataBytes != 0)
            throw new IOException($"Namespace {first} formats its blocks with {identity.MetadataBytes} bytes of metadata, which the driver does not serve.");
        var completions = m_arena.Allocate(QUEUE_ENTRIES * NvmeCommands.COMPLETION_BYTES, NvmeCommands.PAGE);
        var submissions = m_arena.Allocate(QUEUE_ENTRIES * NvmeCommands.SUBMISSION_BYTES, NvmeCommands.PAGE);
        // Completion queue 1 on interrupt vector 0, enabled and physically contiguous; then submission queue 1 onto it.
        Admin(NvmeCommands.CREATE_COMPLETION_QUEUE, 0, completions.Physical, [(QUEUE_ENTRIES - 1) << 16 | 1, 1 << 1 | 1], "Create I/O Completion Queue");
        Admin(NvmeCommands.CREATE_SUBMISSION_QUEUE, 0, submissions.Physical, [(QUEUE_ENTRIES - 1) << 16 | 1, 1 << 16 | 1], "Create I/O Submission Queue");
        m_io = new Queue(1, submissions, completions);
        Namespace = new NvmeNamespace(this, first, identity, m_arena);
    }

    /// <summary>
    /// Runs an NVM command on the I/O queue pair and waits for its completion.
    /// </summary>
    /// <param name="opcode">The command.</param>
    /// <param name="namespaceId">The namespace.</param>
    /// <param name="prp1">PRP entry 1.</param>
    /// <param name="prp2">PRP entry 2 or a PRP list's address.</param>
    /// <param name="dwords">Command dwords 10 and after.</param>
    /// <param name="what">The command, for a failure's message: "a read at block 8".</param>
    /// <exception cref="IOException">The command failed or did not complete.</exception>
    internal void Run(byte opcode, uint namespaceId, ulong prp1, ulong prp2, ReadOnlySpan<uint> dwords, string what) =>
        Submit(m_io!, opcode, namespaceId, prp1, prp2, dwords, what);

    // An admin command whose data are one page at most.
    private void Admin(byte opcode, uint namespaceId, ulong data, ReadOnlySpan<uint> dwords, string what) =>
        Submit(m_admin!, opcode, namespaceId, data, 0, dwords, what);

    // A command at the queue's tail, its doorbell rung, and its completion awaited: an entry of the completion queue whose
    // phase tag is the lap's, consumed, the head's doorbell rung, the INTx line unmasked.
    private void Submit(Queue queue, byte opcode, uint namespaceId, ulong prp1, ulong prp2, ReadOnlySpan<uint> dwords, string what)
    {
        var identifier = ++m_identifier;
        NvmeCommands.WriteSubmission(queue.Submissions.Bytes[(queue.Tail * NvmeCommands.SUBMISSION_BYTES)..], opcode, identifier, namespaceId, prp1, prp2, dwords);
        queue.Tail = (queue.Tail + 1) % QUEUE_ENTRIES;
        MmioRegion.Barrier();
        m_bar.Write32(DOORBELLS + 2 * queue.Id * m_stride, (uint)queue.Tail);
        var seconds = queue == m_io ? IO_SECONDS : ADMIN_SECONDS;
        var deadline = Clock.MonotonicAfter(seconds);
        for (; ; )
        {
            MmioRegion.Barrier();
            var completion = NvmeCommands.ReadCompletion(queue.Completions.Bytes[(queue.Head * NvmeCommands.COMPLETION_BYTES)..]);
            if (completion.Phase == queue.Phase)
            {
                queue.Head = (queue.Head + 1) % QUEUE_ENTRIES;
                if (queue.Head == 0)
                    queue.Phase = !queue.Phase;
                m_bar.Write32(DOORBELLS + (2 * queue.Id + 1) * m_stride, (uint)queue.Head);
                if (m_msiX is null)
                    m_binding.Acknowledge();
                if (completion.Identifier != identifier)
                    continue; // a command's that ended before; the queue holds one at a time
                if (!completion.Succeeded)
                    throw new IOException($"the controller failed {what}: status type {completion.StatusType}, code {completion.StatusCode:x2}");
                return;
            }
            var now = Clock.Monotonic();
            if ((m_bar.Read32(CSTS) & CSTS_FATAL) != 0)
                throw new IOException($"the controller reports a fatal status during {what}: CSTS {m_bar.Read32(CSTS):x8}");
            if (now > deadline)
                throw new IOException($"the controller did not complete {what} in {seconds} s");
            try
            {
                Event.Wait(Math.Min(deadline, now + Clock.Frequency() / 100), m_signaled);
            }
            catch (CapabilityException exception) when (exception.Error == 110)
            {
                // The slice passed; the next look reads the queue itself, so that a lost interrupt delays the command.
            }
            if (m_msiX is null)
                m_binding.Acknowledge();
        }
    }

    // CC.EN cleared and CSTS.RDY awaited clear: the controller resets its queues and ends its DMA.
    private void Disable()
    {
        m_bar.Write32(CC, m_bar.Read32(CC) & ~CC_ENABLE);
        AwaitStatus(CSTS_READY, 0, "become disabled");
        m_enabled = false;
    }

    private void AwaitStatus(uint mask, uint value, string what)
    {
        var deadline = Clock.Monotonic() + m_timeoutTicks;
        for (; ; )
        {
            var status = m_bar.Read32(CSTS);
            if ((status & mask) == value)
                return;
            if (value != 0 && (status & CSTS_FATAL) != 0)
                throw new IOException($"The controller did not {what}: it reports a fatal status, CSTS {status:x8}.");
            if (Clock.Monotonic() > deadline)
                throw new IOException($"The controller did not {what} in its timeout: CSTS {status:x8}.");
            Thread.Sleep(1);
        }
    }

    // A 64-bit register as two 32-bit writes, the low half first.
    private void Write64(int register, ulong value)
    {
        m_bar.Write32(register, (uint)value);
        m_bar.Write32(register + 4, (uint)(value >> 32));
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (m_enabled)
        {
            try
            {
                // A normal shutdown writes the cache to the medium (§7.6.2); the disable then ends the DMA.
                m_bar.Write32(CC, m_bar.Read32(CC) & ~(3U << 14) | CC_SHUTDOWN_NORMAL);
                AwaitStatus(3U << 2, CSTS_SHUTDOWN_COMPLETE, "complete its shutdown");
                Disable();
                ShutDown = true;
            }
            catch (IOException)
            {
                // A controller that does not stop keeps its pages; the arena stays only as long as the process.
                m_enabled = false;
                return;
            }
        }
        m_msiX?.Disable();
        m_binding.Dispose();
        m_msiX?.Dispose();
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
    /// VS: the version, major in bits 31–16, minor in bits 15–8, tertiary in bits 7–0.
    /// </summary>
    public uint Version { get; }

    /// <summary>
    /// CAP.MQES: the entries of the largest queue.
    /// </summary>
    public int MaxQueueEntries { get; }

    /// <summary>
    /// CAP.MPSMIN: the least memory page the controller takes.
    /// </summary>
    public int MinPageBytes { get; }

    /// <summary>
    /// The controller's Identify data.
    /// </summary>
    public NvmeControllerIdentity Identity { get; private set; } = null!;

    /// <summary>
    /// The first active namespace, as a disk.
    /// </summary>
    public NvmeNamespace Namespace { get; private set; } = null!;

    /// <summary>
    /// Whether the controller completed a normal shutdown when it was let go.
    /// </summary>
    public bool ShutDown { get; private set; }

    /// <summary>
    /// What the interrupt is, for the driver's log.
    /// </summary>
    public string InterruptDescription { get; }

    /// <summary>
    /// The bytes of the arena taken.
    /// </summary>
    public int ArenaUsed => m_arena.Used;

    #endregion

    #region Nested Types

    // A queue pair: its submission entries with their tail, its completion entries with their head and the phase tag the
    // current lap's new entries carry.
    private sealed class Queue(int id, IDmaBuffer submissions, IDmaBuffer completions)
    {
        public int Id { get; } = id;

        public IDmaBuffer Submissions { get; } = submissions;

        public IDmaBuffer Completions { get; } = completions;

        public int Tail { get; set; }

        public int Head { get; set; }

        public bool Phase { get; set; } = true;
    }

    #endregion
}
