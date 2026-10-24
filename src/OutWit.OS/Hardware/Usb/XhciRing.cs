using System;
using System.Buffers.Binary;
using System.Threading;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// A ring the driver produces into and the controller consumes (xHCI 1.2 §4.9.2): the command ring or a transfer ring,
/// one segment closed by a Link TRB back to its start with Toggle Cycle. The driver writes a TRB's body, then its control
/// word with the producer's cycle bit, so that the controller never sees a TRB half written; at the Link TRB the cycle
/// toggles, and a TRB the controller left from the last lap keeps the old cycle, where the controller stops.
/// </summary>
public sealed class XhciRing
{
    #region Fields

    private readonly IDmaBuffer m_segment;
    private readonly Action m_barrier;
    private readonly int m_count;
    private int m_index;
    private bool m_cycle = true;

    #endregion

    #region Constructors

    /// <summary>
    /// Makes a ring over a zeroed segment.
    /// </summary>
    /// <param name="segment">The segment, a multiple of sixteen bytes, at least two TRBs.</param>
    /// <param name="barrier">What orders the driver's writes for the device; a full fence by default.</param>
    public XhciRing(IDmaBuffer segment, Action? barrier = null)
    {
        m_segment = segment;
        m_barrier = barrier ?? Interlocked.MemoryBarrier;
        m_count = segment.Bytes.Length / XhciTrb.BYTES;
        if (m_count < 2)
            throw new ArgumentException("A ring needs room for a TRB and its Link TRB.", nameof(segment));
        XhciTrb.Of(XhciTrb.LINK, segment.Physical).WriteBody(segment.Bytes[((m_count - 1) * XhciTrb.BYTES)..]);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Puts a TRB on the ring.
    /// </summary>
    /// <param name="trb">The TRB; its cycle bit is the ring's to set.</param>
    /// <returns>The physical address of the TRB, which the controller's event names.</returns>
    public ulong Enqueue(XhciTrb trb)
    {
        var bytes = m_segment.Bytes;
        var at = m_index * XhciTrb.BYTES;
        trb.WriteBody(bytes[at..]);
        m_barrier();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[(at + 12)..], trb.Control & ~XhciTrb.CYCLE | (m_cycle ? XhciTrb.CYCLE : 0));
        var physical = m_segment.Physical + (ulong)at;
        if (++m_index == m_count - 1)
        {
            m_barrier();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(m_index * XhciTrb.BYTES + 12)..],
                XhciTrb.LINK << 10 | XhciTrb.TOGGLE_CYCLE | (m_cycle ? XhciTrb.CYCLE : 0));
            m_index = 0;
            m_cycle = !m_cycle;
        }
        m_barrier();
        return physical;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The physical address of the segment, where the controller starts.
    /// </summary>
    public ulong Base => m_segment.Physical;

    /// <summary>
    /// The TRBs the ring holds at once: the segment's less its Link TRB.
    /// </summary>
    public int Capacity => m_count - 1;

    #endregion
}

/// <summary>
/// The ring the controller produces events into and the driver consumes (xHCI 1.2 §4.9.4): one segment the Event Ring
/// Segment Table names. A TRB whose cycle bit equals the consumer's cycle state is the controller's new event; at the
/// segment's end the state toggles. After taking events the driver writes the dequeue pointer back to ERDP.
/// </summary>
public sealed class XhciEventRing
{
    #region Fields

    private readonly IDmaBuffer m_segment;
    private readonly Action m_barrier;
    private readonly int m_count;
    private int m_index;
    private bool m_cycle = true;

    #endregion

    #region Constructors

    /// <summary>
    /// Makes an event ring over a zeroed segment.
    /// </summary>
    /// <param name="segment">The segment, at least sixteen TRBs (the smallest the specification allows).</param>
    /// <param name="barrier">What orders the driver's reads after the device's writes; a full fence by default.</param>
    public XhciEventRing(IDmaBuffer segment, Action? barrier = null)
    {
        m_segment = segment;
        m_barrier = barrier ?? Interlocked.MemoryBarrier;
        m_count = segment.Bytes.Length / XhciTrb.BYTES;
        if (m_count < 16)
            throw new ArgumentException("An event ring segment holds at least sixteen TRBs.", nameof(segment));
    }

    #endregion

    #region Functions

    /// <summary>
    /// Takes the next event, if the controller wrote one.
    /// </summary>
    /// <param name="trb">The event.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryDequeue(out XhciTrb trb)
    {
        m_barrier();
        var bytes = m_segment.Bytes[(m_index * XhciTrb.BYTES)..];
        var control = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if ((control & XhciTrb.CYCLE) != 0 != m_cycle)
        {
            trb = default;
            return false;
        }
        m_barrier();
        trb = XhciTrb.Read(bytes);
        if (++m_index == m_count)
        {
            m_index = 0;
            m_cycle = !m_cycle;
        }
        return true;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The physical address of the segment, for the Event Ring Segment Table.
    /// </summary>
    public ulong Base => m_segment.Physical;

    /// <summary>
    /// The segment's TRBs, for the Event Ring Segment Table.
    /// </summary>
    public int Count => m_count;

    /// <summary>
    /// The physical address of the next event the driver will take, for ERDP.
    /// </summary>
    public ulong DequeuePointer => m_segment.Physical + (ulong)(m_index * XhciTrb.BYTES);

    #endregion
}
