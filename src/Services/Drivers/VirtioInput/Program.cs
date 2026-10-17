using System;
using System.Buffers.Binary;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Drivers.VirtioInput.Virtio;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Virtio;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.VirtioInput;

/// <summary>
/// The virtio input driver (plan steps C2.1 and C2.2, ADR 0025): the device manager starts it with the grant of a
/// channel to itself and moves the device and its descriptor to it over that channel. It proves the capabilities it
/// received — the device's configuration region, mapped through DEVICE_MEMORY, reads the identity the descriptor names —
/// then drives the keyboard over the modern virtio PCI transport: its event queue on pages it pins for the device, its
/// line bound to an event. It answers that it is ready with the other end of an input channel, on which it serves the
/// input protocol (witos/input.h) to the display service, reports every key event to the log as well, and serves both
/// channels and the device until the device manager asks it to stop or closes its own.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[VIRTIO-INPUT] ";

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
        VirtioInputDevice? input = null;
        InputServer? server = null;
        Channel? events = null;
        KernelHandle? theirs = null;
        try
        {
            (device, var descriptor) = Take(channel, name);
            transport = VirtioPciTransport.Open(device, descriptor);
            input = VirtioInputDevice.Open(transport, device);
            server = new InputServer();
            (events, theirs) = Channel.Create();
            Console.WriteLine($"{PREFIX}driver of {name}: '{input.Name}', bus {input.Ids.Bus} vendor {input.Ids.Vendor:x4} product {input.Ids.Product:x4}, " +
                $"{input.Keys} keys, an event queue of {input.QueueSize} entries: reporting events, serving the input protocol");
            channel.Send(ServiceStatus.Encode(0), theirs); // the input channel's other end moves with the answer
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException or IOException)
        {
            var error = exception is CapabilityException capability ? capability.Error
                : exception is FormatException ? ServiceStatus.PROTOCOL_ERROR : DEVICE_ERROR;
            Console.WriteLine($"{PREFIX}driver of {name} failed: {exception.Message}");
            theirs?.Dispose();
            events?.Dispose();
            server?.Dispose();
            input?.Dispose();
            transport?.Dispose();
            device?.Dispose();
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        var stopped = Serve(channel, events, server, input, name);
        server.Dispose();
        input.Dispose(); // the device quiesced before its pin goes
        transport.Dispose();
        device.Dispose();
        if (stopped)
        {
            Console.WriteLine($"{PREFIX}driver of {name}: stopped, the device reset{(server.Dropped > 0 ? $", {server.Dropped} events dropped" : "")}");
            channel.Send(ServiceStatus.Encode(0));
        }
        return 0;
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

    // Both channels and the device's line until the device manager asks the driver to stop (D6.1), which it answers
    // once the device is reset, or closes its own: the input protocol on the input channel while its client keeps it,
    // each key event reported to the log and queued for the client, the READ held answered once events come. Whether it
    // was a stop.
    private static bool Serve(Channel manager, Channel events, InputServer server, VirtioInputDevice input, string name)
    {
        Channel? client = events;
        for (; ; )
        {
            var index = client is null
                ? Event.Wait(Event.INFINITE, manager.Endpoint, input.Interrupt)
                : Event.Wait(Event.INFINITE, manager.Endpoint, input.Interrupt, client.Endpoint);
            if (index == 1)
            {
                var reported = input.Service();
                foreach (var (type, code, value) in reported)
                {
                    if (type == VirtioInputDevice.EVENT_KEY)
                        Console.WriteLine($"{PREFIX}key {code} {(value == 0 ? "up" : value == 1 ? "down" : "repeat")}");
                    else if (type != VirtioInputDevice.EVENT_SYN)
                        Console.WriteLine($"{PREFIX}event type {type} code {code} value {value}");
                }
                if (client is not null)
                {
                    server.Report(reported);
                    if (server.Completed() is { } completed)
                        Answer(client, completed);
                }
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
                Console.WriteLine($"{PREFIX}driver of {name}: the client closed the input channel");
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
