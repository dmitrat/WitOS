using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Host;

/// <summary>
/// The runner's side of the guest's network (plan step W1.2b): an echo server on the host's loopback, which the guest
/// reaches at <see cref="ECHO_ADDRESS"/>:<see cref="ECHO_PORT"/> through QEMU's guestfwd whatever port the server got, and
/// a client that connects to a guest's listening socket through hostfwd and requires its bytes echoed back whole; and,
/// given a <see cref="RunnerAuthority"/> (plan step W3.1), an HTTPS server at <see cref="ECHO_ADDRESS"/>:<see cref="HTTPS_PORT"/>
/// with the authority's server certificate, which answers every request with <see cref="HTTPS_TEXT"/>, so that a guest
/// trusting the authority through its root store reaches it as a public site.
/// </summary>
internal sealed class NetworkPeer : IDisposable
{
    #region Constants

    /// <summary>
    /// The address in the guest's network that QEMU forwards to the echo server.
    /// </summary>
    public const string ECHO_ADDRESS = "10.0.2.100";

    /// <summary>
    /// The port there: echo's.
    /// </summary>
    public const int ECHO_PORT = 7;

    /// <summary>
    /// The port of the HTTPS server at the same address.
    /// </summary>
    public const int HTTPS_PORT = 443;

    /// <summary>
    /// What the HTTPS server answers every request with.
    /// </summary>
    public const string HTTPS_TEXT = "WitOS runner over HTTPS.";

    private const int BUFFER_BYTES = 64 * 1024;

    #endregion

    #region Fields

    private readonly TcpListener m_listener;
    private readonly TcpListener? m_https;
    private readonly RunnerAuthority? m_authority;
    private readonly CancellationTokenSource m_stop = new();
    private readonly Task m_serving;
    private readonly Task? m_servingHttps;
    private long m_echoed;
    private int m_httpsRequests;
    private HttpClient? m_http;

    #endregion

    #region Constructors

    /// <summary>
    /// Starts the echo server and picks a free host port for the forward.
    /// </summary>
    /// <param name="guestPort">The guest's port the forward reaches; zero for none.</param>
    /// <param name="authority">The authority whose server certificate the HTTPS server serves with; none for no server.</param>
    public NetworkPeer(ushort guestPort, RunnerAuthority? authority = null)
    {
        m_listener = new TcpListener(IPAddress.Loopback, 0);
        m_listener.Start();
        EchoPort = ((IPEndPoint)m_listener.LocalEndpoint).Port;
        GuestPort = guestPort;
        if (guestPort != 0)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            ForwardPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        m_serving = ServeAsync(m_stop.Token);
        m_authority = authority;
        if (authority is not null)
        {
            m_https = new TcpListener(IPAddress.Loopback, 0);
            m_https.Start();
            HttpsPort = ((IPEndPoint)m_https.LocalEndpoint).Port;
            m_servingHttps = ServeHttpsAsync(m_https, authority, m_stop.Token);
        }
    }

    #endregion

    #region Functions

