using System;
using System.Buffers.Binary;
using System.Text;
using OutWit.OS.Networking;
using OutWit.OS.Resources;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The network service's probe (plan step W1.2a): the device manager connects to the service as a process will, reads
/// the interface's configuration, which the service's DHCP client took from QEMU's user networking, and asks the DNS
/// server of the lease, slirp's forwarder, for a name over a datagram socket of the service. Any answer to the query
/// proves the path — the socket, the stack's UDP, IPv4 and ARP, the driver and slirp — whatever the host's resolver makes
/// of the name; the runtime-devices scenario requires the configuration and the answer.
/// </summary>
internal static class DnsProbe
{
    #region Constants

    /// <summary>
    /// The name the probe asks for.
    /// </summary>
    public const string NAME = "example.com";

    private const string PREFIX = "[DEVICES] ";

    private const ushort DNS_PORT = 53;

    // slirp forwards a query to the host's resolver, whose answer comes in the host's time, while an idle guest's virtual
    // time runs ahead under -icount's sleep=off: many short attempts, a minute of the guest's time in all.
    private const int ATTEMPTS = 12;

    private const int WAIT_SECONDS = 5;

    private const int HEADER_BYTES = 12;

    private const ushort RESPONSE = 0x8000;

    private const ushort RECURSION_DESIRED = 0x0100;

    private const ushort TYPE_A = 1;

    private const ushort CLASS_INTERNET = 1;

    #endregion

    #region Functions

    /// <summary>
    /// Runs the probe.
    /// </summary>
    /// <param name="clients">The network service's clients' endpoint.</param>
    public static void Run(KernelHandle clients)
    {
        try
        {
            using var client = SocketClient.Connect(clients);
            var error = client.GetConfiguration(out var configuration);
            if (error != 0 || configuration is null)
            {
                Console.WriteLine($"{PREFIX}network service probe: CONFIGURATION failed, errno {error}");
                return;
            }
            Console.WriteLine($"{PREFIX}network service probe: {configuration.Address}/{System.Numerics.BitOperations.PopCount(configuration.Mask.Value)}, " +
                $"router {configuration.Gateway}, DNS {string.Join(", ", configuration.DnsServers)}, MTU {configuration.Mtu}, " +
                $"{(configuration.Leased ? "leased by DHCP" : "no lease")}");
            if (!configuration.Leased || configuration.DnsServers.Count == 0)
                return;
            var server = configuration.DnsServers[0];
            error = client.Open(SocketMessages.DATAGRAM, out var socket);
            if (error == 0)
                error = client.ConnectTo(socket, SocketAddress.Inet(server, DNS_PORT));
            if (error != 0)
            {
                Console.WriteLine($"{PREFIX}network service probe: a datagram socket to {server}:{DNS_PORT} failed, errno {error}");
                return;
            }
            var buffer = new byte[SocketClient.WINDOW_BYTES];
            for (var attempt = 1; attempt <= ATTEMPTS; attempt++)
            {
                var id = (ushort)(0x5700 + attempt);
                error = client.Send(socket, Query(id, NAME), SocketAddress.None, out _);
                if (error != 0)
                {
                    Console.WriteLine($"{PREFIX}network service probe: the query to {server} failed, errno {error}");
                    return;
                }
                var deadline = Clock.MonotonicAfter(WAIT_SECONDS);
                for (; ; )
                {
                    error = client.Receive(socket, buffer, out var received, out var source);
                    if (error == NetworkErrors.AGAIN)
                    {
                        if (client.WaitEvent(deadline) is null)
                            break;
                        continue;
                    }
                    if (error != 0)
                    {
                        Console.WriteLine($"{PREFIX}network service probe: the receive failed, errno {error}");
                        return;
                    }
                    if (TryReadAnswer(buffer.AsSpan(0, received), id, out var rcode, out var answers, out var address))
                    {
                        Console.WriteLine($"{PREFIX}network: DNS answer from {source.AddressText} for {NAME} through a socket of the network service: " +
                            $"rcode {rcode}, {answers} answers{(address is { } first ? $", first address {first}" : "")}, attempt {attempt}");
                        client.Close(socket);
                        return;
                    }
                }
            }
            Console.WriteLine($"{PREFIX}network service probe: no DNS answer from {server} for {NAME} after {ATTEMPTS} attempts of {WAIT_SECONDS} s");
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException)
        {
            Console.WriteLine($"{PREFIX}network service probe failed: {exception.Message}");
        }
    }

    // A recursive query for a name's IPv4 addresses (RFC 1035 section 4.1).
    private static byte[] Query(ushort id, string name)
    {
        var labels = name.Split('.');
        var length = HEADER_BYTES + 1 + 4;
        foreach (var label in labels)
            length += 1 + label.Length;
        var query = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(query, id);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(2), RECURSION_DESIRED);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4), 1); // one question
        var offset = HEADER_BYTES;
        foreach (var label in labels)
        {
            query[offset++] = (byte)label.Length;
            offset += Encoding.ASCII.GetBytes(label, query.AsSpan(offset));
        }
        query[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(offset), TYPE_A);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(offset + 2), CLASS_INTERNET);
        return query;
    }

    // Whether bytes are the response to the query: its code, its answers and the first IPv4 address among them.
    private static bool TryReadAnswer(ReadOnlySpan<byte> bytes, ushort id, out int rcode, out int answers, out Ipv4Address? address)
    {
        rcode = 0;
        answers = 0;
        address = null;
        if (bytes.Length < HEADER_BYTES || BinaryPrimitives.ReadUInt16BigEndian(bytes) != id)
            return false;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
        if ((flags & RESPONSE) == 0)
            return false;
        rcode = flags & 0xF;
        var questions = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        answers = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
        var offset = HEADER_BYTES;
        for (var i = 0; i < questions && offset >= 0; i++)
            offset = SkipName(bytes, offset) is var end and >= 0 && end + 4 <= bytes.Length ? end + 4 : -1;
        for (var i = 0; i < answers && offset >= 0; i++)
        {
            var end = SkipName(bytes, offset);
            if (end < 0 || end + 10 > bytes.Length)
                break;
            var type = BinaryPrimitives.ReadUInt16BigEndian(bytes[end..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(end + 8)..]);
            if (end + 10 + length > bytes.Length)
                break;
            if (type == TYPE_A && length == 4)
            {
                address = Ipv4Address.Read(bytes[(end + 10)..]);
                break;
            }
            offset = end + 10 + length;
        }
        return true;
    }

    // The offset after a name: labels to a zero byte, or a compression pointer; -1 when the bytes end first.
    private static int SkipName(ReadOnlySpan<byte> bytes, int offset)
    {
        while (offset < bytes.Length)
        {
            var length = bytes[offset];
            if (length == 0)
                return offset + 1;
            if ((length & 0xC0) == 0xC0)
                return offset + 2 <= bytes.Length ? offset + 2 : -1;
            offset += 1 + length;
        }
        return -1;
    }

    #endregion
}
