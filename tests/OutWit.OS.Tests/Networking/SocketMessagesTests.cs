using System;
using OutWit.OS.Networking;

namespace OutWit.OS.Tests.Networking;

/// <summary>
/// The socket protocol's codec (witos/socket.h, plan step W1.2a): requests, replies, events, the configuration and the
/// adapters' messages round-trip at the header's sizes and offsets, and bytes of another version or size are refused;
/// AF_INET6 addresses and the IPv6 configuration (W4.3) too.
/// </summary>
[TestFixture]
public sealed class SocketMessagesTests
{
    #region Tests

    [Test]
    public void RequestsAndRepliesRoundTripTest()
    {
        var address = SocketAddress.Inet(Ipv4Address.Parse("10.0.2.3"), 53);
        var request = new SocketRequest(SocketMessages.SEND, SocketMessages.PEEK, 7, 512, 4096, 1, 2, 3, address);
        var bytes = SocketMessages.Encode(request);
        Assert.That(bytes, Has.Length.EqualTo(SocketMessages.REQUEST_SIZE));
        Assert.That(SocketMessages.DecodeRequest(bytes), Is.EqualTo(request));
        Assert.That(bytes[64], Is.EqualTo(SocketMessages.INET), "the address's family at 64");
        Assert.That(BitConverter.ToUInt16(bytes, 66), Is.EqualTo(53), "its port in host order at 66");
        Assert.That(bytes.AsSpan(72, 4).ToArray(), Is.EqualTo(new byte[] { 10, 0, 2, 3 }), "its address in network order at 72");
        var reply = new SocketReply(SocketMessages.RECEIVE, 0, 7, 100, 1400, address);
        var replyBytes = SocketMessages.Encode(reply);
        Assert.That(replyBytes, Has.Length.EqualTo(SocketMessages.REPLY_SIZE));
        Assert.That(SocketMessages.DecodeReply(replyBytes), Is.EqualTo(reply));
        Assert.That(replyBytes.AsSpan(48, 4).ToArray(), Is.EqualTo(new byte[] { 10, 0, 2, 3 }), "the reply's address at 40 + 8");
        Assert.Throws<FormatException>(() => SocketMessages.DecodeRequest(replyBytes), "a reply is no request");
        Assert.Throws<FormatException>(() => SocketMessages.DecodeReply(bytes), "a request is no reply");
        // AF_INET6 (W4.3): sixteen bytes at 72; an AF_INET address's mapped form stays AF_INET on the wire.
        var address6 = SocketAddress.Inet6(Ipv6Address.Parse("fec0::3"), 53);
        var bytes6 = SocketMessages.Encode(new SocketRequest(SocketMessages.CONNECT_TO, Socket: 7, Address: address6));
        Assert.That(bytes6[64], Is.EqualTo(SocketMessages.INET6));
        Assert.That(bytes6.AsSpan(72, 16).ToArray(), Is.EqualTo(new byte[] { 0xfe, 0xc0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3 }));
        Assert.That(SocketMessages.DecodeRequest(bytes6).Address, Is.EqualTo(address6));
        Assert.That(address.Address, Is.EqualTo(Ipv6Address.Parse("::ffff:10.0.2.3")), "the socket layer's form of an IPv4 address");
        Assert.That((address.AddressText, address6.AddressText, address.ToString(), address6.ToString()),
            Is.EqualTo(("10.0.2.3", "fec0::3", "10.0.2.3:53", "[fec0::3]:53")));
        Assert.That(SocketAddress.Of(SocketMessages.INET6, Ipv6Address.Map(Ipv4Address.Parse("10.0.2.3")), 53).AddressText, Is.EqualTo("::ffff:10.0.2.3"),
            "a dual-stack socket's IPv4 peer");
    }

