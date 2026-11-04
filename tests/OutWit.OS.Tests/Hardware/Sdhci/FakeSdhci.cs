using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using OutWit.OS.Hardware;

namespace OutWit.OS.Tests.Hardware.Sdhci;

/// <summary>
/// Memory a model device reaches by physical address (plan step H8.2): buffers the test lays at addresses of their own.
/// </summary>
internal sealed class FakeMemory
{
    #region Fields

    private readonly List<(ulong Address, byte[] Bytes)> m_buffers = [];
    private ulong m_next = 0x1000_0000;

    #endregion

    #region Functions

    /// <summary>
    /// Lays a buffer at the next free page-aligned address.
    /// </summary>
    /// <param name="bytes">The buffer.</param>
    /// <returns>Its address.</returns>
    public ulong Add(byte[] bytes)
    {
        var address = m_next;
        m_buffers.Add((address, bytes));
        m_next += ((ulong)bytes.Length + 0xFFF & ~0xFFFUL) + 0x1000;
        return address;
    }

    /// <summary>
    /// A buffer laid for the device, as an <see cref="IDmaBuffer"/>.
    /// </summary>
    /// <param name="bytes">Its size.</param>
    /// <returns>The buffer.</returns>
    public IDmaBuffer Buffer(int bytes)
    {
        var array = new byte[bytes];
        return new Laid(array, Add(array));
    }

    /// <summary>
    /// The bytes at an address, all within one buffer.
    /// </summary>
    /// <param name="address">The first byte.</param>
    /// <param name="length">The bytes.</param>
    /// <returns>The span.</returns>
    /// <exception cref="InvalidOperationException">No buffer holds the range.</exception>
    public Span<byte> At(ulong address, int length)
    {
        foreach (var (start, bytes) in m_buffers)
        {
            if (address >= start && address + (ulong)length <= start + (ulong)bytes.Length)
                return bytes.AsSpan((int)(address - start), length);
        }
        throw new InvalidOperationException($"The device reached 0x{address:X}, where no buffer lies.");
    }

    #endregion

    #region Nested Types

    private sealed class Laid(byte[] bytes, ulong physical) : IDmaBuffer
    {
        public Span<byte> Bytes => bytes;
        public ulong Physical { get; } = physical;
    }

    #endregion
}

/// <summary>
/// A model of a standard SD host controller of specification 3.00 with an SD memory card in its slot (plan step H8.2), as
/// QEMU's generic SDHCI serves one: its registers as 32-bit words, the software resets, the internal clock, the
/// interrupt status cleared by writing ones, each command executed at the write of the command register with its
/// response and status at once, and the data of a read or write moved through the ADMA2 table at its address. The card
/// answers the identification sequence of the SD specification (version 2.0 or 1.x, high or standard capacity) and its
/// block commands; a block the test names fails with a data CRC error.
/// </summary>
internal sealed class FakeSdhci : IDeviceRegisters
{
    #region Constants

    public const ushort RELATIVE_ADDRESS = 0xB368;
    public const string PRODUCT = "WITSD";
    public const uint SERIAL = 0x1234_5678;
    private const uint ERROR = 1 << 15;
    private const uint COMMAND_COMPLETE = 1;
    private const uint TRANSFER_COMPLETE = 2;
    private const uint COMMAND_TIMEOUT = 1 << 16;
    private const uint DATA_CRC = 1 << 21;
    private const uint STATE_TRANSFER = 4 << 9;
    private const uint OUT_OF_RANGE = 1u << 31;

    #endregion

    #region Fields

    private readonly uint[] m_registers = new uint[64];
    private readonly byte[] m_card;
    private readonly FakeMemory m_memory;
    private readonly bool m_highCapacity;
    private readonly bool m_version1;
    private bool m_application;
    private int m_ready;

    #endregion

    #region Constructors

