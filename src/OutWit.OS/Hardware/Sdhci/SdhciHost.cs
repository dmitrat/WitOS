using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace OutWit.OS.Hardware.Sdhci;

/// <summary>
/// How a host waits for its controller between looks at its status (plan step H8.2): the driver's interrupt binding in the
/// guest, nothing in a host test whose model answers at once.
/// </summary>
public interface IInterruptWait
{
    /// <summary>
    /// Waits until the device may have changed its status, or the time passed.
    /// </summary>
    /// <param name="timeout">The longest wait.</param>
    void Wait(TimeSpan timeout);
}

/// <summary>
/// What a board says of its SD controller (plan step H8.2), from its device tree node: the bus width the card is wired
/// for, the controller's base clock where its capabilities report none, whether the card's write-protect pin means
/// anything (a microSD slot has none), and the offset of the device's view of memory (the node's dma-ranges).
/// </summary>
/// <param name="BusWidth">1 or 4 data lines.</param>
/// <param name="BaseClockHz">The base clock, or 0 for the capabilities' value.</param>
/// <param name="IgnoreWriteProtect">Whether the write-protect pin is ignored.</param>
/// <param name="DmaOffset">What the device's addresses are less the physical ones.</param>
public sealed record SdhciOptions(int BusWidth = 4, long BaseClockHz = 0, bool IgnoreWriteProtect = false, long DmaOffset = 0);

/// <summary>
/// An SD memory card behind a standard SD Host Controller (SD Host Controller Simplified Specification 3.00 and SD
/// Physical Layer Simplified Specification 3.01; plan step H8.2): the Raspberry Pi 4's EMMC2 and QEMU's generic SDHCI.
/// <list type="bullet">
/// <item>Its registers are read and written as 32-bit words alone, since the Pi 4's controller serves no other width
/// (Linux drives it through 32-bit accessors), with the transfer mode written together with the command.</item>
/// <item>It identifies the card as the specification's sequence does: reset, power at 3.3 V, 400 kHz, CMD0, CMD8 (a
/// version 1.x card does not answer), ACMD41 until the card is ready, CMD2, CMD3, CMD9, CMD7, then CMD16 for a standard
/// capacity card, the 4-bit bus where the board wires it, and 25 MHz.</item>
/// <item>It moves data by ADMA2 with 32-bit descriptors over the client's physical segments, in pieces of 32 KiB,
/// single-block commands for one block and multiple-block commands with Auto CMD12 for more.</item>
/// <item>A card has no volatile cache this host turns on, so a write is on the card when its transfer completes.</item>
/// </list>
/// </summary>
public sealed class SdhciHost : IBlockDevice
{
    #region Constants

    private const int BLOCK = 512;
    private const int PIECE = 32 * 1024;
    private const int DESCRIPTOR_BYTES = 8;
    private const int SEGMENTS = 64;
    private const int TRANSFER_BYTES = 1024 * 1024;
    private const uint DMA_LIMIT = uint.MaxValue;

    // Registers, as 32-bit words (SDHCI 3.00 section 2.2).
    private const int BLOCK_SIZE_COUNT = 0x04;
    private const int ARGUMENT = 0x08;
    private const int TRANSFER_COMMAND = 0x0C;
    private const int RESPONSE = 0x10;
    private const int PRESENT_STATE = 0x24;
    private const int HOST_POWER = 0x28;
    private const int CLOCK_TIMEOUT_RESET = 0x2C;
    private const int INTERRUPT_STATUS = 0x30;
    private const int INTERRUPT_ENABLE = 0x34;
    private const int SIGNAL_ENABLE = 0x38;
    private const int CAPABILITIES = 0x40;
    private const int ADMA_ERROR = 0x54;
    private const int ADMA_ADDRESS = 0x58;
    private const int VERSION = 0xFC;

    // Present state.
    private const uint COMMAND_INHIBIT = 1 << 0;
    private const uint DATA_INHIBIT = 1 << 1;
    private const uint CARD_INSERTED = 1 << 16;
    private const uint WRITABLE = 1 << 19;

