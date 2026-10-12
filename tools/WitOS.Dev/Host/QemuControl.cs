using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WitOS.Dev.Host;

/// <summary>
/// The parent owns a loopback listener before QEMU starts; no released-port race.
/// QEMU connects as a transport client but remains the QMP protocol server.
/// Keeping QMP off inherited stdin removes QEMU's synchronous pipe reader path.
/// </summary>
internal sealed class QemuControl : IDisposable
{
    #region Constants

    private const int DIAGNOSTIC_SAMPLES = 3;

    private const int REPLY_LIMIT = 65536;

    private const int TRANSCRIPT_LIMIT = 1048576;

    #endregion

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
    /// Records where the processor is in a few samples, then asks QEMU to quit through QMP after it connects.
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
                if (text.Length == REPLY_LIMIT)
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
        // The reply to a command; for quit, QEMU's SHUTDOWN event for it acknowledges too, since QEMU may close the
        // connection before the reply leaves (seen on a Linux host, plan step T2.1a).
        async Task Reply(string id, bool shutdownAcknowledges = false)
        {
            for (var count = 0; count < 32; ++count)
            {
                using var message = await Read();
                var value = message.RootElement;
                if (shutdownAcknowledges && value.TryGetProperty("event", out var name) && name.ValueKind == JsonValueKind.String &&
                    name.GetString() == "SHUTDOWN" && value.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object && data.TryGetProperty("reason", out var reason) &&
                    reason.ValueKind == JsonValueKind.String && reason.GetString() == "host-qmp-quit")
                    return;
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
        // The guest ran past its deadline: before QEMU quits, the transcript records where a few samples find the
        // processor (plan step D5.2, where one process spun on a lock whose holder waited for a process the dispatcher
        // never ran). On x64 a sample also records the kernel stack's page above its stack pointer, whose top holds
        // the frame a system call or an interrupt saved, and the user stack that frame names. QEMU reads guest memory
        // without changing it.
        var serial = 0;
        async Task<string> Human(string command)
        {
            var id = "witos-human-" + serial++;
            var line = JsonSerializer.Serialize(new
            {
                execute = "human-monitor-command",
                arguments = new Dictionary<string, string> { ["command-line"] = command },
                id,
            });
            Record("> " + line);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), token);
            await stream.FlushAsync(token);
            for (var count = 0; count < 32; ++count)
            {
                using var message = await Read();
                if (message.RootElement.TryGetProperty("id", out var field) && field.ValueKind == JsonValueKind.String &&
                    field.GetString() == id)
                    return message.RootElement.TryGetProperty("return", out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString()!
                        : "";
            }
            throw new InvalidDataException("QMP matching reply not received within message limit.");
        }
        for (var sample = 0; sample < DIAGNOSTIC_SAMPLES; ++sample)
        {
            var stackPointer = Regex.Match(await Human("info registers"), "RSP=([0-9a-f]{16})");
            if (!stackPointer.Success)
                continue;
            var bottom = Convert.ToUInt64(stackPointer.Groups[1].Value, 16);
            var top = (bottom | 0xFFF) + 1;
            var words = Regex.Matches(await Human($"xp /{(top - bottom) / 8}gx 0x{bottom:x}"), "0x([0-9a-f]{16})")
                .Select(word => Convert.ToUInt64(word.Groups[1].Value, 16)).ToArray();
            // The saved frame's last five words: RIP, CS, RFLAGS, RSP, SS; CS 0x33 is user mode.
            if (words.Length >= 5 && words[^4] == 0x33)
                await Human($"x /64gx 0x{words[^2]:x}");
        }
        await Send("quit", "witos-quit");
        await Reply("witos-quit", shutdownAcknowledges: true);
        QuitAcknowledged = true;
    }

    #endregion

    #region Tools

    private void Record(string line)
    {
        lock (m_transcript)
        {
            if (m_transcript.Length + line.Length + 1 > TRANSCRIPT_LIMIT)
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
