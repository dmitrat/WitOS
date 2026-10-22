using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotNetCompatibility;

/// <summary>
/// Communication between processes of one machine (plan step W4.1): Unix domain sockets through Socket, a path's and
/// an abstract name's, named pipes through System.IO.Pipes (on Unix a Unix domain socket in the temporary directory)
/// within the process and with a child, and the runtime's diagnostic IPC, which a tool reaches as dotnet-counters
/// and dotnet-trace do.
/// </summary>
internal static class IpcChecks
{
    private const int BYTES = 512 * 1024;

    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        // A path in the temporary directory: bound, connected, half a megabyte echoed both ways, the end of the stream, and
        // the file gone once the listener is disposed.
        yield return new Check("Ipc", "UnixSocketEcho", async () =>
        {
            if (!Socket.OSSupportsUnixDomainSockets)
                return CheckResult.That(false, "Socket.OSSupportsUnixDomainSockets is false");
            var path = Path.Combine(Path.GetTempPath(), $"wipc-{Environment.ProcessId}-{Random.Shared.Next():x}.sock");
            var endPoint = new UnixDomainSocketEndPoint(path);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(endPoint);
            listener.Listen();
            var bound = File.Exists(path);
            var echo = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptAsync();
                var buffer = new byte[16 * 1024];
                for (int count; (count = await accepted.ReceiveAsync(buffer)) > 0;)
                    await accepted.SendAsync(buffer.AsMemory(0, count));
                accepted.Shutdown(SocketShutdown.Send);
            });
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(endPoint);
            var data = new byte[BYTES];
            new Random(11).NextBytes(data);
            var writing = Task.Run(async () =>
            {
                await client.SendAsync(data);
                client.Shutdown(SocketShutdown.Send);
            });
            var back = new byte[BYTES];
            var received = 0;
            for (int count; received < BYTES && (count = await client.ReceiveAsync(back.AsMemory(received))) > 0;)
                received += count;
            var end = await client.ReceiveAsync(new byte[16]);
            await writing;
            await echo;
            var local = listener.LocalEndPoint?.ToString();
            listener.Dispose();
            var removed = !File.Exists(path);
            return CheckResult.That(bound && received == BYTES && back.AsSpan().SequenceEqual(data) && end == 0 && local == path && removed,
                $"{received} of {BYTES} bytes back through {local} (a file while bound: {bound}, removed with the listener: {removed}), " +
                $"the end of the stream {end == 0}");
        });
        // Linux's abstract names, which no directory holds; Windows has none.
        yield return new Check("Ipc", "UnixSocketAbstract", async () =>
        {
            if (OperatingSystem.IsWindows())
                return CheckResult.NotApplicable("Windows has no abstract names for Unix domain sockets");
            var endPoint = new UnixDomainSocketEndPoint($"\0witos-compatibility-{Environment.ProcessId}-{Random.Shared.Next():x}");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(endPoint);
            listener.Listen();
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var accepting = listener.AcceptAsync();
            await client.ConnectAsync(endPoint);
            using var accepted = await accepting;
            await client.SendAsync("abstract"u8.ToArray());
            var buffer = new byte[16];
            var count = await accepted.ReceiveAsync(buffer);
            return CheckResult.That(count == 8 && buffer.AsSpan(0, 8).SequenceEqual("abstract"u8),
                $"{count} bytes through the abstract name {listener.LocalEndPoint}");
        });
        // A named pipe within the process, asynchronous on both ends.
        yield return new Check("Ipc", "NamedPipe", async () =>
        {
            var name = $"witos-pipe-{Environment.ProcessId}-{Random.Shared.Next():x}";
            await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(30000));
            var data = new byte[BYTES];
            new Random(13).NextBytes(data);
            var writing = Task.Run(async () =>
            {
                await client.WriteAsync(data);
                await client.FlushAsync();
            });
            var back = new byte[BYTES];
            var received = 0;
            for (int count; received < BYTES && (count = await server.ReadAsync(back.AsMemory(received))) > 0;)
                received += count;
            await writing;
            // The client reads before the server writes: a Windows pipe without a buffer completes a write once it is read.
            var answer = new byte[4];
            var answering = client.ReadAsync(answer).AsTask();
            await server.WriteAsync("done"u8.ToArray());
            var answered = await answering;
            return CheckResult.That(server.IsConnected && received == BYTES && back.AsSpan().SequenceEqual(data) && answered == 4,
                $"{received} of {BYTES} bytes from the client, {answered} back to it through the pipe {name}");
        });
        // A named pipe to a child process (Program.cs's --pipe): its line upper-cased back.
        yield return new Check("Ipc", "NamedPipeChild", async () =>
        {
            var name = $"witos-child-{Environment.ProcessId}-{Random.Shared.Next():x}";
            await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var child = Process.Start(new ProcessStartInfo(PlatformChecks.Muxer, [PlatformChecks.Suite, "--pipe", name]) { UseShellExecute = false })!;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            await server.WaitForConnectionAsync(cancellation.Token);
            using var reader = new StreamReader(server, leaveOpen: true);
            await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("from the parent");
            var line = await reader.ReadLineAsync(cancellation.Token);
            await child.WaitForExitAsync(cancellation.Token);
            return CheckResult.That(line == "FROM THE PARENT" && child.ExitCode == 0, $"the child answered \"{line}\" and exited with {child.ExitCode}");
        });
        // The runtime's diagnostic IPC in its reverse mode (DOTNET_DiagnosticPorts): the suite listens as dotnet-trace does
        // with --diagnostic-port, a child runtime connects to it, and the child's environment comes back over the protocol.
        yield return new Check("Ipc", "DiagnosticsIpc", async () =>
        {
            var port = OperatingSystem.IsWindows()
                ? $"witos-diagnostics-{Environment.ProcessId}-{Random.Shared.Next():x}"
                : Path.Combine(Path.GetTempPath(), $"wdiag-{Environment.ProcessId}-{Random.Shared.Next():x}.sock");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var connecting = DiagnosticsClientConnector.FromDiagnosticPort(port, cancellation.Token);
            using var child = Process.Start(new ProcessStartInfo(PlatformChecks.Muxer, [PlatformChecks.Suite, "--sleep"])
            {
                UseShellExecute = false,
                Environment = { ["DOTNET_DiagnosticPorts"] = port + ",connect,nosuspend", ["DOTNET_COMPATIBILITY_CHILD"] = "diagnostics" }
            })!;
            try
            {
                await using var connector = await connecting;
                var environment = connector.Instance.GetProcessEnvironment();
                var seen = environment.TryGetValue("DOTNET_COMPATIBILITY_CHILD", out var value) ? value : null;
                return CheckResult.That(seen == "diagnostics",
                    $"the child's environment through its diagnostic port {port}: {environment.Count} variables, DOTNET_COMPATIBILITY_CHILD={seen}");
            }
            finally
            {
                child.Kill();
                await child.WaitForExitAsync();
            }
        });
    }
}
