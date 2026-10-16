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
/// The virtio input driver (plan step C2.1, ADR 0025): the device manager starts it with the grant of a channel to
/// itself and moves the device and its descriptor to it over that channel. It proves the capabilities it received —
/// the device's configuration region, mapped through DEVICE_MEMORY, reads the identity the descriptor names — then
/// drives the keyboard over the modern virtio PCI transport: its event queue on pages it pins for the device, its line
/// bound to an event. It answers that it is ready and reports every key event to the log until the device manager
/// asks it to stop or closes its channel; the display's terminal takes the keys from C2.2.
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
        try
        {
            (device, var descriptor) = Take(channel, name);
            transport = VirtioPciTransport.Open(device, descriptor);
            input = VirtioInputDevice.Open(transport, device);
            Console.WriteLine($"{PREFIX}driver of {name}: '{input.Name}', bus {input.Ids.Bus} vendor {input.Ids.Vendor:x4} product {input.Ids.Product:x4}, " +
                $"{input.Keys} keys, an event queue of {input.QueueSize} entries: reporting events");
            channel.Send(ServiceStatus.Encode(0));
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException or IOException)
        {
            var error = exception is CapabilityException capability ? capability.Error
                : exception is FormatException ? ServiceStatus.PROTOCOL_ERROR : DEVICE_ERROR;
            Console.WriteLine($"{PREFIX}driver of {name} failed: {exception.Message}");
            input?.Dispose();
            transport?.Dispose();
            device?.Dispose();
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        var stopped = Serve(channel, input, name);
        input.Dispose(); // the device quiesced before its pin goes
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

    // The channel and the device's line until the device manager asks the driver to stop (D6.1), which it answers once
    // the device is reset, or closes its own; each key event reported. Whether it was a stop.
    private static bool Serve(Channel manager, VirtioInputDevice input, string name)
    {
        for (; ; )
        {
            var index = Event.Wait(Event.INFINITE, manager.Endpoint, input.Interrupt);
            if (index == 1)
            {
                foreach (var (type, code, value) in input.Service())
                {
                    if (type == VirtioInputDevice.EVENT_KEY)
                        Console.WriteLine($"{PREFIX}key {code} {(value == 0 ? "up" : value == 1 ? "down" : "repeat")}");
                    else if (type != VirtioInputDevice.EVENT_SYN)
                        Console.WriteLine($"{PREFIX}event type {type} code {code} value {value}");
                }
                continue;
            }
            ChannelMessage message;
            try
            {
                message = manager.Receive();
            }
            catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
            {
                return false;
            }
            using (message)
            {
                if (ServiceStop.TryDecode(message.Bytes, out _))
                    return true;
                Console.WriteLine($"{PREFIX}a request of {message.Bytes.Length} bytes, which the driver does not serve");
            }
        }
    }

    #endregion
}
