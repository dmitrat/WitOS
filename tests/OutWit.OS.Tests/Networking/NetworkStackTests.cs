using System;
using System.Linq;
using System.Text;
using OutWit.OS.Networking;

namespace OutWit.OS.Tests.Networking;

/// <summary>
/// The managed stack's base (plan step W1.2a): headers that write and read back, ARP between two stacks, UDP over the
/// segment and the loopback, ICMP's echo and port unreachable, the DHCP lease, and what the stack drops.
/// </summary>
[TestFixture]
public sealed class NetworkStackTests
{
    #region Constants

    private static readonly Ipv4Address A = new(10, 0, 0, 1);
    private static readonly Ipv4Address B = new(10, 0, 0, 2);
    private static readonly Ipv4Address MASK = new(255, 255, 255, 0);

    #endregion

    #region Tests

    [Test]
    public void ChecksumIsRfc1071sTest()
    {
        // RFC 1071 §3: the words 0001 f203 f4f5 f6f7 sum to ddf2, whose complement is 220d.
        Assert.That(InternetChecksum.Compute([0x00, 0x01, 0xf2, 0x03, 0xf4, 0xf5, 0xf6, 0xf7]), Is.EqualTo(0x220d));
        Assert.That(InternetChecksum.Compute([0x00, 0x01, 0xf2, 0x03, 0xf4, 0xf5, 0xf6, 0xf7, 0x22, 0x0d]), Is.Zero, "a sum with its checksum");
    }

    [Test]
    public void HeadersWriteAndReadBackTest()
    {
        var packet = new byte[Ipv4Header.SIZE + UdpHeader.SIZE + 3];
        "abc"u8.CopyTo(packet.AsSpan(Ipv4Header.SIZE + UdpHeader.SIZE));
        UdpHeader.Write(packet.AsSpan(Ipv4Header.SIZE), A, B, 1234, 53);
        Ipv4Header.Write(packet, A, B, Ipv4Header.UDP, 7, UdpHeader.SIZE + 3);
        Assert.That(Ipv4Header.TryRead(packet, out var ip), Is.True);
        Assert.That((ip.Source, ip.Destination, ip.Protocol, ip.Identification, ip.PayloadLength, ip.IsFragment),
            Is.EqualTo((A, B, Ipv4Header.UDP, (ushort)7, UdpHeader.SIZE + 3, false)));
        Assert.That(UdpHeader.TryRead(packet.AsSpan(Ipv4Header.SIZE), A, B, out var udp), Is.True);
        Assert.That((udp.SourcePort, udp.DestinationPort, udp.Length), Is.EqualTo(((ushort)1234, (ushort)53, UdpHeader.SIZE + 3)));
        packet[^1] ^= 1;
        Assert.That(UdpHeader.TryRead(packet.AsSpan(Ipv4Header.SIZE), A, B, out _), Is.False, "a payload that no longer sums");
        packet[10] ^= 1;
        Assert.That(Ipv4Header.TryRead(packet, out _), Is.False, "a header that no longer sums");
        var arp = new byte[ArpPacket.SIZE];
        var request = new ArpPacket(ArpPacket.REQUEST, new MacAddress(0x5634_1200_5452), A, MacAddress.None, B);
        request.Write(arp);
        Assert.That(ArpPacket.TryRead(arp, out var read) && read == request, Is.True);
        var discover = DhcpMessage.Discover(new MacAddress(0x5634_1200_5452), 0xCAFEF00D);
        Assert.That(DhcpMessage.TryRead(discover, out var message), Is.True);
        Assert.That((message!.MessageType, message.TransactionId, message.ClientMac),
            Is.EqualTo((DhcpMessage.DISCOVER, 0xCAFEF00DU, new MacAddress(0x5634_1200_5452))));
        Assert.That(Ipv4Address.Parse("10.0.2.15").ToString(), Is.EqualTo("10.0.2.15"));
        Assert.That(new MacAddress(0x5634_1200_5452).ToString(), Is.EqualTo("52:54:00:12:34:56"));
    }

