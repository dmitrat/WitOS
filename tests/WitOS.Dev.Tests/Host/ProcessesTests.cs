using System.Diagnostics;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Host;

/// <summary>
/// Child process runner: deadlines, descendant cleanup, pipes, file capture, control callbacks and environment.
/// </summary>
[TestFixture]
public sealed class ProcessesTests
{
    #region Functions

    [Test]
    public async Task DeadlineIncludesDescendantPipesTest()
    {
        var root = TestEnvironment.Root;
        var scratch = TestEnvironment.Scratch();
        var pid = Path.Combine(scratch, "leaf.pid");
        var clock = Stopwatch.StartNew();
        var result = await Processes.RunAsync("dotnet", TestEnvironment.Child("launcher", pid), root, 3);
        clock.Stop();
        Assert.That(result.TimedOut, Is.True, "Descendant retained pipes past deadline but result was success");
        Assert.That(clock.Elapsed < TimeSpan.FromSeconds(7), Is.True, "Deadline/cleanup was not bounded");
        Assert.That(File.Exists(pid), Is.True, "Fixture did not start its descendant");
        try
        { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pid))); Assert.That(child.HasExited, Is.True, "Descendant survived timeout"); }
        catch (ArgumentException) { }
    }

    [Test]
    public async Task ArgumentsEnvironmentAndExitTest()
    {
        var root = TestEnvironment.Root;
        string[] values = ["", "two words", "C:\\with space\\", "a\"b", "tail\\\"", "?????????"];
        var result = await Processes.RunAsync("dotnet", TestEnvironment.Child(["echo", .. values]), root, 15);
        Assert.That(!result.TimedOut && result.ExitCode == 7, Is.True, "Exit status changed");
        Assert.That(JsonSerializer.Deserialize<string[]>(result.Output)!.SequenceEqual(values), Is.True, "Argument escaping changed");
        Assert.That(result.Error == "stderr-tail", Is.True, "Stderr lost");
    }

    [Test]
    public async Task BothStreamsDrainTest()
    {
        var root = TestEnvironment.Root;
        var result = await Processes.RunAsync("dotnet", TestEnvironment.Child("flood"), root, 15);
        Assert.That(!result.TimedOut && result.ExitCode == 0 && result.Output.Length == 200000 && result.Error.Length == 200000, Is.True, "Large simultaneous output lost");
    }

    [Test]
    public async Task ExitedJobWithExternalPipeOwnerTest()
    {
        var root = TestEnvironment.Root;
        var scratch = TestEnvironment.Scratch();
        var report = Path.Combine(scratch, "exported-handles.txt");
        var time = Stopwatch.StartNew();
        try
        {
            var result = await Processes.RunAsync("dotnet", TestEnvironment.Child("export-pipes", Environment.ProcessId.ToString(), report), root, 2);
            Assert.That(File.Exists(report), Is.True, "Child did not publish duplicate handles");
            Assert.That(result.TimedOut && result.ExitCode == 0 && result.Output.Contains("before exported pipes"), Is.True, "Expected timeout/partial output after parent exit");
            Assert.That(time.Elapsed < TimeSpan.FromSeconds(6), Is.True, "Cleanup waited for unrelated writer EOF");
        }
        finally { ExportedHandles.Close(report); }
    }

    [Test]
    public async Task TimeoutControlHandshakeTest()
    {
        var root = TestEnvironment.Root;
        var result = await Processes.RunWithTimeoutControlAsync("dotnet", TestEnvironment.Child("stop-handshake"), root, 2, async (input, output, token) =>
        {
            await input.WriteAsync(System.Text.Encoding.UTF8.GetBytes("hello\n"), token);
            await input.FlushAsync(token);
            while (!output().Contains("ready"))
                await Task.Delay(10, token);
            await input.WriteAsync(System.Text.Encoding.UTF8.GetBytes("quit\n"), token);
            await input.FlushAsync(token);
        });
        Assert.That(result.TimedOut && result.ExitCode == 25 && result.Output.Contains("ready"), Is.True, "Handshake shutdown changed timeout or missed acknowledgement");
    }

    [Test]
    public async Task CooperativeTimeoutStillTimesOutTest()
    {
        var root = TestEnvironment.Root;
        var time = Stopwatch.StartNew();
        var result = await Processes.RunWithTimeoutInputAsync("dotnet", TestEnvironment.Child("stop-input"), root, 2, "quit\n");
        Assert.That(result.TimedOut && result.ExitCode == 23 && result.Output.Contains("before cooperative stop") && time.Elapsed < TimeSpan.FromSeconds(7), Is.True, "Cooperative cleanup changed timeout/exit semantics");
        var fallback = await Processes.RunWithTimeoutInputAsync("dotnet", TestEnvironment.Child("wait"), root, 2, "ignored\n");
        Assert.That(fallback.TimedOut && fallback.ExitCode != 0 && fallback.Output.Contains("before wait"), Is.True, "Uncooperative child escaped forced cleanup");
    }

    [Test]
    public async Task TimeoutDuringOutputBurstTest()
    {
        var root = TestEnvironment.Root;
        var scratch = TestEnvironment.Scratch();
        var pid = Path.Combine(scratch, "burst.pid");
        var time = Stopwatch.StartNew();
        var result = await Processes.RunAsync("dotnet", TestEnvironment.Child("burst-wait", pid), root, 2);
        Assert.That(result.TimedOut && result.Output.Contains("before burst") && time.Elapsed < TimeSpan.FromSeconds(7), Is.True, "Burst timeout/cleanup contract failed");
        try
        { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pid))); Assert.That(child.HasExited, Is.True, "Burst child survived timeout"); }
        catch (ArgumentException) { }
    }

    [Test]
    public async Task RootDeadlineAndPartialOutputTest()
    {
        var root = TestEnvironment.Root;
        var r = await Processes.RunAsync("dotnet", TestEnvironment.Child("wait"), root, 2);
        Assert.That(r.TimedOut && r.Output.Contains("before wait"), Is.True, "Root timeout lost status/output");
    }

    [Test]
    public async Task NormalExitReapsPipeIndependentDescendantTest()
    {
        var root = TestEnvironment.Root;
        var scratch = TestEnvironment.Scratch();
        var pid = Path.Combine(scratch, "normal-child.pid");
        var time = Stopwatch.StartNew();
        var r = await Processes.RunAsync("dotnet", TestEnvironment.Child("launcher-no-pipes", pid), root, 15);
        await File.WriteAllTextAsync(Path.Combine(scratch, "normal-descendant.json"), JsonSerializer.Serialize(new { result = r, elapsed = time.Elapsed, childPidFileExists = File.Exists(pid) }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.That(!r.TimedOut && r.ExitCode == 0, Is.True, $"Normal exit changed: timeout={r.TimedOut}, exit={r.ExitCode}, stdout={r.Output.Length}, stderr={r.Error.Length}; see {scratch}");
        Assert.That(time.Elapsed < TimeSpan.FromSeconds(6), Is.True, "Waited for a pipe-independent child to exit naturally");
        try
        { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pid))); Assert.That(child.HasExited, Is.True, "Daemon escaped command ownership"); }
        catch (ArgumentException) { }
    }

    [Test]
    public async Task LaunchFailureAndEnvironmentTest()
    {
        var root = TestEnvironment.Root;
        var scratch = TestEnvironment.Scratch();
        bool failed = false;
        try
        { await Processes.RunAsync(Path.Combine(scratch, "does-not-exist.exe"), [], root, 2); }
        catch (System.ComponentModel.Win32Exception) { failed = true; }
        Assert.That(failed, Is.True, "Launch failure returned success");
        var r = await Processes.RunAsync("dotnet", TestEnvironment.Child("env"), root, 15, new Dictionary<string, string> { { "WITOS_Q0_TEST_ENV", "value with spaces ?" } });
        Assert.That(r.Output == "value with spaces ?" && !r.TimedOut, Is.True, "Environment corrupted");
    }

    [Test]
    public async Task ConcurrentPipesStayIsolatedTest()
    {
        var root = TestEnvironment.Root;
        var scratch = TestEnvironment.Scratch();
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Processes.RunAsync("dotnet", TestEnvironment.Child("echo", i.ToString()), root, 15)));
        await File.WriteAllTextAsync(Path.Combine(scratch, "concurrent-pipes.json"), JsonSerializer.Serialize(runs, new JsonSerializerOptions { WriteIndented = true }));
        for (int i = 0; i < runs.Length; i++)
        {
            var result = runs[i];
            Assert.That(!result.TimedOut && result.ExitCode == 7, Is.True, $"Concurrent child {i}: timeout={result.TimedOut}, exit={result.ExitCode}, stdout={result.Output.Length}, stderr={result.Error.Length}; see {scratch}");
            Assert.That(result.Error == "stderr-tail", Is.True, $"Concurrent child {i}: stderr missing or crossed");
            Assert.That(JsonSerializer.Deserialize<string[]>(result.Output)!.Single() == i.ToString(), Is.True, $"Concurrent child {i}: cross-process pipe leak");
        }
    }

    [Test]
    public async Task ControlIgnoresCancellationTest()
    {
        var root = TestEnvironment.Root;
        foreach (var synchronous in new[] { false, true })
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var time = Stopwatch.StartNew();
            var result = await Processes.RunWithTimeoutControlAsync("dotnet", [TestEnvironment.ChildAssembly, "wait"], root, 1,
                async (input, _, _) =>
                {
                    try
                    {
                        if (synchronous)
                            Thread.Sleep(4000);
                        else
                            await Task.Delay(4000);
                        await input.WriteAsync(new byte[] { 1 }); // Must fault after the runner has disposed input.
                        throw new InvalidOperationException("Late control failure");
                    }
                    finally { completed.TrySetResult(); }
                });
            Assert.That(result.TimedOut && result.ExitCode != 0 && time.Elapsed < TimeSpan.FromSeconds(3.8), Is.True, "Control wait escaped its independent bound");
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(6));
        }
        var failed = false;
        try
        {
            await Processes.RunWithTimeoutControlAsync("dotnet", [TestEnvironment.ChildAssembly, "wait"], root, 1,
                (_, _, _) => throw new InvalidOperationException("control failed"));
        }
        catch (InvalidOperationException e) { failed = e.Message == "control failed"; }
        Assert.That(failed, Is.True, "Immediate callback failure lost");
    }

    [Test]
    public async Task FileCaptureOwnershipAndLimitsTest()
    {
        var root = TestEnvironment.Root;
        var scratch = TestEnvironment.Scratch();
        var assembly = TestEnvironment.ChildAssembly;
        var output = Path.Combine(scratch, "file.stdout.log");
        var error = Path.Combine(scratch, "file.stderr.log");
        var result = await Processes.RunWithFilesAsync("dotnet", [assembly, "echo", "file mode"], root, 15, output, error, _ => Task.CompletedTask);
        Assert.That(result.ExitCode == 7 && !result.TimedOut && result.Output.Contains("file mode") && result.Error == "stderr-tail", Is.True, "File capture lost exit/stdout/stderr");
        result = await Processes.RunWithFilesAsync("dotnet", [assembly, "wait"], root, 1, output, error, _ => Task.CompletedTask);
        Assert.That(result.TimedOut && result.ExitCode != 0, Is.True, "File mode failed forced cleanup");
        var pid = Path.Combine(scratch, "file-child.pid");
        result = await Processes.RunWithFilesAsync("dotnet", [assembly, "launcher-no-pipes", pid], root, 15, output, error, _ => Task.CompletedTask);
        Assert.That(result.ExitCode == 0 && !result.TimedOut, Is.True, "File mode changed normal exit");
        try
        { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pid))); Assert.That(child.HasExited, Is.True, "File mode left a descendant"); }
        catch (ArgumentException) { }
        var rejected = false;
        try
        { await Processes.RunWithFilesAsync("dotnet", [assembly, "capture-limit", "stderr"], root, 15, output, error, _ => Task.CompletedTask); }
        catch (InvalidDataException) { rejected = true; }
        Assert.That(rejected, Is.True, "File capture accepted oversized output");
    }

    #endregion
}
