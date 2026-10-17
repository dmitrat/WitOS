using System.Net;
using System.Net.Sockets;
using System.Text;
using OutWit.OS.Display;

namespace WitOS.Dev.Host;

/// <summary>
/// The runner's side of the guest's terminal (plan step N4.3a): a loopback listener QEMU's character device connects
/// to (server=off, as the QMP control's does, so that no released port races), everything the guest wrote kept as the
/// transcript and written to the terminal log, and the request's script typed into the guest, each input once the
/// line it waits for appeared in the transcript after the previous input's. The bytes also paint a
/// <see cref="TerminalScreen"/> (C1.2), whose answers to the guest's queries — the cursor position report .NET's Console
/// and PSReadLine wait for — go back before anything the script types, unless the link is a mirror of a display that
/// answers them itself (C2.3), since two answers garble the input.
/// </summary>
internal sealed class TerminalLink : IDisposable
{
    #region Constants

    /// <summary>
    /// The transcript's bound: a terminal that writes more than this is broken.
    /// </summary>
    private const int TRANSCRIPT_LIMIT = 4 * 1024 * 1024;

    private const int ACCEPT_SECONDS = 60;

    #endregion

    #region Fields

    private readonly TcpListener m_listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource m_lifetime = new();
    private readonly StringBuilder m_transcript = new();
    private readonly TerminalScreen m_screen = new();
    private readonly IReadOnlyList<TerminalInput> m_script;
    private readonly string m_logPath;
    private readonly bool m_answersQueries;
    private readonly Task m_pump;
    private int m_next;
    private int m_searchFrom;
    private bool m_disposed;

    #endregion

    #region Constructors

    /// <summary>
    /// Opens the listener and starts the pump, which waits for QEMU to connect.
    /// </summary>
    /// <param name="script">The inputs to type, in order.</param>
    /// <param name="logPath">The terminal log the transcript is written to as it grows.</param>
    /// <param name="answersQueries">Whether the screen's answers to the guest's queries are typed; false for the mirror of
    /// a display, which answers them.</param>
    public TerminalLink(IReadOnlyList<TerminalInput> script, string logPath, bool answersQueries = true)
    {
        m_script = script;
        m_logPath = logPath;
        m_answersQueries = answersQueries;
        if (File.Exists(logPath))
            File.Delete(logPath);
        m_listener.Start();
        var port = ((IPEndPoint)m_listener.LocalEndpoint).Port;
        Argument = $"socket,id=term0,host=127.0.0.1,port={port},server=off";
        m_pump = Task.Run(PumpAsync);
    }

    #endregion

    #region Functions

    private async Task PumpAsync()
    {
        var token = m_lifetime.Token;
        TcpClient client;
        try
        {
            using var accepting = CancellationTokenSource.CreateLinkedTokenSource(token);
            accepting.CancelAfter(TimeSpan.FromSeconds(ACCEPT_SECONDS));
            client = await m_listener.AcceptTcpClientAsync(accepting.Token);
        }
        catch (OperationCanceledException)
        {
            return; // no QEMU connected: the boot failed before, or the device is not there
        }
        using (client)
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            using var log = new FileStream(m_logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var buffer = new byte[4096];
            try
            {
                for (; ; )
                {
                    var count = await stream.ReadAsync(buffer, token);
                    if (count == 0)
                        return;
                    await log.WriteAsync(buffer.AsMemory(0, count), token);
                    await log.FlushAsync(token);
                    string typed;
                    lock (m_transcript)
                    {
                        if (m_transcript.Length + count > TRANSCRIPT_LIMIT)
                            return;
                        m_transcript.Append(Encoding.UTF8.GetString(buffer, 0, count));
                        var answers = m_screen.Feed(buffer.AsSpan(0, count));
                        typed = (m_answersQueries ? answers : "") + NextInput(); // the terminal's answers, then the script
                    }
                    if (typed.Length > 0)
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(typed), token);
                        await stream.FlushAsync(token);
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The boot ended, or QEMU closed the device.
            }
        }
    }

    // The inputs whose lines appeared, in order, each searched for after the previous one's line; under the lock.
    private string NextInput()
    {
        var typed = new StringBuilder();
        while (m_next < m_script.Count)
        {
            var text = m_transcript.ToString();
            var at = text.IndexOf(m_script[m_next].After, m_searchFrom, StringComparison.Ordinal);
            if (at < 0)
                break;
            m_searchFrom = at + m_script[m_next].After.Length;
            typed.Append(m_script[m_next].Text);
            m_next++;
        }
        return typed.ToString();
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (m_disposed)
            return;
        m_disposed = true;
        m_lifetime.Cancel();
        m_listener.Stop();
        try
        {
            m_pump.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The pump ended with the cancellation.
        }
        m_lifetime.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// QEMU's -chardev argument for the device.
    /// </summary>
    public string Argument { get; }

    /// <summary>
    /// Everything the guest wrote to the terminal so far.
    /// </summary>
    public string Transcript
    {
        get
        {
            lock (m_transcript)
                return m_transcript.ToString();
        }
    }

    /// <summary>
    /// The screen the guest's bytes painted so far, as <see cref="TerminalScreen.Text"/> gives it.
    /// </summary>
    public string Screen
    {
        get
        {
            lock (m_transcript)
                return m_screen.Text;
        }
    }

    /// <summary>
    /// The inputs typed so far.
    /// </summary>
    public int Typed
    {
        get
        {
            lock (m_transcript)
                return m_next;
        }
    }

    #endregion
}

/// <summary>
/// One input the runner types into the guest's terminal once a line appeared in the terminal's output.
/// </summary>
/// <param name="After">The text the terminal must have written, after the previous input's.</param>
/// <param name="Text">What to type then, with its own newline.</param>
internal sealed record TerminalInput(string After, string Text);
