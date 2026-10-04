namespace WitOS.Dev.NativeAot.Acceptance;

/// <summary>
/// Complete ordered wire shape. Semantic values are checked by RuntimeBootProtocol.
/// Keeping the envelope separate makes duplicates and misplaced reports fail closed.
/// </summary>
internal static class RuntimeBootEnvelope
{
    #region Constants

    internal const string USER = "[USER] [RUNTIME] ";

    #endregion

    #region Functions

    /// <summary>
    /// Checks the ordered wire shape of a runtime-boot log.
    /// </summary>
    /// <param name="lines">Log lines.</param>
    /// <returns>True when the shape is complete and ordered.</returns>
    internal static bool Validate(string[] lines) => Validate(lines, out _);

    /// <summary>
    /// Checks the ordered wire shape of a runtime-boot log.
    /// </summary>
    /// <param name="lines">Log lines.</param>
    /// <param name="error">First violation, when the shape is invalid.</param>
    /// <returns>True when the shape is complete and ordered.</returns>
    internal static bool Validate(string[] lines, out string error)
    {
        error = "Missing or misplaced runtime boundary.";
        var wire = lines.Where(line => line.Length != 0).ToArray();
        var begin = Array.IndexOf(wire, "[TEST-PASS] Runtime.InvalidHandoffTeardown");
        if (begin < 0 || wire[..begin].Any(line => line.StartsWith("Runtime ", StringComparison.Ordinal) || line.StartsWith("[TEST-PASS] Runtime.", StringComparison.Ordinal) || line.StartsWith(USER, StringComparison.Ordinal)))
            return false;
        var cursor = new RuntimeBootEnvelopeCursor(wire, begin);
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

    #endregion

    #region Tools

    private static bool IsRuntime(string line) => line.StartsWith("Runtime ", StringComparison.Ordinal) ||
        line.StartsWith("[TEST-PASS] Runtime.", StringComparison.Ordinal) || line.StartsWith(USER, StringComparison.Ordinal) ||
        line.StartsWith("[USER] [NATIVE-FAIL-FAST]", StringComparison.Ordinal) || line.StartsWith("[USER] WitOS ", StringComparison.Ordinal);

    #endregion
}