    // Host control 1 (byte 0) and power control (byte 1).
    private const uint FOUR_BIT_BUS = 1 << 1;
    private const uint ADMA2_32 = 2 << 3;
    private const uint POWER_3V3 = (7 << 1 | 1) << 8;

    // Clock control (bits 15–0), the data timeout (byte 2) and the software resets (byte 3).
    private const uint INTERNAL_CLOCK = 1 << 0;
    private const uint CLOCK_STABLE = 1 << 1;
    private const uint CARD_CLOCK = 1 << 2;
    private const uint DATA_TIMEOUT = 0xE << 16;
    private const uint RESET_ALL = 1 << 24;
    private const uint RESET_LINES = 3 << 25;

    // Interrupt status: the normal statuses (bits 15–0) and the errors (bits 31–16).
    private const uint COMMAND_COMPLETE = 1 << 0;
    private const uint TRANSFER_COMPLETE = 1 << 1;
    private const uint ERROR = 1 << 15;
    private const uint COMMAND_TIMEOUT = 1 << 16;
    private const uint ERRORS = 0x03FF << 16;
    private const uint NORMAL_ENABLED = COMMAND_COMPLETE | TRANSFER_COMPLETE;

    // Transfer mode.
    private const uint DMA = 1 << 0;
    private const uint BLOCK_COUNT = 1 << 1;
    private const uint AUTO_CMD12 = 1 << 2;
    private const uint READ = 1 << 4;
    private const uint MULTIPLE = 1 << 5;

    // Capabilities.
    private const uint ADMA2 = 1 << 19;
    private const uint VOLTAGE_3V3 = 1 << 24;

    // ADMA2 descriptors' attributes (SDHCI 3.00 section 1.13.4): valid and the transfer action, end on the last.
    private const ushort DESCRIPTOR_TRANSFER = 0x21;
    private const byte DESCRIPTOR_END = 0x02;

    private const long IDENTIFY_HZ = 400_000;
    private const long TRANSFER_HZ = 25_000_000;
    private const uint VOLTAGE_WINDOW = 0x00FF_8000; // 2.7–3.6 V
    private const uint HIGH_CAPACITY = 1u << 30;
    private const uint READY = 1u << 31;

    private static readonly TimeSpan COMMAND_TIME = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan READY_TIME = TimeSpan.FromSeconds(1);

    #endregion

    #region Fields

    private readonly IDeviceRegisters m_registers;
    private readonly IInterruptWait m_wait;
    private readonly IDmaBuffer m_table;
    private readonly SdhciOptions m_options;
    private uint m_card; // the relative card address, shifted into an argument's upper half
    private bool m_blocks; // block addressing: a high capacity card's arguments count blocks, a standard one's bytes

    #endregion

    #region Constructors

    /// <summary>
    /// A host over the controller's registers; <see cref="Start"/> identifies the card.
    /// </summary>
    /// <param name="registers">The controller's registers.</param>
    /// <param name="wait">How the host waits for the controller.</param>
    /// <param name="table">A buffer of at least 1 KiB for the ADMA2 descriptors, within the device's 32-bit reach.</param>
    /// <param name="options">What the board says of the controller.</param>
    /// <exception cref="ArgumentException">The table is too small.</exception>
    public SdhciHost(IDeviceRegisters registers, IInterruptWait wait, IDmaBuffer table, SdhciOptions? options = null)
    {
        if (table.Bytes.Length < 1024)
            throw new ArgumentException("The ADMA2 table needs at least 1 KiB.", nameof(table));
        m_registers = registers;
        m_wait = wait;
        m_table = table;
        m_options = options ?? new SdhciOptions();
    }

    #endregion

    #region Functions

