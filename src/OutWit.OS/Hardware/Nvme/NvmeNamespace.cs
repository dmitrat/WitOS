using System;
using System.Buffers.Binary;
using System.IO;

namespace OutWit.OS.Hardware.Nvme;

/// <summary>
/// An NVMe namespace (plan step H3.2), a disk the block protocol serves (<see cref="IBlockDevice"/>): Read and Write of
/// the client's physical segments as PRP entries — a list in a page of the driver's own where more than two pages are
/// reached — and Flush where the controller keeps a volatile write cache. One command at a time on the I/O queue pair.
/// </summary>
public sealed class NvmeNamespace : IBlockDevice
{
    #region Constants

    /// <summary>
    /// The entries of the PRP list: a page of them, more than the largest window's pages.
    /// </summary>
    private const int PRP_ENTRIES = NvmeCommands.PAGE / 8;

    #endregion

    #region Fields

    private readonly NvmeController m_controller;
    private readonly IDmaBuffer m_list;
    private readonly IDmaBuffer m_block;
    private readonly ulong[] m_prps = new ulong[PRP_ENTRIES];

    #endregion

    #region Constructors

    internal NvmeNamespace(NvmeController controller, uint id, NvmeNamespaceIdentity identity, DmaArena arena)
    {
        m_controller = controller;
        Id = id;
        Identity = identity;
        m_list = arena.Allocate(NvmeCommands.PAGE, NvmeCommands.PAGE);
        m_block = arena.Allocate(NvmeCommands.PAGE, NvmeCommands.PAGE);
        var mdts = controller.Identity.MaxTransferPages;
        MaxTransferBytes = mdts == 0 || mdts > 20 ? long.MaxValue : (long)controller.MinPageBytes << mdts;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads one logical block into the namespace's own page.
    /// </summary>
    /// <param name="block">The block.</param>
    /// <returns>Its bytes.</returns>
    /// <exception cref="IOException">The controller failed the read.</exception>
    public byte[] ReadBlock(ulong block)
    {
        if (BlockSize > NvmeCommands.PAGE)
            throw new IOException($"A block of {BlockSize} bytes exceeds the driver's own page.");
        Transfer(NvmeCommands.READ, block, 1, [(m_block.Physical, BlockSize)], $"a read at block {block}");
        return m_block.Bytes[..BlockSize].ToArray();
    }

    /// <inheritdoc />
    public void Read(long offset, ReadOnlySpan<(ulong Address, int Length)> segments) => Transfer(NvmeCommands.READ, offset, segments, "read");

    /// <inheritdoc />
    public void Write(long offset, ReadOnlySpan<(ulong Address, int Length)> segments) => Transfer(NvmeCommands.WRITE, offset, segments, "write");

    /// <inheritdoc />
    public void Flush() => m_controller.Run(NvmeCommands.FLUSH, Id, 0, 0, [], "a flush");

    // A read or a write of whole blocks.
    private void Transfer(byte opcode, long offset, ReadOnlySpan<(ulong Address, int Length)> segments, string kind)
    {
        var bytes = 0L;
        foreach (var segment in segments)
            bytes += segment.Length;
        if (offset % BlockSize != 0 || bytes % BlockSize != 0 || bytes == 0 || bytes / BlockSize > 65536 || bytes > MaxTransferBytes)
            throw new IOException($"A transfer of {bytes} bytes at {offset} is no run of whole blocks the controller takes in one command.");
        var block = (ulong)(offset / BlockSize);
        Transfer(opcode, block, (int)(bytes / BlockSize), segments, $"a {kind} at block {block}");
    }

    private void Transfer(byte opcode, ulong block, int count, ReadOnlySpan<(ulong Address, int Length)> segments, string what)
    {
        ulong prp1, prp2;
        int listed;
        try
        {
            listed = NvmeCommands.BuildPrps(segments, m_prps, m_list.Physical, out prp1, out prp2);
        }
        catch (ArgumentException exception)
        {
            throw new IOException(exception.Message, exception);
        }
        var list = m_list.Bytes;
        for (var i = 0; i < listed; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(list[(i * 8)..], m_prps[i]);
        // Dwords 10–11 the starting block, dword 12 the blocks less one.
        m_controller.Run(opcode, Id, prp1, prp2, [(uint)block, (uint)(block >> 32), (uint)(count - 1)], what);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The namespace's identifier.
    /// </summary>
    public uint Id { get; }

    /// <summary>
    /// The namespace's Identify data.
    /// </summary>
    public NvmeNamespaceIdentity Identity { get; }

    /// <inheritdoc />
    /// <remarks>MDTS pages of the controller's least page size; no limit where it names none.</remarks>
    public long MaxTransferBytes { get; }

    /// <inheritdoc />
    public long Capacity => Identity.Bytes;

    /// <inheritdoc />
    public int BlockSize => Identity.BlockBytes;

    /// <inheritdoc />
    public bool IsReadOnly => Identity.WriteProtected;

    /// <inheritdoc />
    public bool Flushes => m_controller.Identity.VolatileWriteCache;

    /// <inheritdoc />
    public int MaxSegments => 128;

    /// <inheritdoc />
    public long MaxSegmentBytes => long.MaxValue;

    #endregion
}
