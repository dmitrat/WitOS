using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using WitOS.Dev;

internal static class Q1Tests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string root, string scratch)
    {
        yield return ("Q1.ProtocolEnvelope", () => Protocol(root));
        yield return ("Q1.PublicationFailureStages", () => Publication(scratch));
        yield return ("Q1.ControlIgnoresCancellation", () => Control(root));
        yield return ("Q1.NamedNativeObjects", () => Objects(scratch));
        yield return ("FileCaptureOwnershipAndLimits", () => FileCapture(root, scratch));
        yield return ("Q1.BoundedCapture", () => Capture(root, scratch));
    }

    private static Task Protocol(string root)
    {
        var inputs = new List<string> { ProtocolFixtures.Valid };
        var evidence = Path.Combine(root, "artifacts/x64/runtime-boot/last-success.json");
        if (File.Exists(evidence))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(evidence));
            if (json.RootElement.TryGetProperty("logs", out var logs))
                inputs.AddRange(logs.EnumerateArray().Select(log => File.ReadAllText(log.GetProperty("file").GetString()!).Replace("\r\n", "\n")));
        }
        foreach (var original in inputs)
        {
            var good = original.Replace("Runtime orderly thread completions: 41", "Runtime orderly thread completions: 43")
                .Replace("0000000000000002/0000000000000001/0000000000000000/0000000000000001", "0000000000000003/0000000000000001/0000000000000001/0000000000000001");
            Check(RuntimeBootEnvelope.Validate(good.Split('\n'), out var diagnostic), diagnostic);
            Check(RuntimeBootProtocol.Validate(good, 33, false), "Baseline semantic protocol rejected");
            const string verdict = "[TEST-PASS] Runtime.NativeFaultContained\n";
            const string capacity = "Runtime managed thread capacity failures: 4";
            var start = good.IndexOf("Runtime boot image base:", StringComparison.Ordinal);
            var afterHeader = good.IndexOf('\n', start) + 1;
            string[] bad = [
                good.Insert(afterHeader, "[USER] [NATIVE-FAIL-FAST] code=0xC000001D address=0x0000008000100000 rip=0x0000008000100000\n"),
                good.Replace(capacity, capacity + "\nRuntime managed thread capacity failures: 0"),
                good.Replace(verdict, "") + verdict + verdict,
                good + RuntimeBootProtocol.Finalization + "\n",
                RuntimeBootProtocol.Finalization + "\n" + good,
                good.Replace("Runtime native fault base: 0x0000008000180000", "Runtime native fault base: 0x0000008000100000"),
                good.Replace("[USER] [RUNTIME] native fault probe entered\n", ""),
                good.Replace("Runtime managed stack overflow base:", "Runtime init failure base:")];
            for (var i = 0; i < bad.Length; ++i)
                Check(!RuntimeBootProtocol.Validate(bad[i], 33, false), $"Protocol mutation {i} accepted");
        }
        // Every runtime report is unique to a position, even when duplicated verbatim.
        foreach (var line in ProtocolFixtures.Valid.Split('\n').Where(s => s.StartsWith("Runtime ") || s.StartsWith("[TEST-PASS] Runtime.")))
            Check(!RuntimeBootProtocol.Validate(ProtocolFixtures.Valid + line + "\n", 33, false), "Trailing report accepted: " + line);
        return Task.CompletedTask;
    }

    private static async Task Publication(string scratch)
    {
        string[] files = ["run/acceptance.json", "run/status.json", "current-run.json", "acceptance.json", "last-success.json"];
        foreach (var file in files)
            foreach (var stage in new[] { "write", "rename" })
            {
                var home = Path.Combine(scratch, "publication-" + Guid.NewGuid().ToString("N"));
                var dir = Path.Combine(home, "artifacts/x64/runtime-boot");
                await RuntimeBootAttempt.RunAsync(home, "old", a => { a.Publish(new { fixture = "old" }); return Task.CompletedTask; });
                var old = File.ReadAllText(Path.Combine(dir, "last-success.json"));
                bool armed = false, injected = false, failed = false;
                string? run = null;
                RuntimeBootAttempt.BeforeWrite = (path, point) =>
                {
                    if (!armed || injected || point != stage)
                        return;
                    var actual = path.StartsWith(run! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        ? "run/" + Path.GetFileName(path) : Path.GetFileName(path);
                    if (actual != file)
                        return;
                    injected = true;
                    throw new IOException("injected-" + file + "-" + stage);
                };
                try
                {
                    await RuntimeBootAttempt.RunAsync(home, "new", a =>
                    {
                        run = a.RunDirectory;
                        a.Publish(new { fixture = "new" });
                        armed = true;
                        return Task.CompletedTask;
                    });
                }
                catch (IOException error) { failed = error.Message.StartsWith("injected-", StringComparison.Ordinal); }
                finally { RuntimeBootAttempt.BeforeWrite = null; }
                Check(injected, "Failure point not reached: " + file + "/" + stage);
                var viewFailure = file is "acceptance.json" or "last-success.json";
                Check(failed != viewFailure, "Commit boundary incorrect: " + file + "/" + stage);
                if (!viewFailure)
                    Check(File.ReadAllText(Path.Combine(dir, "last-success.json")) == old, "Uncommitted run advanced last success");
                RuntimeBootAttempt.Recover(dir);
                using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "current-run.json")));
                Check(state.RootElement.GetProperty("status").GetString() == (viewFailure ? "succeeded" : "failed"), "Recovered state incorrect");
                Check(File.Exists(Path.Combine(dir, "acceptance.json")) == viewFailure, "Recovered current acceptance incorrect");
                Check(!Directory.EnumerateFiles(dir, "*.tmp", SearchOption.AllDirectories).Any(), "Temporary publication file leaked");
                if (viewFailure)
                    Check(File.ReadAllText(Path.Combine(dir, "last-success.json")) != old, "Committed success not recovered");
            }
        var broken = Path.Combine(scratch, "blocked-status");
        await RuntimeBootAttempt.RunAsync(broken, "old", a => { a.Publish(new { fixture = "old" }); return Task.CompletedTask; });
        var output = Path.Combine(broken, "artifacts/x64/runtime-boot");
        var previous = File.ReadAllText(Path.Combine(output, "last-success.json"));
        bool rejected = false;
        try
        {
            await RuntimeBootAttempt.RunAsync(broken, "blocked", a =>
            {
                File.Delete(Path.Combine(a.RunDirectory, "status.json"));
                Directory.CreateDirectory(Path.Combine(a.RunDirectory, "status.json"));
                a.Publish(new { fixture = "new" });
                return Task.CompletedTask;
            });
        }
        catch (UnauthorizedAccessException) { rejected = true; }
        Check(rejected && File.ReadAllText(Path.Combine(output, "last-success.json")) == previous, "Original Q1.2 reproduction persists");
        using (var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "current-run.json"))))
            Check(state.RootElement.GetProperty("status").GetString() == "failed", "Blocked run status hid current failure");
        try
        { await RuntimeBootAttempt.RunAsync(broken, "after-publish", a => { a.Publish(new { fixture = "bad" }); throw new IOException("after publish"); }); }
        catch (IOException e) { Check(e.Message == "after publish", "Original exception replaced"); }
        Check(File.ReadAllText(Path.Combine(output, "last-success.json")) == previous, "Throw after Publish committed evidence");
        var interrupted = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "current-run.json")))!.AsObject();
        interrupted["status"] = "running";
        File.WriteAllText(Path.Combine(output, "current-run.json"), interrupted.ToJsonString());
        File.WriteAllText(Path.Combine(output, "acceptance.json"), "stale");
        RuntimeBootAttempt.Recover(output);
        Check(!File.Exists(Path.Combine(output, "acceptance.json")), "Restart retained interrupted current acceptance");
        Check(JsonNode.Parse(File.ReadAllText(Path.Combine(output, "current-run.json")))!["status"]!.GetValue<string>() == "interrupted", "Restart did not classify interrupted attempt");
    }

    private static async Task Control(string root)
    {
        foreach (var synchronous in new[] { false, true })
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var time = Stopwatch.StartNew();
            var result = await Processes.RunWithTimeoutControlAsync("dotnet", [Assembly.GetExecutingAssembly().Location, "wait"], root, 1,
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
            Check(result.TimedOut && result.ExitCode != 0 && time.Elapsed < TimeSpan.FromSeconds(3.8), "Control wait escaped its independent bound");
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(6));
        }
        var failed = false;
        try
        {
            await Processes.RunWithTimeoutControlAsync("dotnet", [Assembly.GetExecutingAssembly().Location, "wait"], root, 1,
                (_, _, _) => throw new InvalidOperationException("control failed"));
        }
        catch (InvalidOperationException e) { failed = e.Message == "control failed"; }
        Check(failed, "Immediate callback failure lost");
    }

    private static Task Objects(string scratch)
    {
        var input = Path.Combine(scratch, "platform-input");
        var output = Path.Combine(scratch, "platform-output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        var entries = NativePlatformObjects.Sources.Select(name =>
        {
            var file = Path.Combine(input, name + ".obj");
            File.WriteAllText(file, name);
            return KeyValuePair.Create(name, file);
        }).Reverse().ToArray(); // Enumeration order must not alter semantic link groups.
        var objects = new NativePlatformObjects(entries);
        Check(objects.Record.Select(Path.GetFileName).SequenceEqual(new[] { "native_services.witos.cpp.obj", "native_services.asm.obj", "pal_events.witos.cpp.obj", "native_thread_handles.witos.cpp.obj", "native_thread_handles.asm.obj" }), "Record group depends on manifest order");
        Check(objects.Thread.Length == 30 && objects.Cpu.Length == 18 && objects.Com.Length == 34, "Native group membership changed");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(objects.CopyTo(output)));
        var loaded = NativePlatformObjects.Read(output, json.RootElement);
        Check(loaded.All.Select(Path.GetFileName).SequenceEqual(objects.All.Select(Path.GetFileName)), "Named manifest roundtrip changed objects");
        var rejected = false;
        try
        { _ = new NativePlatformObjects(entries.Skip(1)); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Missing native source accepted");
        File.AppendAllText(loaded.All[0], "changed");
        rejected = false;
        try
        { NativePlatformObjects.Read(output, json.RootElement); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Changed object hash accepted");
        return Task.CompletedTask;
    }

    private static async Task FileCapture(string root, string scratch)
    {
        var assembly = Assembly.GetExecutingAssembly().Location;
        var output = Path.Combine(scratch, "file.stdout.log");
        var error = Path.Combine(scratch, "file.stderr.log");
        var result = await Processes.RunWithFilesAsync("dotnet", [assembly, "echo", "file mode"], root, 15, output, error, _ => Task.CompletedTask);
        Check(result.ExitCode == 7 && !result.TimedOut && result.Output.Contains("file mode") && result.Error == "stderr-tail", "File capture lost exit/stdout/stderr");
        result = await Processes.RunWithFilesAsync("dotnet", [assembly, "wait"], root, 1, output, error, _ => Task.CompletedTask);
        Check(result.TimedOut && result.ExitCode != 0, "File mode failed forced cleanup");
        var pid = Path.Combine(scratch, "file-child.pid");
        result = await Processes.RunWithFilesAsync("dotnet", [assembly, "launcher-no-pipes", pid], root, 15, output, error, _ => Task.CompletedTask);
        Check(result.ExitCode == 0 && !result.TimedOut, "File mode changed normal exit");
        try
        { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pid))); Check(child.HasExited, "File mode left a descendant"); }
        catch (ArgumentException) { }
        var rejected = false;
        try
        { await Processes.RunWithFilesAsync("dotnet", [assembly, "capture-limit", "stderr"], root, 15, output, error, _ => Task.CompletedTask); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "File capture accepted oversized output");
    }

    private static async Task Capture(string root, string scratch)
    {
        var capture = new BoundedCapture();
        capture.Append(new string('x', BoundedCapture.Limit));
        Check(!capture.Truncated, "Exact capture limit rejected");
        capture.Append("y");
        Check(capture.Truncated && capture.Snapshot().Length == BoundedCapture.Limit, "Capture is unbounded");
        var file = Path.Combine(scratch, "oversized-serial.log");
        using (var output = File.Create(file))
            output.SetLength(BoundedCapture.Limit + 1L);
        bool rejected = false;
        try
        { await BoundedCapture.ReadFileAsync(file); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Oversized serial file accepted");
        foreach (var stream in new[] { "stdout", "stderr" })
        {
            rejected = false;
            try
            { await Processes.RunAsync("dotnet", [Assembly.GetExecutingAssembly().Location, "capture-limit", stream], root, 15); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Truncated process output accepted: " + stream);
        }
    }
}
