using System;
using System.Linq;
using System.Text;
using OutWit.OS.Networking;

namespace OutWit.OS.Tests.Networking;

/// <summary>
/// IPv6 in the stack (plan step W4.3): stateless autoconfiguration through the simulated router (duplicate address
/// detection, the router solicitation, the advertised prefix, router and DNS servers), a duplicate address left
/// unassigned, UDP between two stacks over link-local addresses with neighbour discovery, ICMPv6's echo and port
/// unreachable, the loopback ::1, and dual-stack sockets beside IPV6_V6ONLY and AF_INET ones with Linux's binding rules.
/// </summary>
[TestFixture]
public sealed class Ipv6StackTests
{
    #region Constants

    private const ulong MAC_A = 0x5634_1200_5452; // 52:54:00:12:34:56
    private const ulong MAC_B = 0x5734_1200_5452; // 52:54:00:12:34:57
    private static readonly Ipv6Address LINK_LOCAL_A = Ipv6Address.Parse("fe80::5054:ff:fe12:3456");
    private static readonly Ipv4Address A4 = new(10, 0, 0, 1);
    private static readonly Ipv4Address MASK = new(255, 255, 255, 0);

    #endregion

    #region Tests

    [Test]
    public void AutoconfigurationThroughTheRouterTest()
    {
        var network = new SimulatedNetwork { Ipv6Router = true };
        var stack = network.AddStack(MAC_A);
        var changes = 0;
        stack.ConfigurationChanged += () => changes++;
        stack.StartIpv6();
        network.Pump();
        var solicitation = network.Captured.Single();
        Assert.That(EthernetHeader.TryRead(solicitation, out var ethernet), Is.True);
        Assert.That((ethernet.Type, ethernet.Destination), Is.EqualTo((EthernetHeader.IPV6, Ipv6Address.SolicitedNode(LINK_LOCAL_A).MulticastMac)),
            "duplicate address detection asks the solicited-node group at its multicast hardware address");
        Assert.That(Ipv6Header.TryRead(solicitation.AsSpan(EthernetHeader.SIZE), out var ip), Is.True);
        Assert.That((ip.Source.IsAny, ip.HopLimit, ip.NextHeader), Is.EqualTo((true, (byte)255, Ipv6Header.ICMPV6)), "from the unspecified address");
        Assert.That(stack.Ipv6LinkLocal.IsAny, Is.True, "tentative for a second");
        Assert.That(stack.NextDeadline, Is.EqualTo(1000));
        network.Wait(1000);
        Assert.That(stack.Ipv6LinkLocal, Is.EqualTo(LINK_LOCAL_A), "nobody claimed it");
        Assert.That(network.RouterAdvertisements, Is.EqualTo(1), "the solicitation went once the address was assigned");
        Assert.That((stack.Ipv6Gateway, stack.Ipv6Prefix, stack.Ipv6PrefixLength), Is.EqualTo((SimulatedNetwork.ROUTER, SimulatedNetwork.PREFIX6, 64)));
        Assert.That(stack.Ipv6DnsServers, Is.EqualTo(new[] { SimulatedNetwork.DNS6 }));
        Assert.That(stack.Ipv6Global.IsAny, Is.True, "the global address is tentative for a second too");
        network.Wait(1000);
        Assert.That(stack.Ipv6Global, Is.EqualTo(Ipv6Address.Parse("fec0::5054:ff:fe12:3456")), "the prefix and the interface identifier");
        Assert.That(changes, Is.EqualTo(3), "the link-local address, the advertisement, the global address");
        Assert.That(stack.Statistics.RouterAdvertisements, Is.EqualTo(1));
        network.Wait(10_000);
        Assert.That(network.RouterAdvertisements, Is.EqualTo(1), "no solicitation after an advertisement");
    }

