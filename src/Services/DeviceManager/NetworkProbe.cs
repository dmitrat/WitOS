using System;
using System.Buffers.Binary;
using OutWit.OS.Communication;
using OutWit.OS.DeviceManager.Drivers;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The network's probe (plan step W1.1, as the storage probes of D2): the device manager attaches a window to the network
/// driver's packet channel as the network service will (W1.2), reads the adapter's address, MTU and link, sends an ARP
/// request for the board's gateway from the address QEMU's user networking leases first, and reports the gateway's
/// reply, which the runtime-devices scenario requires. The channel then goes to the network service (W1.2a), whose window
/// replaces the probe's.
/// </summary>
internal static class NetworkProbe
{
    #region Constants

    private const string PREFIX = "[DEVICES] ";

    private const int WAIT_SECONDS = 10;

    // ETIMEDOUT: a wait's deadline passed.
    private const int TIMED_OUT = 110;

    private const int WINDOW_BYTES = 4 * MemoryObject.PAGE;

    private const int RECEIVE_AT = 2 * MemoryObject.PAGE;

    // QEMU's user networking (slirp): its gateway, and the first address its DHCP server leases.
    private static readonly byte[] GATEWAY = [10, 0, 2, 2];
    private static readonly byte[] ADDRESS = [10, 0, 2, 15];

    private const ushort ARP = 0x0806;
    private const ushort IPV4 = 0x0800;
    private const ushort REQUEST = 1;
    private const ushort REPLY = 2;

    #endregion

    #region Functions

    /// <summary>
    /// Runs the probe on a network driver's packet channel, which it takes and gives back for the network service.
    /// </summary>
    /// <param name="driver">A network driver that answered ready.</param>
    /// <returns>The packet channel.</returns>
    public static Channel Run(DriverActivation driver)
    {
        var channel = new Channel(driver.TakeService());
        Probe(channel, driver.Record.Id);
        return channel;
    }

    // The exchange; the probe's window goes when it ends, and the driver keeps its mapping until the next ATTACH.
    private static void Probe(Channel channel, string device)
    {
        using var window = MemoryObject.Create(WINDOW_BYTES);
        using var mapping = MemoryMapping.Map(window, 0, WINDOW_BYTES, MemoryMapping.READ_WRITE);
        try
        {
            using (var theirs = window.Duplicate(Rights.Map | Rights.Write | Rights.Transfer))
            {
                var attached = Call(channel, new PacketRequest(PacketMessages.ATTACH, 0, WINDOW_BYTES), theirs);
                if (attached.Error != 0)
                {
                    Console.WriteLine($"{PREFIX}network {device}: the driver refused a window, errno {attached.Error}");
                    return;
                }
            }
            var info = Call(channel, new PacketRequest(PacketMessages.INFO, 0, 0));
            if (info.Error != 0)
            {
                Console.WriteLine($"{PREFIX}network {device}: INFO failed, errno {info.Error}");
                return;
            }
            Console.WriteLine($"{PREFIX}network {device}: address {Mac(info.Address)}, MTU {info.Mtu}, link " +
                $"{((info.Link & PacketMessages.LINK_UP) != 0 ? "up" : "down")}, through the packet channel and a window of {WINDOW_BYTES} bytes");
            var packed = PacketMessages.Pack(mapping.Writable, ArpRequest(info.Address));
            var sent = Call(channel, new PacketRequest(PacketMessages.SEND, 0, packed, 0, 1));
            if (sent.Error != 0)
            {
                Console.WriteLine($"{PREFIX}network {device}: SEND failed, errno {sent.Error}");
                return;
            }
            var deadline = Clock.MonotonicAfter(WAIT_SECONDS);
            var frames = 0;
            for (; ; )
            {
                channel.Send(PacketMessages.Encode(new PacketRequest(PacketMessages.RECEIVE, 0, WINDOW_BYTES - RECEIVE_AT, RECEIVE_AT)));
                try
                {
                    Event.Wait(deadline, channel.Endpoint);
                }
                catch (CapabilityException exception) when (exception.Error == TIMED_OUT)
                {
                    Console.WriteLine($"{PREFIX}network {device}: no ARP reply from {Ip(GATEWAY)} within {WAIT_SECONDS} s, {frames} other frames");
                    return;
                }
                PacketReply received;
                using (var message = channel.Receive())
                    received = PacketMessages.DecodeReply(message.Bytes);
                if (received.Error != 0)
                {
                    Console.WriteLine($"{PREFIX}network {device}: RECEIVE failed, errno {received.Error}");
                    return;
                }
                var bytes = mapping.Bytes.Slice(RECEIVE_AT, (int)received.Length);
                for (var offset = 0; offset < bytes.Length;)
                {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
                    var frame = bytes.Slice(offset + PacketMessages.FRAME_HEADER_BYTES, length);
                    if (IsGatewayReply(frame, out var gateway))
                    {
                        Console.WriteLine($"{PREFIX}network {device}: ARP reply from {Ip(GATEWAY)} at {gateway}");
                        return;
                    }
                    frames++;
                    offset += PacketMessages.PackedBytes(length);
                }
            }
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException)
        {
            Console.WriteLine($"{PREFIX}network {device}: the probe failed: {exception.Message}");
        }
    }

    private static PacketReply Call(Channel channel, PacketRequest request, params ReadOnlySpan<KernelHandle> handles)
    {
        channel.Send(PacketMessages.Encode(request), handles);
        using var message = channel.Receive();
        return PacketMessages.DecodeReply(message.Bytes);
    }

    // An ARP request (RFC 826) broadcast for the gateway's address, from the adapter with the first leased address.
    private static byte[] ArpRequest(ulong address)
    {
        var frame = new byte[42];
        frame.AsSpan(0, 6).Fill(0xFF);
        WriteMac(frame.AsSpan(6), address);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), ARP);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(14), 1); // Ethernet
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), IPV4);
        frame[18] = 6;
        frame[19] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(20), REQUEST);
        WriteMac(frame.AsSpan(22), address);
        ADDRESS.CopyTo(frame, 28);
        GATEWAY.CopyTo(frame, 38);
        return frame;
    }

    // Whether a frame is the gateway's ARP reply to the probe's address, and the gateway's MAC address.
    private static bool IsGatewayReply(ReadOnlySpan<byte> frame, out string gateway)
    {
        gateway = "";
        if (frame.Length < 42 || BinaryPrimitives.ReadUInt16BigEndian(frame[12..]) != ARP ||
            BinaryPrimitives.ReadUInt16BigEndian(frame[16..]) != IPV4 || BinaryPrimitives.ReadUInt16BigEndian(frame[20..]) != REPLY ||
            !frame.Slice(28, 4).SequenceEqual(GATEWAY) || !frame.Slice(38, 4).SequenceEqual(ADDRESS))
            return false;
        gateway = $"{frame[22]:x2}:{frame[23]:x2}:{frame[24]:x2}:{frame[25]:x2}:{frame[26]:x2}:{frame[27]:x2}";
        return true;
    }

    private static void WriteMac(Span<byte> target, ulong address)
    {
        for (var i = 0; i < 6; i++)
            target[i] = (byte)(address >> (8 * i));
    }

    private static string Mac(ulong address)
    {
        Span<byte> bytes = stackalloc byte[6];
        WriteMac(bytes, address);
        return $"{bytes[0]:x2}:{bytes[1]:x2}:{bytes[2]:x2}:{bytes[3]:x2}:{bytes[4]:x2}:{bytes[5]:x2}";
    }

    private static string Ip(byte[] address) => $"{address[0]}.{address[1]}.{address[2]}.{address[3]}";

    #endregion
}
