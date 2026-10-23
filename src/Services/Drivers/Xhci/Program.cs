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
/// The xHCI driver (plan step H2.1, ADR 0025 and ADR 0029): the device manager starts it with the grant of a channel to
/// itself and moves a USB controller of the xHCI interface and its descriptor to it over that channel. It proves the
/// capabilities it received — the function's configuration region reads the identity the descriptor names — then takes
/// the controller from the firmware, resets and starts it, addresses the device on each connected root port, reads its
/// descriptors and drives every keyboard of the HID boot protocol among them: its interrupt endpoint configured, a report
/// always asked for. It answers that it is ready with the other end of an input channel, on which it serves the input
/// protocol (witos/input.h) to the display service as the virtio keyboard's driver does, reports every key to the log as
/// well, and serves both channels and the controller until the device manager asks it to stop or closes its own.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[XHCI] ";

    /// <summary>
    /// EIO, the errno value of a controller that does not answer as its descriptor says.
    /// </summary>
    private const int DEVICE_ERROR = 5;

    /// <summary>
    /// The bytes of a keyboard's report buffer, the most a boot keyboard's endpoint is asked for.
    /// </summary>
    private const int REPORT_BYTES = 64;

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
        List<Keyboard> keyboards;
        try
        {
            (device, var descriptor) = Take(channel, name);
            controller = XhciController.Open(device, descriptor);
            var protocols = string.Join(", ", controller.Ports.GroupBy(port => (port.Major, port.Minor))
                .Select(group => $"USB {Revision(group.Key.Major, group.Key.Minor)} on {Range(group.Select(port => port.Number))}"));
            Console.WriteLine($"{PREFIX}driver of {name}: xHCI {controller.Version >> 8}.{controller.Version >> 4 & 0xF}{controller.Version & 0xF}, " +
                $"{controller.MaxSlots} slots, {controller.MaxPorts} ports ({protocols}), {controller.ContextBytes}-byte contexts, " +
                $"{controller.Scratchpads} scratchpad pages{(controller.FirmwareHeld ? ", the firmware kept it" : "")}, interrupts through {controller.InterruptDescription}");
            keyboards = Enumerate(controller, name);
            server = new InputServer();
            (events, theirs) = Channel.Create();
            Console.WriteLine($"{PREFIX}driver of {name}: {keyboards.Count} keyboard{(keyboards.Count == 1 ? "" : "s")} of the boot protocol, " +
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
        var stopped = Serve(channel, events, server, controller, keyboards, name);
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

    // The device on every connected root port: addressed, its descriptors read, a boot keyboard among its interfaces
    // configured and asked for its first report. A port whose device fails is logged and left.
    private static List<Keyboard> Enumerate(XhciController controller, string name)
    {
        var keyboards = new List<Keyboard>();
        foreach (var port in controller.Ports)
        {
            var portsc = controller.ReadPort(port.Number);
            if ((portsc & 1) == 0)
                continue;
            var where = $"{PREFIX}port {port.Number} (USB {Revision(port.Major, port.Minor)})";
            try
            {
                var device = controller.Address(port);
                var deviceDescriptor = UsbDescriptors.ReadDevice(controller.Control(device, UsbSetup.GetDescriptor(UsbDescriptors.DEVICE, 0, UsbDescriptors.DEVICE_BYTES)));
                var head = controller.Control(device, UsbSetup.GetDescriptor(UsbDescriptors.CONFIGURATION, 0, UsbDescriptors.CONFIGURATION_BYTES));
                var total = Math.Min(UsbDescriptors.TotalLength(head), 1024);
                var configuration = UsbDescriptors.ReadConfiguration(controller.Control(device, UsbSetup.GetDescriptor(UsbDescriptors.CONFIGURATION, 0, (ushort)total)));
                Console.WriteLine($"{where}: device {deviceDescriptor.Vendor:x4}:{deviceDescriptor.Product:x4}, USB {deviceDescriptor.Usb >> 8}.{deviceDescriptor.Usb >> 4 & 0xF}, {SpeedName(device.Speed)}, " +
                    $"slot {device.Slot}, packets of {device.MaxPacket0} bytes on its control endpoint, configuration {configuration.Value} with " +
                    string.Join(", ", configuration.Interfaces.Select(item => $"interface {item.Number} class {item.Class:x2}/{item.SubClass:x2}/{item.Protocol:x2}")));
                var keyboard = configuration.Interfaces.FirstOrDefault(item => item.IsBootKeyboard && item.AlternateSetting == 0 &&
                    item.Endpoints.Any(endpoint => endpoint.IsIn && endpoint.Type == UsbEndpoint.INTERRUPT));
                if (keyboard is null)
                {
                    Console.WriteLine($"{where}: no keyboard of the boot protocol, left unconfigured");
                    continue;
                }
                var input = keyboard.Endpoints.First(endpoint => endpoint.IsIn && endpoint.Type == UsbEndpoint.INTERRUPT);
                var index = controller.ConfigureInterruptIn(device, input);
                controller.Control(device, UsbSetup.SetConfiguration(configuration.Value));
                controller.Control(device, UsbSetup.SetProtocol(keyboard.Number, UsbSetup.BOOT_PROTOCOL));
                try
                {
                    controller.Control(device, UsbSetup.SetIdle(keyboard.Number, 0));
                }
                catch (IOException exception)
                {
                    Console.WriteLine($"{where}: SET_IDLE refused ({exception.Message}); the keyboard reports as it will");
                }
                var length = Math.Clamp(input.PacketBytes, HidBootKeyboard.REPORT_BYTES, REPORT_BYTES);
                var entry = new Keyboard(device, index, controller.Allocate(REPORT_BYTES), length);
                controller.Receive(device, index, entry.Buffer, length);
                keyboards.Add(entry);
                Console.WriteLine($"{where}: a keyboard of the boot protocol on interface {keyboard.Number}, endpoint {input.Address:x2} " +
                    $"of {input.PacketBytes} bytes at interval {input.Interval}: reporting keys");
            }
            catch (Exception exception) when (exception is IOException or FormatException)
            {
                Console.WriteLine($"{where}: {exception.Message}");
            }
        }
        if (controller.TakePortChanges() is { Count: > 0 } changes)
            Console.WriteLine($"{PREFIX}driver of {name}: ports {string.Join(", ", changes.Distinct())} reported changes while it enumerated");
        return keyboards;
    }

    // Both channels and the controller's interrupt until the device manager asks the driver to stop (D6.1), which it
    // answers once the controller is halted, or closes its own: the input protocol on the input channel while its client
    // keeps it, each report turned into key events, logged and queued for the client, and asked for again. Whether it
    // was a stop.
    private static bool Serve(Channel manager, Channel events, InputServer server, XhciController controller, List<Keyboard> keyboards,
        string name)
    {
        Channel? client = events;
        var reported = new List<(ushort Type, ushort Code, int Value)>();
        for (; ; )
        {
            var index = client is null
                ? Event.Wait(Event.INFINITE, manager.Endpoint, controller.Interrupt)
                : Event.Wait(Event.INFINITE, manager.Endpoint, controller.Interrupt, client.Endpoint);
            if (index == 1)
            {
                reported.Clear();
                foreach (var transfer in controller.Service())
                    Report(controller, keyboards, transfer, reported);
                foreach (var port in controller.TakePortChanges().Distinct())
                    Console.WriteLine($"{PREFIX}port {port} changed: PORTSC {controller.ReadPort(port):x8}; the driver takes no device after its start");
                foreach (var (type, code, value) in reported)
                {
                    if (type == InputMessages.KEY)
                        Console.WriteLine($"{PREFIX}key {code} {(value == 0 ? "up" : value == 1 ? "down" : "repeat")}");
                }
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

    // A keyboard's completed transfer: its report turned into events and the next one asked for; a failed one ends the
    // keyboard's reports, since a halted endpoint takes a reset this driver does not yet send.
    private static void Report(XhciController controller, List<Keyboard> keyboards, XhciTrb transfer,
        List<(ushort Type, ushort Code, int Value)> reported)
    {
        var keyboard = keyboards.FirstOrDefault(item => item.Device.Slot == transfer.Slot && item.Index == transfer.Endpoint);
        if (keyboard is null)
            return;
        if (transfer.CompletionCode is not (XhciTrb.SUCCESS or XhciTrb.SHORT_PACKET))
        {
            Console.WriteLine($"{PREFIX}keyboard of slot {transfer.Slot}: a report completed with code {transfer.CompletionCode}; its reports end");
            keyboards.Remove(keyboard);
            return;
        }
        var bytes = Math.Clamp(keyboard.Length - transfer.Residual, 0, keyboard.Length);
        keyboard.Hid.Update(keyboard.Buffer.Bytes[..bytes], reported);
        controller.Receive(keyboard.Device, keyboard.Index, keyboard.Buffer, keyboard.Length);
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

    private static string SpeedName(int speed) => speed switch
    {
        XhciContexts.FULL_SPEED => "full speed",
        XhciContexts.LOW_SPEED => "low speed",
        XhciContexts.HIGH_SPEED => "high speed",
        XhciContexts.SUPER_SPEED => "SuperSpeed",
        _ => $"speed {speed}",
    };

    // A Supported Protocol's revision: the minor revision is binary-coded decimal (0x20 for USB 3.2), the major plain.
    private static string Revision(int major, int minor) => (minor & 0xF) != 0 ? $"{major}.{minor >> 4}{minor & 0xF}" : $"{major}.{minor >> 4}";

    private static string Range(IEnumerable<int> numbers)
    {
        var list = numbers.ToList();
        return list.Count == 1 ? $"port {list[0]}" : $"ports {list.Min()}–{list.Max()}";
    }

    #endregion

    #region Nested Types

    private sealed class Keyboard(XhciDevice device, int index, IDmaBuffer buffer, int length)
    {
        public XhciDevice Device { get; } = device;

        public int Index { get; } = index;

        public IDmaBuffer Buffer { get; } = buffer;

        public int Length { get; } = length;

        public HidBootKeyboard Hid { get; } = new();
    }

    #endregion
}