    [Test]
    public void DuplicateAddressStaysUnassignedTest()
    {
        var network = new SimulatedNetwork();
        var a = network.AddStack(MAC_A);
        a.StartIpv6();
        network.Wait(1000);
        Assert.That(a.Ipv6LinkLocal, Is.EqualTo(LINK_LOCAL_A));
        var b = network.AddStack(MAC_A); // the same hardware address, so the same link-local address
        b.StartIpv6();
        network.Pump();
        Assert.That(a.Statistics.NeighbourAdvertisements, Is.EqualTo(1), "A defends its address to all nodes");
        Assert.That(b.Statistics.DuplicateAddresses, Is.EqualTo(1));
        network.Wait(1000);
        Assert.That(b.Ipv6LinkLocal.IsAny, Is.True, "never assigned");
        Assert.That(a.Ipv6LinkLocal, Is.EqualTo(LINK_LOCAL_A), "A keeps it");
        b.OpenUdp(out var socket, ipv6: true);
        Assert.That(b.SendTo(socket, "x"u8, LINK_LOCAL_A, 7), Is.EqualTo(NetworkErrors.NETWORK_UNREACHABLE), "no address to send from");
    }

    [Test]
    public void UdpOverLinkLocalAddressesTest()
    {
        var network = new SimulatedNetwork();
        var (a, b) = TwoStacks(network);
        Assert.That(b.OpenUdp(out var server, ipv6: true), Is.Zero);
        Assert.That(b.Bind(server, Ipv6Address.Any, 7), Is.Zero);
        Assert.That(a.OpenUdp(out var client, ipv6: true), Is.Zero);
        var before = network.Captured.Count;
        Assert.That(a.SendTo(client, "hello"u8, b.Ipv6LinkLocal, 7), Is.Zero);
        network.Pump();
        Assert.That(network.Captured.Skip(before).Count(IsIcmpv6), Is.EqualTo(2), "a neighbour solicitation and its advertisement");
        var buffer = new byte[64];
        Assert.That(b.ReceiveFrom(server, buffer, false, out var datagram), Is.Zero);
        Assert.That((Encoding.ASCII.GetString(datagram!.Payload), datagram.Source), Is.EqualTo(("hello", a.Ipv6LinkLocal)));
        Assert.That(datagram.SourcePort, Is.GreaterThanOrEqualTo(NetworkStack.EPHEMERAL_FIRST));
        // The reply needs no solicitation: B learned A's hardware address from the solicitation's option.
        var discovery = network.Captured.Count(IsIcmpv6);
        Assert.That(b.SendTo(server, "world"u8, datagram.Source, datagram.SourcePort), Is.Zero);
        network.Pump();
        Assert.That(network.Captured.Count(IsIcmpv6), Is.EqualTo(discovery));
        Assert.That(a.ReceiveFrom(client, buffer, false, out var back), Is.Zero);
        Assert.That(Encoding.ASCII.GetString(back!.Payload), Is.EqualTo("world"));
        // A port nobody bound: B's port unreachable reaches the connected socket.
        a.OpenUdp(out var lost, ipv6: true);
        Assert.That(a.Connect(lost, b.Ipv6LinkLocal, 4444), Is.Zero);
        Assert.That(a.SendTo(lost, "nobody"u8, Ipv6Address.Any, 0), Is.Zero);
        network.Pump();
        Assert.That(b.Statistics.PortsUnreachable, Is.EqualTo(1));
        Assert.That(a.Poll(lost) & NetworkStack.ERROR, Is.Not.Zero);
        Assert.That(a.SendTo(lost, "again"u8, Ipv6Address.Any, 0), Is.EqualTo(NetworkErrors.CONNECTION_REFUSED), "reported once");
        Assert.That(a.SendTo(lost, "again"u8, Ipv6Address.Any, 0), Is.Zero);
        network.Pump();
        // An echo request from B's address, written by hand: A answers it to B.
        var echo = new byte[8 + 4];
        echo[0] = Icmpv6.ECHO_REQUEST;
        echo[7] = 1;
        "ping"u8.CopyTo(echo.AsSpan(8));
        var checksum = InternetChecksum.Finish(InternetChecksum.Add(echo, InternetChecksum.PseudoHeader(b.Ipv6LinkLocal, a.Ipv6LinkLocal, Ipv6Header.ICMPV6, echo.Length)));
        echo[2] = (byte)(checksum >> 8);
        echo[3] = (byte)checksum;
        var packet = new byte[Ipv6Header.SIZE + echo.Length];
        Ipv6Header.Write(packet, b.Ipv6LinkLocal, a.Ipv6LinkLocal, Ipv6Header.ICMPV6, echo.Length);
        echo.CopyTo(packet, Ipv6Header.SIZE);
        var frame = new byte[EthernetHeader.SIZE + packet.Length];
        new EthernetHeader(new MacAddress(MAC_A), new MacAddress(MAC_B), EthernetHeader.IPV6).Write(frame);
        packet.CopyTo(frame, EthernetHeader.SIZE);
        before = network.Captured.Count;
        a.Receive(frame);
        network.Pump();
        var reply = network.Captured.Skip(before).Single();
        Assert.That(Ipv6Header.TryRead(reply.AsSpan(EthernetHeader.SIZE), out var ip) && ip.NextHeader == Ipv6Header.ICMPV6 && ip.Destination == b.Ipv6LinkLocal, Is.True);
        var message = reply.AsSpan(EthernetHeader.SIZE + Ipv6Header.SIZE, ip.PayloadLength);
        Assert.That(Icmpv6.TryRead(message, ip.Source, ip.Destination, out var type, out _), Is.True, "the reply sums");
        Assert.That((type, Encoding.ASCII.GetString(message[8..])), Is.EqualTo((Icmpv6.ECHO_REPLY, "ping")));
    }