    /// <summary>
    /// Resets the controller, powers the card and identifies it, leaving it selected for transfers.
    /// </summary>
    /// <exception cref="IOException">No card is there, the controller lacks what this host needs, or the card does not answer
    /// as its specification says.</exception>
    public void Start()
    {
        Version = (int)(m_registers.Read32(VERSION) >> 16 & 0xFF);
        var capabilities = m_registers.Read32(CAPABILITIES);
        if ((capabilities & ADMA2) == 0)
            throw new IOException("The SD controller has no ADMA2.");
        if ((capabilities & VOLTAGE_3V3) == 0)
            throw new IOException("The SD controller does not supply 3.3 V.");
        BaseClockHz = m_options.BaseClockHz != 0 ? m_options.BaseClockHz
            : (Version >= 2 ? capabilities >> 8 & 0xFF : capabilities >> 8 & 0x3F) * 1_000_000L;
        if (BaseClockHz == 0)
            throw new IOException("The SD controller reports no base clock and the board names none.");
        Reset(RESET_ALL);
        if ((m_registers.Read32(PRESENT_STATE) & CARD_INSERTED) == 0)
            throw new IOException("No card in the SD slot.");
        m_registers.Write32(INTERRUPT_ENABLE, ERRORS | NORMAL_ENABLED);
        m_registers.Write32(SIGNAL_ENABLE, ERRORS | NORMAL_ENABLED);
        m_registers.Write32(HOST_POWER, (POWER_3V3 & ~(1u << 8)) | ADMA2_32); // the voltage, then the power
        m_registers.Write32(HOST_POWER, POWER_3V3 | ADMA2_32);
        Clock(IDENTIFY_HZ);
        Thread.Sleep(2); // the card's 74 clocks of its power-up sequence

        Command(SdCommand.GO_IDLE_STATE, 0, SdResponse.None);
        // A card of version 2.0 or later echoes the condition; one of version 1.x does not answer.
        var version2 = false;
        try
        {
            var echo = Command(SdCommand.SEND_IF_COND, SdCommand.INTERFACE_CONDITION, SdResponse.R7);
            if ((echo & 0xFFF) != SdCommand.INTERFACE_CONDITION)
                throw new IOException($"The card echoed its interface condition as 0x{echo & 0xFFF:X3}, not 0x1AA.");
            version2 = true;
        }
        catch (SdhciTimeoutException)
        {
            Reset(RESET_LINES);
        }
        uint ocr;
        var ready = Stopwatch.StartNew();
        while (true)
        {
            Command(SdCommand.APP_CMD, 0, SdResponse.R1);
            ocr = Command(SdCommand.SD_SEND_OP_COND, VOLTAGE_WINDOW | (version2 ? HIGH_CAPACITY : 0), SdResponse.R3);
            if ((ocr & READY) != 0)
                break;
            if (ready.Elapsed > READY_TIME)
                throw new IOException($"The card stayed busy for {READY_TIME.TotalSeconds} s after ACMD41 (OCR 0x{ocr:X8}).");
            Thread.Sleep(10);
        }
        m_blocks = (ocr & HIGH_CAPACITY) != 0;
        var cid = LongCommand(SdCommand.ALL_SEND_CID, 0);
        m_card = Command(SdCommand.SEND_RELATIVE_ADDR, 0, SdResponse.R6) & 0xFFFF_0000;
        var csd = LongCommand(SdCommand.SEND_CSD, m_card);
        Capacity = SdCommand.Capacity(csd);
        Command(SdCommand.SELECT_CARD, m_card, SdResponse.R1b);
        if (!m_blocks)
            Command(SdCommand.SET_BLOCKLEN, BLOCK, SdResponse.R1);
        if (m_options.BusWidth == 4)
        {
            Command(SdCommand.APP_CMD, m_card, SdResponse.R1);
            Command(SdCommand.SET_BUS_WIDTH, 2, SdResponse.R1);
            m_registers.Write32(HOST_POWER, POWER_3V3 | ADMA2_32 | FOUR_BIT_BUS);
        }
        Clock(TRANSFER_HZ);
        IsReadOnly = !m_options.IgnoreWriteProtect && (m_registers.Read32(PRESENT_STATE) & WRITABLE) == 0;
        var (name, serial) = SdCommand.Identity(cid);
        Description = $"{(m_blocks ? "SDHC/SDXC" : "SDSC")} card \"{name}\" serial {serial:x8} of {Capacity} bytes" +
            $"{(IsReadOnly ? ", write-protected" : "")}, {m_options.BusWidth}-bit bus at {ClockHz / 1_000_000.0:0.##} MHz, " +
            $"SD host controller {Version + 1}.00 with a base clock of {BaseClockHz / 1_000_000.0:0.##} MHz";
    }

