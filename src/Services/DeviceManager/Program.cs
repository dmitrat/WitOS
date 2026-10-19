using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OutWit.OS.Communication;
using OutWit.OS.DeviceManager.Drivers;
using OutWit.OS.DeviceManager.Uhi;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;
using OutWit.OS.Storage;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The device manager (plan steps D1.1, D1.2 and D3.2, ADR 0025): the root task starts it before /bin/init and moves the
/// device table to it over the channel it granted it. It reads the table, translates every descriptor into a UHI device
/// record and prints its discovery line (Implementation Strategy §44), starts the driver of every device its driver
/// table matches and moves the device to it, probes the disk of every block driver through its block channel (D3.2),
/// starts the namespace service with the disks (D5.1) and the network service with the adapters (W1.2a), answers that it
/// is ready with the namespace's endpoint, and serves its channel until the root task asks it to stop (D6.1), when it
/// stops the network service, the namespace service, which unmounts its volumes, then the drivers, and answers.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[DEVICES] ";

    #endregion

    #region Functions

    private static int Main()
    {
        using var channel = new Channel(Grant.Take());
        KernelHandle table;
        IReadOnlyList<DeviceRecord> records;
        try
        {
            (table, records) = Discover(channel);
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException)
        {
            var error = exception is CapabilityException capability ? capability.Error : ServiceStatus.PROTOCOL_ERROR;
            Console.WriteLine($"{PREFIX}discovery failed: {exception.Message}");
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        using (table)
        {
            foreach (var record in records)
                Console.WriteLine(PREFIX + record.DiscoveryLine);
            var drivers = Activate(table, records);
            var disks = new List<(string Device, ChannelBlockStorage Disk)>();
            // The block probe goes to block drivers alone: a serial driver's channel (N4.3a) speaks the serial protocol,
            // and a probe there would take the channel the terminal service needs.
            foreach (var driver in drivers.Where(driver =>
                         driver.Status == 0 && driver.Service is not null && driver.Record.Class == DriverTable.BLOCK_STORAGE))
            {
                var disk = StorageProbe.RunAsync(driver).GetAwaiter().GetResult();
                if (disk is not null)
                    disks.Add((driver.Record.Id, disk));
            }
            // The network adapters' probe (W1.1): an ARP exchange with the board's gateway through the packet channel, which
            // then goes to the network service (W1.2a).
            var adapters = new List<(string Device, Channel Packets)>();
            foreach (var driver in drivers.Where(driver =>
                         driver.Status == 0 && driver.Service is not null && driver.Record.Class == DriverTable.NETWORK_QUEUE))
                adapters.Add((driver.Record.Id, NetworkProbe.Run(driver)));
            var names = StartNamespace(disks);
            var network = StartNetwork(adapters);
            // The firmware's framebuffer (C2.1): the test pattern through DEVICE_MEMORY's windows, before the display service
            // takes the framebuffer and draws the terminal's screen over it (C2.2).
            DisplayProbe.Run(table, records);
            var display = StartDisplay(table, records, drivers);
            var terminal = StartTerminal(drivers, display);
            Console.WriteLine($"{PREFIX}device manager on {RuntimeInformation.ProcessArchitecture}: {records.Count} devices discovered, " +
                $"{drivers.Count(driver => driver.Status == 0)} drivers ready, DMA isolation: none (no IOMMU), ready");
            // The endpoints for every process the root task starts: the namespace's, then the terminal's (N4.3).
            var offered = new List<KernelHandle>();
            uint capabilities = 0;
            if (names?.Clients is { } clients)
            {
                offered.Add(clients);
                capabilities |= ServiceStatus.NAMESPACE;
            }
            if (terminal?.Clients is { } terminalClients)
            {
                offered.Add(terminalClients);
                capabilities |= ServiceStatus.TERMINAL;
            }
            channel.Send(ServiceStatus.Encode(0, capabilities), [.. offered]);
            var status = Serve(channel, names, network, terminal, display, drivers);
            foreach (var (_, disk) in disks)
                disk.DisposeAsync().AsTask().GetAwaiter().GetResult();
            terminal?.Dispose();
            display?.Dispose();
            network?.Dispose();
            names?.Dispose();
            foreach (var driver in drivers)
                driver.Dispose();
            return status;
        }
    }

    // The root task's start, with the device table, and the records of the table's descriptors; the table's handle stays
    // the device manager's for its acquisitions.
    private static (KernelHandle Table, IReadOnlyList<DeviceRecord> Records) Discover(Channel channel)
    {
        var start = channel.Receive();
        if (!ServiceProtocol.IsStart(start.Bytes) || start.Handles.Count != 1)
        {
            start.Dispose();
            throw new FormatException($"The root task's first message has {start.Bytes.Length} bytes and {start.Handles.Count} handles.");
        }
        var table = start.Handles[0];
        try
        {
            return (table, [.. DeviceTable.Read(table).Select(PciTranslator.Translate)]);
        }
        catch
        {
            table.Dispose();
            throw;
        }
    }

    // The driver of every device the driver table matches, each started and answered in turn (D1.2); a device without
    // a driver stays in the table.
    private static List<DriverActivation> Activate(KernelHandle table, IReadOnlyList<DeviceRecord> records)
    {
        var drivers = new List<DriverActivation>();
        foreach (var record in records)
        {
            var path = DriverTable.Find(record);
            if (path is null)
                continue;
            try
            {
                var driver = DriverActivation.Start(table, record, path);
                drivers.Add(driver);
                Console.WriteLine(driver.Status == 0
                    ? $"{PREFIX}driver {path} of {record.Id}: ready"
                    : $"{PREFIX}driver {path} of {record.Id}: not ready, errno {driver.Status}");
            }
            catch (Exception exception) when (exception is CapabilityException or FileNotFoundException)
            {
                Console.WriteLine($"{PREFIX}driver {path} of {record.Id} could not start: {exception.Message}");
            }
        }
        return drivers;
    }

    // The namespace service with the disks (D5.1); null when it could not start.
    private static NamespaceActivation? StartNamespace(IReadOnlyList<(string Device, ChannelBlockStorage Disk)> disks)
    {
        try
        {
            var names = NamespaceActivation.Start(disks);
            Console.WriteLine(names.Status == 0
                ? $"{PREFIX}namespace service {NamespaceActivation.PATH} with {disks.Count} disks: ready"
                : $"{PREFIX}namespace service {NamespaceActivation.PATH}: not ready, errno {names.Status}");
            return names;
        }
        catch (Exception exception) when (exception is CapabilityException or FileNotFoundException)
        {
            Console.WriteLine($"{PREFIX}namespace service {NamespaceActivation.PATH} could not start: {exception.Message}");
            return null;
        }
    }

    // The network service with the adapters' packet channels (W1.2a), probed once it is ready; null without an adapter or
    // when it could not start.
    private static NetworkActivation? StartNetwork(IReadOnlyList<(string Device, Channel Packets)> adapters)
    {
        if (adapters.Count == 0)
            return null;
        try
        {
            var network = NetworkActivation.Start(adapters);
            Console.WriteLine(network.Status == 0
                ? $"{PREFIX}network service {NetworkActivation.PATH} with {adapters.Count} adapter{(adapters.Count == 1 ? "" : "s")}: ready"
                : $"{PREFIX}network service {NetworkActivation.PATH}: not ready, errno {network.Status}");
            if (network.Clients is { } clients)
            {
                DnsProbe.Run(clients);
                TcpProbe.Run(clients); // W1.2b
            }
            return network;
        }
        catch (Exception exception) when (exception is CapabilityException or FileNotFoundException)
        {
            Console.WriteLine($"{PREFIX}network service {NetworkActivation.PATH} could not start: {exception.Message}");
            return null;
        }
    }

    // The display service with the firmware's framebuffer and the keyboard driver's input channel (C2.2); null without a
    // framebuffer, without a keyboard or when it could not start. A console needs input: a board with a framebuffer and
    // no keyboard (q35's VGA card in the scenarios that boot without a display) keeps its terminal on the serial console
    // and its framebuffer for a later display service, and a keyboard without a framebuffer stays with its driver, which
    // logs its keys.
    private static DisplayActivation? StartDisplay(KernelHandle table, IReadOnlyList<DeviceRecord> records, IReadOnlyList<DriverActivation> drivers)
    {
        var framebuffer = records.FirstOrDefault(record => record.Class == FirmwareTranslator.FRAME_BUFFER);
        if (framebuffer is null)
            return null;
        var keyboard = drivers.FirstOrDefault(driver => driver.Status == 0 && driver.Service is not null &&
            driver.Record.Class == DriverTable.INPUT_DEVICE);
        if (keyboard is null)
        {
            Console.WriteLine($"{PREFIX}display service {DisplayActivation.PATH} not started on {framebuffer.Id}: no keyboard, the terminal stays on the serial console");
            return null;
        }
        try
        {
            var display = DisplayActivation.Start(table, framebuffer, keyboard.TakeService());
            Console.WriteLine(display.Status == 0
                ? $"{PREFIX}display service {DisplayActivation.PATH} on {framebuffer.Id} with the keyboard {keyboard.Record.Id}: ready"
                : $"{PREFIX}display service {DisplayActivation.PATH}: not ready, errno {display.Status}");
            return display;
        }
        catch (Exception exception) when (exception is CapabilityException or FileNotFoundException)
        {
            Console.WriteLine($"{PREFIX}display service {DisplayActivation.PATH} could not start: {exception.Message}");
            return null;
        }
    }

    // The terminal service with the consoles' serial channels (N4.3a, C2.2) — the display's console as the main one when
    // the display service is ready, the virtio console as the mirror or, without a display, the main one — probed once
    // it is ready; null without a console or when it could not start.
    private static TerminalActivation? StartTerminal(IReadOnlyList<DriverActivation> drivers, DisplayActivation? display)
    {
        var console = drivers.FirstOrDefault(driver => driver.Status == 0 && driver.Service is not null &&
            driver.Record.Class == DriverTable.SERIAL_CONSOLE);
        var consoles = new List<KernelHandle>();
        var names = new List<string>();
        if (display is { Status: 0, Console: not null })
        {
            consoles.Add(display.TakeConsole());
            names.Add(display.Record.Id);
        }
        if (console?.Service is not null)
        {
            consoles.Add(console.TakeService());
            names.Add(console.Record.Id);
        }
        if (consoles.Count == 0)
            return null;
        try
        {
            var terminal = TerminalActivation.Start(consoles);
            Console.WriteLine(terminal.Status == 0
                ? $"{PREFIX}terminal service {TerminalActivation.PATH} on {names[0]}{(names.Count > 1 ? $", mirrored on {names[1]}" : "")}: ready"
                : $"{PREFIX}terminal service {TerminalActivation.PATH}: not ready, errno {terminal.Status}");
            if (terminal.Clients is { } clients)
                TerminalProbe.Run(clients);
            return terminal;
        }
        catch (Exception exception) when (exception is CapabilityException or FileNotFoundException)
        {
            Console.WriteLine($"{PREFIX}terminal service {TerminalActivation.PATH} could not start: {exception.Message}");
            return null;
        }
    }

    // The channel until the root task asks the device manager to stop (D6.1) or ends: a stop stops the terminal service
    // first, which holds the consoles' channels, then the display service, which holds the keyboard driver's, then the
    // network service, which holds the adapters' (W1.2a), then the namespace service, since its volumes lie on the
    // drivers' disks, then each driver, and the answer is the first error of theirs.
    private static int Serve(Channel channel, NamespaceActivation? names, NetworkActivation? network, TerminalActivation? terminal,
        DisplayActivation? display, IReadOnlyList<DriverActivation> drivers)
    {
        for (; ; )
        {
            ChannelMessage message;
            try
            {
                message = channel.Receive();
            }
            catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
            {
                return 0;
            }
            using (message)
            {
                if (!ServiceStop.TryDecode(message.Bytes, out var deadline))
                {
                    Console.WriteLine($"{PREFIX}a request of {message.Bytes.Length} bytes, which the device manager does not serve yet");
                    continue;
                }
                var error = 0;
                if (terminal is { Status: 0 })
                    error = Report($"terminal service {TerminalActivation.PATH}", ServiceStop.Request(terminal.Channel, terminal.Process, deadline));
                if (display is { Status: 0 })
                {
                    var stopped = Report($"display service {DisplayActivation.PATH}", ServiceStop.Request(display.Channel, display.Process, deadline));
                    error = error != 0 ? error : stopped;
                }
                if (network is { Status: 0 })
                {
                    var stopped = Report($"network service {NetworkActivation.PATH}", ServiceStop.Request(network.Channel, network.Process, deadline));
                    error = error != 0 ? error : stopped;
                }
                if (names is { Status: 0 })
                {
                    var stopped = Report($"namespace service {NamespaceActivation.PATH}", ServiceStop.Request(names.Channel, names.Process, deadline));
                    error = error != 0 ? error : stopped;
                }
                foreach (var driver in drivers.Where(driver => driver.Status == 0))
                {
                    var stopped = Report($"driver {driver.Path} of {driver.Record.Id}", ServiceStop.Request(driver.Channel, driver.Process, deadline));
                    error = error != 0 ? error : stopped;
                }
                Console.WriteLine($"{PREFIX}device manager: {(error == 0 ? "stopped" : $"stopped, errno {error}")}");
                channel.Send(ServiceStatus.Encode(error));
                return error == 0 ? 0 : 1;
            }
        }
    }

    // A stopped service's line; its answer.
    private static int Report(string service, int status)
    {
        Console.WriteLine(status == 0 ? $"{PREFIX}{service}: stopped" : $"{PREFIX}{service}: did not stop, errno {status}");
        return status;
    }

    #endregion
}
