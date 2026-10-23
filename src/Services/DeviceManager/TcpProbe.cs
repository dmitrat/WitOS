using System;
using System.IO;
using OutWit.OS.Networking;
using OutWit.OS.Resources;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// TCP's probe (plan step W1.2b): the device manager connects to the network service as a process will and exchanges a
/// megabyte with the runner's echo server, which QEMU's user networking forwards from <see cref="ECHO"/>:7 to the host,
/// checking every byte that comes back; then, where the package marks that the runner connects (<see cref="INBOUND_MARK"/>,
/// W3.2), it listens on <see cref="LISTEN_PORT"/>, which the runner reaches through a port forward, and echoes what the
/// runner sends until the runner ends its sending, which the runner checks. The runtime-devices scenario requires both
/// lines, and the runner's check of its own bytes.
/// </summary>
internal static class TcpProbe
{
    #region Constants

    /// <summary>
    /// The bytes the probe exchanges with the runner's echo server.
    /// </summary>
    public const int ECHO_BYTES = 1024 * 1024;

    /// <summary>
    /// The port the probe listens on for the runner.
    /// </summary>
    public const ushort LISTEN_PORT = 8080;

    /// <summary>
    /// The file of the package that says the runner connects to <see cref="LISTEN_PORT"/> (W3.2): runtime-devices' package
    /// holds it, while a scenario that boots with the adapter for another reason (the compatibility suite reaching the
    /// runner's HTTPS server, an application the runner requests on a port of its own) does not, and the probe listens for
    /// nobody.
    /// </summary>
    public const string INBOUND_MARK = "/etc/witos/tcp-probe-inbound";

    private const string PREFIX = "[DEVICES] ";

    private const ushort ECHO_PORT = 7;

    // In the guest's time, which runs ahead while it waits for the host (DnsProbe).
    private const int WAIT_SECONDS = 300;

