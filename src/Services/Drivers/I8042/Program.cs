using System;
using System.Collections.Generic;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.I8042;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.I8042;

/// <summary>
/// The PC's PS/2 keyboard driver (plan step H5.2, ADR 0031): the device manager starts it with the grant of a channel to
/// itself and moves the i8042's legacy descriptor and device to it over that channel. It takes the controller as the
/// firmware left it through DEVICE_PORT (<see cref="I8042Controller"/>), binds the keyboard's line to an event, and
/// answers that it is ready with the other end of an input channel, on which it serves the input protocol
/// (witos/input.h) to the display service. Each key it reports to the log as well, as the xHCI and virtio input drivers
/// do, so that a runner may wait for a key it typed. It serves both channels and the line until the device manager asks
/// it to stop or closes its own; a controller that does not answer is ENODEV.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[I8042] ";

    /// <summary>
    /// ENODEV: no controller answers at the descriptor's ports.
    /// </summary>
    private const int NO_DEVICE = 19;

    private const int KEYBOARD_LINE = 0;

    #endregion

    #region Functions

    private static int Main(string[] arguments)
    {
        var name = arguments.Length > 0 ? arguments[0] : "a device";
        using var channel = new Channel(Grant.Take());
        KernelHandle? device = null;
        KernelHandle? signal = null;
        InterruptBinding? binding = null;
        InputServer? server = null;
        Channel? events = null;
        KernelHandle? theirs = null;
        I8042Controller controller;
        try
        {
            device = Take(channel, name);
            controller = I8042Controller.Open(new I8042DevicePorts(device));
            signal = Event.Create();
            binding = InterruptBinding.Bind(device, KEYBOARD_LINE, signal);
            server = new InputServer();
            (events, theirs) = Channel.Create();
            Console.WriteLine($"{PREFIX}driver of {name}: configuration {controller.FirmwareConfiguration:x2} from the firmware, " +
                $"{controller.Configuration:x2} now (the keyboard's interrupt, translation to set 1), {controller.Flushed} bytes flushed, " +
                $"scanning {(controller.ScanningAcknowledged ? "acknowledged" : "not acknowledged")}: reporting keys, serving the input protocol");
            channel.Send(ServiceStatus.Encode(0), theirs); // the input channel's other end moves with the answer
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException or IOException)
        {
            var error = exception is CapabilityException capability ? capability.Error
                : exception is FormatException ? ServiceStatus.PROTOCOL_ERROR : NO_DEVICE;
            Console.WriteLine($"{PREFIX}driver of {name}: {exception.Message}");
            theirs?.Dispose();
            events?.Dispose();
            server?.Dispose();
            binding?.Dispose();
            signal?.Dispose();
            device?.Dispose();
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        var stopped = Serve(channel, events, server, controller, binding, signal, name);
        server.Dispose();
        binding.Dispose();
        signal.Dispose();
        device.Dispose();
        if (stopped)
        {
            Console.WriteLine($"{PREFIX}driver of {name}: stopped{(server.Dropped > 0 ? $", {server.Dropped} events dropped" : "")}");
            channel.Send(ServiceStatus.Encode(0));
        }
        return 0;
    }

    // The device manager's start: the descriptor, which must be the legacy bus's i8042, and the device, the driver's.
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
        if (descriptor.Bus != DeviceDescriptor.BUS_LEGACY || descriptor.Identity[0] != DeviceDescriptor.LEGACY_I8042 ||
            descriptor.Regions.Count < 2 || descriptor.Lines.Count < 1)
        {
            device.Dispose();
            throw new FormatException($"Descriptor {descriptor.Index} is no i8042 of the legacy bus.");
        }
        Console.WriteLine($"{PREFIX}driver of {name}: holds the device, ports 0x{descriptor.Regions[0].Base:x2} and " +
            $"0x{descriptor.Regions[1].Base:x2}, line {descriptor.Lines[0].Line}, as descriptor {descriptor.Index} names it");
        return device;
    }

    // Both channels and the keyboard's line until the device manager asks the driver to stop (D6.1) or closes its own:
    // the input protocol on the input channel while its client keeps it; on each interrupt every byte the controller holds,
    // each key reported to the log and queued for the client with a SYN, and the line acknowledged. Whether it was a stop.
    private static bool Serve(Channel manager, Channel events, InputServer server, I8042Controller controller,
        InterruptBinding binding, KernelHandle signal, string name)
    {
        Channel? client = events;
        for (; ; )
        {
            var index = client is null
                ? Event.Wait(Event.INFINITE, manager.Endpoint, signal)
                : Event.Wait(Event.INFINITE, manager.Endpoint, signal, client.Endpoint);
            if (index == 1)
            {
                var keys = controller.Service();
                binding.Acknowledge();
                if (keys.Count == 0)
                    continue;
                var reported = new List<(ushort Type, ushort Code, int Value)>();
                foreach (var (code, value) in keys)
                {
                    Console.WriteLine($"{PREFIX}key {code} {(value == ScanCodeSet1.RELEASED ? "up" : value == ScanCodeSet1.PRESSED ? "down" : "repeat")}");
                    reported.Add((ScanCodeSet1.EV_KEY, code, value));
                }
                reported.Add((ScanCodeSet1.EV_SYN, 0, 0));
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
