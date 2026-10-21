using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace WitOS.Dev.Diagnostics;

/// <summary>
/// A guest's physical memory as QEMU's dump-guest-memory writes it without paging (plan step P3): an ELF64 core whose
/// PT_LOAD segments carry the guest's RAM at their physical addresses. Reads go through the file without loading it.
/// </summary>
internal sealed class GuestCore : IDisposable
{
    #region Constants

    private const uint PT_LOAD = 1;

    #endregion

    #region Fields

    private readonly MemoryMappedFile m_file;

    private readonly MemoryMappedViewAccessor m_view;

    private readonly List<Segment> m_segments = [];

    #endregion

    #region Constructors

    private GuestCore(MemoryMappedFile file, MemoryMappedViewAccessor view)
    {
        m_file = file;
        m_view = view;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Opens a core and reads its program headers.
    /// </summary>
    /// <param name="path">The core.</param>
    /// <returns>The core.</returns>
    /// <exception cref="InvalidDataException">The file is no little-endian ELF64 core with loadable segments.</exception>
    public static GuestCore Open(string path)
    {
        var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        var core = new GuestCore(file, view);
        try
        {
            core.ReadHeaders();
            return core;
        }
        catch
        {
            core.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Bytes of physical memory.
    /// </summary>
    /// <param name="address">The first byte's physical address.</param>
    /// <param name="count">The bytes.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="InvalidDataException">The range is not in one of the core's segments.</exception>
    public byte[] Read(ulong address, int count)
    {
        foreach (var segment in m_segments)
        {
            if (address < segment.Physical || address + (ulong)count > segment.Physical + segment.Bytes)
                continue;
            var bytes = new byte[count];
            m_view.ReadArray((long)(segment.Offset + (address - segment.Physical)), bytes, 0, count);
            return bytes;
        }
        throw new InvalidDataException($"Physical 0x{address:x} (+{count}) is not in the core.");
    }

    /// <summary>A 64-bit word of physical memory.</summary>
    /// <param name="address">Its physical address.</param>
    /// <returns>The word.</returns>
    public ulong Read64(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(Read(address, 8));

    /// <summary>A 32-bit word of physical memory.</summary>
    /// <param name="address">Its physical address.</param>
    /// <returns>The word.</returns>
    public uint Read32(ulong address) => BinaryPrimitives.ReadUInt32LittleEndian(Read(address, 4));

    /// <summary>
    /// Whether a physical range lies in the core.
    /// </summary>
    /// <param name="address">The first byte.</param>
    /// <param name="count">The bytes.</param>
    /// <returns>Whether it does.</returns>
    public bool Contains(ulong address, int count) =>
        m_segments.Any(segment => address >= segment.Physical && address + (ulong)count <= segment.Physical + segment.Bytes);

    /// <summary>
    /// The physical address of an ARM64 virtual address through a stage-1 table of 4 KiB granules and 48-bit addresses,
    /// such as TTBR1's (the kernel's stack window).
    /// </summary>
    /// <param name="root">The level-0 table's physical address.</param>
    /// <param name="virtualAddress">The virtual address.</param>
    /// <returns>The physical address, or null where nothing is mapped.</returns>
    public ulong? TranslateA64(ulong root, ulong virtualAddress)
    {
        const ulong ADDRESS = 0x0000_FFFF_FFFF_F000UL;
        var table = root & ADDRESS;
        for (var level = 0; level < 4; level++)
        {
            var shift = 39 - 9 * level;
            var index = (virtualAddress >> shift) & 0x1FF;
            var descriptor = Read64(table + index * 8);
            if ((descriptor & 1) == 0)
                return null;
            var isTable = (descriptor & 2) != 0;
            if (level == 3)
                return isTable ? (descriptor & ADDRESS) | (virtualAddress & 0xFFF) : null;
            if (!isTable)
            {
                if (level == 0)
                    return null; // no blocks at level 0
                var mask = (1UL << shift) - 1;
                return (descriptor & ADDRESS & ~mask) | (virtualAddress & mask);
            }
            table = descriptor & ADDRESS;
        }
        return null;
    }

    private void ReadHeaders()
    {
        var identity = new byte[64];
        m_view.ReadArray(0, identity, 0, identity.Length);
        if (identity[0] != 0x7F || identity[1] != 'E' || identity[2] != 'L' || identity[3] != 'F' || identity[4] != 2 || identity[5] != 1)
            throw new InvalidDataException("The core is no little-endian ELF64 file.");
        var programHeaders = BinaryPrimitives.ReadUInt64LittleEndian(identity.AsSpan(32));
        var entrySize = BinaryPrimitives.ReadUInt16LittleEndian(identity.AsSpan(54));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(identity.AsSpan(56));
        var header = new byte[56];
        for (var i = 0; i < count; i++)
        {
            m_view.ReadArray((long)(programHeaders + (ulong)(i * entrySize)), header, 0, header.Length);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != PT_LOAD)
                continue;
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
            var physical = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24));
            var bytes = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
            if (bytes > 0)
                m_segments.Add(new Segment(offset, physical, bytes));
        }
        if (m_segments.Count == 0)
            throw new InvalidDataException("The core has no loadable segment.");
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        m_view.Dispose();
        m_file.Dispose();
    }

    #endregion

    #region Nested Types

    private sealed record Segment(ulong Offset, ulong Physical, ulong Bytes);

    #endregion
}
