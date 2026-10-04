using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WitOS.Dev.Host;

/// <summary>
/// The parent owns a loopback listener before QEMU starts; no released-port race.
/// QEMU connects as a transport client but remains the QMP protocol server.
/// Keeping QMP off inherited stdin removes QEMU's synchronous pipe reader path.
/// </summary>
internal sealed class QemuControl : IDisposable
{
    #region Fields

    private readonly TcpListener m_listener = new(IPAddress.Loopback, 0);

    private readonly CancellationTokenSource m_lifetime = new();

    private readonly Task<TcpClient> m_connected;

    private readonly StringBuilder m_transcript = new();

    #endregion

    #region Constructors

    internal QemuControl()
    {
        m_listener.Start(1);
        var port = ((IPEndPoint)m_listener.LocalEndpoint).Port;
        Argument = $"tcp:127.0.0.1:{port},server=off";
        m_connected = m_listener.AcceptTcpClientAsync(m_lifetime.Token).AsTask();
    }

    #endregion

    #region Functions

    /// <summary>
    /// Asks QEMU to quit through QMP after it connects.
    /// </summary>
    /// <param name="cancellation">Stops the request.</param>
    internal async Task QuitAsync(CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, m_lifetime.Token);
        var token = stop.Token;
        var client = await m_connected.WaitAsync(token);
        client.NoDelay = true;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
        async Task<JsonDocument> Read()
        {
            var text = new StringBuilder();
            var one = new char[1];
            for (; ; )
            {
                if (await reader.ReadAsync(one.AsMemory(), token) == 0)
                    throw new IOException("QMP connection closed before reply.");
                if (one[0] == '\n')
                    break;
                if (text.Length == 16384)
                    throw new InvalidDataException("QMP reply exceeded its limit.");
                text.Append(one[0]);
            }
            var line = text.ToString().TrimEnd('\r');
            Record("< " + line);
            return JsonDocument.Parse(line);
        }
        async Task Send(string command, string id)
        {
            var line = JsonSerializer.Serialize(new { execute = command, id });
            Record("> " + line);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), token);
            await stream.FlushAsync(token);
        }
        async Task Reply(string id)
        {
            for (var count = 0; count < 32; ++count)
            {
                using var message = await Read();
                var value = message.RootElement;
                if (value.TryGetProperty("id", out var field) && field.ValueKind == JsonValueKind.String && field.GetString() == id)
                {
                    if (!value.TryGetProperty("return", out _) || value.TryGetProperty("error", out _))
                        throw new InvalidDataException("QMP rejected " + id);
                    return;
                }
            }
            throw new InvalidDataException("QMP matching reply not received within message limit.");
        }
        using (var greeting = await Read())
            if (!greeting.RootElement.TryGetProperty("QMP", out var qmp) || !qmp.TryGetProperty("version", out _))
                throw new InvalidDataException("Expected a QMP greeting.");
        await Send("qmp_capabilities", "witos-capabilities");
        await Reply("witos-capabilities");
        await Send("quit", "witos-quit");
        await Reply("witos-quit");
        QuitAcknowledged = true;
    }

    #endregion

    #region Tools

    private void Record(string line)
    {
        lock (m_transcript)
        {
            if (m_transcript.Length + line.Length + 1 > 65536)
                throw new InvalidDataException("QMP transcript exceeded limit.");
            m_transcript.AppendLine(line);
        }
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        m_lifetime.Cancel();
        m_listener.Stop();
        _ = m_connected.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion)
                task.Result.Dispose();
            else
                _ = task.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        m_lifetime.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// QEMU -qmp argument that connects to the loopback listener.
    /// </summary>
    internal string Argument { get; }

    /// <summary>
    /// Whether QEMU acknowledged the quit command.
    /// </summary>
    internal bool QuitAcknowledged { get; private set; }

    /// <summary>
    /// QMP messages exchanged so far.
    /// </summary>
    internal string Transcript { get { lock (m_transcript) return m_transcript.ToString(); } }

    #endregion
}