    [Test]
    public void ArpResolvesAndUdpCrossesTheSegmentTest()
    {
        var network = new SimulatedNetwork();
        var (a, b) = TwoStacks(network);
        Assert.That(b.OpenUdp(out var server), Is.Zero);
        Assert.That(b.Bind(server, Ipv4Address.Any, 7), Is.Zero);
        Assert.That(a.OpenUdp(out var client), Is.Zero);
        Assert.That(a.SendTo(client, "hello"u8, B, 7), Is.Zero);
        network.Pump();
        Assert.That(network.Captured.Count(frame => IsArp(frame)), Is.EqualTo(2), "a request and its reply");
        Assert.That(b.Poll(server) & NetworkStack.READABLE, Is.Not.Zero);
        var buffer = new byte[64];
        Assert.That(b.ReceiveFrom(server, buffer, false, out var datagram), Is.Zero);
        Assert.That(Encoding.ASCII.GetString(datagram!.Payload), Is.EqualTo("hello"));
        Assert.That(datagram.Source, Is.EqualTo(A));
        Assert.That(datagram.SourcePort, Is.GreaterThanOrEqualTo(NetworkStack.EPHEMERAL_FIRST));
        // The reply needs no ARP: B learned A from its request.
        var arps = network.Captured.Count(frame => IsArp(frame));
        Assert.That(b.SendTo(server, "world"u8, datagram.Source, datagram.SourcePort), Is.Zero);
        network.Pump();
        Assert.That(network.Captured.Count(frame => IsArp(frame)), Is.EqualTo(arps));
        Assert.That(a.ReceiveFrom(client, buffer, false, out var back), Is.Zero);
        Assert.That(Encoding.ASCII.GetString(back!.Payload), Is.EqualTo("world"));
        Assert.That(a.ReceiveFrom(client, buffer, false, out _), Is.EqualTo(NetworkErrors.AGAIN));
    }

    [Test]
    public void ArpRetriesAndGivesUpTest()
    {
        var network = new SimulatedNetwork();
        var (a, _) = TwoStacks(network);
        a.OpenUdp(out var client);
        network.DropNext = 1; // the first request is lost
        Assert.That(a.SendTo(client, "x"u8, B, 9), Is.Zero);
        network.Pump();
        network.Wait(1000); // the retry, answered
        Assert.That(network.Captured.Count(frame => IsArp(frame)), Is.EqualTo(2), "the retry and its reply; the lost request never reached the segment");
        a.SendTo(client, "y"u8, new Ipv4Address(10, 0, 0, 99), 9);
        network.Pump();
        network.Wait(1000);
        network.Wait(1000);
        network.Wait(1000);
        Assert.That(a.Statistics.PacketsUnresolved, Is.EqualTo(1), "nobody answers for 10.0.0.99");
    }

    [Test]
    public void LoopbackAndBindingsTest()
    {
        var network = new SimulatedNetwork();
        var (a, _) = TwoStacks(network);
        a.OpenUdp(out var server);
        Assert.That(a.Bind(server, Ipv4Address.Loopback, 5353), Is.Zero);
        a.OpenUdp(out var other);
        Assert.That(a.Bind(other, Ipv4Address.Any, 5353), Is.EqualTo(NetworkErrors.ADDRESS_IN_USE));
        Assert.That(a.Bind(other, new Ipv4Address(192, 168, 1, 1), 1), Is.EqualTo(NetworkErrors.ADDRESS_NOT_AVAILABLE));
        a.OpenUdp(out var client);
        Assert.That(a.SendTo(client, "loop"u8, Ipv4Address.Loopback, 5353), Is.Zero);
        Assert.That(network.Captured, Is.Empty, "the loopback never reaches the link");
        network.Pump();
        Assert.That(a.ReceiveFrom(server, new byte[8], true, out var peeked), Is.Zero);
        Assert.That(a.ReceiveFrom(server, new byte[2], false, out var cut), Is.Zero);
        Assert.That((Encoding.ASCII.GetString(peeked!.Payload), cut!.Payload.Length), Is.EqualTo(("loop", 4)),
            "a peek leaves the datagram, a short buffer still takes it whole");
        Assert.That(a.ReceiveFrom(server, new byte[8], false, out _), Is.EqualTo(NetworkErrors.AGAIN));
        Assert.That(a.SendTo(client, new byte[1500], Ipv4Address.Loopback, 5353), Is.EqualTo(NetworkErrors.MESSAGE_SIZE));
        Assert.That(a.SendTo(client, "x"u8, Ipv4Address.Broadcast, 5353), Is.EqualTo(NetworkErrors.NOT_SUPPORTED), "SO_BROADCAST unset");
        a.OpenUdp(out var unbound);
        Assert.That(a.SendTo(unbound, "x"u8, Ipv4Address.Any, 0), Is.EqualTo(NetworkErrors.DESTINATION_REQUIRED));
        Assert.That(a.Close(unbound), Is.Zero);
        Assert.That(a.Close(unbound), Is.EqualTo(NetworkErrors.BAD_SOCKET));
    }

