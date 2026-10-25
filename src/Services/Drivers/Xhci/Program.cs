using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OutWit.OS.Communication;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Usb;
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
/// manager asks it to stop or closes its own.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[XHCI] ";

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
        var stopped = Serve(channel, events, server, controller, bus, name);
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
    private static bool Serve(Channel manager, Channel events, InputServer server, XhciController controller, UsbBus bus, string name)
    {
        Channel? client = events;
        for (; ; )
        {
            var index = client is null
                ? Event.Wait(Event.INFINITE, manager.Endpoint, controller.Interrupt)
                : Event.Wait(Event.INFINITE, manager.Endpoint, controller.Interrupt, client.Endpoint);
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

    private static string Count(int count, string what) => $"{count} {what}{(count == 1 ? "" : "s")}";

    private static string Range(IEnumerable<int> numbers)
    {
        var list = numbers.ToList();
        return list.Count == 1 ? $"port {list[0]}" : $"ports {list.Min()}–{list.Max()}";
    }

    #endregion
}
