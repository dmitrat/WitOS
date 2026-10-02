using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WitOS.Dev;

// current-run.json (schema 3) is the sole atomic commit record, including the
// last committed success pointer/hash. All other top-level JSON files are views.
// Readers needing an authoritative verdict must read this record and its hash.
internal sealed class RuntimeBootAttempt : IDisposable
{
    private readonly string directory;
    private readonly FileStream lease;
    private readonly string command;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private JsonObject? evidence;
    private JsonObject? lastSuccess;
    private bool committed;
    private bool startedAttempt;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal static Action<string, string>? BeforeWrite { get; set; } // Failure injection; serialized tests only.
    public string RunId { get; } = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
    public string RunDirectory { get; }

    private RuntimeBootAttempt(string directory, string command, FileStream lease)
    {
        this.directory = directory;
        this.command = command;
        this.lease = lease;
        RunDirectory = Path.Combine(directory, "runs", RunId);
        Directory.CreateDirectory(RunDirectory);
    }

    public static Task RunAsync(string root, string command, Func<RuntimeBootAttempt, Task> action)
        => RunInDirectoryAsync(Path.Combine(root, "artifacts/x64/runtime-boot"), command, action);

    internal static async Task RunInDirectoryAsync(string directory, string command, Func<RuntimeBootAttempt, Task> action)
    {
        Directory.CreateDirectory(directory);
        var lease = new FileStream(Path.Combine(directory, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        RuntimeBootAttempt attempt;
        try
        { attempt = new(directory, command, lease); }
        catch { lease.Dispose(); throw; }
        using (attempt)
        {
            try
            {
                Recover(directory);
                attempt.Start();
                await action(attempt);
                if (attempt.evidence is null)
                    throw new InvalidOperationException("Guest attempt did not publish acceptance.");
                attempt.Commit();
            }
            catch (Exception error)
            {
                // A failed diagnostic write must not hide the original error.
                if (attempt.startedAttempt && !attempt.committed)
                    attempt.Fail(error);
                throw;
            }
        }
    }

    private void Start()
    {
        var state = ReadObject(Path.Combine(directory, "current-run.json"));
        lastSuccess = state?["lastSuccess"]?.DeepClone() as JsonObject;
        if (state?["schemaVersion"]?.GetValue<int>() != 3)
            MigrateLegacy();
        startedAttempt = true;
        File.Delete(Path.Combine(directory, "acceptance.json"));
        var running = State("running", null, lastSuccess);
        Atomic(Path.Combine(directory, "current-run.json"), running);
        Atomic(Path.Combine(RunDirectory, "status.json"), running);
    }

    private void MigrateLegacy()
    {
        var current = Path.Combine(directory, "acceptance.json");
        var source = File.Exists(current) ? current : Path.Combine(directory, "last-success.json");
        if (!File.Exists(source))
            return;
        var previous = Path.Combine(RunDirectory, "previous-acceptance.json");
        File.Copy(source, previous);
        var node = ReadObject(previous);
        if (node is null)
            return; // Retain corrupt bytes as diagnostics, never success.
        if (node["logs"] is JsonArray logs)
            foreach (var item in logs.OfType<JsonObject>())
            {
                var file = item["file"]?.GetValue<string>();
                var oldLogs = Path.GetFullPath(Path.Combine(directory, "../../logs")) + Path.DirectorySeparatorChar;
                if (file is null || !File.Exists(file) || !Path.GetFullPath(file).StartsWith(oldLogs, StringComparison.OrdinalIgnoreCase))
                    continue;
                var copy = Path.Combine(RunDirectory, "previous-" + Path.GetFileName(file));
                File.Copy(file, copy);
                item["file"] = copy;
            }
        Atomic(previous, node);
        lastSuccess = Pointer(previous);
        Atomic(Path.Combine(directory, "last-success.json"), node);
    }

    public string Snapshot(string file)
    {
        var copy = Path.Combine(RunDirectory, Path.GetFileName(file));
        File.Copy(file, copy, overwrite: false);
        return copy;
    }

    // Prepare only. Even an exception after Publish cannot advance last-success.
    public void Publish(object value)
    {
        if (evidence is not null || committed)
            throw new InvalidOperationException("Attempt already published.");
        evidence = JsonSerializer.SerializeToNode(value, Json)!.AsObject();
        evidence["schemaVersion"] = 2;
        evidence["runId"] = RunId;
        evidence["command"] = command;
        evidence["startedUtc"] = started;
        evidence["finishedUtc"] = DateTimeOffset.UtcNow;
    }

    private void Commit()
    {
        var path = Path.Combine(RunDirectory, "acceptance.json");
        Atomic(path, evidence!);
        var success = State("succeeded", null, Pointer(path));
        Atomic(Path.Combine(RunDirectory, "status.json"), success);
        // This rename is the single success commit point. No throwing operations
        // follow it: failed view updates are warnings, repaired on next access.
        Atomic(Path.Combine(directory, "current-run.json"), success);
        committed = true;
        RepairViews(directory, success);
    }

    private JsonObject State(string status, string? error, JsonObject? previous) => new()
    {
        ["schemaVersion"] = 3,
        ["runId"] = RunId,
        ["command"] = command,
        ["status"] = status,
        ["startedUtc"] = started,
        ["finishedUtc"] = status == "running" ? null : JsonValue.Create(DateTimeOffset.UtcNow),
        ["error"] = error,
        ["acceptance"] = status == "succeeded" ? Path.Combine(RunDirectory, "acceptance.json") : null,
        ["lastSuccess"] = previous?.DeepClone()
    };

    private void Fail(Exception error)
    {
        TryDiagnostic(() => File.Delete(Path.Combine(directory, "acceptance.json")), error);
        var failed = State("failed", error.Message, lastSuccess);
        // Independent writes: a blocked per-run path must not leave current running.
        TryDiagnostic(() => Atomic(Path.Combine(directory, "current-run.json"), failed), error);
        TryDiagnostic(() => Atomic(Path.Combine(RunDirectory, "status.json"), failed), error);
        TryDiagnostic(() => File.Delete(Path.Combine(RunDirectory, "acceptance.json")), error);
    }

    internal static void Recover(string directory)
    {
        var state = ReadObject(Path.Combine(directory, "current-run.json"));
        if (state?["schemaVersion"]?.GetValue<int>() != 3)
            return;
        // Runs can call recovery only while owning run.lock (tests use isolated roots).
        if (state["status"]?.GetValue<string>() == "running")
        {
            state["status"] = "interrupted";
            state["error"] = "Previous attempt ended without a commit.";
            state["finishedUtc"] = DateTimeOffset.UtcNow;
            Atomic(Path.Combine(directory, "current-run.json"), state);
        }
        var run = Path.Combine(directory, "runs", state["runId"]!.GetValue<string>());
        if (state["status"]?.GetValue<string>() != "succeeded")
            TryDiagnostic(() => File.Delete(Path.Combine(run, "acceptance.json")));
        TryDiagnostic(() => Atomic(Path.Combine(run, "status.json"), state));
        RepairViews(directory, state);
    }

    private static void RepairViews(string directory, JsonObject state)
    {
        TryDiagnostic(() =>
        {
            if (state["lastSuccess"] is not JsonObject pointer)
                return;
            var file = pointer["file"]!.GetValue<string>();
            var bytes = File.ReadAllBytes(file);
            if (Hash(bytes) != pointer["sha256"]!.GetValue<string>())
                throw new InvalidDataException("Committed acceptance hash changed.");
            AtomicBytes(Path.Combine(directory, "last-success.json"), bytes);
            if (state["status"]!.GetValue<string>() == "succeeded")
                AtomicBytes(Path.Combine(directory, "acceptance.json"), bytes);
        });
        if (state["status"]?.GetValue<string>() != "succeeded")
            TryDiagnostic(() => File.Delete(Path.Combine(directory, "acceptance.json")));
    }

    private static JsonObject Pointer(string path) => new() { ["file"] = path, ["sha256"] = Hash(File.ReadAllBytes(path)) };
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static JsonObject? ReadObject(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { return null; }
    }
    private static void TryDiagnostic(Action write, Exception? primary = null)
    {
        try
        { write(); }
        catch (Exception secondary) when (secondary is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (primary is not null)
                primary.Data["EvidenceWriteError"] = secondary.ToString();
            Console.Error.WriteLine($"WARNING: evidence view requires recovery: {secondary.Message}");
        }
    }
    private static void Atomic(string path, JsonNode value) => AtomicBytes(path, System.Text.Encoding.UTF8.GetBytes(value.ToJsonString(Json)));
    private static void AtomicBytes(string path, byte[] value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            BeforeWrite?.Invoke(path, "write");
            File.WriteAllBytes(temporary, value);
            BeforeWrite?.Invoke(path, "rename");
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() => lease.Dispose();
}