    [Test]
    public void RequestsWithReservedWordsAreRefusedTest()
    {
        var bytes = SocketMessages.Encode(new SocketRequest(SocketMessages.POLL, Socket: 1));
        bytes[20] = 1;
        Assert.Throws<FormatException>(() => SocketMessages.DecodeRequest(bytes), "the request's reserved word");
        bytes = SocketMessages.Encode(new SocketRequest(SocketMessages.POLL, Socket: 1));
        bytes[68] = 1;
        Assert.Throws<FormatException>(() => SocketMessages.DecodeRequest(bytes), "the address's reserved word");
        bytes = SocketMessages.Encode(new SocketRequest(SocketMessages.POLL, Socket: 1));
        bytes[0] = 2;
        Assert.Throws<FormatException>(() => SocketMessages.DecodeRequest(bytes), "another version");
    }

    [Test]
    public void EventsConfigurationsAndAdaptersRoundTripTest()
    {
        var eventBytes = SocketMessages.EncodeEvent(9, NetworkStack.READABLE | NetworkStack.WRITABLE);
        Assert.That(eventBytes, Has.Length.EqualTo(SocketMessages.EVENT_SIZE));
        Assert.That(SocketMessages.DecodeEvent(eventBytes), Is.EqualTo((9U, NetworkStack.READABLE | NetworkStack.WRITABLE)));
        var configuration = new SocketConfiguration(Ipv4Address.Parse("10.0.2.15"), Ipv4Address.Parse("255.255.255.0"),
            Ipv4Address.Parse("10.0.2.2"), [Ipv4Address.Parse("10.0.2.3"), Ipv4Address.Parse("1.1.1.1")],
            new MacAddress(0x5634_1200_5452UL), 1500, true);
        var window = new byte[SocketMessages.CONFIGURATION_SIZE + 8];
        window.AsSpan().Fill(0xEE);
        SocketMessages.WriteConfiguration(window, configuration);
        Assert.That(window[SocketMessages.CONFIGURATION_SIZE], Is.EqualTo(0xEE), "nothing beyond the structure");
        var read = SocketMessages.ReadConfiguration(window);
        Assert.That(read with { DnsServers = [] }, Is.EqualTo(configuration with { DnsServers = [] }));
        Assert.That(read.DnsServers, Is.EqualTo(configuration.DnsServers));
        Assert.That(window.AsSpan(8, 4).ToArray(), Is.EqualTo(new byte[] { 10, 0, 2, 15 }), "the address at 8");
        var configuration6 = new SocketConfiguration6(Ipv6Address.Parse("fe80::5054:ff:fe12:3456"), Ipv6Address.Parse("fec0::5054:ff:fe12:3456"),
            Ipv6Address.Parse("fe80::2"), 64, [Ipv6Address.Parse("fec0::3")], SocketMessages.IPV6_LINK_LOCAL | SocketMessages.IPV6_GLOBAL);
        var window6 = new byte[SocketMessages.CONFIGURATION6_SIZE + 8];
        window6.AsSpan().Fill(0xEE);
        SocketMessages.WriteConfiguration6(window6, configuration6);
        Assert.That(window6[SocketMessages.CONFIGURATION6_SIZE], Is.EqualTo(0xEE), "nothing beyond the structure");
        var read6 = SocketMessages.ReadConfiguration6(window6);
        Assert.That(read6 with { DnsServers = [] }, Is.EqualTo(configuration6 with { DnsServers = [] }));
        Assert.That(read6.DnsServers, Is.EqualTo(configuration6.DnsServers));
        Assert.That(window6[8], Is.EqualTo(0xfe), "the link-local address at 8");
        Assert.That(BitConverter.ToUInt32(window6, 60), Is.EqualTo(64), "the prefix length at 60");
        var adapter = SocketMessages.EncodeAdapter("pci/0000:00:06.0");
        Assert.That(adapter, Has.Length.EqualTo(SocketMessages.ADAPTER_SIZE));
        Assert.That(SocketMessages.DecodeAdapter(adapter), Is.EqualTo(("pci/0000:00:06.0", 0U)));
        Assert.That(SocketMessages.DecodeAdapter(SocketMessages.EncodeAdapter("", SocketMessages.ADAPTERS_END)),
            Is.EqualTo(("", SocketMessages.ADAPTERS_END)));
        Assert.That(SocketMessages.DecodeAdapter(eventBytes), Is.Null, "an event is no adapter's message");
    }

    #endregion
}
