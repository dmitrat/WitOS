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
/// starts the namespace service with the disks (D5.1), answers that it is ready with the namespace's endpoint, and
/// serves its channel until the root task asks it to stop (D6.1), when it stops the namespace service, which unmounts
/// its volumes, then the drivers, and answers.
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
            var names = StartNamespace(disks);
            var terminal = StartTerminal(drivers);
            // The firmware's framebuffer (C2.1): the test pattern, which the runtime-display scenario checks on the host.
            DisplayProbe.Run(table, records);
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
            var status = Serve(channel, names, terminal, drivers);
            foreach (var (_, disk) in disks)
                disk.DisposeAsync().AsTask().GetAwaiter().GetResult();
            terminal?.Dispose();
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

    // The terminal service with the console driver's serial channel (N4.3a), probed once it is ready; null without a
    // console or when it could not start.
    private static TerminalActivation? StartTerminal(IReadOnlyList<DriverActivation> drivers)
    {
        var console = drivers.FirstOrDefault(driver => driver.Status == 0 && driver.Service is not null &&
            driver.Record.Class == DriverTable.SERIAL_CONSOLE);
        if (console?.Service is not { } serial)
            return null;
        try
        {
            var terminal = TerminalActivation.Start(console.TakeService());
            Console.WriteLine(terminal.Status == 0
                ? $"{PREFIX}terminal service {TerminalActivation.PATH} on {console.Record.Id}: ready"
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
    // first, which holds the console driver's channel, then the namespace service, since its volumes lie on the drivers'
    // disks, then each driver, and the answer is the first error of theirs.
    private static int Serve(Channel channel, NamespaceActivation? names, TerminalActivation? terminal, IReadOnlyList<DriverActivation> drivers)
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
