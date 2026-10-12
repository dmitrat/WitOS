using System;
using System.Buffers.Binary;
using OutWit.OS.Resources;

namespace OutWit.OS.Communication;

/// <summary>
/// A creator's request that its service stop (witos/manager.h's WitServiceStop, plan step D6.1), on the channel it
/// granted the service: the service stops the services it started, ends its work so that nothing of it is lost and
/// answers a service status before it ends.
/// </summary>
public static class ServiceStop
{
    #region Constants

    /// <summary>
    /// WIT_SERVICE_STOP.
    /// </summary>
    public const uint OPERATION = 2;

    /// <summary>
    /// The size of a WitServiceStop.
    /// </summary>
    public const int SIZE = 24;

    /// <summary>
    /// ECHILD: the service ended without answering.
    /// </summary>
    public const int NO_ANSWER = 10;

    #endregion

    #region Functions

    /// <summary>
    /// The bytes of a stop.
    /// </summary>
    /// <param name="deadline">The absolute monotonic deadline of the answer, or Event.INFINITE.</param>
    /// <returns>The message's bytes.</returns>
    public static byte[] Encode(ulong deadline)
    {
        var bytes = new byte[SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, ServiceStatus.VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), OPERATION);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), deadline);
        return bytes;
    }

    /// <summary>
    /// Whether the bytes are a stop, and its deadline.
    /// </summary>
    /// <param name="bytes">A message's bytes.</param>
    /// <param name="deadline">The stop's deadline.</param>
    /// <returns>True for a WitServiceStop of this version.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out ulong deadline)
    {
        deadline = 0;
        if (bytes.Length != SIZE ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != ServiceStatus.VERSION ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != SIZE ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != OPERATION ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 0)
            return false;
        deadline = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        return true;
    }

    /// <summary>
    /// Stops a service: sends the stop on its channel and waits for its answer or its end.
    /// </summary>
    /// <param name="channel">The creator's end of the channel it granted the service.</param>
    /// <param name="process">The service's process, which the wait watches beside the channel.</param>
    /// <param name="deadline">The absolute monotonic deadline of the answer, or Event.INFINITE.</param>
    /// <returns>The service's answer: zero, its errno value, NO_ANSWER when it ended without one, or ETIMEDOUT.</returns>
    public static int Request(Channel channel, KernelHandle process, ulong deadline)
    {
        try
        {
            channel.Send(Encode(deadline));
            Event.Wait(deadline, channel.Endpoint, process);
            using var answer = channel.Receive(); // an answer the service left before it ended is still queued
            return ServiceStatus.Decode(answer.Bytes);
        }
        catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
        {
            return NO_ANSWER;
        }
        catch (CapabilityException exception)
        {
            return exception.Error;
        }
    }

    #endregion
}
