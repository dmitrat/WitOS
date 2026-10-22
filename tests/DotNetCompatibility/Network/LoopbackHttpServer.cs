using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DotNetCompatibility;

/// <summary>
/// A small HTTP/1.1 server on the loopback for the HTTP checks (plan step W2.2), written over TcpListener so that the
/// checks test HttpClient alone: GET /text answers a fixed text with its Content-Length, POST /echo answers the request's
/// body chunked, any other path 404. A connection serves requests until the client closes it, and the server counts the
/// connections, so that a check sees whether HttpClient keeps one alive. Given a certificate (plan step W3.1), it
/// serves the same over TLS, so that a check tests HttpClient over HTTPS against a key of the suite's own.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    /// <summary>
    /// The body of GET /text.
    /// </summary>
    public const string TEXT = "Hello over HTTP from the loopback.";

    private const int CHUNK_BYTES = 16 * 1024;

    private readonly TcpListener m_listener = new(IPAddress.Loopback, 0);

    private readonly Task m_accepting;

    private int m_connections;

    private long m_read; // the bytes every connection read

    private readonly ConcurrentQueue<string> m_journal = new(); // how each connection ended, for a check that failed

    private readonly X509Certificate2? m_certificate; // the server's certificate, when it serves over TLS

    public LoopbackHttpServer(X509Certificate2? certificate = null)
    {
        m_certificate = certificate;
        m_listener.Start();
        m_accepting = AcceptAsync();
    }

    public async ValueTask DisposeAsync()
    {
        m_listener.Stop();
        try
        {
            await m_accepting;
        }
        catch (Exception)
        {
            // The accept the stop ended.
        }
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await m_listener.AcceptTcpClientAsync();
            }
            catch (Exception exception)
            {
                // Disposed when the check ends; anything else is the journal's, which a failed check reports.
                if (exception is not (ObjectDisposedException or SocketException { SocketErrorCode: SocketError.OperationAborted }))
                    m_journal.Enqueue($"accept after {Volatile.Read(ref m_connections)} connections: {exception.GetType().Name}: {exception.Message}");
                return;
            }
            _ = ServeAsync(client, Interlocked.Increment(ref m_connections));
        }
    }

    // The requests of one connection, until the client closes it or sends what the server does not read; the journal keeps
    // how it ended.
    private async Task ServeAsync(TcpClient client, int number)
    {
        using var connection = client;
        var served = 0;
        try
        {
            Stream stream = connection.GetStream();
            if (m_certificate is not null)
            {
                var secure = new SslStream(stream, leaveInnerStreamOpen: false);
                await secure.AuthenticateAsServerAsync(m_certificate, clientCertificateRequired: false, checkCertificateRevocation: false);
                stream = secure;
            }
            await using var _ = stream;
            while (await ReadHeadAsync(stream) is { } head)
            {
                ++served;
                var lines = head.Split("\r\n");
                var request = lines[0].Split(' ');
                var length = lines.Skip(1).Select(line => line.Split(':', 2))
                    .Where(field => field.Length == 2 && field[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    .Select(field => int.Parse(field[1].Trim())).FirstOrDefault();
                var body = new byte[length];
                await stream.ReadExactlyAsync(body);
                if (request is ["GET", "/text", _])
                    await WriteAsync(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(TEXT));
                else if (request is ["POST", "/echo", _])
                    await WriteChunkedAsync(stream, body);
                else
                    await WriteAsync(stream, "404 Not Found", "text/plain; charset=utf-8", []);
            }
            m_journal.Enqueue($"connection {number}: closed by the client after {served} requests");
        }
        catch (Exception exception)
        {
            m_journal.Enqueue($"connection {number}: {exception.GetType().Name} in request {served}: {exception.Message}");
        }
    }

    // A request's head up to its empty line, without it; null at the end of the stream before a request.
    private async Task<string?> ReadHeadAsync(Stream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            Interlocked.Increment(ref m_read);
            head.Add(one[0]);
            if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n')
                return Encoding.ASCII.GetString([.. head])[..^4];
        }
        return head.Count == 0 ? null : throw new IOException($"The connection ended after {head.Count} bytes of a request's head.");
    }

    private static async Task WriteAsync(Stream stream, string status, string type, byte[] body)
    {
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
    }

    private static async Task WriteChunkedAsync(Stream stream, byte[] body)
    {
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nTransfer-Encoding: chunked\r\n\r\n"u8.ToArray());
        for (var offset = 0; offset < body.Length; offset += CHUNK_BYTES)
        {
            var count = Math.Min(CHUNK_BYTES, body.Length - offset);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{count:x}\r\n"));
            await stream.WriteAsync(body.AsMemory(offset, count));
            await stream.WriteAsync("\r\n"u8.ToArray());
        }
        await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
    }

    /// <summary>
    /// The server's address, http://127.0.0.1:port/, or https:// when it serves over TLS.
    /// </summary>
    public Uri Address => new($"{(m_certificate is null ? "http" : "https")}://127.0.0.1:{((IPEndPoint)m_listener.LocalEndpoint).Port}/");

    /// <summary>
    /// The connections accepted so far.
    /// </summary>
    public int Connections => Volatile.Read(ref m_connections);

    /// <summary>
    /// How the connections that ended ended, in order.
    /// </summary>
    public string Journal => $"{Connections} accepted, {Interlocked.Read(ref m_read)} bytes of heads read; " +
        (m_journal.IsEmpty ? "no connection ended" : string.Join("; ", m_journal));
}
