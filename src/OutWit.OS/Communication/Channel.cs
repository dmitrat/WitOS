using System;
using System.Collections.Generic;
using OutWit.OS.Native;
using OutWit.OS.Resources;

namespace OutWit.OS.Communication;

/// <summary>
/// An endpoint of a channel (RFC 0011 §7.6): bounded messages of bytes and handles, which move to the peer with the
/// message.
/// </summary>
public sealed class Channel : IDisposable
{
    #region Constants

    /// <summary>
    /// The bytes one message holds.
    /// </summary>
    public const int MESSAGE_BYTES = 256;

    /// <summary>
    /// The handles one message holds.
    /// </summary>
    public const int MESSAGE_HANDLES = 4;

    /// <summary>
    /// libwitos's errno value for a peer that closed its end (EPIPE).
    /// </summary>
    public const int PEER_CLOSED = 32;

    #endregion

    #region Constructors

    /// <summary>
    /// Takes ownership of an endpoint.
    /// </summary>
    /// <param name="endpoint">The endpoint's handle.</param>
    public Channel(KernelHandle endpoint)
    {
        Endpoint = endpoint;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Sends a message; the handles move to the peer and are no longer the caller's.
    /// </summary>
    /// <param name="bytes">At most MESSAGE_BYTES bytes.</param>
    /// <param name="handles">At most MESSAGE_HANDLES handles, each with TRANSFER.</param>
    /// <exception cref="CapabilityException">The peer closed its end, its queue is full or a handle cannot move; the
    /// handles stay the caller's.</exception>
    public unsafe void Send(ReadOnlySpan<byte> bytes, params ReadOnlySpan<KernelHandle> handles)
    {
        if (bytes.Length > MESSAGE_BYTES || handles.Length > MESSAGE_HANDLES)
            throw new ArgumentException("A message holds 256 bytes and four handles.");
        var values = stackalloc ulong[MESSAGE_HANDLES];
        for (var i = 0; i < handles.Length; i++)
            values[i] = handles[i].Value;
        fixed (byte* data = bytes)
        {
            CapabilityException.ThrowOnError("CHANNEL_SEND",
                Libwitos.ChannelSend(Endpoint.Value, data, (uint)bytes.Length, values, (uint)handles.Length));
        }
        foreach (var handle in handles)
            handle.Moved();
    }

    /// <summary>
    /// Waits for a message and receives it.
    /// </summary>
    /// <returns>The message; its handles are the caller's.</returns>
    /// <exception cref="CapabilityException">The peer closed its end and nothing is queued (Error is PEER_CLOSED).</exception>
    public unsafe ChannelMessage Receive()
    {
        var data = stackalloc byte[MESSAGE_BYTES];
        var values = stackalloc ulong[MESSAGE_HANDLES];
        uint count = 0, handleCount = 0;
        CapabilityException.ThrowOnError("CHANNEL_RECEIVE",
            Libwitos.ChannelReceive(Endpoint.Value, data, MESSAGE_BYTES, &count, values, MESSAGE_HANDLES, &handleCount));
        var handles = new List<KernelHandle>((int)handleCount);
        for (var i = 0; i < handleCount; i++)
            handles.Add(new KernelHandle(values[i]));
        return new ChannelMessage(new ReadOnlySpan<byte>(data, (int)count).ToArray(), handles);
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Endpoint.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The endpoint's handle.
    /// </summary>
    public KernelHandle Endpoint { get; }

    #endregion
}
