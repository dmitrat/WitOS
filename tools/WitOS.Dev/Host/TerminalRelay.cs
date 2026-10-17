using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WitOS.Dev.Host;

/// <summary>
/// The guest's terminal on the host's console (plan step C1.4, the shell command): a loopback listener QEMU's character
/// device connects to, as <see cref="TerminalLink"/> opens one, whose bytes go to the host's standard output as they are
/// (the host's terminal interprets them and answers the guest's queries itself) and whose input is what the person
/// types, read key by key and sent as a terminal sends it: a printable character as its UTF-8, Enter as CR, Backspace
/// as DEL, the cursor keys as their escape sequences, a control key as its control character (Ctrl-C reaches the guest,
/// not the tool). Everything the guest wrote is also kept in the terminal log.
/// </summary>
internal sealed class TerminalRelay : IDisposable
{
    #region Constants

    private const int ACCEPT_SECONDS = 60;

    #endregion

    #region Fields

    private readonly TcpListener m_listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource m_lifetime = new();
    private readonly string m_logPath;
    private readonly Task m_pump;
    private readonly bool m_controlC;
    private bool m_disposed;

    #endregion

    #region Constructors

    /// <summary>
    /// Opens the listener and starts the pump, which waits for QEMU to connect.
    /// </summary>
    /// <param name="logPath">The terminal log the guest's bytes are written to as they come.</param>
    public TerminalRelay(string logPath)
    {
        m_logPath = logPath;
        if (File.Exists(logPath))
            File.Delete(logPath);
        m_controlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        m_listener.Start();
        var port = ((IPEndPoint)m_listener.LocalEndpoint).Port;
        Argument = $"socket,id=term0,host=127.0.0.1,port={port},server=off";
        m_pump = Task.Run(PumpAsync);
    }

    #endregion

    #region Functions

    /// <summary>
    /// What a terminal sends for a key: the escape sequences of the cursor and editing keys, CR for Enter, DEL for
    /// Backspace, the character otherwise (a control character for a control key), with ESC before it for Alt.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The bytes, none for a key a terminal does not send.</returns>
    public static byte[] Bytes(ConsoleKeyInfo key)
    {
        var text = key.Key switch
        {
            ConsoleKey.UpArrow => "\x1B[A",
            ConsoleKey.DownArrow => "\x1B[B",
            ConsoleKey.RightArrow => "\x1B[C",
            ConsoleKey.LeftArrow => "\x1B[D",
            ConsoleKey.Home => "\x1B[H",
            ConsoleKey.End => "\x1B[F",
            ConsoleKey.Insert => "\x1B[2~",
            ConsoleKey.Delete => "\x1B[3~",
            ConsoleKey.PageUp => "\x1B[5~",
            ConsoleKey.PageDown => "\x1B[6~",
            ConsoleKey.Enter => "\r",
            ConsoleKey.Backspace => "\x7F",
            ConsoleKey.Tab => "\t",
            ConsoleKey.Escape => "\x1B",
            _ => key.KeyChar == '\0' ? "" : key.KeyChar.ToString()
        };
        if (text.Length > 0 && (key.Modifiers & ConsoleModifiers.Alt) != 0 && key.Key != ConsoleKey.Escape)
            text = "\x1B" + text;
        return Encoding.UTF8.GetBytes(text);
    }

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
            using var output = Console.OpenStandardOutput();
            var keys = new Thread(() => Keys(stream, token)) { IsBackground = true, Name = "terminal keys" };
            keys.Start();
            var buffer = new byte[4096];
            try
            {
                for (; ; )
                {
                    var count = await stream.ReadAsync(buffer, token);
                    if (count == 0)
                        return;
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    await output.FlushAsync(token);
                    await log.WriteAsync(buffer.AsMemory(0, count), token);
                    await log.FlushAsync(token);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The boot ended, or QEMU closed the device.
            }
        }
    }

    // What the person types, key by key, to the guest; the thread ends with the process, since a blocked ReadKey has no
    // cancellation.
    private static void Keys(NetworkStream stream, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var bytes = Bytes(Console.ReadKey(intercept: true));
                if (bytes.Length > 0)
                {
                    stream.Write(bytes);
                    stream.Flush();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The session ended.
        }
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
        Console.TreatControlCAsInput = m_controlC;
        m_lifetime.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// QEMU's -chardev argument for the device.
    /// </summary>
    public string Argument { get; }

    #endregion
}
