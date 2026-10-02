using System.Globalization;
using System.Text.RegularExpressions;

namespace WitOS.Dev;

internal static class RuntimeBootProtocol
{
    // Versioned fixture contract; these are two distinct kernel-reported bases,
    // not two matches anywhere in a concatenated log.
    internal static readonly ulong[] ImageBases = [0x8000100000, 0x8000180000];
    internal static readonly ulong[] ExecutionBases = [.. ImageBases, .. ImageBases];
    internal const int IntegrationCycles = 4;
    internal const string Cycle = "[USER] [RUNTIME] integration cycle passed";
    internal const string LifecycleAudit = "[USER] [RUNTIME] managed lifecycle audit passed: main+finalizer";
    internal const int WorkersPerExecution = 43;
    internal static int StackFaultsPerProfile => ImageBases.Length;
    internal const string Worker = "[USER] [RUNTIME] worker attach/detach/reuse/rollback and foreign GC/hijack/service-guard/exit-GC passed";
    internal const string Oom = "[USER] [RUNTIME] managed OOM recovery passed: 3 hard-limit + 1 backing-pressure";
    internal const string ManagedEh = "[USER] [RUNTIME] managed EH workers passed: 2 (filters/rethrow/nested-finally/native-release)";
    internal const string Finalization = "[USER] [RUNTIME] managed finalization passed: 48 releases + 8 resurrection passes + suppression";
    internal const string ManagedThreads = "[USER] [RUNTIME] managed threads passed: 28 (Thread/Join/Monitor/TLS/GC)";
    internal const string ThreadQuota = "[USER] [RUNTIME] managed thread quota recovery passed: 4";
    private static readonly string[] Phases = ["Runtime boot load status: 0","[TEST-PASS] Runtime.MemoryProfile",
        "[USER] [RUNTIME] image published","[USER] [RUNTIME] native TLS ready","[USER] [RUNTIME] native initializers ready",
        "[USER] [RUNTIME] entering upstream wmain",Oom,ManagedEh,Worker,Finalization,ManagedThreads,ThreadQuota];

    internal static string SharedManagedObjectHash(System.Text.Json.JsonElement input)
    {
        if (!input.TryGetProperty("buildEvidence", out var build) || !build.TryGetProperty("inputs", out var inputs) ||
            inputs.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidDataException("Guest input snapshot lacks nested build evidence.");
        var matches = inputs.EnumerateArray().Where(e => e.TryGetProperty("file", out var file) &&
            file.ValueKind == System.Text.Json.JsonValueKind.String && Path.GetFileName(file.GetString()) == "NativeAotBoot.obj").ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("sha256", out var hash) || hash.ValueKind != System.Text.Json.JsonValueKind.String)
            throw new InvalidDataException("Guest input snapshot lacks one managed object identity.");
        var value = hash.GetString()!;
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Malformed managed object hash.");
        return value;
    }

