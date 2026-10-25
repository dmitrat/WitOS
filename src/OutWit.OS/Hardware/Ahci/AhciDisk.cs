using System;
using System.IO;

namespace OutWit.OS.Hardware.Ahci;

/// <summary>
/// The ATA disk of an AHCI port (plan step H3.1), a disk the block protocol serves (<see cref="IBlockDevice"/>): its
/// IDENTIFY DEVICE data, then READ DMA EXT and WRITE DMA EXT of the client's physical segments through the port's
/// command slot 0, and FLUSH CACHE EXT where the disk keeps a write cache. One command at a time.
/// </summary>
public sealed class AhciDisk : IBlockDevice
{
    #region Fields

    private readonly AhciController m_controller;
    private readonly IDmaBuffer m_table;
    private readonly IDmaBuffer m_sector;
    private AtaIdentify? m_identify;

    #endregion

    #region Constructors

    internal AhciDisk(AhciController controller, int port, DmaArena arena)
    {
        m_controller = controller;
        Port = port;
        m_table = arena.Allocate(AhciCommands.TableBytes(AhciController.PRDT_ENTRIES), 128);
        m_sector = arena.Allocate(AtaIdentify.BYTES, 512);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Asks the disk for its IDENTIFY DEVICE data.
    /// </summary>
    /// <exception cref="IOException">The disk failed the command, has no 48-bit addresses or reports no sector.</exception>
    internal void Identify()
    {
        Command(AhciCommands.IDENTIFY_DEVICE, 0, 0, [(m_sector.Physical, AtaIdentify.BYTES)], false, "IDENTIFY DEVICE");
        try
        {
            m_identify = AtaIdentify.Read(m_sector.Bytes);
        }
        catch (FormatException exception)
        {
            throw new IOException($"The disk of port {Port}: {exception.Message}", exception);
        }
        if (!m_identify.Lba48)
            throw new IOException($"The disk of port {Port} has no 48-bit addresses.");
    }

    /// <summary>
    /// Reads one logical sector into the disk's own buffer.
    /// </summary>
    /// <param name="sector">The sector.</param>
    /// <returns>Its bytes.</returns>
    /// <exception cref="IOException">The disk failed the read.</exception>
    public byte[] ReadSector(ulong sector)
    {
        if (BlockSize > AtaIdentify.BYTES)
            throw new IOException($"A sector of {BlockSize} bytes exceeds the driver's own buffer.");
        Command(AhciCommands.READ_DMA_EXT, sector, 1, [(m_sector.Physical, BlockSize)], false, $"a read at sector {sector}");
        return m_sector.Bytes[..BlockSize].ToArray();
    }

    /// <inheritdoc />
    public void Read(long offset, ReadOnlySpan<(ulong Address, int Length)> segments) => Transfer(AhciCommands.READ_DMA_EXT, offset, segments, false);

    /// <inheritdoc />
    public void Write(long offset, ReadOnlySpan<(ulong Address, int Length)> segments) => Transfer(AhciCommands.WRITE_DMA_EXT, offset, segments, true);

    /// <inheritdoc />
    public void Flush() => Command(Identity.FlushExt ? AhciCommands.FLUSH_CACHE_EXT : AhciCommands.FLUSH_CACHE, 0, 0, [], false, "a flush");

    // A read or a write of whole sectors.
    private void Transfer(byte command, long offset, ReadOnlySpan<(ulong Address, int Length)> segments, bool write)
    {
        var bytes = 0L;
        foreach (var segment in segments)
            bytes += segment.Length;
        if (offset % BlockSize != 0 || bytes % BlockSize != 0 || bytes == 0 || bytes / BlockSize > AhciCommands.MAX_SECTORS)
            throw new IOException($"A transfer of {bytes} bytes at {offset} is no run of whole sectors the disk takes in one command.");
        var sector = (ulong)(offset / BlockSize);
        Command(command, sector, (int)(bytes / BlockSize), segments, write, $"a {(write ? "write" : "read")} at sector {sector}");
    }

    // A command in slot 0: its FIS, its PRDT, its header, then the controller's issue and wait.
    private void Command(byte command, ulong lba, int count, ReadOnlySpan<(ulong Address, int Length)> segments, bool write, string what)
    {
        if (segments.Length > AhciController.PRDT_ENTRIES)
            throw new IOException($"A command of {segments.Length} segments exceeds the table's {AhciController.PRDT_ENTRIES}.");
        var table = m_table.Bytes;
        table.Clear();
        AhciCommands.WriteRegisterFis(table, command, lba, count);
        for (var i = 0; i < segments.Length; i++)
            AhciCommands.WritePrdtEntry(table, i, segments[i].Address, segments[i].Length);
        AhciCommands.WriteHeader(m_controller.List(Port).Bytes, 0, write, segments.Length, m_table.Physical);
        m_controller.Issue(Port, what);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The disk's port.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// The disk's IDENTIFY DEVICE data.
    /// </summary>
    public AtaIdentify Identity => m_identify ?? throw new InvalidOperationException($"The disk of port {Port} is not identified.");

    /// <inheritdoc />
    public long Capacity => Identity.Bytes;

    /// <inheritdoc />
    public int BlockSize => Identity.LogicalSectorBytes;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public bool Flushes => Identity.WriteCache;

    /// <inheritdoc />
    public int MaxSegments => AhciController.PRDT_ENTRIES;

    /// <inheritdoc />
    public long MaxSegmentBytes => AhciCommands.PRDT_ENTRY_LIMIT;

    /// <inheritdoc />
    public long MaxTransferBytes => (long)AhciCommands.MAX_SECTORS * BlockSize;

    #endregion
}
