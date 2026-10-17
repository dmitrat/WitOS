using System;
using System.IO;
using System.Runtime.InteropServices;
using OutWit.OS.Communication;
using OutWit.OS.Display;
using OutWit.OS.DisplayService.Server;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.DisplayService;

/// <summary>
/// The display service (plan step C2.2, RFC 0020 §28, RFC 0009 §26): /system/Display, which the device manager starts
/// with the firmware's framebuffer and, when the board has one, the keyboard driver's input channel. It maps the
/// framebuffer, reads the bitmap font of the boot package, clears the screen and draws a terminal emulator's cells into
/// it; it answers ready with the end of a channel on which it serves the serial protocol (witos/serial.h) to the terminal
/// service, as the virtio console driver does — a WRITE paints, a READ gives the keys as a terminal's bytes, SIZE the
/// grid — until the device manager asks it to stop or closes its channel.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[DISPLAY] ";

    /// <summary>
    /// EIO, the errno value of a framebuffer or a font the service cannot use.
    /// </summary>
    private const int DEVICE_ERROR = 5;

    #endregion

    #region Functions

    private static int Main(string[] arguments)
    {
        var name = arguments.Length > 0 ? arguments[0] : "a framebuffer";
        using var channel = new Channel(Grant.Take());
        KernelHandle? device = null;
        KernelHandle? keyboardChannel = null;
        FramebufferSink? sink = null;
        KeyboardLink? keyboard = null;
        DisplayConsole? console = null;
        Channel? serial = null;
        KernelHandle? theirs = null;
        try
        {
            (device, var descriptor, keyboardChannel) = Take(channel, name);
            var geometry = FramebufferGeometry.FromDescriptor(descriptor);
            var font = BitmapFont.ParseBdf(File.ReadAllText(DisplayStart.FONT));
            sink = FramebufferSink.Map(device, geometry, (long)descriptor.Regions[0].Size);
            var renderer = new FrameRenderer(geometry, font, sink);
            var screen = new TerminalScreen(renderer.Columns, renderer.Rows);
            renderer.Clear();
            renderer.Render(screen);
            if (keyboardChannel is not null)
            {
                keyboard = KeyboardLink.Open(keyboardChannel);
                keyboardChannel = null; // the link's now
            }
            console = new DisplayConsole(screen, renderer);
            (serial, theirs) = Channel.Create();
            Console.WriteLine($"{PREFIX}display service on {RuntimeInformation.ProcessArchitecture} for {name}: {geometry.Width} by {geometry.Height} " +
                $"{geometry.FormatName} in {sink.Windows} windows, {renderer.Columns} by {renderer.Rows} cells of {font.Width} by {font.Height} " +
                $"({font.Count} glyphs of {Path.GetFileName(DisplayStart.FONT)}), {(keyboard is null ? "no keyboard" : "the keyboard's keys")}: " +
                "serving the serial protocol");
            channel.Send(ServiceStatus.Encode(0), theirs); // the console channel's other end moves with the answer
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException or IOException or ArgumentException)
        {
            var error = exception is CapabilityException capability ? capability.Error
                : exception is FormatException ? ServiceStatus.PROTOCOL_ERROR : DEVICE_ERROR;
            Console.WriteLine($"{PREFIX}display service for {name} failed: {exception.Message}");
            theirs?.Dispose();
            serial?.Dispose();
            console?.Dispose();
            keyboard?.Dispose();
            keyboardChannel?.Dispose();
            sink?.Dispose();
            device?.Dispose();
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        var stopped = Serve(channel, serial, console, ref keyboard, name);
        console.Dispose();
        keyboard?.Dispose();
        sink.Dispose();
        device.Dispose();
        if (stopped)
        {
            Console.WriteLine($"{PREFIX}display service for {name}: stopped after {console.Written} bytes, the framebuffer released");
            channel.Send(ServiceStatus.Encode(0));
        }
        return 0;
    }

    // The device manager's start: the framebuffer's descriptor, the device, and the keyboard driver's channel when there
    // is one; the handles are the service's.
    private static (KernelHandle Device, DeviceDescriptor Descriptor, KernelHandle? Keyboard) Take(Channel channel, string name)
    {
        var start = channel.Receive();
        DeviceDescriptor descriptor;
        try
        {
            descriptor = DisplayStart.Decode(start.Bytes);
            if (start.Handles.Count is not (1 or 2) || descriptor.Regions.Count < 1)
                throw new FormatException($"The display's start carries {start.Handles.Count} handles and {descriptor.Regions.Count} regions.");
        }
        catch
        {
            start.Dispose();
            throw;
        }
        Console.WriteLine($"{PREFIX}display service for {name}: holds the framebuffer, descriptor {descriptor.Index}" +
            $"{(start.Handles.Count == 2 ? ", and the keyboard's channel" : "")}");
        return (start.Handles[0], descriptor, start.Handles.Count == 2 ? start.Handles[1] : null);
    }

    // The device manager's channel, the console channel while its client keeps it, and the keyboard's until the driver
    // goes, until the device manager asks the service to stop (D6.1) or closes its own. Whether it was a stop.
    private static bool Serve(Channel manager, Channel serial, DisplayConsole console, ref KeyboardLink? keyboard, string name)
    {
        Channel? client = serial;
        for (; ; )
        {
            var endpoints = new KernelHandle[1 + (client is null ? 0 : 1) + (keyboard is null ? 0 : 1)];
            endpoints[0] = manager.Endpoint;
            var clientIndex = client is null ? -1 : 1;
            var keyboardIndex = keyboard is null ? -1 : endpoints.Length - 1;
            if (client is not null)
                endpoints[clientIndex] = client.Endpoint;
            if (keyboard is not null)
                endpoints[keyboardIndex] = keyboard.Endpoint;
            var index = Event.Wait(Event.INFINITE, endpoints);
            if (index == keyboardIndex)
            {
                try
                {
                    var bytes = keyboard!.Pump();
                    if (bytes.Length > 0)
                    {
                        console.Keys(bytes);
                        if (client is not null && console.Completed() is { } completed)
                            Answer(client, completed);
                    }
                }
                catch (Exception exception) when (exception is CapabilityException or FormatException)
                {
                    Console.WriteLine($"{PREFIX}display service for {name}: the keyboard went: {exception.Message}");
                    keyboard!.Dispose();
                    keyboard = null;
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
                Console.WriteLine($"{PREFIX}display service for {name}: the client closed the console channel");
                console.Detach();
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
                    Console.WriteLine($"{PREFIX}a request of {message.Bytes.Length} bytes, which the display service does not serve");
                    continue;
                }
                if (console.Serve(message) is { } reply)
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
