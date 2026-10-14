using System;
using System.Runtime.InteropServices;
using OutWit.OS.Communication;
using OutWit.OS.Resources;
using OutWit.OS.Terminal.Server;
using OutWit.OS.Terminals;

namespace OutWit.OS.Terminal;

/// <summary>
/// The terminal service (plan step N4.3, RFC 0020 §6 and §8): /system/Terminal, which the device manager starts with
/// the console driver's serial channel and which owns the line discipline. It answers ready with the clients'
/// endpoint, which the process manager gives every process it starts afterwards, serves each client that connects on
/// a thread of its own until the device manager asks it to stop, and then ends the sessions and the link.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[TERMINAL] ";

    #endregion

    #region Functions

    private static int Main()
    {
        using var channel = new Channel(Grant.Take());
        var hub = new TerminalHub();
        ConsoleLink link;
        try
        {
            var device = channel.Receive(); // its one handle becomes the link's: the message is not disposed with it
            if (TerminalMessages.DecodeRequest(device.Bytes).Operation != TerminalMessages.DEVICE || device.Handles.Count != 1)
            {
                device.Dispose();
                throw new FormatException($"The device manager's message carries {device.Handles.Count} handles.");
            }
            link = ConsoleLink.Open(device.Handles[0], hub.Input);
            hub.Attach(link);
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException or InvalidOperationException)
        {
            var error = exception is CapabilityException capability ? capability.Error : ServiceStatus.PROTOCOL_ERROR;
            Console.WriteLine($"{PREFIX}the console could not be opened: {exception.Message}");
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        var (clients, theirs) = Channel.Create();
        using (theirs)
        {
            using var offered = theirs.Duplicate(Rights.Send | Rights.Duplicate | Rights.Transfer);
            Console.WriteLine($"{PREFIX}terminal service on {RuntimeInformation.ProcessArchitecture}: {hub.Columns} by {hub.Rows}, " +
                "canonical with echo, serving the terminal protocol");
            channel.Send(ServiceStatus.Encode(0), offered);
        }
        try
        {
            for (; ; )
            {
                if (Event.Wait(Event.INFINITE, clients.Endpoint, channel.Endpoint) == 0)
                {
                    TerminalConnection.Accept(clients.Receive(), hub);
                    continue;
                }
                using var message = channel.Receive();
                if (ServiceStop.TryDecode(message.Bytes, out _))
                {
                    channel.Send(ServiceStatus.Encode(Stop(hub, link)));
                    return 0;
                }
                Console.WriteLine($"{PREFIX}a message of {message.Bytes.Length} bytes from the device manager, which the service does not serve");
            }
        }
        catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
        {
            Stop(hub, link);
            return 0;
        }
    }

    // The stop (D6.1): no read waits any more, every session answers ESHUTDOWN, the link closes.
    private static int Stop(TerminalHub hub, ConsoleLink link)
    {
        var sessions = hub.Stop();
        foreach (var session in sessions)
            session.Stop();
        link.Dispose();
        Console.WriteLine($"{PREFIX}terminal service: {sessions.Length} sessions stopped, the console released");
        return 0;
    }

    #endregion
}