    // SO_REUSEPORT (W2.1b; .NET's ReuseAddress sets it beside SO_REUSEADDR): sockets that all set it share a port, a
    // socket without it is refused, and each sender's datagrams go to one member while the members share the senders.
    [Test]
    public void ReusePortSharesADatagramPortTest()
    {
        var network = new SimulatedNetwork();
        var (a, _) = TwoStacks(network);
        a.OpenUdp(out var first);
        a.OpenUdp(out var second);
        a.OpenUdp(out var plain);
        Assert.That(a.SetReusePort(first, true), Is.Zero);
        Assert.That(a.SetReusePort(second, true), Is.Zero);
        Assert.That(a.Bind(first, Ipv4Address.Loopback, 6000), Is.Zero);
        Assert.That(a.Bind(plain, Ipv4Address.Loopback, 6000), Is.EqualTo(NetworkErrors.ADDRESS_IN_USE), "a socket without SO_REUSEPORT");
        Assert.That(a.Bind(second, Ipv4Address.Loopback, 6000), Is.Zero);
        var members = new[] { first, second };
        var senders = new int[2];
        for (var i = 0; i < 16; i++)
        {
            a.OpenUdp(out var sender);
            Assert.That(a.SendTo(sender, "x"u8, Ipv4Address.Loopback, 6000), Is.Zero);
            Assert.That(a.SendTo(sender, "y"u8, Ipv4Address.Loopback, 6000), Is.Zero);
            network.Pump();
            var pending = members.Select(member => a.FindUdp(member)!.Pending).ToArray();
            Assert.That(pending, Is.EqualTo(new[] { 2, 0 }).Or.EqualTo(new[] { 0, 2 }), "both datagrams of a sender at one member");
            var taker = pending[0] == 2 ? 0 : 1;
            senders[taker]++;
            Assert.That(a.ReceiveFrom(members[taker], new byte[4], false, out _), Is.Zero);
            Assert.That(a.ReceiveFrom(members[taker], new byte[4], false, out _), Is.Zero);
        }
        Assert.That(senders, Has.All.GreaterThan(0), "the members share the senders");
    }

    [Test]
    public void IcmpEchoAndPortUnreachableTest()
    {
        var network = new SimulatedNetwork();
        var (a, b) = TwoStacks(network);
        a.OpenUdp(out var client);
        Assert.That(a.Connect(client, B, 4444), Is.Zero);
        Assert.That(a.SendTo(client, "nobody"u8, Ipv4Address.Any, 0), Is.Zero);
        network.Pump();
        Assert.That(b.Statistics.PortsUnreachable, Is.EqualTo(1));
        Assert.That(a.Poll(client) & NetworkStack.ERROR, Is.Not.Zero);
        Assert.That(a.SendTo(client, "again"u8, Ipv4Address.Any, 0), Is.EqualTo(NetworkErrors.CONNECTION_REFUSED), "reported once");
        Assert.That(a.SendTo(client, "again"u8, Ipv4Address.Any, 0), Is.Zero);
        network.Pump();
        // An echo request from B's address, written by hand: A answers it.
        var echo = new byte[8 + 4];
        echo[0] = 8;
        echo[7] = 1;
        "ping"u8.CopyTo(echo.AsSpan(8));
        var checksum = InternetChecksum.Compute(echo);
        echo[2] = (byte)(checksum >> 8);
        echo[3] = (byte)checksum;
        var packet = new byte[Ipv4Header.SIZE + echo.Length];
        Ipv4Header.Write(packet, B, A, Ipv4Header.ICMP, 1, echo.Length);
        echo.CopyTo(packet, Ipv4Header.SIZE);
        var frame = new byte[EthernetHeader.SIZE + packet.Length];
        new EthernetHeader(new MacAddress(0xA), new MacAddress(0xB), EthernetHeader.IPV4).Write(frame);
        packet.CopyTo(frame, EthernetHeader.SIZE);
        var before = network.Captured.Count;
        a.Receive(frame);
        network.Pump();
        var reply = network.Captured.Skip(before).Single();
        Assert.That(Ipv4Header.TryRead(reply.AsSpan(EthernetHeader.SIZE), out var ip) && ip.Protocol == Ipv4Header.ICMP && ip.Destination == B, Is.True);
        var icmp = reply.AsSpan(EthernetHeader.SIZE + ip.HeaderLength, ip.PayloadLength);
        Assert.That((icmp[0], InternetChecksum.Compute(icmp), Encoding.ASCII.GetString(icmp[8..])), Is.EqualTo(((byte)0, (ushort)0, "ping")));
    }

