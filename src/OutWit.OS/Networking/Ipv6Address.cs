using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace OutWit.OS.Networking;

/// <summary>
/// An IPv6 address (plan step W4.3): sixteen bytes as two words in network order. The socket layer names every address
/// so, an IPv4 one as its mapped form <c>::ffff:a.b.c.d</c> (RFC 4291 section 2.5.5.2), as Linux's dual-stack sockets do,
/// so that one code path serves both families and the IP layer tells them apart at its boundary.
/// </summary>
/// <param name="High">The first eight bytes.</param>
/// <param name="Low">The last eight bytes.</param>
public readonly record struct Ipv6Address(ulong High, ulong Low)
{
    #region Constants

    /// <summary>
    /// The address's bytes.
    /// </summary>
    public const int SIZE = 16;

    /// <summary>
    /// The unspecified address, <c>::</c>.
    /// </summary>
    public static readonly Ipv6Address Any = new(0, 0);

    /// <summary>
    /// The loopback address, <c>::1</c>.
    /// </summary>
    public static readonly Ipv6Address Loopback = new(0, 1);

    /// <summary>
    /// The link-local all-nodes multicast address, <c>ff02::1</c>.
    /// </summary>
    public static readonly Ipv6Address AllNodes = new(0xFF02_0000_0000_0000UL, 1);

    /// <summary>
    /// The link-local all-routers multicast address, <c>ff02::2</c>.
    /// </summary>
    public static readonly Ipv6Address AllRouters = new(0xFF02_0000_0000_0000UL, 2);

    private const ulong LINK_LOCAL_PREFIX = 0xFE80_0000_0000_0000UL;
    private const ulong MAPPED_HIGH = 0;
    private const ulong MAPPED_LOW_PREFIX = 0x0000_FFFF_0000_0000UL;
    private const ulong SOLICITED_NODE_LOW_PREFIX = 0x0000_0001_FF00_0000UL;

    #endregion

    #region Functions

    /// <summary>
    /// Reads sixteen bytes in network order.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The address.</returns>
    public static Ipv6Address Read(ReadOnlySpan<byte> bytes) =>
        new(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));

    /// <summary>
    /// Writes the address's sixteen bytes in network order.
    /// </summary>
    /// <param name="bytes">The destination.</param>
    public void Write(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt64BigEndian(bytes, High);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], Low);
    }

    /// <summary>
    /// The mapped form of an IPv4 address, <c>::ffff:a.b.c.d</c>.
    /// </summary>
    /// <param name="address">The IPv4 address.</param>
    /// <returns>Its mapped form; Any for Any.</returns>
    public static Ipv6Address Map(Ipv4Address address) => address == Ipv4Address.Any ? Any : new(MAPPED_HIGH, MAPPED_LOW_PREFIX | address.Value);

    /// <summary>
    /// The link-local address of an interface with a hardware address: <c>fe80::</c> and the modified EUI-64 of the
    /// address (RFC 4291 appendix A: the universal/local bit flipped, <c>ff:fe</c> in the middle).
    /// </summary>
    /// <param name="mac">The hardware address.</param>
    /// <returns>The link-local address.</returns>
    public static Ipv6Address LinkLocal(MacAddress mac) => new(LINK_LOCAL_PREFIX, InterfaceIdentifier(mac));

    /// <summary>
    /// An address of a prefix with the interface identifier of a hardware address (stateless autoconfiguration, RFC 4862).
    /// </summary>
    /// <param name="prefix">The prefix's first eight bytes; its length must be 64.</param>
    /// <param name="mac">The hardware address.</param>
    /// <returns>The address.</returns>
    public static Ipv6Address FromPrefix(Ipv6Address prefix, MacAddress mac) => new(prefix.High, InterfaceIdentifier(mac));

    /// <summary>
    /// The solicited-node multicast address of an address, <c>ff02::1:ffXX:XXXX</c> with its last three bytes (RFC 4291
    /// section 2.7.1).
    /// </summary>
    /// <param name="target">The address.</param>
    /// <returns>The multicast address.</returns>
    public static Ipv6Address SolicitedNode(Ipv6Address target) => new(0xFF02_0000_0000_0000UL, SOLICITED_NODE_LOW_PREFIX | (target.Low & 0xFF_FFFF));

    /// <summary>
    /// Parses the text forms of RFC 4291 section 2.2: groups of hexadecimal digits with one <c>::</c>, and a trailing
    /// IPv4 dotted quad.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The address.</returns>
    /// <exception cref="FormatException">The text is no IPv6 address.</exception>
    public static Ipv6Address Parse(string text)
    {
        if (!TryParse(text, out var address))
            throw new FormatException($"'{text}' is no IPv6 address.");
        return address;
    }

    /// <summary>
    /// Parses as <see cref="Parse"/> does, saying whether it could.
    /// </summary>
    public static bool TryParse(string text, out Ipv6Address address)
    {
        address = Any;
        if (string.IsNullOrEmpty(text))
            return false;
        var gap = text.IndexOf("::", StringComparison.Ordinal);
        if (gap >= 0 && text.IndexOf("::", gap + 1, StringComparison.Ordinal) >= 0)
            return false;
        var head = gap < 0 ? text : text[..gap];
        var tail = gap < 0 ? "" : text[(gap + 2)..];
        var groups = new ushort[8];
        var count = 0;
        if (!AppendGroups(head, groups, ref count, 8))
            return false;
        var headCount = count;
        var tailGroups = new ushort[8];
        var tailCount = 0;
        if (!AppendGroups(tail, tailGroups, ref tailCount, 8))
            return false;
        if (gap < 0 ? headCount != 8 : headCount + tailCount > 7)
            return false;
        for (var i = 0; i < tailCount; i++)
            groups[8 - tailCount + i] = tailGroups[i];
        ulong high = 0, low = 0;
        for (var i = 0; i < 4; i++)
            high = (high << 16) | groups[i];
        for (var i = 4; i < 8; i++)
            low = (low << 16) | groups[i];
        address = new Ipv6Address(high, low);
        return true;
    }

    private static bool AppendGroups(string text, ushort[] groups, ref int count, int capacity)
    {
        if (text.Length == 0)
            return true;
        foreach (var part in text.Split(':'))
        {
            if (part.Contains('.'))
            {
                // A trailing IPv4 address takes two groups.
                if (count + 2 > capacity)
                    return false;
                Ipv4Address ipv4;
                try
                {
                    ipv4 = Ipv4Address.Parse(part);
                }
                catch (FormatException)
                {
                    return false;
                }
                groups[count++] = (ushort)(ipv4.Value >> 16);
                groups[count++] = (ushort)ipv4.Value;
                continue;
            }
            if (part.Length is 0 or > 4 || count >= capacity ||
                !ushort.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var group))
                return false;
            groups[count++] = group;
        }
        return true;
    }

    private static ulong InterfaceIdentifier(MacAddress mac)
    {
        Span<byte> bytes = stackalloc byte[6];
        mac.Write(bytes);
        return ((ulong)(bytes[0] ^ 0x02) << 56) | ((ulong)bytes[1] << 48) | ((ulong)bytes[2] << 40) | (0xFFUL << 32) | (0xFEUL << 24) |
            ((ulong)bytes[3] << 16) | ((ulong)bytes[4] << 8) | bytes[5];
    }

    /// <summary>
    /// The text of RFC 5952: lowercase hexadecimal groups without leading zeros, the longest run of zero groups as
    /// <c>::</c>, a mapped IPv4 address with its dotted quad.
    /// </summary>
    public override string ToString()
    {
        if (IsMapped)
            return "::ffff:" + MappedIpv4;
        var groups = new ushort[8];
        for (var i = 0; i < 4; i++)
            groups[i] = (ushort)(High >> (48 - 16 * i));
        for (var i = 0; i < 4; i++)
            groups[4 + i] = (ushort)(Low >> (48 - 16 * i));
        var bestStart = -1;
        var bestLength = 0;
        for (var i = 0; i < 8;)
        {
            if (groups[i] != 0)
            {
                i++;
                continue;
            }
            var j = i;
            while (j < 8 && groups[j] == 0)
                j++;
            if (j - i > bestLength && j - i >= 2)
            {
                bestStart = i;
                bestLength = j - i;
            }
            i = j;
        }
        var text = new StringBuilder();
        for (var i = 0; i < 8; i++)
        {
            if (i == bestStart)
            {
                text.Append("::");
                i += bestLength - 1;
                continue;
            }
            if (text.Length > 0 && text[^1] != ':')
                text.Append(':');
            text.Append(groups[i].ToString("x", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    #endregion

    #region Properties

    /// <summary>
    /// Whether the address is the unspecified one.
    /// </summary>
    public bool IsAny => High == 0 && Low == 0;

    /// <summary>
    /// Whether the address is the loopback's.
    /// </summary>
    public bool IsLoopback => High == 0 && Low == 1;

    /// <summary>
    /// Whether the address is multicast (<c>ff00::/8</c>).
    /// </summary>
    public bool IsMulticast => High >> 56 == 0xFF;

    /// <summary>
    /// Whether the address is link-local (<c>fe80::/10</c>).
    /// </summary>
    public bool IsLinkLocal => High >> 54 == 0xFE80_0000_0000_0000UL >> 54;

    /// <summary>
    /// Whether the address is an IPv4 address's mapped form (<c>::ffff:0:0/96</c>).
    /// </summary>
    public bool IsMapped => High == MAPPED_HIGH && Low >> 32 == 0xFFFF;

    /// <summary>
    /// The IPv4 address a mapped form carries; Any for an address that is not mapped.
    /// </summary>
    public Ipv4Address MappedIpv4 => IsMapped ? new Ipv4Address((uint)Low) : Ipv4Address.Any;

    /// <summary>
    /// Whether the address is the IPv4 loopback's mapped form or the IPv6 loopback.
    /// </summary>
    public bool IsAnyLoopback => IsLoopback || (IsMapped && MappedIpv4.IsLoopback);

    /// <summary>
    /// The hardware address a multicast address maps to on Ethernet, <c>33:33</c> and its last four bytes (RFC 2464
    /// section 7).
    /// </summary>
    public MacAddress MulticastMac => MacAddress.Read([0x33, 0x33, (byte)(Low >> 24), (byte)(Low >> 16), (byte)(Low >> 8), (byte)Low]);

    #endregion
}
