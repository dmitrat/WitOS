using System;
using System.Collections.Generic;

namespace OutWit.OS.Hardware;

/// <summary>
/// A network adapter as the packet protocol serves it (witos/packet.h, plan steps W1.1 and H4): its address, link and
/// MTU, the frames it received waiting in the driver's own queue, and the frames it transmits. The virtio network device
/// and an Intel adapter of the e1000e family implement it; <see cref="PacketServer"/> serves any of them. One driver
/// thread at a time.
/// </summary>
public interface IPacketDevice
{
    #region Functions

    /// <summary>
    /// Transmits frames, waiting for the device to free a buffer when none is left; what the device receives meanwhile
    /// waits in the frame queue. Returns once the device took every frame.
    /// </summary>
    /// <param name="frames">The frames, each of 14 to 14 + <see cref="Mtu"/> bytes.</param>
    void Transmit(IReadOnlyList<ReadOnlyMemory<byte>> frames);

    /// <summary>
    /// Takes the oldest frame the device received, if any.
    /// </summary>
    /// <returns>The frame, or null when none waits.</returns>
    byte[]? Take();

    /// <summary>
    /// The length of the oldest frame waiting, or zero.
    /// </summary>
    /// <returns>Its bytes.</returns>
    int PeekLength();

    #endregion

    #region Properties

    /// <summary>
    /// The adapter's MAC address, its first byte the lowest.
    /// </summary>
    ulong Address { get; }

    /// <summary>
    /// Whether the link is up.
    /// </summary>
    bool LinkUp { get; }

    /// <summary>
    /// The largest payload of a frame the adapter sends and receives.
    /// </summary>
    int Mtu { get; }

    /// <summary>
    /// The frames waiting for the client.
    /// </summary>
    int Pending { get; }

    #endregion
}