    /// <inheritdoc />
    public void Read(long offset, ReadOnlySpan<(ulong Address, int Length)> segments) => Transfer(offset, segments, read: true);

    /// <inheritdoc />
    public void Write(long offset, ReadOnlySpan<(ulong Address, int Length)> segments)
    {
        if (IsReadOnly)
            throw new IOException("The card is write-protected.");
        Transfer(offset, segments, read: false);
    }

    /// <inheritdoc />
    public void Flush()
    {
        // A write is on the card when its transfer completed: the host turns on no cache of the card's.
    }

    private void Transfer(long offset, ReadOnlySpan<(ulong Address, int Length)> segments, bool read)
    {
        long bytes = 0;
        foreach (var segment in segments)
            bytes += segment.Length;
        if (offset < 0 || offset % BLOCK != 0 || bytes == 0 || bytes % BLOCK != 0 || bytes > TRANSFER_BYTES || offset + bytes > Capacity ||
            segments.Length > SEGMENTS)
            throw new ArgumentException($"A transfer of {bytes} bytes at {offset} in {segments.Length} segments is no request of this card.");
        WriteTable(segments);
        var count = (int)(bytes / BLOCK);
        var argument = m_blocks ? offset / BLOCK : offset;
        if (argument > uint.MaxValue)
            throw new ArgumentException($"The offset {offset} lies beyond the card's addresses.");
        var mode = DMA | (read ? READ : 0) | (count > 1 ? BLOCK_COUNT | AUTO_CMD12 | MULTIPLE : 0);
        var index = read ? count > 1 ? SdCommand.READ_MULTIPLE_BLOCK : SdCommand.READ_SINGLE_BLOCK
            : count > 1 ? SdCommand.WRITE_MULTIPLE_BLOCK : SdCommand.WRITE_BLOCK;
        try
        {
            Command(index, (uint)argument, SdResponse.R1, mode, count);
        }
        catch (IOException exception)
        {
            var adma = m_registers.Read32(ADMA_ERROR) & 0xFF;
            throw new IOException($"{(read ? "Reading" : "Writing")} {count} blocks at block {offset / BLOCK} failed: {exception.Message}" +
                (adma != 0 ? $" (ADMA error status 0x{adma:X2})" : ""), exception);
        }
    }

    // The ADMA2 descriptors of the segments, pieces of at most 32 KiB, the last marked as the end.
    private void WriteTable(ReadOnlySpan<(ulong Address, int Length)> segments)
    {
        var table = m_table.Bytes;
        var at = 0;
        foreach (var (address, length) in segments)
        {
            var bus = address + (ulong)m_options.DmaOffset;
            if (length <= 0 || bus % 4 != 0 || bus + (ulong)length - 1 > DMA_LIMIT)
                throw new ArgumentException($"A segment of {length} bytes at 0x{address:X} lies outside the SD controller's reach.");
            for (var done = 0; done < length; done += PIECE)
            {
                if (at + DESCRIPTOR_BYTES > table.Length)
                    throw new ArgumentException("The transfer's pieces exceed the ADMA2 table.");
                var piece = Math.Min(PIECE, length - done);
                WriteDescriptor(table[at..], DESCRIPTOR_TRANSFER, (ushort)piece, (uint)(bus + (ulong)done));
                at += DESCRIPTOR_BYTES;
            }
        }
        table[at - DESCRIPTOR_BYTES] |= DESCRIPTOR_END;
        m_registers.Fence();
    }

