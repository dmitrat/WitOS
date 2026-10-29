using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OutWit.OS.Communication;
using OutWit.OS.FileSystems.Memory;
using OutWit.OS.Namespace.Server;
using OutWit.OS.Namespace.Volumes;
using OutWit.OS.Namespaces;
using OutWit.OS.Networking;
using OutWit.OS.Resources;
using OutWit.OS.Storage;

namespace OutWit.OS.Namespace;

/// <summary>
/// The namespace service (plan step D5.1, storage document section 21, RFC 0008 sections 12-17): the device manager
/// starts it with the grant of a channel to itself and moves each disk's block channel to it. It mounts the volumes the
/// disks hold by its policy, answers ready with the clients' endpoint, which the process manager gives every process
/// it starts, and serves each client that connects on a thread of its own until the device manager asks it to stop
/// (D6.1): then it refuses every new request, closes what the sessions opened, unmounts every volume and closes the
/// disks (storage document section 98), and answers.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[NAMESPACE] ";

    /// <summary>
    /// What the files of /tmp may hold in all (N4.1).
    /// </summary>
    private const long TEMPORARY_BYTES = 64L * 1024 * 1024;

    /// <summary>
    /// How long a share's connect and each of its reads and writes may wait, in the guest's time (E2.2).
    /// </summary>
    private const int CONNECT_SECONDS = 60;

    #endregion

    #region Functions

    private static int Main()
    {
        using var channel = new Channel(Grant.Take());
        var mounts = new MountTable();
        var kept = new List<IAsyncDisposable>();
        // /tmp (N4.1): a volume in the service's memory, there whatever disks the machine has, with the modes POSIX
        // programs and .NET's named synchronization objects (/tmp/.dotnet) need; its files last the boot.
        var temporary = new MemoryFileSystem(TEMPORARY_BYTES, "tmp");
        mounts.Catalog.Attach(temporary, NamespaceMessages.ORIGIN_MEMORY, "Tmp", null, false);
        kept.Add(temporary);
        // /media (E1, ADR 0032): the directory every volume that is not the system's mounts below, there from the start.
        mounts.Add(MountTable.MEDIA, new MediaDirectory(mounts));
        Console.WriteLine($"{PREFIX}mounted /tmp: {temporary.Volume.Format} volume of {temporary.Volume.Length} bytes in memory");
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
            Console.WriteLine($"{PREFIX}namespace service: {mounts.Catalog.Volumes.Count} volumes mounted, serving their files");
            channel.Send(ServiceStatus.Encode(0), offered);
        }
        try
        {
            for (; ; )
            {
                if (Event.Wait(Event.INFINITE, clients.Endpoint, channel.Endpoint) == 0)
                {
                    NamespaceConnection.Accept(clients.Receive(), mounts);
                    continue;
                }
                var message = channel.Receive();
                if (ServiceStop.TryDecode(message.Bytes, out _))
                {
                    message.Dispose();
                    channel.Send(ServiceStatus.Encode(Stop(mounts, kept)));
                    return 0;
                }
                if (Network(message) is { } network)
                {
                    // E2.2: the network service's endpoint, which a mount of a share connects through; the service keeps it.
                    kept.Add(new HandleKeeper(network));
                    mounts.Connector = (address, port) => SocketStream.Connect(network, Peer(address, port), CONNECT_SECONDS);
                    Console.WriteLine($"{PREFIX}the network service's endpoint: shares mount below {MountTable.MEDIA}");
                    continue;
                }
                message.Dispose();
                Console.WriteLine($"{PREFIX}a message of {message.Bytes.Length} bytes from the device manager, which the service does not serve");
            }
        }
        catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
        {
            return 0;
        }
    }

    // The stop (D6.1): no request runs or starts any more, the sessions' files are closed, then every volume unmounts
    // and every resource closes, the last opened first: a volume before its partition, a partition before its disk.
    // Zero, or EIO when something could not finish, which the log names.
    private static int Stop(MountTable mounts, List<IAsyncDisposable> kept)
    {
        var sessions = NamespaceConnection.StopAll();
        var error = 0;
        // The shares mounted on request (E2.2) log off before the disks' volumes go.
        foreach (var volume in mounts.Catalog.Volumes.Where(volume => volume.Origin == NamespaceMessages.ORIGIN_NETWORK))
        {
            try
            {
                volume.Mount.Volume.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"{PREFIX}stopping {volume.Point}: {exception.GetType().Name}: {exception.Message}");
            }
        }
        for (var i = kept.Count - 1; i >= 0; i--)
        {
            try
            {
                kept[i].DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"{PREFIX}stopping: {exception.GetType().Name}: {exception.Message}");
                error = 5; // EIO
            }
        }
        Console.WriteLine($"{PREFIX}namespace service: {sessions} sessions stopped, {mounts.Catalog.Volumes.Count} volumes unmounted" +
            (error == 0 ? "" : ", with errors"));
        return error;
    }

    // A disk message with DISK_NETWORK and its one handle: the network service's clients' endpoint (E2.2).
    private static KernelHandle? Network(ChannelMessage message)
    {
        try
        {
            var (flags, _) = NamespaceMessages.DecodeDisk(message.Bytes);
            return (flags & NamespaceMessages.DISK_NETWORK) != 0 && message.Handles.Count == 1 ? message.Handles[0] : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // A literal address and a port as the network service names a peer.
    private static SocketAddress Peer(string address, ushort port) => address.Contains(':')
        ? SocketAddress.Inet6(Ipv6Address.Parse(address.Trim('[', ']')), port)
        : SocketAddress.Inet(Ipv4Address.Parse(address), port);

    #endregion

    #region Types

    // A handle the service keeps until it stops.
    private sealed class HandleKeeper(KernelHandle handle) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            handle.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    #endregion
}
