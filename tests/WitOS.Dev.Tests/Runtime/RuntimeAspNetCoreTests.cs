using System.Diagnostics;
using System.Net;
using System.Text;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Runtime;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// The ASP.NET Core scenario's pieces on the host (plan step W3.2): the runner's HTTP actions parse and reach a server
/// through the peer's forward, and the ordinary application answers the scenario's requests on this host as it must in the
/// guest: the text, the JSON, the echo, its own HTTPS check and the stop, exiting with 0.
/// </summary>
[TestFixture]
public sealed class RuntimeAspNetCoreTests
{
    #region Functions

    [Test]
    public void ActionsAreHttpRequestsAfterTheListeningLineTest()
    {
        var actions = RuntimeAspNetCore.Actions();
        Assert.That(actions.Select(action => action.Kind), Is.All.EqualTo(BootActionKind.Http));
        Assert.That(actions[0].AfterLine, Is.EqualTo(RuntimeAspNetCore.LISTENING));
        Assert.That(actions.Skip(1).Select(action => action.AfterLine), Is.All.Empty, "the requests after the first follow at once");
        var parsed = actions.Select(action => BootAction.ParseHttp(action.Argument)).ToList();
        Assert.That(parsed.Select(request => request.Method + " " + request.Path),
            Is.EqualTo(new[] { "GET /", "GET /json", "POST /echo", "GET /stop" }));
        Assert.That(parsed[0].Text, Is.EqualTo(RuntimeAspNetCore.HELLO));
        Assert.That(parsed[2].Text, Is.EqualTo(RuntimeAspNetCore.ECHO));
        Assert.That(RuntimeAspNetCore.RequiredLines(), Does.Contain(RuntimeAspNetCore.LISTENING));
        Assert.That(() => BootAction.ParseHttp("PUT / x"), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => BootAction.ParseHttp("GET nopath x"), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => BootAction.ParseHttp("GET /"), Throws.TypeOf<InvalidDataException>());
        Assert.That(BootAction.Http("", "GET", "/a", "two words").Argument, Is.EqualTo("GET /a two words"));
    }

    [Test]
    public async Task PeerRequestsThroughTheForwardTest()
    {
        using var peer = new NetworkPeer(8080);
        // A server on the host's side of the forward, where QEMU's hostfwd would listen: one request at a time.
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{peer.ForwardPort}/");
        listener.Start();
        var seen = new List<string>();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                seen.Add($"{context.Request.HttpMethod} {context.Request.Url!.AbsolutePath} {body}");
                var answer = Encoding.UTF8.GetBytes(context.Request.Url.AbsolutePath == "/echo" ? body : context.Request.Url.AbsolutePath == "/" ? "Hello there" : "nothing");
                context.Response.StatusCode = context.Request.Url.AbsolutePath == "/missing" ? 404 : 200;
                await context.Response.OutputStream.WriteAsync(answer);
                context.Response.Close();
            }
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await peer.HttpAsync("GET / Hello", stop.Token);
        await peer.HttpAsync("POST /echo an echoed text", stop.Token);
        Assert.That(async () => await peer.HttpAsync("GET / Goodbye", stop.Token), Throws.TypeOf<IOException>().With.Message.Contains("without"));
        Assert.That(async () => await peer.HttpAsync("GET /missing nothing", stop.Token), Throws.TypeOf<IOException>().With.Message.Contains("404"));
        listener.Stop();
        await serving;
        Assert.That(seen, Is.EqualTo(new[] { "GET / ", "POST /echo an echoed text", "GET / ", "GET /missing " }));
    }

    [Test]
    public async Task WebAppAnswersTheScenariosRequestsOnTheHostTest()
    {
        var dll = Path.Combine(TestEnvironment.Root, RuntimeAspNetCore.PROJECT, "bin", "Release", "net10.0", "WebApp.dll");
        Assert.That(File.Exists(dll), Is.True, $"{dll} is built with the solution");
        var start = new ProcessStartInfo("dotnet", ["exec", dll, "--HttpPort", "0", "--HttpsPort", "0"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = TestEnvironment.Scratch()
        };
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(start)!;
        var lines = new List<string>();
        var listening = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string Listening = "[ASPNET] listening on ";
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
                return;
            lock (lines)
                lines.Add(args.Data);
            // The first address is the HTTP one on the any address, 0.0.0.0 or [::] by the host, with the port Kestrel took.
            if (args.Data.StartsWith(Listening, StringComparison.Ordinal))
                listening.TrySetResult(new Uri(args.Data[Listening.Length..].Split(' ')[0]).Port);
        };
        process.BeginOutputReadLine();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            int port;
            try
            {
                port = await listening.Task.WaitAsync(TimeSpan.FromSeconds(90));
            }
            catch (TimeoutException)
            {
                string[] soFar;
                lock (lines)
                    soFar = [.. lines];
                Assert.Fail("the application did not report listening; it wrote:\n" + string.Join("\n", soFar) + "\n" +
                    (process.HasExited ? $"and exited with {process.ExitCode}" : "and still runs"));
                return;
            }
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) };
            foreach (var action in RuntimeAspNetCore.Actions())
            {
                var (method, path, text) = BootAction.ParseHttp(action.Argument);
                using var request = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{port}{path}");
                if (method == "POST")
                    request.Content = new StringContent(text, Encoding.UTF8);
                using var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                Assert.That((int)response.StatusCode, Is.EqualTo(200), $"{method} {path}: {body}");
                Assert.That(body, Does.Contain(text), $"{method} {path}");
            }
            Assert.That(await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)).ContinueWith(_ => process.ExitCode), Is.EqualTo(0), await errors);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        process.WaitForExit();
        string[] all;
        lock (lines)
            all = [.. lines];
        Assert.That(all, Has.Some.StartsWith("[ASPNET] https self-check: ok, "), string.Join("\n", all));
        Assert.That(all, Does.Contain("[ASPNET] served 5 requests, exit 0"), string.Join("\n", all));
        Assert.That(all.Count(line => line.StartsWith("[ASPNET] GET /json", StringComparison.Ordinal)), Is.EqualTo(2), "the runner's and the self-check's");
    }

    #endregion
}