    public FakeSdhci(byte[] card, FakeMemory memory, bool highCapacity = true, bool version1 = false, int version = 2, uint baseMhz = 100,
        bool adma2 = true, bool writable = true, bool inserted = true)
    {
        m_card = card;
        m_memory = memory;
        m_highCapacity = highCapacity && !version1;
        m_version1 = version1;
        Version = version;
        Capabilities = baseMhz << 8 | (adma2 ? 1u << 19 : 0) | 1u << 24;
        Writable = writable;
        Inserted = inserted;
    }

    #endregion

    #region Functions

    public uint Read32(int offset) => offset switch
    {
        0x24 => (Inserted ? 1u << 16 | 1u << 17 : 0) | (Writable ? 1u << 19 : 0),
        0x40 => Capabilities,
        0xFC => (uint)Version << 16,
        _ => m_registers[offset / 4]
    };

    public void Write32(int offset, uint value)
    {
        Writes.Add((offset, value));
        switch (offset)
        {
            case 0x2C:
                if ((value & 1 << 24) != 0)
                    Array.Clear(m_registers);
                if ((value & 3 << 25) != 0)
                    m_registers[0x30 / 4] = 0;
                // The internal clock is stable as soon as it runs; the resets are done at once.
                m_registers[0x2C / 4] = value & 0x00FF_FFFF | ((value & 1) != 0 ? 2u : 0);
                return;
            case 0x30:
                m_registers[0x30 / 4] &= ~value;
                return;
            case 0x0C:
                m_registers[0x0C / 4] = value;
                Execute(value >> 16, value & 0xFFFF);
                return;
            default:
                m_registers[offset / 4] = value;
                return;
        }
    }

    public void Fence() => Fences++;

    private void Execute(uint command, uint mode)
    {
        var index = (int)(command >> 8 & 0x3F);
        var argument = m_registers[0x08 / 4];
        var application = m_application;
        m_application = false;
        Commands.Add(application ? 100 + index : index);
        switch (application ? 100 + index : index)
        {
            case 0:
                Complete(0);
                return;
            case 8:
                if (m_version1)
                {
                    m_registers[0x30 / 4] |= ERROR | COMMAND_TIMEOUT;
                    return;
                }
                Complete(argument & 0xFFF);
                return;
            case 55:
                m_application = true;
                Complete(STATE_TRANSFER | 1 << 5);
                return;
            case 141:
                Ocr = argument;
                Complete(0x00FF_8000 | (++m_ready >= ReadyAfter ? 1u << 31 : 0) | (m_highCapacity && (argument & 1 << 30) != 0 ? 1u << 30 : 0));
                return;
            case 2:
                Long(Cid());
                return;
            case 3:
                Complete((uint)RELATIVE_ADDRESS << 16 | 0x0500);
                return;
            case 9:
                if (argument >> 16 != RELATIVE_ADDRESS)
                    throw new InvalidOperationException($"CMD9 for card 0x{argument >> 16:X4}.");
                Long(Csd());
                return;
            case 7:
                Selected = argument >> 16 == RELATIVE_ADDRESS;
                Complete(STATE_TRANSFER, TRANSFER_COMPLETE);
                return;
            case 16:
                BlockLength = (int)argument;
                Complete(STATE_TRANSFER);
                return;
            case 106:
                BusWidth = argument == 2 ? 4 : 1;
                Complete(STATE_TRANSFER);
                return;
            case 17 or 18 or 24 or 25:
                Data(index, argument, mode);
                return;
            default:
                throw new InvalidOperationException($"The model serves no CMD{index}{(application ? " after CMD55" : "")}.");
        }
    }