    internal static bool ValidateHosted(string output, int exitCode, bool timedOut)
    {
        if (timedOut || exitCode != 42)
            return false;
        var lines = output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] expected = [..Enumerable.Repeat(Cycle[7..],IntegrationCycles),Finalization[7..],ManagedThreads[7..],
            "[RUNTIME] managed thread capacity reference passed: 4"];
        return lines.SequenceEqual(expected);
    }

    internal static bool ValidateHostedLog(string text)
    {
        const string suffix = "\nExit code: 42\n";
        return text.EndsWith(suffix, StringComparison.Ordinal) && ValidateHosted(text[..^suffix.Length], 42, false);
    }

    public static bool Validate(string output, int exitCode, bool timedOut)
    {
        if (timedOut || exitCode != 33 || output.Contains("[PANIC]", StringComparison.Ordinal) || output.Contains("[EXCEPTION]", StringComparison.Ordinal) ||
            output.Contains("[RUNTIME-WORKER-FAIL]", StringComparison.Ordinal))
            return false;
        var lines = output.Replace("\r\n", "\n").Split('\n');
        if (!RuntimeBootEnvelope.Validate(lines))
            return false;
        var starts = lines.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("Runtime boot image base:", StringComparison.Ordinal)).ToArray();
        if (starts.Length != ExecutionBases.Length)
            return false;
        var nativeFaultBases = lines.Where(l => l.StartsWith("Runtime native fault base: ", StringComparison.Ordinal)).ToArray();
        if (!nativeFaultBases.SequenceEqual(ImageBases.Select(b => $"Runtime native fault base: 0x{b:X16}")) ||
            lines.Count(l => l == "[TEST-PASS] Runtime.NativeFaultContained") != 2)
            return false;
        var abrupt = lines.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("Runtime abrupt mode/base: ", StringComparison.Ordinal)).ToArray();
        if (abrupt.Length != 8 || abrupt[^1].index >= starts[0].index)
            return false;
        for (int n = 0; n < abrupt.Length; ++n)
        {
            int mode = n / 2;
            if (abrupt[n].line != $"Runtime abrupt mode/base: {mode}/0x{ImageBases[n % 2]:X16}")
                return false;
            var block = lines[(abrupt[n].index + 1)..(n + 1 < abrupt.Length ? abrupt[n + 1].index : starts[0].index)];
            var report = block.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("Runtime abrupt exit/report: ", StringComparison.Ordinal)).ToArray();
            var done = block.Select((line, index) => (line, index)).Where(x => x.line == "[TEST-PASS] Runtime.AbruptWorkerContained").ToArray();
            var entered = block.Select((line, index) => (line, index)).Where(x => x.line == "[USER] [RUNTIME] entering upstream wmain").ToArray();
            var expected = mode < 2 ? "0x00000000FFFF0002" : "0x00000000C000001D";
            if (report.Length != 1 || done.Length != 1 || entered.Length != 1 || entered[0].index >= report[0].index || report[0].index >= done[0].index ||
                report[0].line != $"Runtime abrupt exit/report: {expected}/1/0/0/0" || block.Contains(Worker) ||
                block.Any(l => l.StartsWith("[USER] [RUNTIME] wmain returned", StringComparison.Ordinal)))
                return false;
            var fatals = block.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("[USER] [NATIVE-FAIL-FAST]", StringComparison.Ordinal)).ToArray();
            if (mode < 2 ? fatals.Length != 0 : fatals.Length != 1)
                return false;
            if (mode >= 2 && (!fatals[0].line.StartsWith("[USER] [NATIVE-FAIL-FAST] code=0xC000001D ", StringComparison.Ordinal) ||
                fatals[0].index <= entered[0].index || fatals[0].index >= report[0].index))
                return false;
        }
        var init = lines.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("Runtime init failure base: ", StringComparison.Ordinal)).ToArray();
        if (init.Length != ImageBases.Length || init[^1].index >= abrupt[0].index)
            return false;
        var stacks = lines.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("Runtime managed stack overflow base: ", StringComparison.Ordinal)).ToArray();
        if (stacks.Length != 2 || stacks[^1].index >= init[0].index)
            return false;
        for (int n = 0; n < stacks.Length; ++n)
        {
            if (stacks[n].line != $"Runtime managed stack overflow base: 0x{ImageBases[n]:X16}")
                return false;
            var block = lines[(stacks[n].index + 1)..(n + 1 < stacks.Length ? stacks[n + 1].index : init[0].index)];
            int entered = Array.IndexOf(block, "[USER] [RUNTIME] entering upstream wmain");
            int probe = Array.IndexOf(block, "[USER] [RUNTIME] managed stack overflow probe entered");
            var frames = block.Select((line, index) => (line, index)).Where(x => x.line == "[USER] [RUNTIME] managed stack frame").ToArray();
            var faults = block.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("[USER-FAULT] ", StringComparison.Ordinal)).ToArray();
            var reports = block.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("Runtime stack fault vector/error/address/low: ", StringComparison.Ordinal)).ToArray();
            int done = Array.IndexOf(block, "[TEST-PASS] Runtime.ManagedStackOverflowContained");
            if (entered < 0 || probe <= entered || frames.Length < 4 || frames.Length > 64 || frames[0].index <= probe || faults.Length != 1 ||
                faults[0].index <= frames[^1].index || reports.Length != 1 || reports[0].index <= faults[0].index || done <= reports[0].index ||
                block.Any(l => l.Contains("unexpected stack", StringComparison.Ordinal) || l.StartsWith("[USER] [RUNTIME] wmain returned", StringComparison.Ordinal) || l.StartsWith("[USER] [NATIVE-FAIL-FAST]", StringComparison.Ordinal)))
                return false;
            var report = Regex.Match(reports[0].line, @"^Runtime stack fault vector/error/address/low: 14/0x000000000000000([46])/0x([0-9A-F]{16})/0x([0-9A-F]{16})$");
            if (!report.Success)
                return false;
            ulong address = ulong.Parse(report.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            ulong low = ulong.Parse(report.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (low < 4096 || address >= low || address < low - 4096 || !Regex.IsMatch(faults[0].line,
                @"^\[USER-FAULT\] id=[0-9]+ vector=14 error=0x000000000000000" + report.Groups[1].Value + @" address=0x" + report.Groups[2].Value + @" cs=0x0000000000000033$"))
                return false;
        }
        for (int n = 0; n < init.Length; ++n)
        {
            if (init[n].line != $"Runtime init failure base: 0x{ImageBases[n]:X16}")
                return false;
            var block = lines[(init[n].index + 1)..(n + 1 < init.Length ? init[n + 1].index : abrupt[0].index)];
            string[] required = ["[USER] [RUNTIME] image published","[USER] [RUNTIME] native TLS ready",
                "[USER] [RUNTIME] native initializers ready","[USER] [RUNTIME] entering upstream wmain",
                "[USER] WitOS GC startup failure: hr = g_pGCHeap->Initialize();",
                "[USER] WitOS startup failure: InitializeGC()","[TEST-PASS] Runtime.GcInitFailureTeardown"];
            int previous = -1;
            foreach (var phase in required)
            {
                var matches = block.Select((line, index) => (line, index)).Where(x => x.line == phase).ToArray();
                if (matches.Length != 1 || matches[0].index <= previous)
                    return false;
                previous = matches[0].index;
            }
            var failure = block.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("Runtime init failure exit/commits: ", StringComparison.Ordinal)).ToArray();
            var returns = block.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("[USER] [RUNTIME] wmain returned", StringComparison.Ordinal)).ToArray();
            if (failure.Length != 1 || returns.Length != 1 || returns[0].index <= Array.IndexOf(block, required[5]) ||
                returns[0].index >= failure[0].index || failure[0].index >= previous ||
                !Regex.IsMatch(failure[0].line, @"^Runtime init failure exit/commits: 0x00000000FFFFFFFF/[1-9][0-9]*$") ||
                !Regex.IsMatch(returns[0].line, @"^\[USER\] \[RUNTIME\] wmain returned 0x00000000FFFFFFFF last-error=0x[0-9A-F]{16}$") ||
                block.Contains(Worker) || block.Contains(Oom))
                return false;
        }
        int Unique(string marker) => Array.FindAll(lines, l => l == marker).Length == 1 ? Array.IndexOf(lines, marker) : -1;
        var invalid = Unique("[TEST-PASS] Runtime.InvalidHandoffTeardown");
        var managed = Unique("[TEST-PASS] Runtime.ManagedBootAndGc");
        var teardown = Unique("[TEST-PASS] Runtime.RelocationAndTeardown");
        if (invalid < 0 || invalid >= starts[0].index || managed <= starts[^1].index || teardown <= managed)
            return false;
        for (int n = 0; n < starts.Length; n++)
        {
            if (starts[n].line != $"Runtime boot image base: 0x{ExecutionBases[n]:X16}")
                return false;
            int end = n + 1 < starts.Length ? starts[n + 1].index : managed;
            var block = lines[(starts[n].index + 1)..end];
            int previous = -1;
            foreach (var phase in Phases)
            {
                var indices = block.Select((line, index) => (line, index)).Where(x => x.line == phase).Select(x => x.index).ToArray();
                if (indices.Length != 1 || indices[0] <= previous)
                    return false;
                previous = indices[0];
            }
            var cycles = block.Select((line, index) => (line, index)).Where(x => x.line == Cycle || x.line == LifecycleAudit).ToArray();
            if (cycles.Length != IntegrationCycles * 2 || cycles[0].index <= Array.IndexOf(block, Worker) || cycles[^1].index >= Array.IndexOf(block, Finalization))
                return false;
            for (int cycle = 0; cycle < IntegrationCycles; ++cycle)
                if (cycles[cycle * 2].line != LifecycleAudit || cycles[cycle * 2 + 1].line != Cycle)
                    return false;
            var counters = block.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("[USER] [RUNTIME] hijack attempts/", StringComparison.Ordinal)).ToArray();
            if (counters.Length != 1 || counters[0].index <= Array.IndexOf(block, Phases[5]) || counters[0].index >= previous)
                return false;
            var values = Regex.Match(counters[0].line, @"^\[USER\] \[RUNTIME\] hijack attempts/redirects/returns/unsafe: ([0-9A-F]{16})/([0-9A-F]{16})/([0-9A-F]{16})/([0-9A-F]{16})$");
            if (!values.Success)
                return false;
            var v = Enumerable.Range(1, 4).Select(i => ulong.Parse(values.Groups[i].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            if (v[0] == 0 || (v[1] == 0 || v[2] == 0) || v[3] == 0 || v.Skip(1).Any(count => count > v[0]))
                return false;
            var returns = block.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("[USER] [RUNTIME] wmain returned", StringComparison.Ordinal)).ToArray();
            if (returns.Length != 1 || returns[0].index <= previous || !Regex.IsMatch(returns[0].line, @"^\[USER\] \[RUNTIME\] wmain returned 0x000000000000002A last-error=0x[0-9A-F]{16}$"))
                return false;
            var states = block.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("Runtime boot state/", StringComparison.Ordinal)).ToArray();
            if (states.Length != 1 || states[0].index <= returns[0].index || !Regex.IsMatch(states[0].line, @"^Runtime boot state/exit/rip/owned: 3/0x000000000000002A/0x0000000000000000/[1-9][0-9]*$"))
                return false;
            var faults = block.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("Runtime hardware faults ", StringComparison.Ordinal)).ToArray();
            if (faults.Length != 1 || faults[0].index <= states[0].index || faults[0].line != "Runtime hardware faults read/write/divide/continue: 12/12/12/36")
                return false;
            var failedCommits = block.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("Runtime managed commit failures: ", StringComparison.Ordinal)).ToArray();
            if (failedCommits.Length != 1 || failedCommits[0].index <= faults[0].index || !Regex.IsMatch(failedCommits[0].line, @"^Runtime managed commit failures: [1-9][0-9]*$"))
                return false;
            var parked = block.Where(l => l.StartsWith("Runtime parked foreign object waits: ", StringComparison.Ordinal)).ToArray();
            if (parked.Length != 1 || !Regex.IsMatch(parked[0], @"^Runtime parked foreign object waits: [1-9][0-9]*$"))
                return false;
            if (block.Count(l => l == "Runtime managed thread capacity failures: 4") != 1)
                return false;
            var budgets = block.Where(l => l.StartsWith("Runtime execution ticks/limit: ", StringComparison.Ordinal)).ToArray();
            if (budgets.Length != 1)
                return false;
            var budget = Regex.Match(budgets[0], @"^Runtime execution ticks/limit: ([0-9]+)/3000$");
            if (!budget.Success || !ulong.TryParse(budget.Groups[1].Value, out var usedTicks) || usedTicks >= 3000)
                return false;
            var orderly = block.Select((line, index) => (line, index)).Where(x => x.line.StartsWith("Runtime orderly thread completions: ", StringComparison.Ordinal)).ToArray();
            if (orderly.Length != 1 || orderly[0].index <= failedCommits[0].index || orderly[0].line != $"Runtime orderly thread completions: {WorkersPerExecution}")
                return false;
        }
        // No extra workload claims outside either validated execution block.
        return lines.Count(l => l == Worker) == ExecutionBases.Length && lines.Count(l => l == Cycle) == ExecutionBases.Length * IntegrationCycles && lines.Count(l => l == LifecycleAudit) == ExecutionBases.Length * IntegrationCycles;
    }
}
