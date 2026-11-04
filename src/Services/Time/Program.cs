using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using OutWit.OS.Communication;
using OutWit.OS.Networking;
using OutWit.OS.Resources;

namespace OutWit.OS.Time;

/// <summary>
/// The time service (plan step H7.4, the author's decision of 2026-10-26: the saved time and SNTP): the root task starts
/// it after the device manager and moves the clock capability to it over the channel it granted it. On a board without a
/// real-time clock it sets UTC to the time it saved on /data, so that the system's dates go on from its last run rather
/// than from 1970; where the package names time servers it asks them over SNTP and sets UTC to their answer; then it
/// answers that it is ready, before /bin/init starts. It asks the servers again every hour and, on a board without a
/// real-time clock, saves the time then and when the root task stops it. An ordinary .NET program over the libc: /data
/// through System.IO and the servers through System.Net.Sockets.
/// </summary>
internal static class Program
{
    #region Constants

    private const string PREFIX = "[TIME] ";

    // WIT_TIME_VERSION, WIT_TIME_START and the size of a WitTimeMessage (witos/time.h).
    private const uint TIME_VERSION = 1;
    private const uint TIME_START = 1;
    private const int MESSAGE_SIZE = 16;

    private const int RESYNCHRONIZE_SECONDS = 3600;

    // An exchange with a server: as many requests, each waiting so long in the guest's time for an answer, as the device
    // manager's DNS probe makes (W1.2b), since an idle guest's time runs ahead of the host's under QEMU's instruction clock.
    private const int TRIES = 12;
    private const int TRY_MILLISECONDS = 5000;

    private const ulong NANOSECONDS = 1_000_000_000;
    private const int TIMED_OUT = 110; // ETIMEDOUT of a wait whose deadline passed

    #endregion

    #region Functions

    private static int Main()
    {
        using var channel = new Channel(Grant.Take());
        KernelHandle clock;
        try
        {
            clock = TakeClock(channel);
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException)
        {
            var error = exception is CapabilityException capability ? capability.Error : ServiceStatus.PROTOCOL_ERROR;
            Console.WriteLine($"{PREFIX}no clock capability: {exception.Message}");
            channel.Send(ServiceStatus.Encode(error));
            return 1;
        }
        using (clock)
        {
            // The board's own clock: UTC is there before this service sets anything, since the root task gave the
            // capability to it alone.
            var boardClock = Clock.TryUtc(out var boardTime);
            if (boardClock)
                Console.WriteLine($"{PREFIX}the board's real-time clock reads {SavedTime.Format(boardTime)}");
            else
                Restore(clock);
            var servers = ReadServers();
            Synchronize(clock, servers);
            if (!boardClock)
                Save();
            channel.Send(ServiceStatus.Encode(0));
            return Serve(channel, clock, servers, boardClock);
        }
    }

    // The root task's start, with the clock capability in its one handle slot.
    private static KernelHandle TakeClock(Channel channel)
    {
        var start = channel.Receive();
        if (start.Bytes.Length != MESSAGE_SIZE || BitConverter.ToUInt32(start.Bytes, 0) != TIME_VERSION ||
            BitConverter.ToUInt32(start.Bytes, 4) != MESSAGE_SIZE || BitConverter.ToUInt32(start.Bytes, 8) != TIME_START ||
            BitConverter.ToUInt32(start.Bytes, 12) != 0 || start.Handles.Count != 1)
        {
            start.Dispose();
            throw new FormatException($"The root task's first message has {start.Bytes.Length} bytes and {start.Handles.Count} handles.");
        }
        return start.Handles[0];
    }