    /// <summary>
    /// Connects to the guest's listening socket through the forward, sends bytes, ends its sending and requires the same
    /// bytes back before the guest ends its own.
    /// </summary>
    /// <param name="bytes">The bytes to send.</param>
    /// <param name="stop">Ends the exchange.</param>
    /// <exception cref="IOException">The connection failed or the bytes did not come back whole.</exception>
    public async Task ExchangeAsync(int bytes, CancellationToken stop)
    {
        var data = new byte[bytes];
        new Random(bytes).NextBytes(data);
        var back = new byte[bytes];
        var received = 0;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ForwardPort, stop);
            var stream = client.GetStream();
            var sending = Task.Run(async () =>
            {
                await stream.WriteAsync(data, stop);
                client.Client.Shutdown(SocketShutdown.Send);
            }, stop);
            var buffer = new byte[BUFFER_BYTES];
            for (int count; (count = await stream.ReadAsync(buffer, stop)) > 0;)
            {
                if (received + count > bytes)
                    throw new IOException($"the guest sent more than the {bytes} bytes it was sent");
                buffer.AsSpan(0, count).CopyTo(back.AsSpan(received));
                received += count;
            }
            await sending;
        }
        catch (SocketException exception)
        {
            throw new IOException($"the connection to 127.0.0.1:{ForwardPort}, forwarded to the guest's port {GuestPort}, failed: {exception.Message}");
        }
        if (received != bytes || !back.AsSpan().SequenceEqual(data))
            throw new IOException($"the guest echoed {received} of {bytes} bytes{(received == bytes ? ", changed" : "")}");
        Console.WriteLine($"The runner's {bytes} bytes came back whole from the guest's port {GuestPort} through 127.0.0.1:{ForwardPort}.");
    }

    /// <summary>
    /// Sends an HTTP request to the guest's listening socket through the forward (W3.2) and requires an answer of 200 whose
    /// body holds the action's text; a POST sends the text as its body.
    /// </summary>
    /// <param name="argument">The action's argument: the method, the path and the text (<see cref="BootAction.Http"/>).</param>
    /// <param name="stop">Ends the request.</param>
    /// <exception cref="IOException">The request failed or the answer is not the expected one.</exception>
    public async Task HttpAsync(string argument, CancellationToken stop)
    {
        var (method, path, text) = BootAction.ParseHttp(argument);
        var address = $"http://127.0.0.1:{ForwardPort}{path}";
        try
        {
            // One client for the boot: Kestrel keeps the connection alive, so the requests after the first reuse it.
            m_http ??= new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(300) };
            using var request = new HttpRequestMessage(new HttpMethod(method), address);
            if (method == "POST")
                request.Content = new StringContent(text, Encoding.UTF8);
            using var response = await m_http.SendAsync(request, stop);
            var body = await response.Content.ReadAsStringAsync(stop);
            if (!response.IsSuccessStatusCode || !body.Contains(text, StringComparison.Ordinal))
                throw new IOException($"{method} {path} on the guest's port {GuestPort} answered {(int)response.StatusCode} with {body.Length} characters, without \"{text}\"");
            Console.WriteLine($"The guest's port {GuestPort} answered {method} {path} with {(int)response.StatusCode} and {body.Length} characters through 127.0.0.1:{ForwardPort}.");
        }
        catch (HttpRequestException exception)
        {
            throw new IOException($"{method} {address}, forwarded to the guest's port {GuestPort}, failed: {exception.Message}");
        }
        catch (TaskCanceledException) when (!stop.IsCancellationRequested)
        {
            throw new IOException($"{method} {address}, forwarded to the guest's port {GuestPort}, was not answered in time");
        }
    }

    // Echoes every connection QEMU's guestfwd makes until the runner stops.
    private async Task ServeAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await m_listener.AcceptTcpClientAsync(stop);
                _ = EchoAsync(client, stop);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    // Answers every request of every connection over TLS with the text until the runner stops; a connection that fails its
    // handshake or its request ends alone, since the guest's checks report what they saw.
    private async Task ServeHttpsAsync(TcpListener listener, RunnerAuthority authority, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                _ = AnswerHttpsAsync(client, authority, stop);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task AnswerHttpsAsync(TcpClient client, RunnerAuthority authority, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                await using var secure = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = authority.Server, ClientCertificateRequired = false }, stop);
                var head = new List<byte>();
                var one = new byte[1];
                while (true)
                {
                    while (await secure.ReadAsync(one, stop) == 1)
                    {
                        head.Add(one[0]);
                        if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n')
                            break;
                    }
                    if (head.Count < 4)
                        return;
                    head.Clear();
                    Interlocked.Increment(ref m_httpsRequests);
                    var body = Encoding.UTF8.GetBytes(HTTPS_TEXT);
                    await secure.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\n\r\n"), stop);
                    await secure.WriteAsync(body, stop);
                    await secure.FlushAsync(stop);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException or AuthenticationException)
            {
            }
        }
    }

    private async Task EchoAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[BUFFER_BYTES];
                for (int count; (count = await stream.ReadAsync(buffer, stop)) > 0;)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, count), stop);
                    Interlocked.Add(ref m_echoed, count);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        m_stop.Cancel();
        m_listener.Stop();
        m_https?.Stop();
        try
        {
            m_serving.Wait(TimeSpan.FromSeconds(5));
            m_servingHttps?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
        m_http?.Dispose();
        m_stop.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The echo server's port on the host's loopback.
    /// </summary>
    public int EchoPort { get; }

    /// <summary>
    /// The host's loopback port forwarded to <see cref="GuestPort"/>; zero without a forward.
    /// </summary>
    public int ForwardPort { get; }

    /// <summary>
    /// The guest's port the forward reaches; zero for none.
    /// </summary>
    public ushort GuestPort { get; }

    /// <summary>
    /// The bytes the echo server sent back.
    /// </summary>
    public long Echoed => Interlocked.Read(ref m_echoed);

    /// <summary>
    /// The HTTPS server's port on the host's loopback; zero without a server.
    /// </summary>
    public int HttpsPort { get; }

    /// <summary>
    /// The requests the HTTPS server answered.
    /// </summary>
    public int HttpsRequests => Volatile.Read(ref m_httpsRequests);

    /// <summary>
    /// The options of QEMU's user networking that reach the echo server and the guest's port.
    /// </summary>
    public string NetdevOptions => $",guestfwd=tcp:{ECHO_ADDRESS}:{ECHO_PORT}-tcp:127.0.0.1:{EchoPort}" +
        (m_authority is not null ? $",guestfwd=tcp:{ECHO_ADDRESS}:{HTTPS_PORT}-tcp:127.0.0.1:{HttpsPort}" : "") +
        (GuestPort != 0 ? $",hostfwd=tcp:127.0.0.1:{ForwardPort}-:{GuestPort}" : "");

    #endregion
}
