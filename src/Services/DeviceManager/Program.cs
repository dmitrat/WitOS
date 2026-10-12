using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using OutWit.OS.Communication;
using OutWit.OS.DeviceManager.Uhi;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The device manager (plan step D1.1, ADR 0025): the root task starts it before /bin/init and moves the device table
/// to it over the channel it granted it. It reads the table, translates every descriptor into a UHI device record and
/// prints its discovery line (Implementation Strategy §44), answers that it is ready, and serves its channel until the
/// root task ends. Drivers come with D1.2.
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
        IReadOnlyList<DeviceRecord> records;
        try
        {
            records = Discover(channel);
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException)
        {
            var error = exception is CapabilityException capability ? capability.Error : ServiceProtocol.PROTOCOL_ERROR;
            Console.WriteLine($"{PREFIX}discovery failed: {exception.Message}");
            channel.Send(ServiceProtocol.Status(error));
            return 1;
        }
        foreach (var record in records)
            Console.WriteLine(PREFIX + record.DiscoveryLine);
        Console.WriteLine($"{PREFIX}device manager on {RuntimeInformation.ProcessArchitecture}: {records.Count} devices discovered, " +
            "DMA isolation: none (no IOMMU), ready");
        channel.Send(ServiceProtocol.Status(0));
        return Serve(channel);
    }

    // The root task's start, with the device table, and the records of the table's descriptors; the table's handle
    // closes with the message, until D1.2 keeps it for its acquisitions.
    private static IReadOnlyList<DeviceRecord> Discover(Channel channel)
    {
        using var start = channel.Receive();
        if (!ServiceProtocol.IsStart(start.Bytes) || start.Handles.Count != 1)
            throw new FormatException($"The root task's first message has {start.Bytes.Length} bytes and {start.Handles.Count} handles.");
        return [.. DeviceTable.Read(start.Handles[0]).Select(PciTranslator.Translate)];
    }

    // The channel until the root task's end closes it.
    private static int Serve(Channel channel)
    {
        for (; ; )
        {
            try
            {
                using var message = channel.Receive();
                Console.WriteLine($"{PREFIX}a request of {message.Bytes.Length} bytes, which D1.1 does not serve");
            }
            catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
            {
                return 0;
            }
        }
    }

    #endregion
}
