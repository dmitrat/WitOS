using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.NativeAot.Acceptance;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.NativeAot.Acceptance;

/// <summary>
/// Runtime-boot protocol: envelope shape, per-base semantics, hosted proofs and malformed mutations.
/// </summary>
[TestFixture]
public sealed class RuntimeBootProtocolTests
{
    #region Functions

    [Test]
    public void MissingSecondWorkloadRejectedTest()
    {
        var valid = ProtocolFixtures.Valid;
        var broken = valid.Remove(valid.LastIndexOf(ProtocolFixtures.WORKER, StringComparison.Ordinal), ProtocolFixtures.WORKER.Length);
        Assert.That(Accept(valid), Is.True, "Valid two-base protocol rejected");
        Assert.That(!Accept(broken), Is.True, "Second workload evidence absent but accepted");
    }

    [Test]
    public void ManagedObjectSnapshotSchemaTest()
    {
        var hash = new string('a', 64);
        using var valid = JsonDocument.Parse(JsonSerializer.Serialize(new { file = "guest.pe", buildEvidence = new { inputs = new[] { new { file = "NativeAotBoot.obj", sha256 = hash } } } }));
        Assert.That(RuntimeBootProtocol.SharedManagedObjectHash(valid.RootElement) == hash, Is.True, "Nested managed input identity rejected");
        foreach (var bad in new[]{"{}",JsonSerializer.Serialize(new{inputs=new[]{new{file="NativeAotBoot.obj",sha256=hash}}}),
            JsonSerializer.Serialize(new{buildEvidence=new{inputs=new[]{new{file="NativeAotBoot.obj",sha256="bad"}}}}),
            JsonSerializer.Serialize(new{buildEvidence=new{inputs=new[]{new{file="NativeAotBoot.obj",sha256=hash},new{file="NativeAotBoot.obj",sha256=hash}}}})})
        {
            using var invalid = JsonDocument.Parse(bad);
            bool rejected = false;
            try
            { _ = RuntimeBootProtocol.SharedManagedObjectHash(invalid.RootElement); }
            catch (InvalidDataException) { rejected = true; }
            Assert.That(rejected, Is.True, "Malformed/ambiguous managed identity accepted");
        }
    }

    [Test]
    public void HostedSemanticContractTest()
    {
        var valid = string.Join("\n", Enumerable.Repeat(RuntimeBootProtocol.CYCLE[7..], 4)) + "\n" +
            RuntimeBootProtocol.FINALIZATION[7..] + "\n" + RuntimeBootProtocol.MANAGED_THREADS[7..] + "\n[RUNTIME] managed thread capacity reference passed: 4\n";
        Assert.That(RuntimeBootProtocol.ValidateHosted(valid, 42, false), Is.True, "Valid hosted semantic workload rejected");
        Assert.That(RuntimeBootProtocol.ValidateHostedLog(valid + "\nExit code: 42\n"), Is.True, "Persisted hosted proof rejected");
        foreach (var invalid in new[]{"",valid.Replace(RuntimeBootProtocol.CYCLE[7..]+"\n",""),valid+RuntimeBootProtocol.CYCLE[7..]+"\n",
            valid.Replace("48 releases","12 releases"),valid.Replace("capacity reference","quota recovery"),valid+"unexpected stderr\n"})
            Assert.That(!RuntimeBootProtocol.ValidateHosted(invalid, 42, false), Is.True, "Incomplete/mismatched hosted proof accepted");
        Assert.That(!RuntimeBootProtocol.ValidateHosted(valid, 0, false) && !RuntimeBootProtocol.ValidateHosted(valid, 42, true), Is.True, "Hosted exit/timeout ignored");
        Assert.That(!RuntimeBootProtocol.ValidateHostedLog(valid + "\nExit code: 0\n"), Is.True, "Wrong persisted exit accepted");
    }

