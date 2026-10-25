using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using OutWit.OS.Communication;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Nvme;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.Nvme;

/// <summary>
/// The NVMe driver (plan step H3.2, ADR 0025): the device manager starts it with the grant of a channel to itself and
/// moves an NVMe controller and its descriptor to it over that channel. It proves the capabilities it received — the
/// function's configuration region reads the identity the descriptor names — then enables the controller with its admin
/// queues, identifies it and its first active namespace, creates an I/O queue pair and reads the namespace's block 0. It
/// answers that it is ready with the other end of a block channel, on which it serves that namespace over the block
/// protocol (witos/block.h) as the virtio block driver serves its disk, and serves both channels until the device manager
/// asks it to stop, which it answers once the controller completed a normal shutdown, or closes its own.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[NVME] ";

    /// <summary>
    /// EIO, the errno value of a controller that does not answer as its descriptor says.
    /// </summary>
    private const int DEVICE_ERROR = 5;

    #endregion

    #region Functions

    private static int Main(string[] arguments)
    {
        var name = arguments.Length > 0 ? arguments[0] : "a device";
        using var channel = new Channel(Grant.Take());
        KernelHandle? device = null;
        NvmeController? controller = null;
        BlockServer? server = null;
        Channel? block = null;
        KernelHandle? theirs = null;
        try
        {
            (device, var descriptor) = Take(channel, name);
            controller = NvmeController.Open(device, descriptor);
            var identity = controller.Identity;
            var version = controller.Version;
            Console.WriteLine($"{PREFIX}driver of {name}: NVMe {version >> 16}.{version >> 8 & 0xFF}{((version & 0xFF) != 0 ? $".{version & 0xFF}" : "")}, " +
                $"\"{identity.Model}\", serial {identity.Serial}, firmware {identity.Firmware}, {identity.Namespaces} namespace{(identity.Namespaces == 1 ? "" : "s")}, " +
                $"queues of up to {controller.MaxQueueEntries} entries, {(identity.VolatileWriteCache ? "a volatile write cache" : "no volatile write cache")}, " +
                $"interrupts through {controller.InterruptDescription}");
            var disk = controller.Namespace;
            Console.WriteLine($"{PREFIX}namespace {disk.Id}: {disk.Capacity} bytes in blocks of {disk.BlockSize}" +
                $"{(disk.IsReadOnly ? ", write protected" : "")}, transfers of up to {(disk.MaxTransferBytes == long.MaxValue ? "any size" : $"{disk.MaxTransferBytes} bytes")}");
            Console.WriteLine($"{PREFIX}driver of {name}: read block 0 of namespace {disk.Id}: {Describe(disk.ReadBlock(0))}");
            server = BlockServer.Create(disk, device, PREFIX, name);
            (block, theirs) = Channel.Create();
            var info = server.Info;
            Console.WriteLine($"{PREFIX}driver of {name}: namespace {disk.Id}, {info.Length} bytes in blocks of {info.BlockSize}, " +
                $"{(disk.IsReadOnly ? "read-only" : "writable")}, {(disk.Flushes ? "a volatile cache it flushes" : "no volatile cache")}, " +
                $"transfers of up to {info.MaxTransfer} bytes in {disk.MaxSegments} segments, {controller.ArenaUsed / MemoryObject.PAGE + 1} pages of DMA memory: " +
                "serving the block protocol");
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
            controller?.Dispose();
            device?.Dispose();
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        var stopped = Serve(channel, block, server, name);
        server.Dispose();
        controller.Dispose(); // shut down and disabled before its memory goes
        device.Dispose();
        if (stopped)
        {
            Console.WriteLine($"{PREFIX}driver of {name}: stopped, {(controller.ShutDown ? "the controller shut down and disabled" : "the controller did not stop")}");
            channel.Send(ServiceStatus.Encode(controller.ShutDown ? 0 : DEVICE_ERROR));
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

    // What the namespace's first block holds: a protective MBR of a GUID partition table, or a boot sector.
    private static string Describe(byte[] block)
    {
        var signature = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(510));
        if (signature != 0xAA55)
            return $"no boot signature (it ends with {signature:x4})";
        if (block[446 + 4] == 0xEE)
            return "a protective MBR (signature aa55) of a GUID partition table";
        var type = Encoding.ASCII.GetString(block, 54, 8).TrimEnd();
        return $"a boot sector (signature aa55, file system {type})";
    }

    // Both channels until the device manager asks the driver to stop (D6.1) or closes its own: the block protocol on the
    // block channel while its client keeps it. Whether it was a stop.
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
