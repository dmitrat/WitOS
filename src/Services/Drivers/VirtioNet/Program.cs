using System;
using System.Buffers.Binary;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Drivers.VirtioNet.Virtio;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Virtio;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.VirtioNet;

/// <summary>
/// The virtio network driver (plan step W1.1, ADR 0025): the device manager starts it with the grant of a channel to
/// itself and moves the device and its descriptor to it over that channel. It proves the capabilities it received — the
/// device's configuration region, mapped through DEVICE_MEMORY, reads the identity the descriptor names — then drives the
/// device over the modern virtio PCI transport: its receive and transmit queues on pages it pins for the device, its line
/// bound to an event. It answers that it is ready with the other end of a packet channel, on which it serves the packet
/// protocol (witos/packet.h) to its one client, and serves both channels and the device until the device manager asks it
/// to stop or closes its own.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[VIRTIO-NET] ";

    /// <summary>
    /// EIO, the errno value of a device that does not answer as its descriptor says.
    /// </summary>
    private const int DEVICE_ERROR = 5;

    #endregion

    #region Functions

    private static int Main(string[] arguments)
    {
        var name = arguments.Length > 0 ? arguments[0] : "a device";
        using var channel = new Channel(Grant.Take());
        KernelHandle? device = null;
        VirtioPciTransport? transport = null;
        VirtioNetDevice? adapter = null;
        PacketServer? server = null;
        Channel? packets = null;
        KernelHandle? theirs = null;
        try
        {
            (device, var descriptor) = Take(channel, name);
            transport = VirtioPciTransport.Open(device, descriptor);
            adapter = VirtioNetDevice.Open(transport, device);
            Console.WriteLine($"{PREFIX}driver of {name}: interrupts through {adapter.InterruptDescription}");
            server = new PacketServer(adapter);
            (packets, theirs) = Channel.Create();
            var (receive, transmit) = adapter.QueueSizes;
            Console.WriteLine($"{PREFIX}driver of {name}: address {FormatAddress(adapter.Address)}, link {(adapter.LinkUp ? "up" : "down")}, " +
                $"a receive queue of {receive} and a transmit queue of {transmit} entries: serving the packet protocol");
            channel.Send(ServiceStatus.Encode(0), theirs); // the packet channel's other end moves with the answer
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException or IOException)
        {
            var error = exception is CapabilityException capability ? capability.Error
                : exception is FormatException ? ServiceStatus.PROTOCOL_ERROR : DEVICE_ERROR;
            Console.WriteLine($"{PREFIX}driver of {name} failed: {exception.Message}");
            theirs?.Dispose();
            packets?.Dispose();
            server?.Dispose();
            adapter?.Dispose();
            transport?.Dispose();
            device?.Dispose();
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        var stopped = Serve(channel, packets, server, adapter, name);
        server.Dispose();
        adapter.Dispose(); // the device quiesced before its pin goes
        transport.Dispose();
        device.Dispose();
        if (stopped)
        {
            Console.WriteLine($"{PREFIX}driver of {name}: stopped, the device reset");
            channel.Send(ServiceStatus.Encode(0));
        }
        return 0;
    }

    /// <summary>
    /// A MAC address as its six bytes in hexadecimal, the first byte first.
    /// </summary>
    /// <param name="address">The address, its first byte the lowest.</param>
    /// <returns>The text.</returns>
    internal static string FormatAddress(ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, address);
        return $"{bytes[0]:x2}:{bytes[1]:x2}:{bytes[2]:x2}:{bytes[3]:x2}:{bytes[4]:x2}:{bytes[5]:x2}";
    }

    // The device manager's start: the descriptor and the device, whose configuration region must read the identity the
    // descriptor names. The device handle is the driver's.
    private static (KernelHandle Device, DeviceDescriptor Descriptor) Take(Channel channel, string name)
    {
        var start = channel.Receive();
        var descriptor = DriverStart.Decode(start.Bytes);
        if (start.Handles.Count != 1)
        {
            start.Dispose();
            throw new FormatException($"The driver's start carries {start.Handles.Count} handles.");
        }
        var device = start.Handles[0];
        try
        {
            using var configuration = Device.Memory(device, 0);
            using var mapping = MemoryMapping.Map(configuration, 0, MemoryObject.PAGE, MemoryMapping.READ);
            var identity = BinaryPrimitives.ReadUInt32LittleEndian(mapping.Bytes);
            if (identity != descriptor.Identity[0])
                throw new IOException($"The configuration space reads {identity:x8} where the descriptor names {descriptor.Identity[0]:x8}.");
            Console.WriteLine($"{PREFIX}driver of {name}: holds the device, its configuration space reads " +
                $"{(ushort)identity:x4}:{identity >> 16:x4}, as descriptor {descriptor.Index} names it");
            return (device, descriptor);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    // Both channels and the device's line until the device manager asks the driver to stop (D6.1), which it answers once
    // the device is reset, or closes its own: the packet protocol on the packet channel while its client keeps it, and
    // the RECEIVE held answered once the device receives a frame. Whether it was a stop.
    private static bool Serve(Channel manager, Channel packets, PacketServer server, VirtioNetDevice adapter, string name)
    {
        Channel? client = packets;
        for (; ; )
        {
            var index = client is null
                ? Event.Wait(Event.INFINITE, manager.Endpoint, adapter.Interrupt)
                : Event.Wait(Event.INFINITE, manager.Endpoint, adapter.Interrupt, client.Endpoint);
            if (index == 1)
            {
                adapter.Service();
                if (client is not null && server.Completed() is { } completed)
                    Answer(client, completed);
                continue;
            }
            var channel = index == 0 ? manager : client!;
            ChannelMessage message;
            try
            {
                message = channel.Receive();
            }
            catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
            {
                if (index == 0)
                {
                    client?.Dispose();
                    return false;
                }
                Console.WriteLine($"{PREFIX}driver of {name}: the client closed the packet channel");
                server.Detach();
                client!.Dispose();
                client = null;
                continue;
            }
            using (message)
            {
                if (index == 0)
                {
                    if (ServiceStop.TryDecode(message.Bytes, out _))
                    {
                        client?.Dispose();
                        return true;
                    }
                    Console.WriteLine($"{PREFIX}a request of {message.Bytes.Length} bytes, which the driver does not serve");
                    continue;
                }
                if (server.Serve(message) is { } reply)
                    Answer(client!, reply);
                // A SEND waited for its transmits and collected what the device received meanwhile: the RECEIVE held may
                // have its frames now, with no interrupt left to bring them.
                if (server.Completed() is { } completed)
                    Answer(client!, completed);
            }
        }
    }

    private static void Answer(Channel client, byte[] reply)
    {
        try
        {
            client.Send(reply);
        }
        catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
        {
            // The client went before its reply; the next wait sees the channel closed.
        }
    }

    #endregion
}
