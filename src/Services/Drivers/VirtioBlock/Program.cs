using System;
using System.Buffers.Binary;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.VirtioBlock;

/// <summary>
/// The virtio block driver (plan step D1.2, ADR 0025): the device manager starts it with the grant of a channel to
/// itself and moves the device and its descriptor to it over that channel. It proves the capabilities it received: the
/// device's configuration region, mapped through DEVICE_MEMORY, reads the identity the descriptor names. It answers
/// that it is ready and serves its channel until the device manager closes it. D2 makes it read the disk.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[VIRTIO-BLOCK] ";

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
        KernelHandle device;
        try
        {
            device = Take(channel, name);
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException or IOException)
        {
            var error = exception is CapabilityException capability ? capability.Error
                : exception is FormatException ? ServiceStatus.PROTOCOL_ERROR : DEVICE_ERROR;
            Console.WriteLine($"{PREFIX}driver of {name} failed: {exception.Message}");
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        using (device)
        {
            channel.Send(ServiceStatus.Encode(0));
            return Serve(channel);
        }
    }

    // The device manager's start: the descriptor and the device, whose configuration region must read the identity the
    // descriptor names. The device handle is the driver's.
    private static KernelHandle Take(Channel channel, string name)
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
            using var mapping = MemoryMapping.Map(configuration, 0, 4096, MemoryMapping.READ);
            var identity = BinaryPrimitives.ReadUInt32LittleEndian(mapping.Bytes);
            if (identity != descriptor.Identity[0])
                throw new IOException($"The configuration space reads {identity:x8} where the descriptor names {descriptor.Identity[0]:x8}.");
            Console.WriteLine($"{PREFIX}driver of {name}: holds the device, its configuration space reads " +
                $"{(ushort)identity:x4}:{identity >> 16:x4}, as descriptor {descriptor.Index} names it");
            return device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    // The channel until the device manager closes it.
    private static int Serve(Channel channel)
    {
        for (; ; )
        {
            try
            {
                using var message = channel.Receive();
                Console.WriteLine($"{PREFIX}a request of {message.Bytes.Length} bytes, which D1.2 does not serve");
            }
            catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
            {
                return 0;
            }
        }
    }

    #endregion
}
