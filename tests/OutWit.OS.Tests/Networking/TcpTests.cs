using System;
using System.Linq;
using System.Text;
using OutWit.OS.Networking;

namespace OutWit.OS.Tests.Networking;

/// <summary>
/// TCP (plan step W1.2b) between two stacks of the simulated segment and within one: the handshake, data both ways and
/// the close with TIME-WAIT; a megabyte echoed whole over a segment that loses and swaps frames; a refusal, a peer that
/// never answers, a closed window and its probes, the backlog, the reset of a close with unread bytes, a simultaneous
/// close and the loopback; over IPv6 (W4.3): the loopback ::1, link-local addresses between two stacks, and a dual-stack
/// listener that takes connections of both families while an IPV6_V6ONLY one refuses IPv4.
/// </summary>
[TestFixture]
public sealed class TcpTests
{
    #region Constants

    private static readonly Ipv4Address A = new(10, 0, 0, 1);
    private static readonly Ipv4Address B = new(10, 0, 0, 2);
    private static readonly Ipv4Address ABSENT = new(10, 0, 0, 9);
    private static readonly Ipv4Address MASK = new(255, 255, 255, 0);

    private const ushort PORT = 7000;

    #endregion

    #region Tests

    [Test]
    public void HandshakeDataAndCloseTest()
    {
        var (network, a, b) = Segment();
        var (client, server, listener) = Connect(network, a, b);
        Assert.That(a.FindTcp(client)!.State, Is.EqualTo(TcpState.Established));
        Assert.That(b.FindTcp(server)!.State, Is.EqualTo(TcpState.Established));
        Assert.That(b.FindTcp(server)!.RemoteAddress, Is.EqualTo(Ipv6Address.Map(A)), "an IPv4 peer in the socket layer's mapped form (W4.3)");
        Assert.That(a.Poll(client) & NetworkStack.WRITABLE, Is.Not.Zero, "a connection with room is writable");
        Assert.That(b.Poll(server) & NetworkStack.READABLE, Is.Zero, "nothing to read yet");

        Assert.That(a.SendStream(client, "hello"u8, out var sent), Is.Zero);
        Assert.That(sent, Is.EqualTo(5));
        network.Pump();
        Assert.That(ReadAll(b, server), Is.EqualTo("hello"));
        Assert.That(b.SendStream(server, "world"u8, out _), Is.Zero);
        network.Wait(50); // the delayed acknowledgment
        Assert.That(ReadAll(a, client), Is.EqualTo("world"));
        var buffer = new byte[16];
        Assert.That(a.ReceiveStream(client, buffer, false, out _), Is.EqualTo(NetworkErrors.AGAIN), "nothing more to read");

        Assert.That(a.Close(client), Is.Zero);
        network.Pump();
        Assert.That(b.FindTcp(server)!.State, Is.EqualTo(TcpState.CloseWait));
        Assert.That(b.ReceiveStream(server, buffer, false, out var count), Is.Zero);
        Assert.That(count, Is.Zero, "the end of the stream");
        Assert.That(b.Poll(server) & NetworkStack.READABLE, Is.Not.Zero, "the end of the stream is readable");
        Assert.That(b.Close(server), Is.Zero);
        network.Pump();
        Assert.That(b.FindTcp(server), Is.Null, "the closing side's connection went with the last acknowledgment");
        Assert.That(a.FindTcp(client)!.State, Is.EqualTo(TcpState.TimeWait));
        network.Wait(60_000);
        Assert.That(a.FindTcp(client), Is.Null, "TIME-WAIT ended");
        Assert.That(b.Close(listener), Is.Zero);
    }

    [Test]
    public void MegabyteEchoesWholeTest()
    {
        var (network, a, b) = Segment();
        Echo(network, a, b, 1024 * 1024);
        Assert.That(a.Statistics.SegmentsRetransmitted + b.Statistics.SegmentsRetransmitted, Is.Zero, "a clean segment needs no retransmission");
    }

    [Test]
    public void MegabyteEchoesWholeOverALossySegmentTest()
    {
        var (network, a, b) = Segment();
        network.LossRate = 0.02;
        network.SwapRate = 0.05;
        Echo(network, a, b, 1024 * 1024);
        Assert.That(network.Lost, Is.Positive);
        Assert.That(a.Statistics.SegmentsRetransmitted + b.Statistics.SegmentsRetransmitted, Is.Positive);
        Assert.That(a.Statistics.FastRetransmits + b.Statistics.FastRetransmits, Is.Positive, "duplicate acknowledgments recovered losses");
    }

