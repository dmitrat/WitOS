using System;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware;

/// <summary>
/// Bytes a device reads and writes in place: what a ring, a context or a buffer of a controller lives in. Each buffer lies
/// within one page, so that its physical bytes are one run.
/// </summary>
public interface IDmaBuffer
{
    /// <summary>
    /// The buffer's bytes as the driver sees them.
    /// </summary>
    Span<byte> Bytes { get; }

    /// <summary>
    /// The physical address of its first byte, as the device sees it.
    /// </summary>
    ulong Physical { get; }
}

/// <summary>
/// One anonymous memory object pinned for a device (plan step H2.1), cut into buffers that each lie within one page:
/// a bump allocator for a driver's structures, all of which live as long as the driver drives the device. The object's
/// pages are zeroed when made, so every buffer starts zeroed.
/// </summary>
public sealed class DmaArena : IDisposable
{
    #region Fields

    private readonly KernelHandle m_memory;
    private readonly MemoryMapping m_mapping;
    private readonly DmaPin m_pin;
    private int m_next;

    #endregion

    #region Constructors

    private DmaArena(KernelHandle memory, MemoryMapping mapping, DmaPin pin)
    {
        m_memory = memory;
        m_mapping = mapping;
        m_pin = pin;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Makes and pins an arena of whole pages for a device.
    /// </summary>
    /// <param name="device">The device, with BIND.</param>
    /// <param name="pages">Its pages, at most 64.</param>
    /// <returns>The arena.</returns>
    /// <exception cref="CapabilityException">The memory could not be made, mapped or pinned.</exception>
    public static DmaArena Create(KernelHandle device, int pages)
    {
        var memory = MemoryObject.Create(pages * MemoryObject.PAGE);
        MemoryMapping? mapping = null;
        try
        {
            mapping = MemoryMapping.Map(memory, 0, pages * MemoryObject.PAGE, MemoryMapping.READ_WRITE);
            return new DmaArena(memory, mapping, DmaPin.Pin(device, memory, 0, pages * MemoryObject.PAGE));
        }
        catch
        {
            mapping?.Dispose();
            memory.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Takes a buffer from the arena: aligned as asked, within one page, after the last one taken.
    /// </summary>
    /// <param name="bytes">Its bytes, at most a page.</param>
    /// <param name="alignment">Its alignment, a power of two up to a page.</param>
    /// <returns>The buffer.</returns>
    /// <exception cref="InvalidOperationException">The arena has no room left.</exception>
    public IDmaBuffer Allocate(int bytes, int alignment = 64)
    {
        if (bytes is <= 0 or > MemoryObject.PAGE || alignment <= 0 || (alignment & alignment - 1) != 0 || alignment > MemoryObject.PAGE)
            throw new ArgumentOutOfRangeException(nameof(bytes), $"A buffer of {bytes} bytes aligned to {alignment}.");
        var offset = m_next + alignment - 1 & -alignment;
        if (offset / MemoryObject.PAGE != (offset + bytes - 1) / MemoryObject.PAGE)
            offset = (offset + MemoryObject.PAGE - 1) & -MemoryObject.PAGE;
        if (offset + bytes > m_mapping.Length)
            throw new InvalidOperationException($"The arena of {m_mapping.Length} bytes has no room for {bytes} more.");
        m_next = offset + bytes;
        return new Buffer(this, offset, bytes);
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        m_pin.Dispose();
        m_mapping.Dispose();
        m_memory.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The bytes taken so far, padding included.
    /// </summary>
    public int Used => m_next;

    #endregion

    #region Nested Types

    private sealed class Buffer(DmaArena arena, int offset, int length) : IDmaBuffer
    {
        public Span<byte> Bytes => arena.m_mapping.Writable.Slice(offset, length);

        public ulong Physical { get; } = arena.m_pin.RunAt(offset).Address;
    }

    #endregion
}
