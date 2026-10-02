using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WitOS.Dev.Host;

// The parent owns a loopback listener before QEMU starts; no released-port race.
// QEMU connects as a transport client but remains the QMP protocol server.
// Keeping QMP off inherited stdin removes QEMU's synchronous pipe reader path.
internal sealed class QemuControl : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task<TcpClient> connected;
    private readonly StringBuilder transcript = new();
    internal string Argument { get; }
    internal bool QuitAcknowledged { get; private set; }
    internal string Transcript { get { lock (transcript) return transcript.ToString(); } }
    internal QemuControl()
    {
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Argument = $"tcp:127.0.0.1:{port},server=off";
        connected = listener.AcceptTcpClientAsync(lifetime.Token).AsTask();
    }
    internal async Task QuitAsync(CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = stop.Token;
        var client = await connected.WaitAsync(token);
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
    private void Record(string line)
    {
        lock (transcript)
        {
            if (transcript.Length + line.Length + 1 > 65536)
                throw new InvalidDataException("QMP transcript exceeded limit.");
            transcript.AppendLine(line);
        }
    }
    public void Dispose()
    {
        lifetime.Cancel();
        listener.Stop();
        _ = connected.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion)
                task.Result.Dispose();
            else
                _ = task.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        lifetime.Dispose();
    }
}
