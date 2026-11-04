using System;
using System.Collections.Generic;
using System.Globalization;
using OutWit.OS.Networking;

namespace OutWit.OS.Time;

/// <summary>
/// The time servers a package names (witos/time.h's WIT_TIME_SERVERS, plan step H7.4): one a line, a host name or an
/// address with NTP's port, or with a port of its own after a colon (an IPv6 address in brackets then); empty lines and
/// lines that start with '#' say nothing.
/// </summary>
internal static class TimeServers
{
    #region Constants

    /// <summary>
    /// The package's file.
    /// </summary>
    public const string PATH = "/etc/witos/time-servers";

    #endregion

    #region Functions

    /// <summary>
    /// The servers the lines name, in their order.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <returns>Each server's host and port.</returns>
    /// <exception cref="FormatException">A line names no host or no valid port.</exception>
    public static IReadOnlyList<TimeServer> Parse(IEnumerable<string> lines)
    {
        var servers = new List<TimeServer>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            string host;
            string? port = null;
            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                if (close < 0 || (close + 1 < line.Length && line[close + 1] != ':'))
                    throw new FormatException($"\"{line}\" is no bracketed address.");
                host = line[1..close];
                if (close + 1 < line.Length)
                    port = line[(close + 2)..];
            }
            else if (line.IndexOf(':') is var colon and >= 0 && line.IndexOf(':', colon + 1) < 0)
            {
                host = line[..colon];
                port = line[(colon + 1)..];
            }
            else
            {
                host = line; // a name, an IPv4 address, or an IPv6 address without a port
            }
            if (host.Length == 0)
                throw new FormatException($"\"{line}\" names no host.");
            var number = Sntp.PORT;
            if (port is not null && (!ushort.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number == 0))
                throw new FormatException($"\"{line}\" names no valid port.");
            servers.Add(new TimeServer(host, number));
        }
        return servers;
    }

    #endregion
}

/// <summary>
/// A time server: its host, a name or an address, and its port.
/// </summary>
/// <param name="Host">The host.</param>
/// <param name="Port">The port.</param>
internal sealed record TimeServer(string Host, ushort Port)
{
    /// <summary>
    /// The server as a line names it.
    /// </summary>
    /// <returns>The text.</returns>
    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}
