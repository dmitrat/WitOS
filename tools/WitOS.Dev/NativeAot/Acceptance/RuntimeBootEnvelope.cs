namespace WitOS.Dev.NativeAot.Acceptance;

// Complete ordered wire shape. Semantic values are checked by RuntimeBootProtocol.
// Keeping the envelope separate makes duplicates and misplaced reports fail closed.
internal static class RuntimeBootEnvelope
{
    private const string USER = "[USER] [RUNTIME] ";
    internal static bool Validate(string[] lines) => Validate(lines, out _);

    internal static bool Validate(string[] lines, out string error)
    {
        error = "Missing or misplaced runtime boundary.";
        var wire = lines.Where(line => line.Length != 0).ToArray();
        var begin = Array.IndexOf(wire, "[TEST-PASS] Runtime.InvalidHandoffTeardown");
        if (begin < 0 || wire[..begin].Any(line => line.StartsWith("Runtime ", StringComparison.Ordinal) || line.StartsWith("[TEST-PASS] Runtime.", StringComparison.Ordinal) || line.StartsWith(USER, StringComparison.Ordinal)))
            return false;
        var cursor = new Cursor(wire, begin);
        bool Parse()
        {
            if (!cursor.Exact("[TEST-PASS] Runtime.InvalidHandoffTeardown"))
                return false;
            foreach (var address in RuntimeBootProtocol.IMAGE_BASES)
            {
                if (!cursor.Exact($"Runtime native fault base: 0x{address:X16}") || !cursor.Startup() ||
                    !cursor.Exact(USER + "native fault probe entered") || !cursor.Fatal() ||
                    !cursor.Exact("[TEST-PASS] Runtime.NativeFaultContained"))
                    return false;
            }
            foreach (var address in RuntimeBootProtocol.IMAGE_BASES)
            {
                if (!cursor.Exact($"Runtime managed stack overflow base: 0x{address:X16}") || !cursor.Startup() ||
                    !cursor.Exact(USER + "managed stack overflow probe entered"))
                    return false;
                var frames = 0;
                while (cursor.Peek == USER + "managed stack frame")
                { cursor.Exact(cursor.Peek); ++frames; }
                if (frames is < 4 or > 64 || !cursor.Prefix("[USER-FAULT] ") ||
                    !cursor.Prefix("Runtime stack fault vector/error/address/low: ") ||
                    !cursor.Exact("[TEST-PASS] Runtime.ManagedStackOverflowContained"))
                    return false;
            }
            foreach (var address in RuntimeBootProtocol.IMAGE_BASES)
            {
                if (!cursor.Exact($"Runtime init failure base: 0x{address:X16}") || !cursor.Startup() ||
                    !cursor.BookkeepingFailure() ||
                    !cursor.Exact("[USER] WitOS GC startup failure: hr = g_pGCHeap->Initialize();") ||
                    !cursor.Exact("[USER] WitOS startup failure: InitializeGC()") ||
                    !cursor.Exact("[USER] WitOS startup failure: InitDLL(PalGetModuleHandleFromPointer((void*)&RhInitialize))") ||
                    !cursor.Prefix(USER + "wmain returned ") || !cursor.Prefix("Runtime init failure exit/commits: ") ||
                    !cursor.Exact("[TEST-PASS] Runtime.GcInitFailureTeardown"))
                    return false;
            }
            for (var mode = 0; mode < 4; ++mode)
                foreach (var address in RuntimeBootProtocol.IMAGE_BASES)
                {
                    if (!cursor.Exact($"Runtime abrupt mode/base: {mode}/0x{address:X16}") || !cursor.Startup() ||
                        (mode >= 2 && !cursor.Fatal()) || !cursor.Prefix("Runtime abrupt exit/report: ") ||
                        !cursor.Exact("[TEST-PASS] Runtime.AbruptWorkerContained"))
                        return false;
                }
            foreach (var address in RuntimeBootProtocol.EXECUTION_BASES)
            {
                if (!cursor.Exact($"Runtime boot image base: 0x{address:X16}") ||
                    !cursor.Exact("Runtime boot load status: 0") || !cursor.Exact("[TEST-PASS] Runtime.MemoryProfile") ||
                    !cursor.Startup() || !cursor.Prefix(USER + "hijack attempts/redirects/returns/unsafe: ") ||
                    !cursor.Exact(RuntimeBootProtocol.OOM) || !cursor.Exact(RuntimeBootProtocol.MANAGED_EH) ||
                    !cursor.Exact(RuntimeBootProtocol.WORKER))
                    return false;
                for (var cycle = 0; cycle < RuntimeBootProtocol.INTEGRATION_CYCLES; ++cycle)
                    if (!cursor.Exact(RuntimeBootProtocol.LIFECYCLE_AUDIT) || !cursor.Exact(RuntimeBootProtocol.CYCLE))
                        return false;
                if (!cursor.Exact(RuntimeBootProtocol.FINALIZATION) || !cursor.Exact(RuntimeBootProtocol.MANAGED_THREADS) ||
                    !cursor.Exact(RuntimeBootProtocol.THREAD_QUOTA) || !cursor.Prefix(USER + "wmain returned ") ||
                    !cursor.Prefix("Runtime boot state/exit/rip/owned: ") || !cursor.Prefix("Runtime hardware faults ") ||
                    !cursor.Prefix("Runtime managed commit failures: ") || !cursor.Prefix("Runtime parked foreign object waits: ") ||
                    !cursor.Exact("Runtime managed thread capacity failures: 4") ||
                    !cursor.Prefix("Runtime orderly thread completions: ") || !cursor.Prefix("Runtime execution ticks/limit: "))
                    return false;
            }
            return cursor.Exact("[TEST-PASS] Runtime.ManagedBootAndGc") &&
                cursor.Exact("[TEST-PASS] Runtime.RelocationAndTeardown") && !wire[cursor.Position..].Any(IsRuntime);
        }
        var valid = Parse();
        error = valid ? "" : $"Runtime protocol line {cursor.Position}: {cursor.Error}; got '{cursor.Peek}'.";
        return valid;
    }

