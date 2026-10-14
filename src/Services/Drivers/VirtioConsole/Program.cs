using System;
using System.Buffers.Binary;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Drivers.VirtioConsole.Virtio;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Virtio;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.VirtioConsole;

/// <summary>
/// The virtio console driver (plan step N4.3a, ADR 0025): the device manager starts it with the grant of a channel to
/// itself and moves the device and its descriptor to it over that channel. It proves the capabilities it received —
/// the device's configuration region, mapped through DEVICE_MEMORY, reads the identity the descriptor names — then
/// drives the device over the modern virtio PCI transport: its receive and transmit queues on pages it pins for the
/// device, its line bound to an event. It answers that it is ready with the other end of a serial channel, on which it
/// serves the serial protocol (witos/serial.h) to the terminal service, and serves both channels and the device until
/// the device manager asks it to stop or closes its own.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[VIRTIO-CONSOLE] ";

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
        VirtioConsoleDevice? console = null;
        SerialServer? server = null;
        Channel? serial = null;
        KernelHandle? theirs = null;
        try
        {
            (device, var descriptor) = Take(channel, name);
            transport = VirtioPciTransport.Open(device, descriptor);
            console = VirtioConsoleDevice.Open(transport, device);
            server = new SerialServer(console, name);
            (serial, theirs) = Channel.Create();
            var (receive, transmit) = console.QueueSizes;
            Console.WriteLine($"{PREFIX}driver of {name}: port 0 with a receive queue of {receive} and a transmit queue of {transmit} entries, " +
                $"{(console.Columns > 0 ? $"{console.Columns} by {console.Rows}" : "no size reported")}: serving the serial protocol");
            channel.Send(ServiceStatus.Encode(0), theirs); // the serial channel's other end moves with the answer
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException or IOException)
        {
            var error = exception is CapabilityException capability ? capability.Error
                : exception is FormatException ? ServiceStatus.PROTOCOL_ERROR : DEVICE_ERROR;
            Console.WriteLine($"{PREFIX}driver of {name} failed: {exception.Message}");
            theirs?.Dispose();
            serial?.Dispose();
            server?.Dispose();
            console?.Dispose();
            transport?.Dispose();
            device?.Dispose();
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        var stopped = Serve(channel, serial, server, console, name);
        server.Dispose();
        console.Dispose(); // the device quiesced before its pin goes
        transport.Dispose();
        device.Dispose();
        if (stopped)
        {
            Console.WriteLine($"{PREFIX}driver of {name}: stopped, the device reset");
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
    // once the device is reset, or closes its own: the serial protocol on the serial channel while its client keeps
    // it, and the READ held answered once the device receives something. Whether it was a stop.
    private static bool Serve(Channel manager, Channel serial, SerialServer server, VirtioConsoleDevice console, string name)
    {
        Channel? client = serial;
        for (; ; )
        {
            var index = client is null
                ? Event.Wait(Event.INFINITE, manager.Endpoint, console.Interrupt)
                : Event.Wait(Event.INFINITE, manager.Endpoint, console.Interrupt, client.Endpoint);
            if (index == 1)
            {
                console.Service();
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
                Console.WriteLine($"{PREFIX}driver of {name}: the client closed the serial channel");
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
