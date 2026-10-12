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
/// serves its channel until the root task ends.
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
            foreach (var driver in drivers.Where(driver => driver.Status == 0 && driver.Service is not null))
            {
                var disk = StorageProbe.RunAsync(driver).GetAwaiter().GetResult();
                if (disk is not null)
                    disks.Add((driver.Record.Id, disk));
            }
            var names = StartNamespace(disks);
            Console.WriteLine($"{PREFIX}device manager on {RuntimeInformation.ProcessArchitecture}: {records.Count} devices discovered, " +
                $"{drivers.Count(driver => driver.Status == 0)} drivers ready, DMA isolation: none (no IOMMU), ready");
            if (names?.Clients is { } clients)
                channel.Send(ServiceStatus.Encode(0), clients); // the namespace for every process the root task starts
            else
                channel.Send(ServiceStatus.Encode(0));
            var status = Serve(channel);
            foreach (var (_, disk) in disks)
                disk.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

    // The channel until the root task's end closes it.
    private static int Serve(Channel channel)
    {
        for (; ; )
        {
            try
            {
                using var message = channel.Receive();
                Console.WriteLine($"{PREFIX}a request of {message.Bytes.Length} bytes, which the device manager does not serve yet");
            }
            catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
            {
                return 0;
            }
        }
    }

    #endregion
}
