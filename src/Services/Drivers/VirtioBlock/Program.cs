using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using OutWit.OS.Communication;
using OutWit.OS.Drivers.VirtioBlock.Virtio;
using OutWit.OS.Hardware.Virtio;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.VirtioBlock;

/// <summary>
/// The virtio block driver (plan steps D1.2, D2 and D3.2, ADR 0025): the device manager starts it with the grant of a
/// channel to itself and moves the device and its descriptor to it over that channel. It proves the capabilities it
/// received — the device's configuration region, mapped through DEVICE_MEMORY, reads the identity the descriptor names —
/// then drives the device over the modern virtio PCI transport: its queue on pages it pins for the device, its line
/// bound to an event, and one read of the disk's first sector (M4 §45, point 5). It answers that it is ready with the
/// other end of a block channel, on which it serves the block protocol (witos/block.h), and serves both channels until
/// the device manager closes its own.
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
        KernelHandle? device = null;
        VirtioPciTransport? transport = null;
        VirtioBlockQueue? queue = null;
        BlockServer? server = null;
        Channel? block = null;
        KernelHandle? theirs = null;
        try
        {
            (device, var descriptor) = Take(channel, name);
            transport = VirtioPciTransport.Open(device, descriptor);
            queue = VirtioBlockQueue.Open(transport, device);
            Console.WriteLine($"{PREFIX}driver of {name}: {Describe(queue.Read(0))}");
            server = BlockServer.Create(queue, device, name);
            (block, theirs) = Channel.Create();
            var info = server.Info;
            Console.WriteLine($"{PREFIX}driver of {name}: {info.Length} bytes in blocks of {info.BlockSize}, " +
                $"{(queue.IsReadOnly ? "read-only" : "writable")}, {(queue.Flushes ? "a volatile cache it flushes" : "no volatile cache")}, " +
                $"transfers of up to {info.MaxTransfer} bytes in {queue.MaxSegments} segments: serving the block protocol");
            channel.Send(ServiceStatus.Encode(0), theirs); // the block channel's other end moves with the answer
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException or IOException)
        {
            var error = exception is CapabilityException capability ? capability.Error
                : exception is FormatException ? ServiceStatus.PROTOCOL_ERROR : DEVICE_ERROR;
            Console.WriteLine($"{PREFIX}driver of {name} failed: {exception.Message}");
            theirs?.Dispose();
            block?.Dispose();
            server?.Dispose();
            queue?.Dispose();
            transport?.Dispose();
            device?.Dispose();
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        var stopped = Serve(channel, block, server, name);
        server.Dispose();
        queue.Dispose(); // the device quiesced before its pin goes
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

    // What the disk's first sector holds: the boot disk's FAT boot sector, its signature and its file system type, or a
    // data disk's protective MBR.
    private static string Describe(byte[] sector)
    {
        var signature = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(510));
        if (signature != 0xAA55)
            throw new IOException($"Sector 0 ends with {signature:x4}, not the boot signature aa55.");
        const string READ = "read sector 0 through queue 0 and its interrupt: ";
        if (sector[446 + 4] == 0xEE)
            return READ + "a protective MBR (signature aa55) of a GUID partition table";
        var type = Encoding.ASCII.GetString(sector, 54, 8).TrimEnd();
        return READ + $"a boot sector (signature aa55, file system {type})";
    }

    // Both channels until the device manager asks the driver to stop (D6.1), which it answers once the device is reset,
    // or closes its own: the block protocol on the block channel while its client keeps it. Whether it was a stop.
    private static bool Serve(Channel manager, Channel block, BlockServer server, string name)
    {
        Channel? client = block;
        for (; ; )
        {
            var index = client is null ? Event.Wait(Event.INFINITE, manager.Endpoint) : Event.Wait(Event.INFINITE, manager.Endpoint, client.Endpoint);
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
                Console.WriteLine($"{PREFIX}driver of {name}: the client closed the block channel");
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
                try
                {
                    client!.Send(server.Serve(message));
                }
                catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
                {
                    // The client went before its reply; the next wait sees the channel closed.
                }
            }
        }
    }

    #endregion
}