    private static readonly Ipv4Address ECHO = new(10, 0, 2, 100);

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
            if (Outbound(client))
            {
                if (File.Exists(INBOUND_MARK))
                    Inbound(client);
                else
                    Console.WriteLine($"{PREFIX}network TCP probe: no connection of the runner expected ({INBOUND_MARK} absent)");
            }
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException)
        {
            Console.WriteLine($"{PREFIX}network TCP probe failed: {exception.Message}");
        }
    }

    // A megabyte to the runner's echo server and back, checked byte for byte; whether it came back whole.
    private static bool Outbound(SocketClient client)
    {
        var deadline = Clock.MonotonicAfter(WAIT_SECONDS);
        var error = client.Open(SocketMessages.STREAM, out var socket);
        if (error == 0)
            error = client.ConnectTo(socket, SocketAddress.Inet(ECHO, ECHO_PORT));
        if (error is not 0 and not NetworkErrors.IN_PROGRESS)
            return Failed($"a connection to {ECHO}:{ECHO_PORT} failed at once, errno {error}");
        while ((client.Poll(socket) & (NetworkStack.WRITABLE | NetworkStack.ERROR)) == 0)
        {
            if (client.WaitEvent(deadline) is null)
                return Failed($"no connection to {ECHO}:{ECHO_PORT} within {WAIT_SECONDS} s");
        }
        error = client.GetOption(socket, SocketMessages.SOCKET_LEVEL, SocketMessages.ERROR, out var pending);
        if (error != 0 || pending != 0)
            return Failed($"the connection to {ECHO}:{ECHO_PORT} failed, errno {(error != 0 ? error : (int)pending)}");
        var data = Pattern(ECHO_BYTES);
        var buffer = new byte[SocketClient.WINDOW_BYTES];
        int sent = 0, received = 0;
        var started = Clock.Monotonic();
        while (received < ECHO_BYTES)
        {
            var progress = false;
            if (sent < ECHO_BYTES)
            {
                error = client.Send(socket, data.AsSpan(sent, Math.Min(SocketClient.WINDOW_BYTES, ECHO_BYTES - sent)), SocketAddress.None, out var count);
                if (error == 0 && count > 0)
                {
                    sent += count;
                    progress = true;
                }
                else if (error is not 0 and not NetworkErrors.AGAIN)
                {
                    return Failed($"a send to {ECHO}:{ECHO_PORT} failed after {sent} bytes, errno {error}");
                }
            }
            error = client.Receive(socket, buffer, out var got, out _);
            if (error == 0)
            {
                if (got == 0)
                    return Failed($"the echo server ended the stream after {received} bytes");
                if (!buffer.AsSpan(0, got).SequenceEqual(data.AsSpan(received, got)))
                    return Failed($"the echo changed bytes after {received} bytes");
                received += got;
                progress = true;
            }
            else if (error != NetworkErrors.AGAIN)
            {
                return Failed($"a receive from {ECHO}:{ECHO_PORT} failed after {received} bytes, errno {error}");
            }
            if (!progress && client.WaitEvent(deadline) is null)
                return Failed($"{received} of {ECHO_BYTES} bytes came back within {WAIT_SECONDS} s");
        }
        var milliseconds = (Clock.Monotonic() - started) * 1000 / Clock.Frequency();
        client.Close(socket);
        Console.WriteLine($"{PREFIX}network: {ECHO_BYTES} bytes echoed by the runner through {ECHO}:{ECHO_PORT} over TCP, unchanged, " +
            $"in {milliseconds} ms of the guest's time");
        return true;
    }

    // A listening socket the runner connects to through the port forward; what the runner sends goes back.
    private static void Inbound(SocketClient client)
    {
        var deadline = Clock.MonotonicAfter(WAIT_SECONDS);
        var error = client.Open(SocketMessages.STREAM, out var listener);
        if (error == 0)
            error = client.SetOption(listener, SocketMessages.SOCKET_LEVEL, SocketMessages.REUSE_ADDRESS, 1);
        if (error == 0)
            error = client.Bind(listener, SocketAddress.Inet(Ipv4Address.Any, LISTEN_PORT));
        if (error == 0)
            error = client.Listen(listener, 1);
        if (error != 0)
        {
            Failed($"a socket listening on port {LISTEN_PORT} failed, errno {error}");
            return;
        }
        Console.WriteLine($"{PREFIX}network: listening on port {LISTEN_PORT} for the runner");
        uint connection;
        SocketAddress peer;
        while ((error = client.Accept(listener, out connection, out peer)) == NetworkErrors.AGAIN)
        {
            if (client.WaitEvent(deadline) is null)
            {
                Failed($"the runner did not connect to port {LISTEN_PORT} within {WAIT_SECONDS} s");
                return;
            }
        }
        if (error != 0)
        {
            Failed($"an accept on port {LISTEN_PORT} failed, errno {error}");
            return;
        }
        var buffer = new byte[SocketClient.WINDOW_BYTES];
        int held = 0, offset = 0;
        long echoed = 0;
        for (; ; )
        {
            var progress = false;
            if (held == 0)
            {
                error = client.Receive(connection, buffer, out held, out _);
                if (error == 0 && held == 0)
                    break; // the runner ended its sending
                if (error == 0)
                {
                    offset = 0;
                    progress = true;
                }
                else if (error != NetworkErrors.AGAIN)
                {
                    Failed($"a receive from the runner failed after {echoed} bytes, errno {error}");
                    return;
                }
            }
            else
            {
                error = client.Send(connection, buffer.AsSpan(offset, held - offset), SocketAddress.None, out var count);
                if (error == 0 && count > 0)
                {
                    offset += count;
                    echoed += count;
                    if (offset == held)
                        held = 0;
                    progress = true;
                }
                else if (error is not 0 and not NetworkErrors.AGAIN)
                {
                    Failed($"a send to the runner failed after {echoed} bytes, errno {error}");
                    return;
                }
            }
            if (!progress && client.WaitEvent(deadline) is null)
            {
                Failed($"the runner's exchange did not end within {WAIT_SECONDS} s, {echoed} bytes echoed");
                return;
            }
        }
        client.Shutdown(connection, SocketMessages.SHUT_WRITE);
        client.Close(connection);
        client.Close(listener);
        Console.WriteLine($"{PREFIX}network: echoed {echoed} bytes back to the runner's connection from {peer.AddressText}:{peer.Port}, " +
            "inbound through the port forward");
    }

    // Bytes that tell their offsets apart: a reordered or shifted echo changes them.
    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)(i * 31 + (i >> 8) * 7 + (i >> 16));
        return bytes;
    }

    private static bool Failed(string reason)
    {
        Console.WriteLine($"{PREFIX}network TCP probe: {reason}");
        return false;
    }

    #endregion
}
