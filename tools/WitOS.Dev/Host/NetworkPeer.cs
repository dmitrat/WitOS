using System.Net;
using System.Net.Sockets;

namespace WitOS.Dev.Host;

/// <summary>
/// The runner's side of the guest's network (plan step W1.2b): an echo server on the host's loopback, which the guest
/// reaches at <see cref="ECHO_ADDRESS"/>:<see cref="ECHO_PORT"/> through QEMU's guestfwd whatever port the server got, and
/// a client that connects to a guest's listening socket through hostfwd and requires its bytes echoed back whole.
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

    private const int BUFFER_BYTES = 64 * 1024;

    #endregion

    #region Fields

    private readonly TcpListener m_listener;
    private readonly CancellationTokenSource m_stop = new();
    private readonly Task m_serving;
    private long m_echoed;

    #endregion

    #region Constructors

    /// <summary>
    /// Starts the echo server and picks a free host port for the forward.
    /// </summary>
    /// <param name="guestPort">The guest's port the forward reaches; zero for none.</param>
    public NetworkPeer(ushort guestPort)
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
        try
        {
            m_serving.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
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
    /// The options of QEMU's user networking that reach the echo server and the guest's port.
    /// </summary>
    public string NetdevOptions => $",guestfwd=tcp:{ECHO_ADDRESS}:{ECHO_PORT}-tcp:127.0.0.1:{EchoPort}" +
        (GuestPort != 0 ? $",hostfwd=tcp:127.0.0.1:{ForwardPort}-:{GuestPort}" : "");

    #endregion
}