    [Test]
    public void LoopbackAndDualStackSocketsTest()
    {
        var network = new SimulatedNetwork();
        var stack = network.AddStack(MAC_A);
        stack.Configure(A4, MASK, Ipv4Address.Any, []);
        // The loopback ::1 needs no host on the link.
        Assert.That(stack.OpenUdp(out var dual, ipv6: true), Is.Zero);
        Assert.That(stack.Bind(dual, Ipv6Address.Any, 5000), Is.Zero);
        Assert.That(stack.OpenUdp(out var inet), Is.Zero);
        Assert.That(stack.Bind(inet, Ipv4Address.Any, 5000), Is.EqualTo(NetworkErrors.ADDRESS_IN_USE), "a dual-stack socket at [::] holds the port's IPv4 side");
        Assert.That(stack.OpenUdp(out var only, ipv6: true), Is.Zero);
        Assert.That(stack.SetV6Only(only, true), Is.Zero);
        Assert.That(stack.Bind(only, Ipv6Address.Any, 5001), Is.Zero);
        Assert.That(stack.SetV6Only(only, false), Is.EqualTo(NetworkErrors.INVALID), "bound already, as Linux refuses it");
        Assert.That(stack.SetV6Only(inet, true), Is.EqualTo(NetworkErrors.NO_OPTION), "no IPv6 option of an AF_INET socket");
        Assert.That(stack.Bind(inet, Ipv4Address.Any, 5001), Is.Zero, "an IPV6_V6ONLY socket leaves the port's IPv4 side free");
        Assert.That(stack.OpenUdp(out var sender4), Is.Zero);
        Assert.That(stack.OpenUdp(out var sender6, ipv6: true), Is.Zero);
        Assert.That(stack.SendTo(sender4, "four"u8, Ipv4Address.Loopback, 5000), Is.Zero);
        Assert.That(stack.SendTo(sender6, "six"u8, Ipv6Address.Loopback, 5000), Is.Zero);
        Assert.That(stack.SendTo(sender6, "mapped"u8, Ipv6Address.Map(Ipv4Address.Loopback), 5001), Is.Zero, "a dual-stack sender reaches IPv4 through the mapped form");
        network.Pump();
        Assert.That(network.Captured, Is.Empty, "the loopback never reaches the link");
        var buffer = new byte[16];
        Assert.That(stack.ReceiveFrom(dual, buffer, false, out var first), Is.Zero);
        Assert.That((Encoding.ASCII.GetString(first!.Payload), first.Source), Is.EqualTo(("four", Ipv6Address.Map(Ipv4Address.Loopback))), "the IPv4 sender in its mapped form");
        Assert.That(stack.ReceiveFrom(dual, buffer, false, out var second), Is.Zero);
        Assert.That((Encoding.ASCII.GetString(second!.Payload), second.Source), Is.EqualTo(("six", Ipv6Address.Loopback)));
        Assert.That(stack.ReceiveFrom(only, buffer, false, out _), Is.EqualTo(NetworkErrors.AGAIN), "an IPV6_V6ONLY socket takes no IPv4");
        Assert.That(stack.ReceiveFrom(inet, buffer, false, out var third), Is.Zero);
        Assert.That((Encoding.ASCII.GetString(third!.Payload), third.Source), Is.EqualTo(("mapped", Ipv6Address.Map(Ipv4Address.Loopback))));
        Assert.That(stack.FindUdp(sender6)!.LocalAddress.IsAny, Is.True, "bound to [::] by its first send");
        Assert.That(stack.FindUdp(sender6)!.LocalPort, Is.GreaterThanOrEqualTo(NetworkStack.EPHEMERAL_FIRST));
        // What the families refuse.
        Assert.That(stack.SendTo(only, "x"u8, Ipv6Address.Map(Ipv4Address.Loopback), 5000), Is.EqualTo(NetworkErrors.NETWORK_UNREACHABLE), "an IPV6_V6ONLY socket reaches no IPv4 address");
        Assert.That(stack.Connect(inet, Ipv6Address.Loopback, 5000), Is.EqualTo(NetworkErrors.NETWORK_UNREACHABLE), "an AF_INET socket reaches no IPv6 address");
        Assert.That(stack.OpenUdp(out var elsewhere, ipv6: true), Is.Zero);
        Assert.That(stack.Bind(elsewhere, Ipv6Address.Parse("2001:db8::1"), 1), Is.EqualTo(NetworkErrors.ADDRESS_NOT_AVAILABLE));
        Assert.That(stack.OpenUdp(out var inet2), Is.Zero);
        Assert.That(stack.Bind(inet2, Ipv6Address.Loopback, 1), Is.EqualTo(NetworkErrors.ADDRESS_NOT_AVAILABLE), "an AF_INET socket binds no IPv6 address");
        Assert.That(stack.SendTo(sender6, "x"u8, Ipv6Address.Parse("fe80::1"), 7), Is.EqualTo(NetworkErrors.NETWORK_UNREACHABLE), "no link-local address before the host started");
        Assert.That(stack.OpenUdp(out var bound, ipv6: true), Is.Zero);
        Assert.That(stack.Bind(bound, Ipv6Address.Loopback, 5002), Is.Zero);
        Assert.That(stack.FindUdp(bound)!.LocalAddress, Is.EqualTo(Ipv6Address.Loopback));
        Assert.That(stack.SendTo(sender4, new byte[1500 - Ipv4Header.SIZE - UdpHeader.SIZE], Ipv4Address.Loopback, 5000), Is.Zero, "IPv4's largest datagram");
        Assert.That(stack.SendTo(sender6, new byte[1500 - Ipv4Header.SIZE - UdpHeader.SIZE], Ipv6Address.Loopback, 5000), Is.EqualTo(NetworkErrors.MESSAGE_SIZE),
            "IPv6's header takes twenty bytes more");
    }

    #endregion

    #region Functions

    // Two stacks with their link-local addresses assigned.
    private static (NetworkStack A, NetworkStack B) TwoStacks(SimulatedNetwork network)
    {
        var a = network.AddStack(MAC_A);
        var b = network.AddStack(MAC_B);
        a.StartIpv6();
        b.StartIpv6();
        network.Wait(1000);
        Assert.That((a.Ipv6LinkLocal, b.Ipv6LinkLocal), Is.EqualTo((LINK_LOCAL_A, Ipv6Address.Parse("fe80::5054:ff:fe12:3457"))));
        return (a, b);
    }

    private static bool IsIcmpv6(byte[] frame) => frame.Length > EthernetHeader.SIZE + Ipv6Header.SIZE && frame[12] == 0x86 && frame[13] == 0xDD &&
        frame[EthernetHeader.SIZE + 6] == Ipv6Header.ICMPV6;

    #endregion
}
