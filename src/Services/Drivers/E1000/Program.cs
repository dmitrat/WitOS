using System;
using System.Buffers.Binary;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.E1000;
using OutWit.OS.Networking;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.E1000;

/// <summary>
/// The e1000e driver (plan step H4, ADR 0025): the device manager starts it with the grant of a channel to itself and
/// moves an Intel 82574L and its descriptor to it over that channel. It proves the capabilities it received — the
/// function's configuration region reads the identity the descriptor names — then takes the adapter from whatever drove
/// it, resets it and starts its receiver and transmitter on pages it pins for the adapter, its interrupt bound to an
/// event. It answers that it is ready with the other end of a packet channel, on which it serves the packet protocol
/// (witos/packet.h) to its one client as the virtio network driver does, and serves both channels and the adapter until
/// the device manager asks it to stop or closes its own.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[E1000] ";

    /// <summary>
    /// EIO, the errno value of an adapter that does not answer as its descriptor says.
    /// </summary>
    private const int DEVICE_ERROR = 5;

    #endregion

    #region Functions

    private static int Main(string[] arguments)
    {
        var name = arguments.Length > 0 ? arguments[0] : "a device";
        using var channel = new Channel(Grant.Take());
        KernelHandle? device = null;
        E1000Adapter? adapter = null;
        PacketServer? server = null;
        Channel? packets = null;
        KernelHandle? theirs = null;
        try
        {
            (device, var descriptor) = Take(channel, name);
            adapter = E1000Adapter.Open(device, descriptor);
            Console.WriteLine($"{PREFIX}driver of {name}: Intel {Model(descriptor.Device)}, interrupts through {adapter.InterruptDescription}");
            server = new PacketServer(adapter);
            (packets, theirs) = Channel.Create();
            var link = adapter.LinkUp ? $"up at {adapter.Speed} Mb/s {(adapter.FullDuplex ? "full" : "half")} duplex" : "down";
            Console.WriteLine($"{PREFIX}driver of {name}: address {new MacAddress(adapter.Address)}, link {link}, " +
                $"{E1000Adapter.RECEIVE_COUNT} receive and {E1000Adapter.TRANSMIT_COUNT} transmit descriptors, {adapter.ArenaPages} pages of DMA memory: " +
                "serving the packet protocol");
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
            device?.Dispose();
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        var stopped = Serve(channel, packets, server, adapter, name);
        server.Dispose();
        adapter.Dispose(); // the adapter reset before its pages go
        device.Dispose();
        if (stopped)
        {
            Console.WriteLine($"{PREFIX}driver of {name}: stopped, the adapter reset{(adapter.Dropped + adapter.Overruns > 0 ? $" ({adapter.Dropped} frames dropped for a slow client, {adapter.Overruns} receive overruns)" : "")}");
            channel.Send(ServiceStatus.Encode(0));
        }
        return 0;
    }

    // The adapter's name by its device identifier.
    private static string Model(ushort device) => device == E1000Adapter.DEVICE_82574L ? "82574L" : $"adapter {device:x4}";

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

    // Both channels and the adapter's interrupt until the device manager asks the driver to stop (D6.1), which it answers
    // once the adapter is reset, or closes its own: the packet protocol on the packet channel while its client keeps it,
    // and the RECEIVE held answered once the adapter receives a frame. Whether it was a stop.
    private static bool Serve(Channel manager, Channel packets, PacketServer server, E1000Adapter adapter, string name)
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
                byte[]? reply;
                try
                {
                    reply = server.Serve(message);
                }
                catch (IOException exception)
                {
                    // The adapter stopped completing transmits: the SEND fails and the driver goes on serving.
                    Console.WriteLine($"{PREFIX}driver of {name}: {exception.Message}");
                    reply = PacketMessages.Encode(new PacketReply(PacketMessages.SEND, PacketMessages.DEVICE_ERROR));
                }
                if (reply is not null)
                    Answer(client!, reply);
                // A SEND waited for its transmits and collected what the adapter received meanwhile: the RECEIVE held may
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
