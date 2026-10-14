using System;
using System.Buffers.Binary;
using System.IO;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware.Virtio;

/// <summary>
/// A split virtqueue (virtio 1.x, §2.6) of up to 128 entries in three pages of a window the driver owns and pinned
/// for the device: the descriptor table, the available ring and the used ring, each in a page of its own. The driver
/// writes descriptors, submits a chain by its head and takes the chains the device used; the device's notification
/// goes through the transport. One driver thread at a time.
/// </summary>
public sealed class Virtqueue
{
    #region Constants

    /// <summary>
    /// The pages one queue takes of its window: the descriptors, the available ring, the used ring.
    /// </summary>
    public const int PAGES = 3;

    /// <summary>
    /// The largest queue: 128 descriptors of 16 bytes fill half a page, and the rings fit their pages.
    /// </summary>
    public const ushort LARGEST = 128;

    /// <summary>
    /// VIRTQ_DESC_F_NEXT: the descriptor continues in Next.
    /// </summary>
    public const ushort NEXT = 1;

    /// <summary>
    /// VIRTQ_DESC_F_WRITE: the device writes the buffer.
    /// </summary>
    public const ushort WRITE = 2;

    private const int PAGE = MemoryObject.PAGE;
    private const int DESCRIPTORS = 0;
    private const int AVAILABLE = PAGE;
    private const int USED = 2 * PAGE;

    #endregion

    #region Fields

    private readonly VirtioPciTransport m_transport;
    private readonly MemoryMapping m_pages;
    private readonly int m_offset;
    private readonly int m_notify;
    private ushort m_available;
    private ushort m_used;

    #endregion

    #region Constructors

    private Virtqueue(VirtioPciTransport transport, MemoryMapping pages, int offset, int notify, ushort size, ushort index)
    {
        m_transport = transport;
        m_pages = pages;
        m_offset = offset;
        m_notify = notify;
        Size = size;
        Index = index;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Sets up a queue of the device on three pages of the window at the offset, as large as the device offers up to
    /// the largest.
    /// </summary>
    /// <param name="transport">The device's transport, after the features were negotiated.</param>
    /// <param name="index">The queue's index.</param>
    /// <param name="pages">The window's mapping.</param>
    /// <param name="pin">The window's pin for the device.</param>
    /// <param name="offset">The offset of the queue's three pages in the window, page-aligned.</param>
    /// <param name="least">The fewest entries the driver can work with.</param>
    /// <returns>The queue.</returns>
    /// <exception cref="IOException">The device offers no such queue, or too small a one.</exception>
    public static Virtqueue Open(VirtioPciTransport transport, ushort index, MemoryMapping pages, DmaPin pin, int offset, int least)
    {
        if (offset % PAGE != 0 || offset + PAGES * PAGE > pages.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "A queue takes three whole pages of the window.");
        var offered = transport.SelectQueue(index);
        if (offered < least)
            throw new IOException($"The device's queue {index} holds {offered} entries, fewer than {least}.");
        var size = Math.Min(offered, LARGEST);
        pages.Writable.Slice(offset, PAGES * PAGE).Clear();
        var notify = transport.EnableQueue(size, pin.RunAt(offset + DESCRIPTORS).Address, pin.RunAt(offset + AVAILABLE).Address,
            pin.RunAt(offset + USED).Address);
        return new Virtqueue(transport, pages, offset, notify, size, index);
    }

    /// <summary>
    /// Writes a descriptor.
    /// </summary>
    /// <param name="index">The descriptor's index, below Size.</param>
    /// <param name="address">The buffer's physical address.</param>
    /// <param name="length">The buffer's bytes.</param>
    /// <param name="flags">NEXT, WRITE.</param>
    /// <param name="next">The next descriptor of the chain when NEXT is set.</param>
    public void Describe(int index, ulong address, uint length, ushort flags, ushort next)
    {
        if ((uint)index >= Size)
            throw new ArgumentOutOfRangeException(nameof(index));
        var descriptor = m_pages.Writable[(m_offset + DESCRIPTORS + index * 16)..];
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor, address);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[8..], length);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[12..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[14..], next);
    }

    /// <summary>
    /// Makes a chain available to the device and notifies it.
    /// </summary>
    /// <param name="head">The chain's first descriptor.</param>
    public void Submit(ushort head)
    {
        Offer(head);
        Notify();
    }

    /// <summary>
    /// Makes a chain available to the device without notifying it yet: for several chains at once, then Notify.
    /// </summary>
    /// <param name="head">The chain's first descriptor.</param>
    public void Offer(ushort head)
    {
        var pages = m_pages.Writable;
        BinaryPrimitives.WriteUInt16LittleEndian(pages[(m_offset + AVAILABLE + 4 + m_available % Size * 2)..], head);
        MmioRegion.Barrier();
        BinaryPrimitives.WriteUInt16LittleEndian(pages[(m_offset + AVAILABLE + 2)..], ++m_available);
        MmioRegion.Barrier();
    }

    /// <summary>
    /// Notifies the device of what was offered.
    /// </summary>
    public void Notify() => m_transport.Bar.Write16(m_notify, Index);

    /// <summary>
    /// Takes the next chain the device used, if any.
    /// </summary>
    /// <param name="head">The chain's first descriptor.</param>
    /// <param name="length">The bytes the device wrote into the chain.</param>
    /// <returns>Whether a used chain was there.</returns>
    public bool TryComplete(out ushort head, out uint length)
    {
        MmioRegion.Barrier();
        var bytes = m_pages.Bytes;
        var used = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(m_offset + USED + 2)..]);
        if (used == m_used)
        {
            head = 0;
            length = 0;
            return false;
        }
        var element = bytes[(m_offset + USED + 4 + m_used % Size * 8)..];
        head = (ushort)BinaryPrimitives.ReadUInt32LittleEndian(element);
        length = BinaryPrimitives.ReadUInt32LittleEndian(element[4..]);
        ++m_used;
        return true;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The queue's entries.
    /// </summary>
    public ushort Size { get; }

    /// <summary>
    /// The queue's index.
    /// </summary>
    public ushort Index { get; }

    /// <summary>
    /// The chains offered that the device has not used yet.
    /// </summary>
    public int Pending => (ushort)(m_available - m_used);

    #endregion
}
