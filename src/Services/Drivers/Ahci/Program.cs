using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using OutWit.OS.Communication;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Ahci;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.Ahci;

/// <summary>
/// The AHCI driver (plan step H3.1, ADR 0025): the device manager starts it with the grant of a channel to itself and
/// moves a SATA controller of the AHCI interface and its descriptor to it over that channel. It proves the capabilities
/// it received — the function's configuration region reads the identity the descriptor names — then takes the controller
/// from the firmware, resets it, starts the port of every ATA disk and reads the first disk's sector 0. It answers that
/// it is ready with the other end of a block channel, on which it serves that disk over the block protocol
/// (witos/block.h) as the virtio block driver serves its disk, and serves both channels until the device manager asks it
/// to stop or closes its own. A controller without an ATA disk answers ENODEV.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[AHCI] ";

    /// <summary>
    /// EIO, the errno value of a controller that does not answer as its descriptor says.
    /// </summary>
    private const int DEVICE_ERROR = 5;

    /// <summary>
    /// ENODEV, the errno value of a controller without a disk to serve.
    /// </summary>
    private const int NO_DEVICE = 19;

    #endregion

    #region Functions

    private static int Main(string[] arguments)
    {
        var name = arguments.Length > 0 ? arguments[0] : "a device";
        using var channel = new Channel(Grant.Take());
        KernelHandle? device = null;
        AhciController? controller = null;
        BlockServer? server = null;
        Channel? block = null;
        KernelHandle? theirs = null;
        try
        {
            (device, var descriptor) = Take(channel, name);
            controller = AhciController.Open(device, descriptor);
            Console.WriteLine($"{PREFIX}driver of {name}: AHCI {Version(controller.Version)}, {controller.PortCount} ports (implemented {controller.Implemented:x}), " +
                $"{controller.CommandSlots} command slots, {(controller.Is64Bit ? 64 : 32)}-bit addresses{(controller.FirmwareHeld ? ", the firmware kept it" : "")}, " +
                $"interrupts through {controller.InterruptDescription}");
            foreach (var port in controller.Ports.Where(port => port.Detection != 0))
                Console.WriteLine($"{PREFIX}port {port.Number}: {Describe(controller, port)}");
            if (controller.Disks.Count == 0)
            {
                Console.WriteLine($"{PREFIX}driver of {name}: no ATA disk on its {controller.Ports.Count} ports");
                controller.Dispose();
                device.Dispose();
                channel.Send(ServiceStatus.Encode(NO_DEVICE));
                return 1;
            }
            var disk = controller.Disks[0];
            Console.WriteLine($"{PREFIX}driver of {name}: read sector 0 of port {disk.Port}: {Describe(disk.ReadSector(0))}");
            server = BlockServer.Create(disk, device, PREFIX, name);
            (block, theirs) = Channel.Create();
            var info = server.Info;
            Console.WriteLine($"{PREFIX}driver of {name}: port {disk.Port}, {info.Length} bytes in blocks of {info.BlockSize}, writable, " +
                $"{(disk.Flushes ? "a volatile cache it flushes" : "no volatile cache")}, transfers of up to {info.MaxTransfer} bytes in " +
                $"{disk.MaxSegments} segments, {controller.ArenaUsed / MemoryObject.PAGE + 1} pages of DMA memory: serving the block protocol");
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
        controller.Dispose(); // its ports stopped before their memory goes
        device.Dispose();
        if (stopped)
        {
            Console.WriteLine($"{PREFIX}driver of {name}: stopped, the ports idle");
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

    // A port the reset found something on: the disk the driver started, or what it left.
    private static string Describe(AhciController controller, AhciPort port)
    {
        if (port.Detection != 3)
            return "a device without communication, left";
        if (controller.Disks.FirstOrDefault(disk => disk.Port == port.Number) is not { } disk)
            return port.Signature == AhciController.SIGNATURE_ATAPI
                ? $"an ATAPI device at {port.SpeedName}, left"
                : $"a device of signature {port.Signature:x8} at {port.SpeedName}, left";
        var identity = disk.Identity;
        return $"an ATA disk \"{identity.Model}\", serial {identity.Serial}, firmware {identity.Firmware}, {identity.Bytes} bytes in blocks of " +
            $"{identity.LogicalSectorBytes}{(identity.PhysicalSectorBytes != identity.LogicalSectorBytes ? $" ({identity.PhysicalSectorBytes} physical)" : "")}, " +
            $"{(identity.WriteCache ? "a write cache" : "no write cache")}, link {port.SpeedName}";
    }

    // What the disk's first sector holds: a protective MBR of a GUID partition table, or a boot sector.
    private static string Describe(byte[] sector)
    {
        var signature = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(510));
        if (signature != 0xAA55)
            return $"no boot signature (it ends with {signature:x4})";
        if (sector[446 + 4] == 0xEE)
            return "a protective MBR (signature aa55) of a GUID partition table";
        var type = Encoding.ASCII.GetString(sector, 54, 8).TrimEnd();
        return $"a boot sector (signature aa55, file system {type})";
    }

    // AHCI's version: the major in bits 31–16, then the minor's two digits, binary-coded decimal.
    private static string Version(uint version) =>
        $"{version >> 16}.{version >> 8 & 0xFF:x}{((version & 0xFF) != 0 ? $".{version & 0xFF:x}" : "")}";

    // Both channels until the device manager asks the driver to stop (D6.1), which it answers once the ports are idle,
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