    private static bool IsRuntime(string line) => line.StartsWith("Runtime ", StringComparison.Ordinal) ||
        line.StartsWith("[TEST-PASS] Runtime.", StringComparison.Ordinal) || line.StartsWith(USER, StringComparison.Ordinal) ||
        line.StartsWith("[USER] [NATIVE-FAIL-FAST]", StringComparison.Ordinal) || line.StartsWith("[USER] WitOS ", StringComparison.Ordinal);

    private sealed class Cursor(string[] lines, int position)
    {
        internal int Position { get; private set; } = position;
        internal string Error { get; private set; } = "Invalid block count";
        internal string Peek => Position < lines.Length ? lines[Position] : "";
        internal bool Exact(string value)
        {
            if (Position >= lines.Length || Peek != value)
            { Error = "Expected " + value; return false; }
            ++Position;
            return true;
        }
        internal bool Prefix(string value)
        {
            if (!Peek.StartsWith(value, StringComparison.Ordinal))
            { Error = "Expected prefix " + value; return false; }
            ++Position;
            return true;
        }
        internal bool Startup() => Exact(USER + "image published") && Exact(USER + "native TLS ready") &&
            Exact(USER + "native initializers ready") && Exact(USER + "entering upstream wmain");
        internal bool BookkeepingFailure()
        {
            if (!Peek.StartsWith("[USER] Committing ", StringComparison.Ordinal))
                return true;
            if (!System.Text.RegularExpressions.Regex.IsMatch(Peek,
                @"^\[USER\] Committing [1-9][0-9]* bytes \([0-9]+\.[0-9]+ mb\) for GC bookkeeping element#[0-9]+ failed\[USER\] $"))
                return false;
            ++Position;
            return true;
        }
        internal bool Fatal()
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Peek,
                @"^\[USER\] \[NATIVE-FAIL-FAST\] code=0xC000001D address=0x([0-9A-F]{16}) rip=0x\1$"))
                return false;
            ++Position;
            return true;
        }
    }
}
