using System;
using System.IO;
using System.Text;
using OutWit.OS.Resources;
using OutWit.OS.Terminals;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The terminal's probe (plan step N4.3a, as the storage probes of D2 to D4): the device manager connects to the
/// terminal service as a process would, asks for a line, and, when one is typed within the time, writes it back to
/// the terminal and to the log; the runtime-terminal scenario types the line and requires both. The attributes and
/// the size are reported too. A session a person works in (C2.7: its package holds /etc/witos/session) skips the probe,
/// whose wait would hold /bin/init back.
/// </summary>
internal static class TerminalProbe
{
    #region Constants

    /// <summary>
    /// The prompt the probe writes; the scenario's runner types after it.
    /// </summary>
    public const string PROMPT = "[TERMINAL] type a line";

    private const string PREFIX = "[DEVICES] ";

    private const int WAIT_SECONDS = 30;

    /// <summary>
    /// The package's mark of a session a person works in (C2.7).
    /// </summary>
    public const string SESSION_MARK = "/etc/witos/session";

    #endregion

    #region Functions

    /// <summary>
    /// Runs the probe.
    /// </summary>
    /// <param name="clients">The terminal service's clients' endpoint.</param>
    public static void Run(KernelHandle clients)
    {
        if (File.Exists(SESSION_MARK))
        {
            Console.WriteLine($"{PREFIX}terminal probe: skipped in a session ({SESSION_MARK})");
            return;
        }
        try
        {
            using var client = TerminalClient.Connect(clients);
            var size = client.GetSize(out var columns, out var rows);
            var attributes = client.GetAttributes(out var got);
            var canonical = got is not null && (got.Local & Termios.ICANON) != 0 && (got.Local & Termios.ECHO) != 0;
            Console.WriteLine($"{PREFIX}terminal probe: {(size == 0 ? $"{columns} by {rows}" : $"size errno {size}")}, " +
                $"{(attributes == 0 ? canonical ? "canonical with echo" : "attributes without ICANON or ECHO" : $"attributes errno {attributes}")}");
            var written = client.Write(Encoding.ASCII.GetBytes(PROMPT + "\n"));
            if (written != 0)
            {
                Console.WriteLine($"{PREFIX}terminal probe: the prompt was not written, errno {written}");
                return;
            }
            var deadline = Clock.MonotonicAfter(WAIT_SECONDS);
            for (; ; )
            {
                var kind = client.WaitEvent(deadline);
                if (kind == 0)
                {
                    Console.WriteLine($"{PREFIX}terminal probe: nothing typed within {WAIT_SECONDS} s");
                    return;
                }
                if (kind != TerminalMessages.EVENT_INPUT)
                    continue;
                var buffer = new byte[256];
                var error = client.Read(buffer, nonblocking: true, out var count);
                if (error == TerminalMessages.AGAIN)
                    continue;
                if (error != 0)
                {
                    Console.WriteLine($"{PREFIX}terminal probe: the read failed, errno {error}");
                    return;
                }
                var line = Encoding.UTF8.GetString(buffer, 0, count).TrimEnd('\n', '\r');
                client.Write(Encoding.UTF8.GetBytes($"[TERMINAL] echo: {line}\n"));
                Console.WriteLine($"{PREFIX}terminal probe: echo: {line}");
                return;
            }
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException)
        {
            Console.WriteLine($"{PREFIX}terminal probe failed: {exception.Message}");
        }
    }

    #endregion
}
