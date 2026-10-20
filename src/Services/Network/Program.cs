using System;
using OutWit.OS.Communication;
using OutWit.OS.Network.Server;
using OutWit.OS.Networking;
using OutWit.OS.Resources;

namespace OutWit.OS.Network;

/// <summary>
/// The network service (plan step W1.2a, W0's decisions 2 and 4, witos/socket.h): the device manager starts it with the
/// grant of a channel to itself and moves each adapter's packet channel to it. It runs the managed TCP/IP stack on the
/// first adapter, or on the loopback alone without one (W2.1a), waits for the DHCP client's lease a bounded time, answers
/// ready with the clients' endpoint and serves
/// each client that connects on a thread of its own until the device manager asks it to stop (D6.1): then it refuses
/// every new request, closes what the sessions opened, stops the stack and answers.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[NETWORK] ";

    /// <summary>
    /// How long the service waits for a lease before it answers ready without one; the DHCP client goes on.
    /// </summary>
    private const int LEASE_MILLISECONDS = 20_000;

    #endregion

    #region Functions

    private static int Main()
    {
        using var channel = new Channel(Grant.Take());
        StackHost? host = null;
        try
        {
            for (; ; )
            {
                var message = channel.Receive(); // its one handle becomes the adapter's: the message is not disposed with it
                var adapter = SocketMessages.DecodeAdapter(message.Bytes);
                if (adapter is not { } decoded || (decoded.Flags & SocketMessages.ADAPTERS_END) != 0 || message.Handles.Count != 1)
                {
                    message.Dispose();
                    if (adapter is { Flags: var flags } && (flags & SocketMessages.ADAPTERS_END) != 0)
                        break;
                    throw new FormatException($"An adapter's message of {message.Bytes.Length} bytes came with {message.Handles.Count} handles.");
                }
                var packets = new Channel(message.Handles[0]);
                if (host is not null)
                {
                    // One interface until a later step names how a second adapter is configured.
                    Console.WriteLine($"{PREFIX}{decoded.Device}: a second adapter, which the service leaves alone");
                    packets.Dispose();
                    continue;
                }
                var link = AdapterLink.Attach(decoded.Device, packets);
                Console.WriteLine($"{PREFIX}{decoded.Device}: address {link.Address}, MTU {link.Mtu}, link {(link.LinkUp ? "up" : "down")}");
                host = new StackHost(link);
            }
            host ??= new StackHost(null); // the loopback alone: processes have sockets on every board
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException)
        {
            var error = exception is CapabilityException capability ? capability.Error : ServiceStatus.PROTOCOL_ERROR;
            Console.WriteLine($"{PREFIX}the adapters could not be taken: {exception.Message}");
            host?.Dispose();
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        using (host)
        {
            host.Start();
            var leased = host.Adapter is not null && host.WaitForLease(LEASE_MILLISECONDS);
            var (clients, theirs) = Channel.Create();
            using (clients)
            {
                using (theirs)
                {
                    using var offered = theirs.Duplicate(Rights.Send | Rights.Duplicate | Rights.Transfer);
                    Console.WriteLine($"{PREFIX}network service on {host.Device}: " +
                        $"{(host.Adapter is null ? "no adapter" : leased ? "configured by DHCP" : $"no lease within {LEASE_MILLISECONDS / 1000} s, DHCP goes on")}, " +
                        "serving sockets");
                    channel.Send(ServiceStatus.Encode(0), offered);
                }
                return Serve(channel, clients, host);
            }
        }
    }

    // The clients' connections and the device manager's channel until it asks the service to stop or ends.
    private static int Serve(Channel channel, Channel clients, StackHost host)
    {
        try
        {
            for (; ; )
            {
                if (Event.Wait(Event.INFINITE, clients.Endpoint, channel.Endpoint) == 0)
                {
                    SocketConnection.Accept(clients.Receive(), host);
                    continue;
                }
                using var message = channel.Receive();
                if (ServiceStop.TryDecode(message.Bytes, out _))
                {
                    var sessions = SocketConnection.StopAll();
                    host.Stop();
                    var statistics = host.Statistics;
                    Console.WriteLine($"{PREFIX}network service: {sessions} sessions stopped; {statistics.FramesReceived} frames received, " +
                        $"{host.Adapter?.Sent ?? 0} sent, {statistics.PacketsReceived} packets received, {statistics.PacketsSent} sent, " +
                        $"{SocketConnection.UndeliveredEvents} readiness events undelivered");
                    channel.Send(ServiceStatus.Encode(0));
                    return 0;
                }
                Console.WriteLine($"{PREFIX}a message of {message.Bytes.Length} bytes from the device manager, which the service does not serve");
            }
        }
        catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
        {
            return 0;
        }
    }

    #endregion
}