    private static void WriteDescriptor(Span<byte> descriptor, ushort attributes, ushort length, uint address)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor, attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[2..], length);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], address);
    }

    // A command with a 48-bit response or none: the response's 32 bits of status, OCR or address.
    private uint Command(int index, uint argument, SdResponse response, uint mode = 0, int blocks = 0)
    {
        var data = blocks > 0;
        WaitIdle(data || response == SdResponse.R1b ? COMMAND_INHIBIT | DATA_INHIBIT : COMMAND_INHIBIT, index);
        if (data)
        {
            m_registers.Write32(ADMA_ADDRESS, (uint)(m_table.Physical + (ulong)m_options.DmaOffset));
            m_registers.Write32(BLOCK_SIZE_COUNT, BLOCK | (uint)blocks << 16);
        }
        m_registers.Write32(ARGUMENT, argument);
        m_registers.Write32(TRANSFER_COMMAND, mode | (uint)SdCommand.Register(index, response, data) << 16);
        WaitFor(COMMAND_COMPLETE, index);
        var value = m_registers.Read32(RESPONSE);
        if (response is SdResponse.R1 or SdResponse.R1b && (value & SdCommand.STATUS_ERRORS) != 0)
        {
            if (data || response == SdResponse.R1b)
                Reset(RESET_LINES);
            throw new IOException($"CMD{index} answered the card status 0x{value:X8}, which names an error.");
        }
        if (data || response == SdResponse.R1b)
            WaitFor(TRANSFER_COMPLETE, index);
        return value;
    }

    // A command with a 136-bit response, the CID or the CSD: the controller keeps bits 127–8 of it, without the CRC.
    private UInt128 LongCommand(int index, uint argument)
    {
        WaitIdle(COMMAND_INHIBIT, index);
        m_registers.Write32(ARGUMENT, argument);
        m_registers.Write32(TRANSFER_COMMAND, (uint)SdCommand.Register(index, SdResponse.R2, data: false) << 16);
        WaitFor(COMMAND_COMPLETE, index);
        UInt128 value = 0;
        for (var i = 3; i >= 0; i--)
            value = value << 32 | m_registers.Read32(RESPONSE + i * 4);
        return value << 8;
    }

    // Until the controller is free for a command, which every command it completed leaves it.
    private void WaitIdle(uint inhibit, int index)
    {
        var started = Stopwatch.StartNew();
        while ((m_registers.Read32(PRESENT_STATE) & inhibit) != 0)
        {
            if (started.Elapsed > COMMAND_TIME)
                throw new IOException($"The SD controller stayed busy before CMD{index}.");
            Thread.Sleep(1);
        }
    }

    // Until the status bits are set, which are then cleared; an error resets the command and data lines.
    private void WaitFor(uint bits, int index)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            var status = m_registers.Read32(INTERRUPT_STATUS);
            if ((status & (ERROR | ERRORS)) != 0)
            {
                m_registers.Write32(INTERRUPT_STATUS, status);
                Reset(RESET_LINES);
                if ((status & ERRORS) == COMMAND_TIMEOUT)
                    throw new SdhciTimeoutException(index);
                throw new IOException($"CMD{index} failed with the error status 0x{status >> 16:X4}.");
            }
            if ((status & bits) == bits)
            {
                m_registers.Write32(INTERRUPT_STATUS, bits);
                return;
            }
            var left = COMMAND_TIME - started.Elapsed;
            if (left <= TimeSpan.Zero)
                throw new IOException($"CMD{index} was not answered within {COMMAND_TIME.TotalSeconds} s (status 0x{status:X8}).");
            m_wait.Wait(left);
        }
    }

    private void Reset(uint lines)
    {
        m_registers.Write32(CLOCK_TIMEOUT_RESET, m_registers.Read32(CLOCK_TIMEOUT_RESET) & 0xFFFF | DATA_TIMEOUT | lines);
        var started = Stopwatch.StartNew();
        while ((m_registers.Read32(CLOCK_TIMEOUT_RESET) & lines) != 0)
        {
            if (started.Elapsed > COMMAND_TIME)
                throw new IOException("The SD controller's reset did not end.");
            Thread.Sleep(1);
        }
    }

    // The card's clock at the highest frequency not above the one asked, stopped while it changes.
    private void Clock(long hz)
    {
        var divider = Divider(BaseClockHz, hz, Version);
        ClockHz = Frequency(BaseClockHz, divider, Version);
        m_registers.Write32(CLOCK_TIMEOUT_RESET, DATA_TIMEOUT);
        var clock = (uint)(divider & 0xFF) << 8 | (uint)(divider >> 8 & 3) << 6;
        m_registers.Write32(CLOCK_TIMEOUT_RESET, DATA_TIMEOUT | clock | INTERNAL_CLOCK);
        var started = Stopwatch.StartNew();
        while ((m_registers.Read32(CLOCK_TIMEOUT_RESET) & CLOCK_STABLE) == 0)
        {
            if (started.Elapsed > COMMAND_TIME)
                throw new IOException("The SD controller's internal clock did not become stable.");
            Thread.Sleep(1);
        }
        m_registers.Write32(CLOCK_TIMEOUT_RESET, DATA_TIMEOUT | clock | INTERNAL_CLOCK | CARD_CLOCK);
    }

    /// <summary>
    /// The divider of a controller's base clock for a frequency not above the one asked (SDHCI 3.00 section 2.2.14):
    /// version 3.00's 10-bit divided mode, the clock the base over twice the divider, the base itself for 0; earlier
    /// versions' 8-bit powers of two.
    /// </summary>
    /// <param name="baseHz">The base clock.</param>
    /// <param name="hz">The frequency asked.</param>
    /// <param name="version">The controller's specification version, 2 for 3.00.</param>
    /// <returns>The divider's field.</returns>
    public static int Divider(long baseHz, long hz, int version)
    {
        if (baseHz <= hz)
            return 0;
        if (version >= 2)
            return (int)Math.Min(1023, (baseHz + 2 * hz - 1) / (2 * hz));
        var divider = 1;
        while (divider < 0x80 && baseHz / (2 * divider) > hz)
            divider <<= 1;
        return divider;
    }

    /// <summary>
    /// The card's clock a divider gives.
    /// </summary>
    /// <param name="baseHz">The base clock.</param>
    /// <param name="divider">The divider's field.</param>
    /// <param name="version">The controller's specification version.</param>
    /// <returns>The frequency.</returns>
    public static long Frequency(long baseHz, int divider, int version) => divider == 0 ? baseHz : baseHz / (2L * divider);

    #endregion

    #region Properties

    /// <inheritdoc />
    public long Capacity { get; private set; }

    /// <inheritdoc />
    public int BlockSize => BLOCK;

    /// <inheritdoc />
    public bool IsReadOnly { get; private set; }

    /// <inheritdoc />
    public bool Flushes => false;

    /// <inheritdoc />
    public int MaxSegments => SEGMENTS;

    /// <inheritdoc />
    public long MaxSegmentBytes => TRANSFER_BYTES;

    /// <inheritdoc />
    public long MaxTransferBytes => TRANSFER_BYTES;

    /// <summary>
    /// The controller's specification version: 0 for 1.00, 1 for 2.00, 2 for 3.00.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// The controller's base clock.
    /// </summary>
    public long BaseClockHz { get; private set; }

    /// <summary>
    /// The card's clock now.
    /// </summary>
    public long ClockHz { get; private set; }

    /// <summary>
    /// The card and the controller, for the driver's line.
    /// </summary>
    public string Description { get; private set; } = "";

    #endregion
}

/// <summary>
/// A command no card answered (plan step H8.2): what a version 1.x card does with CMD8.
/// </summary>
/// <param name="index">The command's index.</param>
public sealed class SdhciTimeoutException(int index) : IOException($"CMD{index} was not answered.");
