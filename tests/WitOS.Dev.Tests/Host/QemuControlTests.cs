using System.Diagnostics;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Host;

/// <summary>
/// QMP control: message sequencing and errors over the socket, the samples a timeout records (D5.2), and timeout cleanup
/// of real QEMU processes.
/// </summary>
[TestFixture]
public sealed class QemuControlTests
{
    #region Functions

    [Test]
    public async Task SocketQmpSequencingAndErrorsTest()
    {
        foreach (var reject in new[] { false, true })
        {
            using var control = new QemuControl();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var port = int.Parse(control.Argument.Split(':')[2].Split(',')[0]);
            var commands = new List<string>();
            var peer = Task.Run(async () =>
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, stop.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream);
                async Task Send(string line) => await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line + "\r\n"), stop.Token);
                await Send("{\"QMP\":{\"version\":{},\"capabilities\":[]}}");
                commands.Add((await reader.ReadLineAsync(stop.Token))!);
                if (reject)
                { await Send("{\"error\":{\"class\":\"GenericError\"},\"id\":\"witos-capabilities\"}"); return; }
                await Send("{\"return\":{},\"id\":\"unrelated\"}");
                await Send("{\"return\":{},\"id\":\"witos-capabilities\"}");
                // The samples: registers whose stack pointer lies 40 bytes below a page's end, that page holding a user
                // frame (RIP, CS 0x33, RFLAGS, RSP, SS), then the user stack it names; then the quit.
                for (; ; )
                {
                    var line = (await reader.ReadLineAsync(stop.Token))!;
                    commands.Add(line);
                    using var command = JsonDocument.Parse(line);
                    var id = command.RootElement.GetProperty("id").GetString();
                    if (command.RootElement.GetProperty("execute").GetString() == "quit")
                    {
                        await Send("{\"event\":\"SHUTDOWN\"}");
                        await Send("{\"return\":{},\"id\":\"witos-quit\"}");
                        return;
                    }
                    var text = command.RootElement.GetProperty("arguments").GetProperty("command-line").GetString()!;
                    var answer = text == "info registers" ? "RIP=0000000000401000 RSP=0000000000001fd8 CPL=3\r\n"
                        : text.StartsWith("xp ", StringComparison.Ordinal)
                            ? "0000000000001fd8: 0x0000000000401000 0x0000000000000033\r\n" +
                              "0000000000001fe8: 0x0000000000000202 0x00000000007ff000\r\n0000000000001ff8: 0x000000000000002b\r\n"
                            : "";
                    await Send(JsonSerializer.Serialize(new { @return = answer, id }));
                }
            });
            bool refused = false;
            try
            { await control.QuitAsync(stop.Token); }
            catch (InvalidDataException) { refused = true; }
            await peer;
            string[] samples = ["info registers", "xp /5gx 0x1fd8", "x /64gx 0x7ff000"];
            if (refused != reject || control.QuitAcknowledged == reject || commands.Count != (reject ? 1 : 2 + 3 * samples.Length) ||
                !commands[0].Contains("qmp_capabilities") || (!reject && !commands[^1].Contains("quit")) ||
                (!reject && !commands.Skip(1).SkipLast(1).Select((line, i) => line.Contains(samples[i % samples.Length])).All(match => match)))
                throw new InvalidDataException("QMP sequencing/error handling failed.");
        }
    }

    [Test]
    [Category(TestCategories.QEMU_CLEANUP)]
    [Explicit("Stress test; requires the pinned QEMU")]
    public async Task QemuTimeoutCleanupTest()
    {
        var root = TestEnvironment.Root;
        var output = TestEnvironment.Scratch();
        Toolchain.RequireQemu(root);
        for (var round = 0; round < 12; ++round)
        {
            using var control = new QemuControl();
            var clock = Stopwatch.StartNew();
            ProcessResult result;
            try
            {
                result = await Processes.RunWithFilesAsync(Toolchain.Qemu(root),
                    ["-machine", "none", "-display", "none", "-monitor", "none", "-serial", "none", "-nodefaults", "-S", "-qmp", control.Argument], root, 1, Path.Combine(output, $"qmp-{round}.stdout.log"), Path.Combine(output, $"qmp-{round}.stderr.log"), control.QuitAsync);
            }
            finally { await File.WriteAllTextAsync(Path.Combine(output, $"qmp-{round}.log"), control.Transcript); }
            if (!result.TimedOut || result.ExitCode != 0 || clock.Elapsed > TimeSpan.FromSeconds(7) || !control.QuitAcknowledged)
                throw new InvalidDataException($"QMP shutdown failed: round={round}, exit={result.ExitCode}, timeout={result.TimedOut}, seconds={clock.Elapsed.TotalSeconds}");
        }
        Console.WriteLine("PASS: 12 QEMU socket-QMP timeouts with native process/job completion.");
    }

    #endregion
}
