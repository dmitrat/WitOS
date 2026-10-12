using System;
using System.Collections.Generic;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Namespace.Server;
using OutWit.OS.Namespace.Volumes;
using OutWit.OS.Namespaces;
using OutWit.OS.Resources;
using OutWit.OS.Storage;

namespace OutWit.OS.Namespace;

/// <summary>
/// The namespace service (plan step D5.1, storage document section 21, RFC 0008 sections 12-17): the device manager
/// starts it with the grant of a channel to itself and moves each disk's block channel to it. It mounts the volumes the
/// disks hold by its policy, answers ready with the clients' endpoint, which the process manager gives every process
/// it starts, and serves each client that connects on a thread of its own.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[NAMESPACE] ";

    #endregion

    #region Functions

    private static int Main()
    {
        using var channel = new Channel(Grant.Take());
        var mounts = new MountTable();
        var kept = new List<IAsyncDisposable>();
        try
        {
            for (; ; )
            {
                var message = channel.Receive(); // its one handle becomes the disk's: the message is not disposed with it
                var (flags, device) = NamespaceMessages.DecodeDisk(message.Bytes);
                if ((flags & NamespaceMessages.DISK_END) != 0 || message.Handles.Count != 1)
                {
                    message.Dispose();
                    if ((flags & NamespaceMessages.DISK_END) != 0)
                        break;
                    throw new FormatException($"The disk {device} came with {message.Handles.Count} handles.");
                }
                var disk = ChannelBlockStorage.Attach(new Channel(message.Handles[0]));
                kept.Add(disk);
                kept.AddRange(VolumeMounter.MountAsync(mounts, disk, device).GetAwaiter().GetResult());
            }
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException or IOException)
        {
            var error = exception is CapabilityException capability ? capability.Error
                : exception is FormatException ? ServiceStatus.PROTOCOL_ERROR : 5;
            Console.WriteLine($"{PREFIX}mounting failed: {exception.Message}");
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        var (clients, theirs) = Channel.Create();
        using (theirs)
        {
            using var offered = theirs.Duplicate(Rights.Send | Rights.Duplicate | Rights.Transfer);
            Console.WriteLine($"{PREFIX}namespace service: {mounts.Mounts.Count} volumes mounted, serving their files");
            channel.Send(ServiceStatus.Encode(0), offered);
        }
        try
        {
            for (; ; )
                NamespaceConnection.Accept(clients.Receive(), mounts);
        }
        catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
        {
            return 0;
        }
    }

    #endregion
}
