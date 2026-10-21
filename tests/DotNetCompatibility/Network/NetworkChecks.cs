using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace DotNetCompatibility;

/// <summary>
/// Networking over the loopback (plan steps W2.1b and W2.2): TCP through TcpListener, TcpClient and NetworkStream,
/// asynchronous and synchronous Socket calls with their timeouts and options, UDP through UdpClient, a refused
/// connection, the end of a stream, name resolution, the IPv6 the platform offers, HttpClient against a server of
/// the suite's own, and TLS (W3.1): SslStream on both ends and HttpClient over HTTPS with a certificate of the suite's own. Every check stays on the machine, so that it means the same on a host and in a guest whatever
/// network reaches them.
/// </summary>
internal static class NetworkChecks
{
    private const int BYTES = 1024 * 1024;

    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("Network", "TcpEcho", async () =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var echo = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                var stream = accepted.GetStream();
                var buffer = new byte[16 * 1024];
                for (int count; (count = await stream.ReadAsync(buffer)) > 0;)
                    await stream.WriteAsync(buffer.AsMemory(0, count));
            });
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var data = new byte[BYTES];
            new Random(5).NextBytes(data);
            var network = client.GetStream();
            var writing = Task.Run(async () =>
            {
                await network.WriteAsync(data);
                client.Client.Shutdown(SocketShutdown.Send);
            });
            var back = new byte[BYTES];
            var received = 0;
            for (int count; received < BYTES && (count = await network.ReadAsync(back.AsMemory(received))) > 0;)
                received += count;
            await writing;
            await echo;
            return CheckResult.That(received == BYTES && back.AsSpan().SequenceEqual(data),
                $"{received} of {BYTES} bytes came back through the loopback's port {port}");
        });
        yield return new Check("Network", "SocketSync", () =>
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(listener.LocalEndPoint!);
            using var server = listener.Accept();
            var sent = client.Send("hello"u8);
            var buffer = new byte[16];
            var received = server.Receive(buffer);
            server.ReceiveTimeout = 200;
            SocketError timedOut = SocketError.Success;
            try
            {
                server.Receive(buffer);
            }
            catch (SocketException exception)
            {
                timedOut = exception.SocketErrorCode;
            }
            return CheckResult.Now(sent == 5 && received == 5 && buffer.AsSpan(0, 5).SequenceEqual("hello"u8) &&
                timedOut == SocketError.TimedOut, $"sent {sent}, received {received}, a timed out receive: {timedOut}");
        });
        yield return new Check("Network", "SocketOptions", () =>
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            var reuse = (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress)! != 0;
            return CheckResult.Now(socket.NoDelay && reuse && socket.ReceiveBufferSize > 0 && socket.SendBufferSize > 0,
                $"NoDelay {socket.NoDelay}, ReuseAddress {reuse}, buffers {socket.ReceiveBufferSize}/{socket.SendBufferSize}");
        });
        yield return new Check("Network", "UdpEcho", async () =>
        {
            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var to = (IPEndPoint)receiver.Client.LocalEndPoint!;
            await sender.SendAsync("datagram"u8.ToArray(), to);
            var result = await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var from = (IPEndPoint)sender.Client.LocalEndPoint!;
            return CheckResult.That(result.Buffer.AsSpan().SequenceEqual("datagram"u8) && result.RemoteEndPoint.Equals(from),
                $"{result.Buffer.Length} bytes from {result.RemoteEndPoint}");
        });
        yield return new Check("Network", "ConnectionRefused", async () =>
        {
            // A port nobody listens on: a listener's, closed again.
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return CheckResult.That(false, "connected to a port nobody listens on");
            }
            catch (SocketException exception)
            {
                return CheckResult.That(exception.SocketErrorCode == SocketError.ConnectionRefused, $"{exception.SocketErrorCode}");
            }
        });
        yield return new Check("Network", "EndOfStream", async () =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var server = await listener.AcceptTcpClientAsync();
            server.Client.Shutdown(SocketShutdown.Send);
            var read = await client.GetStream().ReadAsync(new byte[8]);
            return CheckResult.That(read == 0, $"a read after the peer's shutdown returned {read}");
        });
        yield return new Check("Network", "ResolveLocalhost", async () =>
        {
            var addresses = await Dns.GetHostAddressesAsync("localhost");
            return CheckResult.That(addresses.Any(IPAddress.IsLoopback), string.Join(", ", addresses.Select(address => address.ToString())));
        });
        yield return new Check("Network", "IPv6", () =>
            CheckResult.Now(Socket.OSSupportsIPv6, $"OSSupportsIPv4 {Socket.OSSupportsIPv4}, OSSupportsIPv6 {Socket.OSSupportsIPv6}"));
        // HttpClient over the loopback (W2.2), against the suite's own server, with no proxy whatever the environment names.
        yield return new Check("Network", "HttpGet", async () =>
        {
            await using var server = new LoopbackHttpServer();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var response = await client.GetAsync(new Uri(server.Address, "text"));
            var text = await response.Content.ReadAsStringAsync();
            var type = response.Content.Headers.ContentType?.MediaType;
            return CheckResult.That(response.StatusCode == HttpStatusCode.OK && text == LoopbackHttpServer.TEXT && type == "text/plain",
                $"{(int)response.StatusCode}, {text.Length} characters of {type}");
        });
        yield return new Check("Network", "HttpPostChunked", async () =>
        {
            await using var server = new LoopbackHttpServer();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            var data = new byte[256 * 1024];
            new Random(7).NextBytes(data);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            HttpResponseMessage response;
            byte[] back;
            try
            {
                response = await client.PostAsync(new Uri(server.Address, "echo"), new ByteArrayContent(data), limit.Token);
                back = await response.Content.ReadAsByteArrayAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                return CheckResult.That(false, $"no response within 40 s; the server: {server.Journal}");
            }
            using var _ = response;
            return CheckResult.That(response.StatusCode == HttpStatusCode.OK && response.Headers.TransferEncodingChunked == true &&
                back.AsSpan().SequenceEqual(data),
                $"{(int)response.StatusCode}, {back.Length} of {data.Length} bytes back, chunked {response.Headers.TransferEncodingChunked}");
        });
        yield return new Check("Network", "HttpKeepAlive", async () =>
        {
            await using var server = new LoopbackHttpServer();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            for (var i = 0; i < 3; i++)
                await client.GetStringAsync(new Uri(server.Address, "text"));
            using var absent = await client.GetAsync(new Uri(server.Address, "absent"));
            return CheckResult.That(server.Connections == 1 && absent.StatusCode == HttpStatusCode.NotFound,
                $"4 requests over {server.Connections} connection(s), the last answered {(int)absent.StatusCode}");
        });
        // TLS (W3.1): SslStream on both ends of a loopback connection with a certificate of the suite's own, which the
        // client trusts by its thumbprint, and HttpClient over HTTPS against the suite's server with the same trust.
        yield return new Check("Network", "TlsEcho", async () =>
        {
            using var authority = TestCertificate.Authority("WitOS compatibility CA");
            using var certificate = TestCertificate.Server(authority);
            var expected = TestCertificate.Thumbprint(certificate);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var data = new byte[BYTES];
            new Random(13).NextBytes(data);
            var echo = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                await using var server = new SslStream(accepted.GetStream(), leaveInnerStreamOpen: false);
                await server.AuthenticateAsServerAsync(certificate, clientCertificateRequired: false, checkCertificateRevocation: false);
                var buffer = new byte[16 * 1024];
                var total = 0;
                for (int count; total < BYTES && (count = await server.ReadAsync(buffer)) > 0;)
                {
                    total += count;
                    await server.WriteAsync(buffer.AsMemory(0, count));
                }
                return server.SslProtocol;
            });
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await using var secure = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                (_, remote, _, _) => TestCertificate.Thumbprint(remote) == expected);
            await secure.AuthenticateAsClientAsync(TestCertificate.SERVER_NAME);
            var writing = secure.WriteAsync(data).AsTask();
            var back = new byte[BYTES];
            var received = 0;
            for (int count; received < BYTES && (count = await secure.ReadAsync(back.AsMemory(received))) > 0;)
                received += count;
            await writing;
            var serverProtocol = await echo;
            return CheckResult.That(received == BYTES && back.AsSpan().SequenceEqual(data) && secure.IsEncrypted && secure.IsAuthenticated,
                $"{received} of {BYTES} bytes back over {secure.SslProtocol} ({secure.NegotiatedCipherSuite}), the server saw {serverProtocol}");
        });
        yield return new Check("Network", "HttpsGet", async () =>
        {
            using var authority = TestCertificate.Authority("WitOS compatibility CA");
            using var certificate = TestCertificate.Server(authority);
            var expected = TestCertificate.Thumbprint(certificate);
            await using var server = new LoopbackHttpServer(certificate);
            using var client = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, remote, _, _) => TestCertificate.Thumbprint(remote) == expected
            });
            using var response = await client.GetAsync(new Uri(server.Address, "text"));
            var text = await response.Content.ReadAsStringAsync();
            return CheckResult.That(response.StatusCode == HttpStatusCode.OK && text == LoopbackHttpServer.TEXT && server.Address.Scheme == "https",
                $"{(int)response.StatusCode} over {server.Address.Scheme}, {text.Length} characters; the server: {server.Journal}");
        });
    }
}