    private void Data(int index, uint argument, uint mode)
    {
        var read = index is 17 or 18;
        var multiple = index is 18 or 25;
        var blocks = (int)(m_registers[0x04 / 4] >> 16);
        if ((m_registers[0x04 / 4] & 0xFFF) != 512 || (mode & 1) == 0 || (m_registers[0x28 / 4] >> 3 & 3) != 2 || ((mode & 1 << 4) != 0) != read ||
            ((mode & 1 << 5) != 0) != multiple || (multiple && (mode & 1 << 2) == 0) || (!multiple && blocks != 1) || !Selected)
            throw new InvalidOperationException($"CMD{index} with transfer mode 0x{mode:X4} and block register 0x{m_registers[1]:X8}.");
        var offset = m_highCapacity ? (long)argument * 512 : argument;
        var bytes = blocks * 512;
        if (offset + bytes > m_card.Length)
        {
            Complete(STATE_TRANSFER | OUT_OF_RANGE);
            return;
        }
        if (FailedBlock is { } failed && failed >= offset / 512 && failed < offset / 512 + blocks)
        {
            m_registers[0x10 / 4] = STATE_TRANSFER;
            m_registers[0x30 / 4] |= COMMAND_COMPLETE | ERROR | DATA_CRC;
            return;
        }
        // The ADMA2 table: valid transfer descriptors, the last with its end bit.
        var table = (ulong)m_registers[0x58 / 4];
        var done = 0;
        while (true)
        {
            var descriptor = m_memory.At(table, 8);
            var attributes = BinaryPrimitives.ReadUInt16LittleEndian(descriptor);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[2..]);
            var address = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
            if ((attributes & 1) == 0 || (attributes >> 4 & 3) != 2)
                throw new InvalidOperationException($"An ADMA2 descriptor with attributes 0x{attributes:X4}.");
            var piece = length == 0 ? 65536 : length;
            var memory = m_memory.At(address, piece);
            var card = m_card.AsSpan((int)offset + done, piece);
            if (read)
                card.CopyTo(memory);
            else
                memory.CopyTo(card);
            done += piece;
            Descriptors++;
            if ((attributes & 2) != 0)
                break;
            table += 8;
        }
        if (done != bytes)
            throw new InvalidOperationException($"The ADMA2 table moved {done} bytes of a transfer of {bytes}.");
        AutoStops += multiple ? 1 : 0;
        Complete(STATE_TRANSFER, TRANSFER_COMPLETE);
    }

    private void Complete(uint response, uint more = 0)
    {
        m_registers[0x10 / 4] = response;
        m_registers[0x30 / 4] |= COMMAND_COMPLETE | more;
    }

    // A 136-bit response as the controller keeps it: bits 127–8 of the register in bits 119–0 of the response registers.
    private void Long(UInt128 value)
    {
        var kept = value >> 8;
        for (var i = 0; i < 4; i++)
            m_registers[0x10 / 4 + i] = (uint)(kept >> (32 * i));
        m_registers[0x30 / 4] |= COMMAND_COMPLETE;
    }

    private static UInt128 Cid()
    {
        UInt128 cid = (UInt128)0x03 << 120 | (UInt128)0x5344 << 104; // the manufacturer and "SD"
        for (var i = 0; i < PRODUCT.Length; i++)
            cid |= (UInt128)PRODUCT[i] << (96 - i * 8);
        return cid | (UInt128)0x10 << 56 | (UInt128)SERIAL << 24 | (UInt128)0x1A5 << 8;
    }

    private UInt128 Csd()
    {
        if (m_highCapacity)
            return (UInt128)1 << 126 | (UInt128)(ulong)(m_card.Length / (512 * 1024) - 1) << 48 | (UInt128)9 << 80;
        // Version 1.0: blocks of 512 bytes, C_SIZE_MULT 7, so C_SIZE counts 256 KiB.
        return (UInt128)9 << 80 | (UInt128)(ulong)(m_card.Length / (256 * 1024) - 1) << 62 | (UInt128)7 << 47;
    }

    #endregion

    #region Properties

    public byte[] Card => m_card;

    public int Version { get; }

    public uint Capabilities { get; }

    public bool Writable { get; set; }

    public bool Inserted { get; set; }

    public int ReadyAfter { get; set; } = 2;

    public long? FailedBlock { get; set; }

    public List<int> Commands { get; } = [];

    public List<(int Offset, uint Value)> Writes { get; } = [];

    public uint Ocr { get; private set; }

    public bool Selected { get; private set; }

    public int BlockLength { get; private set; } = 512;

    public int BusWidth { get; private set; } = 1;

    public int Descriptors { get; private set; }

    public int AutoStops { get; private set; }

    public int Fences { get; private set; }

    #endregion
}
