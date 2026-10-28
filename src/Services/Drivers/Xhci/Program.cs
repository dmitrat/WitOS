using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OutWit.OS.Communication;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Usb;
using OutWit.OS.Namespaces;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.Xhci;

/// <summary>
/// The xHCI driver (plan steps H2.1 and H2.2, ADR 0025 and ADR 0029): the device manager starts it with the grant of a
/// channel to itself and moves a USB controller of the xHCI interface and its descriptor to it over that channel. It
/// proves the capabilities it received — the function's configuration region reads the identity the descriptor names —
/// then takes the controller from the firmware, resets and starts it, and runs the USB core over it: the device on each
/// connected root port, the hubs among them and the devices below them, every HID keyboard among those with its reports
/// always asked for, and the devices that come and go afterwards. It answers that it is ready with the other end of an
/// input channel, on which it serves the input protocol (witos/input.h) to the display service as the virtio keyboard's
/// driver does, reports every key to the log as well, and serves both channels and the controller until the device
/// manager asks it to stop or closes its own. A drive (plan step E3: mass storage of SCSI over Bulk-Only Transport) it
/// opens as a disk when it comes, serves the block protocol on a channel of its own and announces to the device manager
/// as a removable disk, whose channel goes on to the namespace service; when the drive goes, it says so. One drive at a
/// time: the wait takes the manager's channel, the controller's interrupt, the input channel and one block channel.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[XHCI] ";

    /// <summary>
    /// EIO, the errno value of a controller that does not answer as its descriptor says.
    /// </summary>
    private const int DEVICE_ERROR = 5;

    // A drive's requests (E3): sixteen segments of at most 64 KiB, 64 KiB a command, so that a transfer's TRBs, cut at
    // 64 KiB boundaries, fit a bulk ring.
    private const int DRIVE_SEGMENTS = 16;

    private const long DRIVE_SEGMENT_BYTES = 64 * 1024;

    private const long DRIVE_TRANSFER_BYTES = 64 * 1024;

    #endregion

    #region Functions

    private static int Main(string[] arguments)
    {
        var name = arguments.Length > 0 ? arguments[0] : "a device";
        using var channel = new Channel(Grant.Take());
        KernelHandle? device = null;
        XhciController? controller = null;
        InputServer? server = null;
        Channel? events = null;
        KernelHandle? theirs = null;
        UsbBus bus;
        try
        {
            (device, var descriptor) = Take(channel, name);
            controller = XhciController.Open(device, descriptor);
            var protocols = string.Join(", ", controller.RootPorts.GroupBy(port => port.Revision)
                .Select(group => $"USB {group.Key} on {Range(group.Select(port => port.Number))}"));
            Console.WriteLine($"{PREFIX}driver of {name}: xHCI {controller.Version >> 8}.{controller.Version >> 4 & 0xF}{controller.Version & 0xF}, " +
                $"{controller.MaxSlots} slots, {controller.MaxPorts} ports ({protocols}), {controller.ContextBytes}-byte contexts, " +
                $"{controller.Scratchpads} scratchpad pages{(controller.FirmwareHeld ? ", the firmware kept it" : "")}, interrupts through {controller.InterruptDescription}");
            bus = new UsbBus(controller, line => Console.WriteLine(PREFIX + line));
            bus.Start();
            server = new InputServer();
            (events, theirs) = Channel.Create();
            Console.WriteLine($"{PREFIX}driver of {name}: {Count(bus.Devices, "device")}, {Count(bus.Hubs, "hub")}, {Count(bus.Keyboards, "keyboard")}, " +
                $"{controller.ArenaUsed / MemoryObject.PAGE + 1} pages of DMA memory: reporting keys, serving the input protocol");
            channel.Send(ServiceStatus.Encode(0), theirs); // the input channel's other end moves with the answer
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException or IOException or InvalidOperationException)
        {
            var error = exception is CapabilityException capability ? capability.Error
                : exception is FormatException ? ServiceStatus.PROTOCOL_ERROR : DEVICE_ERROR;
            Console.WriteLine($"{PREFIX}driver of {name} failed: {exception.Message}");
            theirs?.Dispose();
            events?.Dispose();
            server?.Dispose();
            controller?.Dispose();
            device?.Dispose();
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        var stopped = Serve(channel, events, server, controller, bus, device, name);
        server.Dispose();
        controller.Dispose(); // halted before its memory goes
        device.Dispose();
        if (stopped)
        {
            Console.WriteLine($"{PREFIX}driver of {name}: stopped, the controller halted and reset{(server.Dropped > 0 ? $", {server.Dropped} events dropped" : "")}");
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

    // Both channels and the controller's interrupt until the device manager asks the driver to stop (D6.1), which it
    // answers once the controller is halted, or closes its own: the input protocol on the input channel while its client
    // keeps it; on each interrupt the controller's events in order — each completed transfer to the USB core, which
    // turns a keyboard's report into key events and serves a hub's changed ports, each root port's change to the core,
    // which takes the device that came and lets go the device that went — and the key events queued for the client.
    // Whether it was a stop.
    private static bool Serve(Channel manager, Channel events, InputServer server, XhciController controller, UsbBus bus, KernelHandle device,
        string name)
    {
        Channel? client = events;
        Drive? drive = null;
        drive = Drives(manager, controller, bus, device, name, drive); // the drives the bus found at its start
        try
        {
            return Serve(manager, ref client, server, controller, bus, device, name, ref drive);
        }
        finally
        {
            drive?.Dispose();
        }
    }

    private static bool Serve(Channel manager, ref Channel? client, InputServer server, XhciController controller, UsbBus bus, KernelHandle device,
        string name, ref Drive? drive)
    {
        for (; ; )
        {
            // The manager's channel, the interrupt, then the input channel and the drive's block channel while they are open.
            var waited = new List<KernelHandle>(4) { manager.Endpoint, controller.Interrupt };
            if (client is not null)
                waited.Add(client.Endpoint);
            var blockAt = drive?.Block is null ? -1 : waited.Count;
            if (drive?.Block is { } block)
                waited.Add(block.Endpoint);
            var index = Event.Wait(Event.INFINITE, [.. waited]);
            if (index == blockAt)
            {
                ServeDrive(drive!, name);
                continue;
            }
            if (index == 1)
            {
                controller.Service();
                for (; ; )
                {
                    if (controller.TryTakeTransfer(out var transfer))
                        bus.Completed(transfer.Slot, transfer.Endpoint, transfer.CompletionCode, transfer.Residual);
                    else if (controller.TryTakePortChange(out var port))
                        bus.RootPortChanged(port);
                    else
                        break;
                }
                var reported = bus.TakeEvents();
                if (client is not null && reported.Count > 0)
                {
                    server.Report(reported);
                    if (server.Completed() is { } completed)
                        Answer(client, completed);
                }
                drive = Drives(manager, controller, bus, device, name, drive);
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
                    client = null;
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
                        client = null;
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

    // The drives that came and went since the last look (E3): one that comes opened as a disk, served on a block channel
    // whose other end goes to the device manager in a removable disk's message; one that goes let go with a message
    // that says so. A second drive while one is served is left.
    private static Drive? Drives(Channel manager, XhciController controller, UsbBus bus, KernelHandle device, string name, Drive? drive)
    {
        foreach (var (arrived, storage) in bus.TakeStorage())
        {
            var identity = $"{name}/usb-{storage.Name.Replace("port ", "", StringComparison.Ordinal)}";
            if (!arrived)
            {
                if (drive?.Storage.Slot != storage.Slot)
                    continue;
                drive.Dispose();
                drive = null;
                Tell(manager, NamespaceMessages.EncodeDisk(NamespaceMessages.DISK_GONE, identity), null);
                Console.WriteLine($"{PREFIX}{storage.Name}: the drive went; the device manager is told");
                continue;
            }
            if (drive is not null)
            {
                Console.WriteLine($"{PREFIX}{storage.Name}: a second drive, left while the one at {drive.Storage.Name} is served (one at a time)");
                continue;
            }
            try
            {
                var disk = ScsiDisk.Open(new BulkOnlyTransport(new UsbBulkPipes(controller, storage)), controller.Delay, DRIVE_SEGMENTS, DRIVE_SEGMENT_BYTES,
                    DRIVE_TRANSFER_BYTES);
                var server = BlockServer.Create(disk, device, PREFIX, identity);
                var (block, theirs) = Channel.Create();
                drive = new Drive(storage, server, block);
                Console.WriteLine($"{PREFIX}{storage.Name}: drive \"{disk.Vendor} {disk.Product} {disk.Revision}\" ({storage.Vendor:x4}:{storage.Product:x4}), " +
                    $"{disk.Capacity} bytes in blocks of {disk.BlockSize}, {(disk.IsRemovable ? "removable" : "fixed")}, " +
                    $"{(disk.IsReadOnly ? "write-protected" : "writable")}: serving the block protocol");
                var flags = NamespaceMessages.DISK_REMOVABLE | (disk.IsReadOnly ? NamespaceMessages.DISK_READ_ONLY : 0);
                Tell(manager, NamespaceMessages.EncodeDisk(flags, identity), theirs);
            }
            catch (Exception exception) when (exception is IOException or CapabilityException or ArgumentException)
            {
                Console.WriteLine($"{PREFIX}{storage.Name}: the drive could not be opened: {exception.Message}");
            }
        }
        return drive;
    }

    // One request of the drive's block channel; a client that closed it leaves the drive served to no one.
    private static void ServeDrive(Drive drive, string name)
    {
        ChannelMessage message;
        try
        {
            message = drive.Block!.Receive();
        }
        catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
        {
            Console.WriteLine($"{PREFIX}driver of {name}: the client closed the block channel of the drive at {drive.Storage.Name}");
            drive.Close();
            return;
        }
        using (message)
            Answer(drive.Block!, drive.Server.Serve(message));
    }

    private static void Tell(Channel manager, byte[] bytes, KernelHandle? handle)
    {
        try
        {
            if (handle is null)
                manager.Send(bytes);
            else
                manager.Send(bytes, handle);
        }
        catch (CapabilityException exception)
        {
            handle?.Dispose();
            Console.WriteLine($"{PREFIX}the device manager could not be told of a drive: error {exception.Error}");
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

    private static string Count(int count, string what) => $"{count} {what}{(count == 1 ? "" : "s")}";

    private static string Range(IEnumerable<int> numbers)
    {
        var list = numbers.ToList();
        return list.Count == 1 ? $"port {list[0]}" : $"ports {list.Min()}–{list.Max()}";
    }

    #endregion

    #region Types

    // A drive served (E3): its storage, the block protocol's server over its disk and the block channel, null once its
    // client closed it.
    private sealed class Drive(UsbStorage storage, BlockServer server, Channel block) : IDisposable
    {
        public UsbStorage Storage { get; } = storage;

        public BlockServer Server { get; } = server;

        public Channel? Block { get; private set; } = block;

        public void Close()
        {
            Server.Detach();
            Block?.Dispose();
            Block = null;
        }

        public void Dispose()
        {
            Close();
            Server.Dispose();
        }
    }

    #endregion
}
