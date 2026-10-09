using System.Text.Json;
using System.Text.Json.Nodes;
using WitOS.Dev.NativeAot.Acceptance;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.NativeAot.Acceptance;

/// <summary>
/// Runtime-boot attempt records: history, failure stages, exclusion, recovery and publication failures.
/// </summary>
[TestFixture]
public sealed class RuntimeBootAttemptTests
{
    #region Functions

    [Test]
    public async Task AttemptHistoryAndFailureStagesTest()
    {
        var scratch = TestEnvironment.Scratch();
        var home = Path.Combine(scratch, "history");
        var dir = Path.Combine(home, "artifacts/x64/runtime-boot");
        string? lastId = null;
        string? lastAcceptance = null;
        await RuntimeBootAttempt.RunAsync(home, "test", a => { lastId = a.RunId; lastAcceptance = Path.Combine(a.RunDirectory, "acceptance.json"); a.Publish(new { fixture = "unit-test" }); return Task.CompletedTask; });
        var saved = File.ReadAllText(lastAcceptance!);
        foreach (var stage in new[] { "discovery", "build", "hash", "launch", "boot" })
        {
            bool failed = false;
            try
            {
                await RuntimeBootAttempt.RunAsync(home, "test", a =>
                {
                    Assert.That(!File.Exists(Path.Combine(dir, "acceptance.json")), Is.True, "Old success visible during attempt");
                    using var running = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "current-run.json")));
                    Assert.That(running.RootElement.GetProperty("status").GetString() == "running" && a.RunId != lastId, Is.True, "Run identity/status not refreshed");
                    throw new InvalidDataException(stage);
                });
            }
            catch (InvalidDataException e) { failed = e.Message == stage; }
            Assert.That(failed && !File.Exists(Path.Combine(dir, "acceptance.json")), Is.True, "Failure published success");
            using var current = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "current-run.json")));
            Assert.That(current.RootElement.GetProperty("status").GetString() == "failed" && current.RootElement.GetProperty("error").GetString() == stage, Is.True, "Failure status lost");
            Assert.That(File.ReadAllText(Path.Combine(dir, "last-success.json")) == saved && File.ReadAllText(lastAcceptance!) == saved, Is.True, "Historical success changed");
        }
        await RuntimeBootAttempt.RunAsync(home, "retry", a => { a.Publish(new { fixture = "retry" }); return Task.CompletedTask; });
        Assert.That(File.Exists(Path.Combine(dir, "acceptance.json")), Is.True, "Success after failure missing");
        Assert.That(!Directory.EnumerateFiles(dir, "*.tmp", SearchOption.AllDirectories).Any(), Is.True, "Atomic publication left temp files");
    }

    [Test]
    public async Task AttemptExclusionAndIncompleteRunTest()
    {
        var scratch = TestEnvironment.Scratch();
        var home = Path.Combine(scratch, "exclusive");
        await RuntimeBootAttempt.RunAsync(home, "outer", async a =>
        {
            bool busy = false;
            try
            { await RuntimeBootAttempt.RunAsync(home, "inner", b => Task.CompletedTask); }
            catch (IOException) { busy = true; }
            Assert.That(busy, Is.True, "Concurrent attempt acquired output ownership");
            a.Publish(new { fixture = "exclusive" });
        });
        bool missing = false;
        try
        { await RuntimeBootAttempt.RunAsync(home, "no-publication", a => Task.CompletedTask); }
        catch (InvalidOperationException) { missing = true; }
        Assert.That(missing && !File.Exists(Path.Combine(home, "artifacts/x64/runtime-boot/acceptance.json")), Is.True, "Incomplete attempt accepted");
    }

    [Test]
    public async Task LegacyHistoryAndPublicationFailureTest()
    {
        var scratch = TestEnvironment.Scratch();
        var home = Path.Combine(scratch, "legacy");
        var dir = Path.Combine(home, "artifacts/x64/runtime-boot");
        Directory.CreateDirectory(dir);
        var log = Path.Combine(home, "artifacts/logs/legacy.serial.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "historical bytes");
        File.WriteAllText(Path.Combine(dir, "acceptance.json"), JsonSerializer.Serialize(new { logs = new[] { new { file = log } } }));
        try
        { await RuntimeBootAttempt.RunAsync(home, "migration", a => throw new IOException("injected")); }
        catch (IOException) { }
        using (var prior = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "last-success.json"))))
        {
            var preserved = prior.RootElement.GetProperty("logs")[0].GetProperty("file").GetString()!;
            File.WriteAllText(log, "new run bytes");
            Assert.That(File.ReadAllText(preserved) == "historical bytes", Is.True, "Legacy logs were not snapshotted");
        }
        IEnumerable<int> Broken()
        { yield return 1; throw new IOException("publication"); }
        try
        { await RuntimeBootAttempt.RunAsync(home, "publication", a => { a.Publish(new { values = Broken() }); return Task.CompletedTask; }); }
        catch (IOException) { }
        Assert.That(!File.Exists(Path.Combine(dir, "acceptance.json")), Is.True, "Partial serialization published success");
        using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "current-run.json")));
        Assert.That(status.RootElement.GetProperty("status").GetString() == "failed", Is.True, "Publication failure missing");
    }

    [Test]
    [Platform(Include = TestPlatforms.WINDOWS, Reason = TestPlatforms.MSVC)]
    public async Task PublicationFailureStagesTest()
    {
        var scratch = TestEnvironment.Scratch();
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
                Assert.That(injected, Is.True, "Failure point not reached: " + file + "/" + stage);
                var viewFailure = file is "acceptance.json" or "last-success.json";
                Assert.That(failed != viewFailure, Is.True, "Commit boundary incorrect: " + file + "/" + stage);
                if (!viewFailure)
                    Assert.That(File.ReadAllText(Path.Combine(dir, "last-success.json")) == old, Is.True, "Uncommitted run advanced last success");
                RuntimeBootAttempt.Recover(dir);
                using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "current-run.json")));
                Assert.That(state.RootElement.GetProperty("status").GetString() == (viewFailure ? "succeeded" : "failed"), Is.True, "Recovered state incorrect");
                Assert.That(File.Exists(Path.Combine(dir, "acceptance.json")) == viewFailure, Is.True, "Recovered current acceptance incorrect");
                Assert.That(!Directory.EnumerateFiles(dir, "*.tmp", SearchOption.AllDirectories).Any(), Is.True, "Temporary publication file leaked");
                if (viewFailure)
                    Assert.That(File.ReadAllText(Path.Combine(dir, "last-success.json")) != old, Is.True, "Committed success not recovered");
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
        Assert.That(rejected && File.ReadAllText(Path.Combine(output, "last-success.json")) == previous, Is.True, "Original Q1.2 reproduction persists");
        using (var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "current-run.json"))))
            Assert.That(state.RootElement.GetProperty("status").GetString() == "failed", Is.True, "Blocked run status hid current failure");
        try
        { await RuntimeBootAttempt.RunAsync(broken, "after-publish", a => { a.Publish(new { fixture = "bad" }); throw new IOException("after publish"); }); }
        catch (IOException e) { Assert.That(e.Message == "after publish", Is.True, "Original exception replaced"); }
        Assert.That(File.ReadAllText(Path.Combine(output, "last-success.json")) == previous, Is.True, "Throw after Publish committed evidence");
        var interrupted = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "current-run.json")))!.AsObject();
        interrupted["status"] = "running";
        File.WriteAllText(Path.Combine(output, "current-run.json"), interrupted.ToJsonString());
        File.WriteAllText(Path.Combine(output, "acceptance.json"), "stale");
        RuntimeBootAttempt.Recover(output);
        Assert.That(!File.Exists(Path.Combine(output, "acceptance.json")), Is.True, "Restart retained interrupted current acceptance");
        Assert.That(JsonNode.Parse(File.ReadAllText(Path.Combine(output, "current-run.json")))!["status"]!.GetValue<string>() == "interrupted", Is.True, "Restart did not classify interrupted attempt");
    }

    #endregion
}