    // The saved time on a board without a real-time clock: an old time, but one the system lived, where 1970 is none.
    private static void Restore(KernelHandle clock)
    {
        string text;
        try
        {
            text = File.ReadAllText(SavedTime.PATH);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"{PREFIX}no real-time clock and no saved time ({exception.GetType().Name} on {SavedTime.PATH}): UTC waits for a time server");
            return;
        }
        if (!SavedTime.TryParse(text, out var saved))
        {
            Console.WriteLine($"{PREFIX}no real-time clock, and {SavedTime.PATH} holds no time: UTC waits for a time server");
            return;
        }
        try
        {
            Clock.SetUtc(clock, saved);
            Console.WriteLine($"{PREFIX}no real-time clock: UTC set to {SavedTime.Format(saved)}, the time saved on {SavedTime.PATH}");
        }
        catch (CapabilityException exception)
        {
            Console.WriteLine($"{PREFIX}no real-time clock, and the saved time {SavedTime.Format(saved)} could not be set: {exception.Message}");
        }
    }

    private static IReadOnlyList<TimeServer> ReadServers()
    {
        try
        {
            return File.Exists(TimeServers.PATH) ? TimeServers.Parse(File.ReadAllLines(TimeServers.PATH)) : [];
        }
        catch (Exception exception) when (exception is IOException or FormatException)
        {
            Console.WriteLine($"{PREFIX}{TimeServers.PATH} is unreadable: {exception.Message}");
            return [];
        }
    }

    // The first server that answers sets UTC.
    private static void Synchronize(KernelHandle clock, IReadOnlyList<TimeServer> servers)
    {
        foreach (var server in servers)
        {
            if (TryAsk(clock, server))
                return;
        }
    }

    private static bool TryAsk(KernelHandle clock, TimeServer server)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(server.Host, out var literal) ? [literal] : Dns.GetHostAddresses(server.Host);
        }
        catch (SocketException exception)
        {
            Console.WriteLine($"{PREFIX}SNTP {server}: the name does not resolve ({exception.SocketErrorCode})");
            return false;
        }
        foreach (var address in addresses)
        {
            var endpoint = new IPEndPoint(address, server.Port);
            try
            {
                if (TryExchange(clock, server, endpoint))
                    return true;
            }
            catch (SocketException exception)
            {
                Console.WriteLine($"{PREFIX}SNTP {server} at {endpoint}: {exception.SocketErrorCode}");
            }
        }
        return false;
    }

    // Requests until one is answered: an answer to any of them counts, since an idle guest's time may run past a try
    // before the host's answer arrives, and the answer is judged from that request's moment.
    private static bool TryExchange(KernelHandle clock, TimeServer server, IPEndPoint endpoint)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = TRY_MILLISECONDS };
        socket.Connect(endpoint);
        var sent = new Dictionary<ulong, ulong>(); // each request's nonce and the monotonic moment it went
        var buffer = new byte[512];
        for (var attempt = 0; attempt < TRIES; attempt++)
        {
            var nonce = Nonce();
            sent[nonce] = Clock.Monotonic();
            socket.Send(Sntp.Request(nonce));
            while (true)
            {
                int received;
                try
                {
                    received = socket.Receive(buffer);
                }
                catch (SocketException exception) when (exception.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
                {
                    break; // the next request
                }
                var arrived = Clock.Monotonic();
                var origin = received >= Sntp.PACKET_BYTES ? BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(24)) : 0;
                if (!sent.TryGetValue(origin, out var departed))
                    continue; // nothing this exchange asked
                if (!Sntp.TryRead(buffer.AsSpan(0, received), origin, out var reply, out var refused))
                {
                    Console.WriteLine($"{PREFIX}SNTP {server} at {endpoint}: refused, {refused}");
                    return false;
                }
                var atArrival = Sntp.TimeAtArrival(reply, Nanoseconds(arrived - departed));
                var now = atArrival + Nanoseconds(Clock.Monotonic() - arrived);
                var had = Clock.TryUtc(out var before);
                Clock.SetUtc(clock, now);
                Console.WriteLine($"{PREFIX}SNTP {server} at {endpoint}, stratum {reply.Stratum}, after {attempt + 1} request(s): UTC set to " +
                    $"{SavedTime.Format(now)}{(had ? $", {Offset(now, before)} from the clock's time" : "")}");
                return true;
            }
        }
        Console.WriteLine($"{PREFIX}SNTP {server} at {endpoint}: no answer to {TRIES} requests");
        return false;
    }

    // Saves UTC on a board without a real-time clock; a board with none and no time yet has nothing to save.
    private static void Save()
    {
        if (!Clock.TryUtc(out var now))
            return;
        try
        {
            SavedTime.Save(SavedTime.PATH, now);
            Console.WriteLine($"{PREFIX}saved {SavedTime.Format(now)} on {SavedTime.PATH}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"{PREFIX}the time could not be saved on {SavedTime.PATH}: {exception.Message}");
        }
    }

    // The root task's channel until it asks the service to stop: every hour the servers again and, on a board without a
    // real-time clock, the time saved.
    private static int Serve(Channel channel, KernelHandle clock, IReadOnlyList<TimeServer> servers, bool boardClock)
    {
        var next = Clock.MonotonicAfter(RESYNCHRONIZE_SECONDS);
        for (; ; )
        {
            try
            {
                Event.Wait(next, channel.Endpoint);
            }
            catch (CapabilityException exception) when (exception.Error == TIMED_OUT)
            {
                Synchronize(clock, servers);
                if (!boardClock)
                    Save();
                next = Clock.MonotonicAfter(RESYNCHRONIZE_SECONDS);
                continue;
            }
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
                if (!ServiceStop.TryDecode(message.Bytes, out _))
                {
                    Console.WriteLine($"{PREFIX}a request of {message.Bytes.Length} bytes, which the time service does not serve");
                    continue;
                }
                if (!boardClock)
                    Save();
                Console.WriteLine($"{PREFIX}time service: stopped");
                channel.Send(ServiceStatus.Encode(0));
                return 0;
            }
        }
    }

    // A request's nonce from the kernel's random source through the libc's /dev/urandom: .NET's RandomNumberGenerator is
    // OpenSSL's on Unix, which the boot package of the services does not carry.
    private static ulong Nonce()
    {
        Span<byte> bytes = stackalloc byte[8];
        using (var random = File.OpenRead("/dev/urandom"))
            random.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes) | 1;
    }

    // Monotonic ticks as nanoseconds, whole seconds first so that no product overflows.
    private static ulong Nanoseconds(ulong ticks)
    {
        var frequency = Clock.Frequency();
        return ticks / frequency * NANOSECONDS + ticks % frequency * NANOSECONDS / frequency;
    }

    private static string Offset(ulong now, ulong before)
    {
        var seconds = (now >= before ? (double)(now - before) : -(double)(before - now)) / NANOSECONDS;
        return seconds.ToString("+0.000;-0.000", CultureInfo.InvariantCulture) + " s";
    }

    #endregion
}