    [Test]
    public void DhcpLeasesTheAddressTest()
    {
        var network = new SimulatedNetwork { DhcpServer = true };
        var stack = network.AddStack(0x5634_1200_5452);
        Assert.That(stack.Address, Is.EqualTo(Ipv4Address.Any));
        stack.StartDhcp();
        network.Pump();
        Assert.That(network.Answers, Is.EqualTo(2), "an offer and an acknowledgment");
        Assert.That(stack.Dhcp.IsBound, Is.True);
        Assert.That((stack.Address, stack.Mask, stack.Gateway), Is.EqualTo((SimulatedNetwork.LEASED, new Ipv4Address(255, 255, 255, 0),
            SimulatedNetwork.SERVER)));
        Assert.That(stack.DnsServers, Is.EqualTo(new[] { new Ipv4Address(10, 0, 2, 3) }));
        Assert.That(stack.Dhcp.Lease!.LeaseSeconds, Is.EqualTo(3600));
        // Renewal at half the lease: a unicast request to the server, acknowledged.
        network.Wait(1_800_000);
        Assert.That(network.Answers, Is.EqualTo(3));
        Assert.That(stack.Address, Is.EqualTo(SimulatedNetwork.LEASED));
    }

    // W4.2: the stack announces its configuration's changes — the lease taken, and the lease lost once the server stops
    // answering and it expires — and not a renewal that changes nothing; the service tells every session of each.
    [Test]
    public void DhcpAnnouncesAChangedConfigurationTest()
    {
        var network = new SimulatedNetwork { DhcpServer = true };
        var stack = network.AddStack(0x5634_1200_5452);
        var announced = 0;
        stack.ConfigurationChanged += () => announced++;
        stack.StartDhcp();
        network.Pump();
        Assert.That((announced, stack.Address), Is.EqualTo((1, SimulatedNetwork.LEASED)), "the lease taken");
        network.Wait(1_800_000);
        Assert.That((announced, network.Answers), Is.EqualTo((1, 3)), "a renewal with the same values announces nothing");
        network.DhcpServer = false;
        network.Wait(3_600_000);
        Assert.That((announced, stack.Address), Is.EqualTo((2, Ipv4Address.Any)), "the lease lost once it expired unanswered");
    }

    [Test]
    public void DhcpRetransmitsWithoutAServerTest()
    {
        var network = new SimulatedNetwork();
        var stack = network.AddStack(0x5634_1200_5452);
        stack.StartDhcp();
        network.Pump();
        network.Wait(DhcpClient.RETRY_MILLISECONDS);
        Assert.That(network.Captured, Has.Count.EqualTo(2), "a discover and its retransmission");
        Assert.That(stack.Dhcp.IsBound, Is.False);
        a_StackWithoutAddressRefusesToSend(stack);
    }

    #endregion

    #region Functions

    private static void a_StackWithoutAddressRefusesToSend(NetworkStack stack)
    {
        stack.OpenUdp(out var socket);
        Assert.That(stack.SendTo(socket, "x"u8, new Ipv4Address(10, 0, 2, 2), 53), Is.EqualTo(NetworkErrors.NETWORK_UNREACHABLE));
    }

    private static (NetworkStack A, NetworkStack B) TwoStacks(SimulatedNetwork network)
    {
        var a = network.AddStack(0xA);
        var b = network.AddStack(0xB);
        a.Configure(A, MASK, Ipv4Address.Any, []);
        b.Configure(B, MASK, Ipv4Address.Any, []);
        return (a, b);
    }

    private static bool IsArp(byte[] frame) => EthernetHeader.TryRead(frame, out var header) && header.Type == EthernetHeader.ARP;

    #endregion
}