    [Test]
    public void PerBaseProtocolMutationsTest()
    {
        var root = TestEnvironment.Root;
        var good = ProtocolFixtures.Valid;
        var second = good.LastIndexOf("Runtime boot image base:", StringComparison.Ordinal);
        Assert.That(Accept(good.Replace("\n", "\r\n")), Is.True, "CRLF protocol rejected");
        string[] mutations = [
            good.Replace(RuntimeBootProtocol.CYCLE,""),
            good.Replace(RuntimeBootProtocol.LIFECYCLE_AUDIT,""),
            good.Replace(RuntimeBootProtocol.LIFECYCLE_AUDIT+"\n"+RuntimeBootProtocol.CYCLE,RuntimeBootProtocol.CYCLE+"\n"+RuntimeBootProtocol.LIFECYCLE_AUDIT),
            good.Replace("Runtime execution ticks/limit: 120/3000","Runtime execution ticks/limit: 3000/3000"),
            good.Replace(RuntimeBootProtocol.THREAD_QUOTA,""),
            good.Replace("Runtime managed thread capacity failures: 4","Runtime managed thread capacity failures: 0"),
            good.Replace("[TEST-PASS] Runtime.ManagedStackOverflowContained",""),
            good.Replace("[USER] [RUNTIME] managed stack frame",""),
            good.Replace("/0x0000008000015000","/0x0000008000025000"),
            good.Replace("[TEST-PASS] Runtime.ManagedStackOverflowContained","[USER] [RUNTIME] unexpected stack-finally cleanup\n[TEST-PASS] Runtime.ManagedStackOverflowContained"),
            good.Replace(RuntimeBootProtocol.MANAGED_THREADS, ""),
            good.Replace("Runtime parked foreign object waits: 2", "Runtime parked foreign object waits: 0"),
            good.Replace(RuntimeBootProtocol.FINALIZATION, ""),
            good.Replace("48 releases + 8 resurrection passes", "12 releases + 1 resurrection passes"),
            good.Replace(RuntimeBootProtocol.MANAGED_EH, ""),
            good.Insert(second, RuntimeBootProtocol.MANAGED_EH+"\n"),
            good.Replace("0x0000008000180000","0x0000008000100000"),
            good+"[PANIC] injected\n",good+"[EXCEPTION] injected\n",
            good.Insert(second,ProtocolFixtures.WORKER+"\n"),
            good.Replace("[USER] [RUNTIME] native TLS ready\n",""),
            good.Replace("[USER] [RUNTIME] native TLS ready","[USER] [RUNTIME] image published"),
            good.Replace("[USER] [RUNTIME] native TLS ready\n[USER] [RUNTIME] native initializers ready","[USER] [RUNTIME] native initializers ready\n[USER] [RUNTIME] native TLS ready"),
            good[..second]+good[second..].Replace("wmain returned 0x000000000000002A","wmain returned 0x000000000000002B"),
            good.Replace("0000000000000003/0000000000000001/0000000000000001/0000000000000001","0000000000000001/0000000000000000/0000000000000000/0000000000000001"),
            good.Replace("Runtime hardware faults read/write/divide/continue: 12/12/12/36","Runtime hardware faults read/write/divide/continue: 0/0/0/0"),
            good.Replace("Runtime managed commit failures: 2","Runtime managed commit failures: 0"),
            good.Replace("[USER] [RUNTIME] managed OOM recovery passed: 3 hard-limit + 1 backing-pressure", ""),
            good.Replace("Runtime init failure exit/commits: 0x00000000FFFFFFFF/1","Runtime init failure exit/commits: 0x00000000FFFFFFFF/0"),
            good.Replace("[USER] WitOS GC startup failure: hr = g_pGCHeap->Initialize();", ""),
            good.Replace("Runtime abrupt exit/report: 0x00000000FFFF0002/1/0/0/0","Runtime abrupt exit/report: 0x00000000FFFF0002/1/1/0/0"),
            good.Replace("[TEST-PASS] Runtime.AbruptWorkerContained", ""),
            good.Replace("Runtime orderly thread completions: 43","Runtime orderly thread completions: 0"),
            good.Replace("[TEST-PASS] Runtime.RelocationAndTeardown", "")];
        foreach (var mutation in mutations)
            Assert.That(!Accept(mutation), Is.True, "Malformed protocol accepted");
        Assert.That(!RuntimeBootProtocol.Validate(good, 33, true) && !RuntimeBootProtocol.Validate(good, 35, false), Is.True, "Timeout/exit ignored");
        var header = File.ReadAllText(Path.Combine(root, "src/Kernel/include/witos/user_layout.h"));
        var bases = Regex.Matches(header, @"#define WIT_USER_IMAGE_(?:BASE|ALTERNATE) 0x([0-9a-fA-F]+)ULL").Select(m => Convert.ToUInt64(m.Groups[1].Value, 16));
        Assert.That(bases.SequenceEqual(RuntimeBootProtocol.IMAGE_BASES), Is.True, "Kernel/host base contract drift");
    }

    [Test]
    public void ProtocolEnvelopeTest()
    {
        var root = TestEnvironment.Root;
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
            Assert.That(RuntimeBootEnvelope.Validate(good.Split('\n'), out var diagnostic), Is.True, diagnostic);
            Assert.That(RuntimeBootProtocol.Validate(good, 33, false), Is.True, "Baseline semantic protocol rejected");
            const string verdict = "[TEST-PASS] Runtime.NativeFaultContained\n";
            const string capacity = "Runtime managed thread capacity failures: 4";
            var start = good.IndexOf("Runtime boot image base:", StringComparison.Ordinal);
            var afterHeader = good.IndexOf('\n', start) + 1;
            string[] bad = [
                good.Insert(afterHeader, "[USER] [NATIVE-FAIL-FAST] code=0xC000001D address=0x0000008000100000 rip=0x0000008000100000\n"),
                good.Replace(capacity, capacity + "\nRuntime managed thread capacity failures: 0"),
                good.Replace(verdict, "") + verdict + verdict,
                good + RuntimeBootProtocol.FINALIZATION + "\n",
                RuntimeBootProtocol.FINALIZATION + "\n" + good,
                good.Replace("Runtime native fault base: 0x0000008000180000", "Runtime native fault base: 0x0000008000100000"),
                good.Replace("[USER] [RUNTIME] native fault probe entered\n", ""),
                good.Replace("Runtime managed stack overflow base:", "Runtime init failure base:")];
            for (var i = 0; i < bad.Length; ++i)
                Assert.That(!RuntimeBootProtocol.Validate(bad[i], 33, false), Is.True, $"Protocol mutation {i} accepted");
        }
        // Every runtime report is unique to a position, even when duplicated verbatim.
        foreach (var line in ProtocolFixtures.Valid.Split('\n').Where(s => s.StartsWith("Runtime ") || s.StartsWith("[TEST-PASS] Runtime.")))
            Assert.That(!RuntimeBootProtocol.Validate(ProtocolFixtures.Valid + line + "\n", 33, false), Is.True, "Trailing report accepted: " + line);
    }

    #endregion

    #region Tools

    private static bool Accept(string log) => RuntimeBootProtocol.Validate(log, 33, false);

    #endregion
}
