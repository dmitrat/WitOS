using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OutWit.OS.Communication;
using OutWit.OS.Resources;
using OutWit.OS.Terminal.Server;
using OutWit.OS.Terminals;

namespace OutWit.OS.Terminal;

/// <summary>
/// The terminal service (plan steps N4.3 and C2.2, RFC 0020 §6, §8 and §31): /system/Terminal, which the device manager
/// starts with the consoles' serial channels — the main console first, whose size the terminal takes, then a console that
/// mirrors it: the display's console and the virtio console — and which owns the line discipline. It answers ready with
/// the clients' endpoint, which the process manager gives every process it starts afterwards, serves each client that
/// connects on a thread of its own until the device manager asks it to stop, and then ends the sessions and the links.
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
        var links = new List<ConsoleLink>();
        try
        {
            var device = channel.Receive(); // its handles become the links': the message is not disposed with them
            if (TerminalMessages.DecodeRequest(device.Bytes).Operation != TerminalMessages.DEVICE || device.Handles.Count is not (1 or 2))
            {
                device.Dispose();
                throw new FormatException($"The device manager's message carries {device.Handles.Count} handles.");
            }
            foreach (var console in device.Handles)
            {
                var link = ConsoleLink.Open(console, hub.Input);
                links.Add(link);
                hub.Attach(link);
            }
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException or InvalidOperationException)
        {
            var error = exception is CapabilityException capability ? capability.Error : ServiceStatus.PROTOCOL_ERROR;
            Console.WriteLine($"{PREFIX}a console could not be opened: {exception.Message}");
            foreach (var link in links)
                link.Dispose();
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        var (clients, theirs) = Channel.Create();
        using (theirs)
        {
            using var offered = theirs.Duplicate(Rights.Send | Rights.Duplicate | Rights.Transfer);
            Console.WriteLine($"{PREFIX}terminal service on {RuntimeInformation.ProcessArchitecture}: {hub.Columns} by {hub.Rows}" +
                $"{(hub.Sized ? "" : " by default")}, {links.Count} console{(links.Count == 1 ? "" : "s")}, canonical with echo, serving the terminal protocol");
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
                    channel.Send(ServiceStatus.Encode(Stop(hub, links)));
                    return 0;
                }
                Console.WriteLine($"{PREFIX}a message of {message.Bytes.Length} bytes from the device manager, which the service does not serve");
            }
        }
        catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
        {
            Stop(hub, links);
            return 0;
        }
    }

    // The stop (D6.1): no read waits any more, every session answers ESHUTDOWN, the links close.
    private static int Stop(TerminalHub hub, List<ConsoleLink> links)
    {
        var sessions = hub.Stop();
        foreach (var session in sessions)
            session.Stop();
        foreach (var link in links)
            link.Dispose();
        Console.WriteLine($"{PREFIX}terminal service: {sessions.Length} sessions stopped, {links.Count} console{(links.Count == 1 ? "" : "s")} released");
        return 0;
    }

    #endregion
}
