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
/// The session serves a few commands while the guest runs (a screendump, a key, plan step C2.1) and the quit with its
/// diagnostic samples at the end; one command at a time.
/// </summary>
internal sealed class QemuControl : IDisposable
{
    #region Constants

    private const int DIAGNOSTIC_SAMPLES = 3;

    private const int REPLY_LIMIT = 65536;

    private const int TRANSCRIPT_LIMIT = 1048576;

    private const int REPLY_MESSAGES = 64;

    #endregion

    #region Fields

    private readonly TcpListener m_listener = new(IPAddress.Loopback, 0);

    private readonly CancellationTokenSource m_lifetime = new();

    private readonly Task<TcpClient> m_connected;

    private readonly StringBuilder m_transcript = new();

    private readonly SemaphoreSlim m_turn = new(1, 1);

    private NetworkStream? m_stream;

    private StreamReader? m_reader;

    private int m_serial;

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
        await m_turn.WaitAsync(token);
        try
        {
            await SessionAsync(token);
            // The guest ran past its deadline: before QEMU quits, the transcript records where a few samples find the
            // processor (plan step D5.2, where one process spun on a lock whose holder waited for a process the dispatcher
            // never ran). On x64 a sample also records the kernel stack's page above its stack pointer, whose top holds
            // the frame a system call or an interrupt saved, and the user stack that frame names. QEMU reads guest memory
            // without changing it.
            for (var sample = 0; sample < DIAGNOSTIC_SAMPLES; ++sample)
            {
                var stackPointer = Regex.Match(await HumanAsync("info registers", token), "RSP=([0-9a-f]{16})");
                if (!stackPointer.Success)
                    continue;
                var bottom = Convert.ToUInt64(stackPointer.Groups[1].Value, 16);
                var top = (bottom | 0xFFF) + 1;
                var words = Regex.Matches(await HumanAsync($"xp /{(top - bottom) / 8}gx 0x{bottom:x}", token), "0x([0-9a-f]{16})")
                    .Select(word => Convert.ToUInt64(word.Groups[1].Value, 16)).ToArray();
                // The saved frame's last five words: RIP, CS, RFLAGS, RSP, SS; CS 0x33 is user mode.
                if (words.Length >= 5 && words[^4] == 0x33)
                    await HumanAsync($"x /64gx 0x{words[^2]:x}", token);
            }
            // For quit, QEMU's SHUTDOWN event for it acknowledges too, since QEMU may close the connection before the
            // reply leaves (seen on a Linux host, plan step T2.1a).
            await ExecuteAsync("quit", null, token, "witos-quit", shutdownAcknowledges: true);
            QuitAcknowledged = true;
        }
        finally
        {
            m_turn.Release();
        }
    }

    /// <summary>
    /// Writes the display's current frame to a file (plan step C2.1): QEMU's screendump of its console as a binary PPM.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="cancellation">Stops the request.</param>
    internal async Task ScreendumpAsync(string path, CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, m_lifetime.Token);
        await m_turn.WaitAsync(stop.Token);
        try
        {
            await SessionAsync(stop.Token);
            await ExecuteAsync("screendump", new { filename = path.Replace('\\', '/'), format = "ppm" }, stop.Token);
        }
        finally
        {
            m_turn.Release();
        }
    }

    /// <summary>
    /// Presses and releases a key on the guest's keyboard (plan step C2.1): QEMU's input-send-event with a QKeyCode name,
    /// which reaches the board's keyboard device.
    /// </summary>
    /// <param name="key">The QKeyCode name, such as a or ret.</param>
    /// <param name="cancellation">Stops the request.</param>
    internal async Task SendKeyAsync(string key, CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, m_lifetime.Token);
        await m_turn.WaitAsync(stop.Token);
        try
        {
            await SessionAsync(stop.Token);
            object Event(bool down) => new { type = "key", data = new { down, key = new { type = "qcode", data = key } } };
            await ExecuteAsync("input-send-event", new { events = new[] { Event(true), Event(false) } }, stop.Token);
        }
        finally
        {
            m_turn.Release();
        }
    }

    #endregion

    #region Tools

    // The connection once QEMU connected, with the greeting read and the capabilities negotiated.
    private async Task SessionAsync(CancellationToken token)
    {
        if (m_stream is not null)
            return;
        var client = await m_connected.WaitAsync(token);
        client.NoDelay = true;
        m_stream = client.GetStream();
        m_reader = new StreamReader(m_stream, Encoding.UTF8, false, 1024, true);
        using (var greeting = await ReadAsync(token))
            if (!greeting.RootElement.TryGetProperty("QMP", out var qmp) || !qmp.TryGetProperty("version", out _))
                throw new InvalidDataException("Expected a QMP greeting.");
        await ExecuteAsync("qmp_capabilities", null, token, "witos-capabilities");
    }

    private async Task<JsonDocument> ReadAsync(CancellationToken token)
    {
        var text = new StringBuilder();
        var one = new char[1];
        for (; ; )
        {
            if (await m_reader!.ReadAsync(one.AsMemory(), token) == 0)
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

    // One command and its reply, matched by the id (the command's own name for the capabilities and the quit, which the
    // host tests know, a serial otherwise); events arriving meanwhile are recorded and skipped.
    private async Task<string> ExecuteAsync(string command, object? arguments, CancellationToken token, string? identifier = null,
        bool shutdownAcknowledges = false)
    {
        var id = identifier ?? $"witos-{m_serial++}";
        var line = arguments is null
            ? JsonSerializer.Serialize(new { execute = command, id })
            : JsonSerializer.Serialize(new { execute = command, arguments, id });
        Record("> " + line);
        await m_stream!.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), token);
        await m_stream.FlushAsync(token);
        for (var count = 0; count < REPLY_MESSAGES; ++count)
        {
            using var message = await ReadAsync(token);
            var value = message.RootElement;
            if (shutdownAcknowledges && value.TryGetProperty("event", out var name) && name.ValueKind == JsonValueKind.String &&
                name.GetString() == "SHUTDOWN" && value.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object && data.TryGetProperty("reason", out var reason) &&
                reason.ValueKind == JsonValueKind.String && reason.GetString() == "host-qmp-quit")
                return "";
            if (value.TryGetProperty("id", out var field) && field.ValueKind == JsonValueKind.String && field.GetString() == id)
            {
                if (value.TryGetProperty("error", out var error) || !value.TryGetProperty("return", out var result))
                    throw new InvalidDataException($"QMP rejected {command}: {(value.TryGetProperty("error", out error) ? error.GetRawText() : "no return")}");
                return result.ValueKind == JsonValueKind.String ? result.GetString()! : result.GetRawText();
            }
        }
        throw new InvalidDataException("QMP matching reply not received within message limit.");
    }

    private Task<string> HumanAsync(string command, CancellationToken token) =>
        ExecuteAsync("human-monitor-command", new Dictionary<string, string> { ["command-line"] = command }, token);

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
        m_reader?.Dispose();
        m_stream?.Dispose();
        _ = m_connected.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion)
                task.Result.Dispose();
            else
                _ = task.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        m_lifetime.Dispose();
        m_turn.Dispose();
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
