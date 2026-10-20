using System;

namespace OutWit.OS.Networking;

/// <summary>
/// The link the stack transmits on (plan step W1.2a): the network service's adapter behind the packet protocol, or a
/// simulated link in the host's tests. Received frames reach the stack through <see cref="NetworkStack.Receive"/>.
/// </summary>
public interface ILink
{
    #region Functions

    /// <summary>
    /// Queues a frame for transmission; the link copies it.
    /// </summary>
    /// <param name="frame">An Ethernet II frame without its check sequence.</param>
    void Transmit(ReadOnlySpan<byte> frame);

    #endregion

    #region Properties

    /// <summary>
    /// The adapter's address.
    /// </summary>
    MacAddress Address { get; }

    /// <summary>
    /// The largest payload of a frame.
    /// </summary>
    int Mtu { get; }

    #endregion
}