    [Test]
    public void RefusedConnectionReportsItTest()
    {
        var (network, a, _) = Segment();
        Assert.That(a.OpenTcp(out var client), Is.Zero);
        Assert.That(a.Connect(client, B, 9), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        Assert.That(a.Connect(client, B, 9), Is.EqualTo(NetworkErrors.ALREADY));
        network.Pump();
        Assert.That(a.Poll(client) & NetworkStack.ERROR, Is.Not.Zero);
        Assert.That(a.TakeError(client), Is.EqualTo(NetworkErrors.CONNECTION_REFUSED));
        Assert.That(a.TakeError(client), Is.Zero, "the error is reported once");
        Assert.That(b_ResetsOf(network), Is.Positive);
    }

    [Test]
    public void SilentPeerTimesOutTest()
    {
        var (network, a, _) = Segment();
        Assert.That(a.OpenTcp(out var client), Is.Zero);
        Assert.That(a.Connect(client, ABSENT, PORT), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        for (var second = 0; second < 300 && a.FindTcp(client)!.State == TcpState.SynSent; second++)
            network.Wait(1000);
        Assert.That(a.FindTcp(client)!.State, Is.EqualTo(TcpState.Closed));
        Assert.That(a.TakeError(client), Is.EqualTo(NetworkErrors.TIMED_OUT));
    }

    // An ICMP destination unreachable report for a SYN ends the attempt with the report's error, as Linux does, so that
    // a program moves on to its next address: slirp answers a SYN to an address the host cannot reach so (W4.3: .NET
    // tried example.com's IPv6 address first on a runner without IPv6 and waited for its 30 s timeout).
    [Test]
    public void UnreachableReportEndsAConnectionAttemptTest()
    {
        var (network, a, b) = Segment();
        a.Configure(A, MASK, B, []); // B is A's gateway: a SYN to another network goes to it
        Assert.That(a.OpenTcp(out var client), Is.Zero);
        var before = network.Captured.Count;
        Assert.That(a.Connect(client, new Ipv4Address(192, 168, 1, 1), 443), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        network.Pump();
        var syn = network.Captured.Skip(before).Single(frame => frame[12] == 0x08 && frame[13] == 0x00 && frame[14 + 9] == Ipv4Header.TCP);
        // B, as a router, reports the network unreachable, quoting the datagram's header and the SYN's first eight bytes.
        var quoted = syn.AsSpan(EthernetHeader.SIZE, Ipv4Header.SIZE + 8);
        var message = new byte[8 + quoted.Length];
        message[0] = 3; // destination unreachable
        message[1] = 0; // network unreachable
        quoted.CopyTo(message.AsSpan(8));
        var checksum = InternetChecksum.Compute(message);
        message[2] = (byte)(checksum >> 8);
        message[3] = (byte)checksum;
        var packet = new byte[Ipv4Header.SIZE + message.Length];
        Ipv4Header.Write(packet, B, A, Ipv4Header.ICMP, 1, message.Length);
        message.CopyTo(packet, Ipv4Header.SIZE);
        var frame = new byte[EthernetHeader.SIZE + packet.Length];
        new EthernetHeader(new MacAddress(0x01_00_00_5E_00_02), new MacAddress(0x02_00_00_5E_00_02), EthernetHeader.IPV4).Write(frame);
        packet.CopyTo(frame, EthernetHeader.SIZE);
        a.Receive(frame);
        Assert.That(a.FindTcp(client)!.State, Is.EqualTo(TcpState.Closed));
        Assert.That(a.Poll(client) & (NetworkStack.ERROR | NetworkStack.HANGUP), Is.EqualTo(NetworkStack.ERROR | NetworkStack.HANGUP));
        Assert.That(a.TakeError(client), Is.EqualTo(NetworkErrors.NETWORK_UNREACHABLE));
        Assert.That(a.Statistics.UnreachableReports, Is.EqualTo(1));
        // A forged report, quoting a sequence number that is not the attempt's, ends nothing.
        Assert.That(a.OpenTcp(out var second), Is.Zero);
        Assert.That(a.Connect(second, new Ipv4Address(192, 168, 1, 1), 443), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        network.Pump();
        a.Receive(frame); // the old SYN's ports and sequence number
        Assert.That(a.FindTcp(second)!.State, Is.EqualTo(TcpState.SynSent), "another attempt's report");
    }

    [Test]
    public void ClosedWindowIsProbedAndReopensTest()
    {
        var (network, a, b) = Segment();
        var (client, server, _) = Connect(network, a, b);
        var chunk = new byte[64 * 1024];
        long accepted = 0;
        for (var round = 0; round < 64; round++)
        {
            if (a.SendStream(client, chunk, out var sent) == NetworkErrors.AGAIN)
                break;
            accepted += sent;
            network.Wait(10);
        }
        network.Wait(5000);
        var receiver = b.FindTcp(server)!;
        Assert.That(receiver.Receive.Free, Is.LessThan(8), "the receiver's buffer is full to within a unit of its window's shift of 3");
        Assert.That(a.FindTcp(client)!.Send.Count, Is.Positive, "the sender holds what the window refused");
        Assert.That(a.SendStream(client, chunk, out _), Is.EqualTo(NetworkErrors.AGAIN));
        long read = 0;
        var buffer = new byte[64 * 1024];
        for (var round = 0; round < 200 && read < accepted; round++)
        {
            while (b.ReceiveStream(server, buffer, false, out var count) == 0 && count > 0)
                read += count;
            network.Wait(250);
        }
        Assert.That(read, Is.EqualTo(accepted), "every byte arrived once the window opened again");
    }

    // P3: a .NET process froze on ARM64 with four processors in the suite's TLS echo over the loopback, and the network
    // service's stack thread was found in Advance forever, holding the stack's lock, while every socket of every process
    // waited for it. Both connections' receive buffers were full and both had probed the other's closed window with a
    // byte, so each side's pure acknowledgments lay beyond the other's RCV.NXT: unacceptable with a zero window, each
    // answered with an acknowledgment that was unacceptable in turn — an acknowledgment storm that the loopback queue
    // fed back into the same Advance without end. Linux never consumes sequence space for a window probe
    // (tcp_xmit_probe_skb) and rate-limits the acknowledgments it sends for out-of-window segments
    // (tcp_invalid_ratelimit, 500 ms); the stack does the same, and an Advance takes the loopback's queue as it was.
    [Test]
    public void ClosedWindowsBothWaysOverTheLoopbackDoNotStormTest()
    {
        var network = new SimulatedNetwork();
        var stack = network.AddStack(0x01_00_00_5E_00_04);
        Assert.That(stack.OpenTcp(out var listener), Is.Zero);
        Assert.That(stack.Bind(listener, Ipv4Address.Loopback, PORT), Is.Zero);
        Assert.That(stack.Listen(listener, 4), Is.Zero);
        Assert.That(stack.OpenTcp(out var client), Is.Zero);
        Assert.That(stack.Connect(client, Ipv4Address.Loopback, PORT), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        network.Pump();
        Assert.That(stack.Accept(listener, out var server), Is.Zero);
        var chunk = new byte[64 * 1024];
        long toServer = 0, toClient = 0;
        for (var round = 0; round < 64; round++)
        {
            var clientFull = stack.SendStream(client, chunk, out var sent) == NetworkErrors.AGAIN;
            toServer += clientFull ? 0 : sent;
            var serverFull = stack.SendStream(server, chunk, out sent) == NetworkErrors.AGAIN;
            toClient += serverFull ? 0 : sent;
            network.Wait(10);
            if (clientFull && serverFull)
                break;
        }
        var clientSide = stack.FindTcp(client)!;
        var serverSide = stack.FindTcp(server)!;
        Assert.That(clientSide.Receive.Free, Is.LessThan(8), "the client's buffer is full");
        Assert.That(serverSide.Receive.Free, Is.LessThan(8), "the server's buffer is full");
        Assert.That(clientSide.Send.Count, Is.Positive.And.EqualTo(serverSide.Send.Count), "both hold what the other's window refused");
        // Nobody reads for ten seconds: the probes back off, and nothing else goes.
        var before = stack.Statistics.SegmentsSent;
        for (var step = 0; step < 100; step++)
        {
            network.Now += 100;
            stack.Advance();
        }
        var sent2 = stack.Statistics.SegmentsSent - before;
        TestContext.Out.WriteLine($"{sent2} segments in ten seconds: {stack.Statistics.WindowProbes} probes, " +
            $"{stack.Statistics.AcknowledgmentsSkipped} acknowledgments held back");
        Assert.That(sent2, Is.LessThan(64), $"the probes and their answers alone go over a closed window, not {sent2} segments");
        Assert.That(clientSide.SndNxt, Is.EqualTo(clientSide.SndUna), "a probe consumes no sequence space");
        Assert.That(serverSide.SndNxt, Is.EqualTo(serverSide.SndUna), "a probe consumes no sequence space");
        // Both read: every byte arrives once, and the connections stay synchronized.
        var buffer = new byte[64 * 1024];
        long fromClient = 0, fromServer = 0;
        for (var round = 0; round < 400 && (fromClient < toServer || fromServer < toClient); round++)
        {
            while (stack.ReceiveStream(server, buffer, false, out var count) == 0 && count > 0)
                fromClient += count;
            while (stack.ReceiveStream(client, buffer, false, out var count) == 0 && count > 0)
                fromServer += count;
            network.Wait(50);
        }
        Assert.That(fromClient, Is.EqualTo(toServer), "the server read every byte the client sent");
        Assert.That(fromServer, Is.EqualTo(toClient), "the client read every byte the server sent");
        Assert.That(stack.DescribeConnections(), Has.Count.EqualTo(2).And.All.Contains("Established"));
    }

    [Test]
    public void Ipv6LoopbackConnectionTest()
    {
        var network = new SimulatedNetwork();
        var stack = network.AddStack(0x01_00_00_5E_00_05);
        Assert.That(stack.OpenTcp(out var listener, ipv6: true), Is.Zero);
        Assert.That(stack.Bind(listener, Ipv6Address.Loopback, PORT), Is.Zero);
        Assert.That(stack.Listen(listener, 4), Is.Zero);
        Assert.That(stack.OpenTcp(out var client, ipv6: true), Is.Zero);
        Assert.That(stack.Connect(client, Ipv6Address.Loopback, PORT), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        network.Pump();
        Assert.That(network.Captured, Is.Empty, "the loopback never reaches the link");
        Assert.That(stack.Accept(listener, out var server), Is.Zero);
        var serverSide = stack.FindTcp(server)!;
        Assert.That((serverSide.Family, serverSide.LocalAddress, serverSide.RemoteAddress, serverSide.Mss),
            Is.EqualTo((SocketMessages.INET6, Ipv6Address.Loopback, Ipv6Address.Loopback, 1500 - Ipv6Header.SIZE - TcpHeader.SIZE)));
        Assert.That(stack.SendStream(client, "hello"u8, out _), Is.Zero);
        network.Pump();
        Assert.That(ReadAll(stack, server), Is.EqualTo("hello"));
        Assert.That(stack.SendStream(server, "world"u8, out _), Is.Zero);
        network.Wait(50);
        Assert.That(ReadAll(stack, client), Is.EqualTo("world"));
        Assert.That(stack.Close(client), Is.Zero);
        network.Pump();
        Assert.That(stack.FindTcp(server)!.State, Is.EqualTo(TcpState.CloseWait));
        Assert.That(stack.Close(server), Is.Zero);
        network.Pump();
        Assert.That(stack.FindTcp(server), Is.Null, "the passive closer's connection went with the last acknowledgment");
        Assert.That(stack.FindTcp(client)!.State, Is.EqualTo(TcpState.TimeWait));
    }

    [Test]
    public void DualStackListenerTakesBothFamiliesAndV6OnlyRefusesIpv4Test()
    {
        var network = new SimulatedNetwork();
        var stack = network.AddStack(0x01_00_00_5E_00_06);
        stack.Configure(A, MASK, Ipv4Address.Any, []);
        Assert.That(stack.OpenTcp(out var listener, ipv6: true), Is.Zero);
        Assert.That(stack.Bind(listener, Ipv6Address.Any, PORT), Is.Zero);
        Assert.That(stack.Listen(listener, 4), Is.Zero);
        Assert.That(stack.OpenTcp(out var taken), Is.Zero);
        Assert.That(stack.Bind(taken, Ipv4Address.Any, PORT), Is.EqualTo(NetworkErrors.ADDRESS_IN_USE), "a dual-stack listener holds the port's IPv4 side");
        Assert.That(stack.OpenTcp(out var client4), Is.Zero);
        Assert.That(stack.Connect(client4, Ipv4Address.Loopback, PORT), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        Assert.That(stack.OpenTcp(out var client6, ipv6: true), Is.Zero);
        Assert.That(stack.Connect(client6, Ipv6Address.Loopback, PORT), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        network.Pump();
        Assert.That(stack.Accept(listener, out var first), Is.Zero);
        Assert.That(stack.Accept(listener, out var second), Is.Zero);
        var peers = new[] { stack.FindTcp(first)!, stack.FindTcp(second)! };
        Assert.That(peers.Select(peer => peer.Family), Has.All.EqualTo(SocketMessages.INET6), "a dual-stack listener's connections are AF_INET6");
        Assert.That(peers.Select(peer => peer.RemoteAddress), Is.EquivalentTo(new[] { Ipv6Address.Map(Ipv4Address.Loopback), Ipv6Address.Loopback }),
            "the IPv4 peer in its mapped form, as Linux names it");
        Assert.That(peers.Select(peer => peer.Mss), Is.EquivalentTo(new[] { 1500 - Ipv4Header.SIZE - TcpHeader.SIZE, 1500 - Ipv6Header.SIZE - TcpHeader.SIZE }));
        Assert.That(stack.SendStream(client4, "four"u8, out _), Is.Zero);
        network.Pump();
        var ipv4Side = peers.Single(peer => peer.RemoteAddress.IsMapped);
        Assert.That(ReadAll(stack, ipv4Side.Id), Is.EqualTo("four"));

        Assert.That(stack.OpenTcp(out var only, ipv6: true), Is.Zero);
        Assert.That(stack.SetV6Only(only, true), Is.Zero);
        Assert.That(stack.Bind(only, Ipv6Address.Any, PORT + 1), Is.Zero);
        Assert.That(stack.Listen(only, 4), Is.Zero);
        Assert.That(stack.Bind(taken, Ipv4Address.Any, PORT + 1), Is.Zero, "an IPV6_V6ONLY listener leaves the port's IPv4 side free");
        Assert.That(stack.Close(taken), Is.Zero);
        Assert.That(stack.OpenTcp(out var refused), Is.Zero);
        Assert.That(stack.Connect(refused, Ipv4Address.Loopback, PORT + 1), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        network.Pump();
        Assert.That(stack.TakeError(refused), Is.EqualTo(NetworkErrors.CONNECTION_REFUSED), "no listener takes IPv4 at the port");
        Assert.That(stack.OpenTcp(out var client6b, ipv6: true), Is.Zero);
        Assert.That(stack.Connect(client6b, Ipv6Address.Loopback, PORT + 1), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        network.Pump();
        Assert.That(stack.Accept(only, out _), Is.Zero);
        Assert.That(stack.OpenTcp(out var mapped, ipv6: true), Is.Zero);
        Assert.That(stack.SetV6Only(mapped, true), Is.Zero);
        Assert.That(stack.Connect(mapped, Ipv4Address.Loopback, PORT), Is.EqualTo(NetworkErrors.NETWORK_UNREACHABLE), "an IPV6_V6ONLY socket reaches no IPv4 address");
    }

    [Test]
    public void Ipv6BetweenStacksTest()
    {
        var (network, a, b) = Segment();
        a.StartIpv6();
        b.StartIpv6();
        network.Wait(1000);
        Assert.That((a.Ipv6LinkLocal.IsLinkLocal, b.Ipv6LinkLocal.IsLinkLocal), Is.EqualTo((true, true)));
        Assert.That(b.OpenTcp(out var listener, ipv6: true), Is.Zero);
        Assert.That(b.Bind(listener, Ipv6Address.Any, PORT), Is.Zero);
        Assert.That(b.Listen(listener, 8), Is.Zero);
        Assert.That(a.OpenTcp(out var client, ipv6: true), Is.Zero);
        Assert.That(a.Connect(client, b.Ipv6LinkLocal, PORT), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        network.Pump();
        Assert.That(b.Accept(listener, out var server), Is.Zero);
        var clientSide = a.FindTcp(client)!;
        Assert.That((clientSide.State, clientSide.LocalAddress, clientSide.RemoteAddress, clientSide.Mss),
            Is.EqualTo((TcpState.Established, a.Ipv6LinkLocal, b.Ipv6LinkLocal, 1500 - Ipv6Header.SIZE - TcpHeader.SIZE)));
        Assert.That(b.FindTcp(server)!.RemoteAddress, Is.EqualTo(a.Ipv6LinkLocal));
        Assert.That(a.SendStream(client, "hello"u8, out _), Is.Zero);
        network.Pump();
        Assert.That(ReadAll(b, server), Is.EqualTo("hello"));
        Assert.That(b.SendStream(server, "world"u8, out _), Is.Zero);
        network.Wait(50);
        Assert.That(ReadAll(a, client), Is.EqualTo("world"));
        Assert.That(network.Captured.Count(frame => frame[12] == 0x86 && frame[13] == 0xDD), Is.EqualTo(network.Captured.Count), "IPv6 frames alone");
        Assert.That(a.Statistics.NeighbourSolicitations, Is.EqualTo(2), "duplicate address detection and the peer's address");
    }

    [Test]
    public void BacklogHoldsConnectionsUntilAcceptedTest()
    {
        var (network, a, b) = Segment();
        Assert.That(b.OpenTcp(out var listener), Is.Zero);
        Assert.That(b.Bind(listener, Ipv4Address.Any, PORT), Is.Zero);
        Assert.That(b.Listen(listener, 1), Is.Zero);
        Assert.That(a.OpenTcp(out var first), Is.Zero);
        Assert.That(a.OpenTcp(out var second), Is.Zero);
        Assert.That(a.Connect(first, B, PORT), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        Assert.That(a.Connect(second, B, PORT), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        network.Pump();
        Assert.That(a.FindTcp(first)!.State, Is.EqualTo(TcpState.Established));
        Assert.That(a.FindTcp(second)!.State, Is.EqualTo(TcpState.SynSent), "the backlog of one dropped the second request");
        Assert.That(b.Statistics.ConnectionsDropped, Is.EqualTo(1));
        Assert.That(b.Accept(listener, out _), Is.Zero);
        Assert.That(b.Accept(listener, out _), Is.EqualTo(NetworkErrors.AGAIN));
        network.Wait(1000); // the SYN's retransmission
        Assert.That(a.FindTcp(second)!.State, Is.EqualTo(TcpState.Established));
        Assert.That(b.Accept(listener, out _), Is.Zero);
    }

    [Test]
    public void CloseWithUnreadBytesResetsTest()
    {
        var (network, a, b) = Segment();
        var (client, server, _) = Connect(network, a, b);
        Assert.That(a.SendStream(client, "unread"u8, out _), Is.Zero);
        network.Pump();
        Assert.That(b.Close(server), Is.Zero);
        network.Pump();
        Assert.That(a.FindTcp(client)!.State, Is.EqualTo(TcpState.Closed));
        Assert.That(a.ReceiveStream(client, new byte[8], false, out _), Is.EqualTo(NetworkErrors.CONNECTION_RESET));
        Assert.That(a.SendStream(client, "more"u8, out _), Is.EqualTo(NetworkErrors.BROKEN_PIPE));
    }

    [Test]
    public void SimultaneousCloseEndsBothSidesTest()
    {
        var (network, a, b) = Segment();
        var (client, server, _) = Connect(network, a, b);
        Assert.That(a.Shutdown(client, read: false, write: true), Is.Zero);
        Assert.That(b.Shutdown(server, read: false, write: true), Is.Zero);
        network.Pump();
        Assert.That(a.FindTcp(client)!.State, Is.EqualTo(TcpState.TimeWait));
        Assert.That(b.FindTcp(server)!.State, Is.EqualTo(TcpState.TimeWait));
        Assert.That(a.Poll(client) & NetworkStack.HANGUP, Is.Not.Zero, "both directions ended");
        network.Wait(60_000);
        Assert.That(a.FindTcp(client)!.State, Is.EqualTo(TcpState.Closed), "a socket the application holds stays, closed");
        Assert.That(a.Close(client), Is.Zero);
        Assert.That(a.FindTcp(client), Is.Null);
    }

    [Test]
    public void LoopbackConnectsWithinOneStackTest()
    {
        var network = new SimulatedNetwork();
        var stack = network.AddStack(0x01_00_00_5E_00_01);
        Assert.That(stack.OpenTcp(out var listener), Is.Zero);
        Assert.That(stack.Bind(listener, Ipv4Address.Loopback, PORT), Is.Zero);
        Assert.That(stack.Listen(listener, 4), Is.Zero);
        Assert.That(stack.OpenTcp(out var client), Is.Zero);
        Assert.That(stack.Connect(client, Ipv4Address.Loopback, PORT), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        network.Pump();
        Assert.That(stack.Accept(listener, out var server), Is.Zero);
        Assert.That(stack.SendStream(client, "loop"u8, out _), Is.Zero);
        network.Pump();
        Assert.That(ReadAll(stack, server), Is.EqualTo("loop"));
        Assert.That(stack.DescribeConnections(), Has.Count.EqualTo(2).And.All.Contains("Established"), "the listener is not a connection");
        Assert.That(stack.Bind(listener, Ipv4Address.Loopback, PORT), Is.EqualTo(NetworkErrors.INVALID), "bound already");
        Assert.That(stack.OpenTcp(out var other), Is.Zero);
        Assert.That(stack.Bind(other, Ipv4Address.Any, PORT), Is.EqualTo(NetworkErrors.ADDRESS_IN_USE));
    }

    // The right edge of the window a side advertises starts at RCV.NXT (W2.2): it started at zero, which lies ahead of
    // RCV.NXT for half the peers' random initial sequence numbers, so the window of the first acknowledgment was garbage
    // and the edge, which never moves back, stayed at 65535 << 3, twice the buffer: the peer then sent into a full
    // buffer, and its retransmissions backed off for a minute.
    [Test]
    public void AdvertisedWindowStaysWithinTheBufferTest()
    {
        var (network, a, b) = Segment();
        Assert.That(b.OpenTcp(out var listener), Is.Zero);
        Assert.That(b.Bind(listener, Ipv4Address.Any, PORT), Is.Zero);
        Assert.That(b.Listen(listener, 64), Is.Zero);
        for (var i = 0; i < 32; i++)
        {
            Assert.That(a.OpenTcp(out var client), Is.Zero);
            Assert.That(a.Connect(client, B, PORT), Is.EqualTo(NetworkErrors.IN_PROGRESS));
            network.Pump();
            Assert.That(b.Accept(listener, out var server), Is.Zero);
            Assert.That(a.SendStream(client, "x"u8, out _), Is.Zero);
            Assert.That(b.SendStream(server, "y"u8, out _), Is.Zero);
            network.Pump();
            foreach (var tcp in new[] { a.FindTcp(client)!, b.FindTcp(server)! })
                Assert.That(tcp.AdvertisedEdge - tcp.RcvNxt, Is.LessThanOrEqualTo((uint)tcp.Receive.Capacity),
                    $"connection {i}: the advertised window exceeds the buffer");
        }
    }

    // SO_REUSEPORT (W2.1b): listeners that all set it share a port, a socket without it is refused, and each connection
    // reaches one of them while they share the connections, as Linux spreads them over its group.
    [Test]
    public void ReusePortSharesAListeningPortTest()
    {
        var network = new SimulatedNetwork();
        var stack = network.AddStack(0x01_00_00_5E_00_03);
        var listeners = new int[2];
        for (var i = 0; i < listeners.Length; i++)
        {
            Assert.That(stack.OpenTcp(out listeners[i]), Is.Zero);
            Assert.That(stack.SetReusePort(listeners[i], true), Is.Zero);
            Assert.That(stack.Bind(listeners[i], Ipv4Address.Loopback, PORT), Is.Zero);
            Assert.That(stack.Listen(listeners[i], 8), Is.Zero);
        }
        Assert.That(stack.OpenTcp(out var plain), Is.Zero);
        Assert.That(stack.Bind(plain, Ipv4Address.Loopback, PORT), Is.EqualTo(NetworkErrors.ADDRESS_IN_USE), "a socket without SO_REUSEPORT");
        var accepted = new int[listeners.Length];
        for (var i = 0; i < 8; i++)
        {
            Assert.That(stack.OpenTcp(out var client), Is.Zero);
            Assert.That(stack.Connect(client, Ipv4Address.Loopback, PORT), Is.EqualTo(NetworkErrors.IN_PROGRESS));
            network.Pump();
            var takers = Enumerable.Range(0, listeners.Length).Where(listener => stack.Accept(listeners[listener], out _) == 0).ToArray();
            Assert.That(takers, Has.Length.EqualTo(1), "a connection reaches one listener");
            accepted[takers[0]]++;
        }
        Assert.That(accepted, Has.All.GreaterThan(0), "the listeners share the connections");
    }

    [Test]
    public void HeaderOptionsWriteAndReadBackTest()
    {
        var segment = new byte[TcpHeader.SIZE + TcpHeader.OptionBytes(1460, 7) + 3];
        "abc"u8.CopyTo(segment.AsSpan(segment.Length - 3));
        var length = TcpHeader.Write(segment, A, B, 1234, 80, 0x01020304, 0x0A0B0C0D, TcpHeader.SYN | TcpHeader.ACK, 29200, 1460, 7);
        Assert.That(length, Is.EqualTo(28));
        Assert.That(TcpHeader.TryRead(segment, A, B, out var header), Is.True);
        Assert.That(header, Is.EqualTo(new TcpHeader(1234, 80, 0x01020304, 0x0A0B0C0D, 28, TcpHeader.SYN | TcpHeader.ACK, 29200, 1460, 7)));
        segment[^1] ^= 1;
        Assert.That(TcpHeader.TryRead(segment, A, B, out _), Is.False, "a payload that no longer sums");
    }

    #endregion

    #region Functions

    private static (SimulatedNetwork Network, NetworkStack A, NetworkStack B) Segment()
    {
        var network = new SimulatedNetwork();
        var a = network.AddStack(0x01_00_00_5E_00_02);
        var b = network.AddStack(0x02_00_00_5E_00_02);
        a.Configure(A, MASK, Ipv4Address.Any, []);
        b.Configure(B, MASK, Ipv4Address.Any, []);
        return (network, a, b);
    }

    // A connection from A to a listener of B: the client's, the server's and the listener's sockets.
    private static (int Client, int Server, int Listener) Connect(SimulatedNetwork network, NetworkStack a, NetworkStack b)
    {
        Assert.That(b.OpenTcp(out var listener), Is.Zero);
        Assert.That(b.Bind(listener, Ipv4Address.Any, PORT), Is.Zero);
        Assert.That(b.Listen(listener, 8), Is.Zero);
        Assert.That(b.Accept(listener, out _), Is.EqualTo(NetworkErrors.AGAIN));
        Assert.That(a.OpenTcp(out var client), Is.Zero);
        Assert.That(a.Connect(client, B, PORT), Is.EqualTo(NetworkErrors.IN_PROGRESS));
        Assert.That(a.SendStream(client, "x"u8, out _), Is.EqualTo(NetworkErrors.AGAIN), "not yet established");
        network.Pump();
        Assert.That(b.Poll(listener) & NetworkStack.READABLE, Is.Not.Zero, "a connection to accept");
        Assert.That(b.Accept(listener, out var server), Is.Zero);
        return (client, server, listener);
    }

    // A's bytes to B, which echoes them; A checks them back.
    private static void Echo(SimulatedNetwork network, NetworkStack a, NetworkStack b, int bytes)
    {
        var (client, server, _) = Connect(network, a, b);
        var data = new byte[bytes];
        new Random(3).NextBytes(data);
        var echoed = new byte[bytes];
        var buffer = new byte[32 * 1024];
        int sent = 0, received = 0;
        var pending = new System.Collections.Generic.Queue<byte>();
        for (var round = 0; round < 100_000 && received < bytes; round++)
        {
            if (sent < bytes && a.SendStream(client, data.AsSpan(sent, Math.Min(32 * 1024, bytes - sent)), out var accepted) == 0)
                sent += accepted;
            while (b.ReceiveStream(server, buffer, false, out var count) == 0 && count > 0)
                foreach (var value in buffer.AsSpan(0, count))
                    pending.Enqueue(value);
            if (pending.Count > 0)
            {
                var back = pending.ToArray();
                if (b.SendStream(server, back, out var echoedBack) == 0)
                    for (var i = 0; i < echoedBack; i++)
                        pending.Dequeue();
            }
            while (a.ReceiveStream(client, buffer, false, out var count) == 0 && count > 0)
            {
                buffer.AsSpan(0, count).CopyTo(echoed.AsSpan(received));
                received += count;
            }
            network.Wait(5);
        }
        Assert.That(received, Is.EqualTo(bytes), "every byte came back");
        Assert.That(echoed.SequenceEqual(data), Is.True, "in order and unchanged");
    }

    private static string ReadAll(NetworkStack stack, int socket)
    {
        var buffer = new byte[4096];
        var text = new StringBuilder();
        while (stack.ReceiveStream(socket, buffer, false, out var count) == 0 && count > 0)
            text.Append(Encoding.ASCII.GetString(buffer, 0, count));
        return text.ToString();
    }

    private static int b_ResetsOf(SimulatedNetwork network) =>
        network.Captured.Count(frame => frame.Length > 14 + 20 + 13 && frame[12] == 0x08 && frame[13] == 0x00 && frame[14 + 9] == Ipv4Header.TCP &&
            (frame[14 + 20 + 13] & TcpHeader.RST) != 0);

    #endregion
}
