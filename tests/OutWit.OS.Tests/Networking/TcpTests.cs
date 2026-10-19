using System;
using System.Linq;
using System.Text;
using OutWit.OS.Networking;

namespace OutWit.OS.Tests.Networking;

/// <summary>
/// TCP (plan step W1.2b) between two stacks of the simulated segment and within one: the handshake, data both ways and
/// the close with TIME-WAIT; a megabyte echoed whole over a segment that loses and swaps frames; a refusal, a peer that
/// never answers, a closed window and its probes, the backlog, the reset of a close with unread bytes, a simultaneous
/// close and the loopback.
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
        Assert.That(b.FindTcp(server)!.RemoteAddress, Is.EqualTo(A));
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
        Assert.That(stack.Bind(listener, Ipv4Address.Loopback, PORT), Is.EqualTo(NetworkErrors.INVALID), "bound already");
        Assert.That(stack.OpenTcp(out var other), Is.Zero);
        Assert.That(stack.Bind(other, Ipv4Address.Any, PORT), Is.EqualTo(NetworkErrors.ADDRESS_IN_USE));
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
