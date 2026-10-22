using System;
using System.Linq;
using NUnit.Framework;
using OutWit.OS.Networking;

namespace OutWit.OS.Tests.Networking;

/// <summary>
/// IPv6's types (plan step W4.3): the address's forms, the header, ICMPv6's messages with their options and the pseudo
/// header's checksum.
/// </summary>
[TestFixture]
public sealed class Ipv6Tests
{
    #region Constants

    private static readonly MacAddress MAC = MacAddress.Read([0x52, 0x54, 0x00, 0x12, 0x34, 0x56]);
    private static readonly Ipv6Address LINK_LOCAL = Ipv6Address.Parse("fe80::5054:ff:fe12:3456");
    private static readonly Ipv6Address PREFIX = Ipv6Address.Parse("fec0::");

    #endregion

    #region Tests

    [Test]
    public void AddressFormsTest()
    {
        foreach (var text in new[] { "::", "::1", "fe80::1", "2001:db8::8a2e:370:7334", "2001:db8:0:1:1:1:1:1", "ff02::1:ff12:3456", "::ffff:10.0.2.15" })
            Assert.That(Ipv6Address.Parse(text).ToString(), Is.EqualTo(text), "the text comes back as RFC 5952 writes it");
        Assert.That(Ipv6Address.Parse("2001:0DB8:0000:0000:0000:0000:0000:0001").ToString(), Is.EqualTo("2001:db8::1"));
        Assert.That(Ipv6Address.Parse("::ffff:10.0.2.15"), Is.EqualTo(Ipv6Address.Map(new Ipv4Address(10, 0, 2, 15))));
        Assert.That(Ipv6Address.Map(new Ipv4Address(10, 0, 2, 15)).MappedIpv4, Is.EqualTo(new Ipv4Address(10, 0, 2, 15)));
        Assert.That(Ipv6Address.Map(Ipv4Address.Any), Is.EqualTo(Ipv6Address.Any), "the unspecified address maps to itself");
        Assert.That(Ipv6Address.LinkLocal(MAC), Is.EqualTo(LINK_LOCAL), "the modified EUI-64 of the hardware address");
        Assert.That(Ipv6Address.FromPrefix(PREFIX, MAC), Is.EqualTo(Ipv6Address.Parse("fec0::5054:ff:fe12:3456")));
        Assert.That(Ipv6Address.SolicitedNode(LINK_LOCAL), Is.EqualTo(Ipv6Address.Parse("ff02::1:ff12:3456")));
        Assert.That(Ipv6Address.AllNodes.MulticastMac, Is.EqualTo(MacAddress.Read([0x33, 0x33, 0, 0, 0, 1])));
        Assert.That((LINK_LOCAL.IsLinkLocal, LINK_LOCAL.IsMulticast, Ipv6Address.AllNodes.IsMulticast, Ipv6Address.Loopback.IsLoopback,
                Ipv6Address.Any.IsAny, Ipv6Address.Map(Ipv4Address.Loopback).IsAnyLoopback, Ipv6Address.Loopback.IsMapped),
            Is.EqualTo((true, false, true, true, true, true, false)));
        foreach (var bad in new[] { "", "1::2::3", ":::", "12345::", "1:2:3:4:5:6:7:8:9", "1:2:3:4:5:6:7", "::ffff:300.1.1.1", "g::" })
            Assert.That(Ipv6Address.TryParse(bad, out _), Is.False, $"'{bad}' is no address");
        var bytes = new byte[Ipv6Address.SIZE];
        LINK_LOCAL.Write(bytes);
        Assert.That(Ipv6Address.Read(bytes), Is.EqualTo(LINK_LOCAL));
        Assert.That(bytes[0], Is.EqualTo(0xfe));
    }

    [Test]
    public void HeaderAndDiscoveryMessagesTest()
    {
        var solicitation = Icmpv6.NeighbourSolicitation(LINK_LOCAL, MAC, Ipv6Address.Any, Ipv6Address.SolicitedNode(LINK_LOCAL));
        Assert.That(solicitation, Has.Length.EqualTo(24), "no source link-layer option from the unspecified address");
        Assert.That(Icmpv6.TryRead(solicitation, Ipv6Address.Any, Ipv6Address.SolicitedNode(LINK_LOCAL), out var type, out var code), Is.True);
        Assert.That((type, code), Is.EqualTo((Icmpv6.NEIGHBOUR_SOLICITATION, (byte)0)));
        solicitation[9] ^= 1;
        Assert.That(Icmpv6.TryRead(solicitation, Ipv6Address.Any, Ipv6Address.SolicitedNode(LINK_LOCAL), out _, out _), Is.False, "a message that no longer sums");
        var packet = new byte[Ipv6Header.SIZE + 8];
        Ipv6Header.Write(packet, LINK_LOCAL, Ipv6Address.AllRouters, Ipv6Header.ICMPV6, 8, Ipv6Header.DISCOVERY_HOP_LIMIT);
        Assert.That(Ipv6Header.TryRead(packet, out var header), Is.True);
        Assert.That((header.Source, header.Destination, header.NextHeader, header.HopLimit, header.PayloadLength, header.TotalLength),
            Is.EqualTo((LINK_LOCAL, Ipv6Address.AllRouters, Ipv6Header.ICMPV6, (byte)255, 8, 48)));
        Assert.That(Ipv6Header.TryRead(packet.AsSpan(0, 47), out _), Is.False, "a payload cut short");
        var router = Ipv6Address.Parse("fe80::2");
        var dns = Ipv6Address.Parse("fec0::3");
        var advertisement = Icmpv6.RouterAdvertisement(1800, PREFIX, 64, 86400, 14400, SimulatedNetwork.SERVER_MAC, 1500, [dns], router, Ipv6Address.AllNodes);
        Assert.That(Icmpv6.TryRead(advertisement, router, Ipv6Address.AllNodes, out type, out _), Is.True);
        Assert.That(type, Is.EqualTo(Icmpv6.ROUTER_ADVERTISEMENT));
        var options = Icmpv6.Options(advertisement.AsMemory(16)).ToList();
        Assert.That(options.Select(option => option.Type), Is.EqualTo(new[] { Icmpv6.OPTION_SOURCE_LINK_LAYER, Icmpv6.OPTION_PREFIX_INFORMATION, Icmpv6.OPTION_MTU, Icmpv6.OPTION_RECURSIVE_DNS }));
        Assert.That(Icmpv6.LinkLayer(options[0].Body), Is.EqualTo(SimulatedNetwork.SERVER_MAC));
        Assert.That(Icmpv6.TryPrefix(options[1].Body, out var prefix, out var length, out var flags, out var valid, out var preferred), Is.True);
        Assert.That((prefix, length, flags, valid, preferred), Is.EqualTo((PREFIX, (byte)64, (byte)(Icmpv6.ON_LINK_FLAG | Icmpv6.AUTONOMOUS_FLAG), 86400U, 14400U)));
        Assert.That(Icmpv6.DnsServers(options[3].Body, out var lifetime), Is.EqualTo(new[] { dns }));
        Assert.That(lifetime, Is.EqualTo(86400U));
        var a = new Ipv4Address(10, 0, 0, 1);
        var b = new Ipv4Address(10, 0, 0, 2);
        Assert.That(InternetChecksum.PseudoHeader(Ipv6Address.Map(a), Ipv6Address.Map(b), Ipv4Header.UDP, 20),
            Is.EqualTo(InternetChecksum.PseudoHeader(a, b, Ipv4Header.UDP, 20)), "mapped addresses sum as the IPv4 pseudo header");
    }

    #endregion
}
