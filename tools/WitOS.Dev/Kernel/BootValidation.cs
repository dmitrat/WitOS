using System.Globalization;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.NativeAot.Acceptance;

namespace WitOS.Dev.Kernel;

/// <summary>
/// Classifies a finished QEMU boot from its serial log, exit code and timeout state.
/// </summary>
internal static class BootValidation
{
    #region Constants

    private const string HPET_FREQUENCY = "100000000";

    // Unmapped address that the page-fault and data-abort scenarios read.
    private const ulong FAULT_PROBE = 0x0000400000000000UL;

    #endregion

    #region Functions

    /// <summary>
    /// Decides whether a boot produced the outcome its request requires.
    /// </summary>
    /// <param name="root">Repository root, used to read the expected kernel banner and tests/Expectations.</param>
    /// <param name="request">Boot request with the expected outcome and suite.</param>
    /// <param name="result">Exit code, timeout state and serial log of the boot.</param>
    /// <returns>The verdict and a summary of the success check groups.</returns>
    public static BootVerdict Evaluate(string root, BootRequest request, ProcessResult result)
    {
        if (request.Suite == BootSuite.Release)
        {
            return EvaluateRelease(root, request, result);
        }
        var output = result.Output;
        var exitedFirmware = output.IndexOf("[BOOT] ExitBootServices OK", StringComparison.Ordinal);
        var contract = output.IndexOf("[TEST-PASS] Boot.Contract", StringComparison.Ordinal);
        var hello = output.IndexOf("[TEST-PASS] Boot.Hello", StringComparison.Ordinal);
        var panic = output.Contains("[PANIC]", StringComparison.Ordinal);
        var memory = Regex.Match(output, @"Usable memory: (\d+) MiB");
        var validMemory = memory.Success && int.TryParse(memory.Groups[1].Value, out var usable) && usable > 0 &&
            usable < request.MemoryMiB;
        var counterFrequency = Regex.Match(output, @"HPET frequency: (\d+)");
        var foundation = BootExpectations.Read(root, BootExpectations.X64_FOUNDATION);
        var foundationReady = foundation.Required.All(test => output.Contains(test, StringComparison.Ordinal)) &&
            validMemory &&
            counterFrequency.Success &&
            counterFrequency.Groups[1].Value == HPET_FREQUENCY &&
            MarkersInOrder(output, FoundationOrder(root, foundation));

        // The kernel console emits CRLF; match the banner as one exact line.
        var bannerReady = Regex.IsMatch(output, "^" + Regex.Escape(KernelAbi.Banner(root)) + @"\r?$", RegexOptions.Multiline);
        var schedulerReady = ValidateScheduler(output);
        var usersReady = ValidateUsers(root, output);
        var helloReady = hello > output.IndexOf("[TEST-PASS] Scheduler.RegisterState", StringComparison.Ordinal);
        var exception = output.Contains("[EXCEPTION]", StringComparison.Ordinal);
        var booted = request.Suite == BootSuite.Foundation
            ? FoundationReady(root, request, output)
            : bannerReady && foundationReady && schedulerReady && usersReady && helloReady && !panic && !exception;
        var diagnostics = $"banner={bannerReady} foundation={foundationReady} scheduler={schedulerReady} " +
            $"users={usersReady} hello={helloReady} panic={panic} exception={exception}";
        booted = booted && SuiteReady(root, request, result);

        var failedBeforeContract = !result.TimedOut && result.ExitCode == 35 && exitedFirmware >= 0 && contract < 0 && hello < 0;
        var passed = request.Expected switch
        {
            ExpectedOutcome.EntropyUnavailable => !result.TimedOut && result.ExitCode == 35 && exitedFirmware < 0 &&
                contract < 0 && hello < 0 &&
                output.Contains("[PANIC] UEFI RNG unavailable", StringComparison.Ordinal),
            ExpectedOutcome.ClockUnavailable => !result.TimedOut && result.ExitCode == 35 && hello < 0 &&
                MarkersInOrder(output, "[TEST-PASS] Boot.Contract", "[TEST-PASS] Memory.KernelPaging",
                    "[PANIC] Unsupported q35 HPET") &&
                !output.Contains("[EXCEPTION]", StringComparison.Ordinal),
            ExpectedOutcome.Success => !result.TimedOut && result.ExitCode == 33 && booted,
            ExpectedOutcome.InvalidBootInfo => failedBeforeContract &&
                output.Contains("[PANIC] Invalid WitBootInfo", StringComparison.Ordinal),
            ExpectedOutcome.InvalidMap => failedBeforeContract &&
                output.Contains("[PANIC] Invalid memory map", StringComparison.Ordinal),
            ExpectedOutcome.Exception => !result.TimedOut && result.ExitCode == 35 && hello < 0 &&
                (request.Suite == BootSuite.Foundation
                    ? ValidateA64Exception(root, request, output)
                    : foundationReady && request.Fault is not null && ValidateException(output, request.Fault)),
            ExpectedOutcome.Timeout => result.TimedOut && booted,
            _ => false
        };
        return new BootVerdict(passed, diagnostics);
    }

    /// <summary>
    /// Decides whether a release kernel booted: the boot contract, paging, exception tables and clock are ready,
    /// the kernel reports Hello and finishes with success, and no self-test or user component ran.
    /// </summary>
    /// <param name="root">Repository root, used to read the expected kernel banner.</param>
    /// <param name="request">Boot request of the release suite.</param>
    /// <param name="result">Exit code, timeout state and serial log of the boot.</param>
    /// <returns>The verdict and a summary of the release check groups.</returns>
    public static BootVerdict EvaluateRelease(string root, BootRequest request, ProcessResult result)
    {
        var output = result.Output;
        var banner = Regex.IsMatch(output, "^" + Regex.Escape(KernelAbi.Banner(root)) + @"\r?$", RegexOptions.Multiline);
        var ready = MarkersInOrder(output, "[TEST-PASS] Boot.Contract", "[TEST-PASS] Cpu.ExceptionTables",
            "[TEST-PASS] Memory.KernelPaging", "[TEST-PASS] Clock.IrqIndependent", "Kernel initialized.",
            "[TEST-PASS] Boot.Hello");
        string[] forbidden = ["[PANIC]", "[EXCEPTION]", "[USER", "[TEST-BEGIN] User.", "Scheduler.", "Random.ChaCha20Vector",
            "Memory.VirtualMappings", "Cpu.ContextStateProfile"];
        var clean = forbidden.All(marker => !output.Contains(marker, StringComparison.Ordinal));
        var passed = request.Expected == ExpectedOutcome.Success && !result.TimedOut && result.ExitCode == 33 &&
            banner && ready && clean;
        return new BootVerdict(passed, $"banner={banner} ready={ready} clean={clean}");
    }

    /// <summary>
    /// Requires each marker after the previous one in the log.
    /// </summary>
    /// <param name="output">Serial log.</param>
    /// <param name="markers">Markers in their required order.</param>
    /// <returns>True when every marker occurs after its predecessor.</returns>
    public static bool MarkersInOrder(string output, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = output.IndexOf(marker, StringComparison.Ordinal);
            if (current <= previous)
            {
                return false;
            }
            previous = current;
        }
        return true;
    }

    /// <summary>
    /// Checks the contained user faults against the guest summary: one "[TEST-SUMMARY] faults=N checked=N" line after
    /// every fault, the kernel's count equal to the faults the tests accepted and to the well-formed x64 fault lines,
    /// each taken at CPL3.
    /// </summary>
    /// <param name="output">Serial log.</param>
    /// <returns>True when every fault line is well formed and was expected by a test.</returns>
    public static bool ValidateUserFaults(string output)
    {
        var faults = Regex.Matches(output,
            @"(?m)^\[USER-FAULT\] id=(\d+) vector=(\d+) error=(0x[0-9A-F]{16}) address=(0x[0-9A-F]{16}) cs=(0x[0-9A-F]{16})\r?$");
        return SummaryAccounts(output, faults) && faults.All(m => Convert.ToUInt64(m.Groups[5].Value[2..], 16) == 0x33);
    }

    /// <summary>
    /// Checks the guest summary line against the fault lines: exactly one summary, after every fault, whose contained
    /// and accepted counts both equal the number of fault lines, all of them well formed.
    /// </summary>
    /// <param name="output">Serial log.</param>
    /// <param name="faults">Well-formed fault lines of the architecture.</param>
    /// <returns>True when the summary accounts for every fault.</returns>
    public static bool SummaryAccounts(string output, MatchCollection faults)
    {
        var summaries = Regex.Matches(output, @"(?m)^\[TEST-SUMMARY\].*$");
        var summary = Regex.Match(output, @"(?m)^\[TEST-SUMMARY\] faults=(\d+) checked=(\d+)\r?$");
        return summaries.Count == 1 && summary.Success &&
            summary.Groups[1].Value == summary.Groups[2].Value &&
            summary.Groups[1].Value == faults.Count.ToString(CultureInfo.InvariantCulture) &&
            Regex.Matches(output, @"(?m)^\[USER-FAULT\]").Count == faults.Count &&
            faults.All(m => m.Index < summary.Index);
    }

    #endregion

    #region Tools

    // The kernel banner from the ABI headers follows the first foundation marker.
    private static string[] FoundationOrder(string root, BootExpectation foundation)
        => [foundation.Markers[0], KernelAbi.Banner(root), .. foundation.Markers[1..]];

    private static string[] A64FoundationOrder(string root)
        => BootExpectations.Read(root, BootExpectations.ARM64_FOUNDATION).Markers;

    // An ARM64 kernel foundation: the foundation markers, kernel-worker preemption, the EL0 isolation tests, then
    // Hello, without panic or exception.
    private static bool FoundationReady(string root, BootRequest request, string output) =>
        A64Foundation(root, request, output) && ValidateScheduler(output) && ValidateA64Users(root, output) &&
        MarkersInOrder(output, A64FoundationOrder(root)[^1], "Kernel initialized.", "[TEST-PASS] Boot.Hello") &&
        !output.Contains("[PANIC]", StringComparison.Ordinal) && !output.Contains("[EXCEPTION]", StringComparison.Ordinal);

    // The banner, the build line of the requested architecture, plausible memory, a counter frequency and the
    // foundation markers in order.
    private static bool A64Foundation(string root, BootRequest request, string output)
    {
        var banner = Regex.IsMatch(output, "^" + Regex.Escape(KernelAbi.Banner(root)) + @"\r?$", RegexOptions.Multiline);
        var build = Regex.IsMatch(output, @"^Build: \S+ \| " + Regex.Escape(request.Architecture.Name) + @" \| Debug\r?$",
            RegexOptions.Multiline);
        var memory = Regex.Match(output, @"Usable memory: (\d+) MiB");
        var validMemory = memory.Success && int.TryParse(memory.Groups[1].Value, out var usable) && usable > 0 &&
            usable < request.MemoryMiB;
        var counter = Regex.Match(output, @"Counter frequency: (\d+)");
        return banner && build && validMemory && counter.Success && ulong.Parse(counter.Groups[1].Value) > 0 &&
            MarkersInOrder(output, A64FoundationOrder(root));
    }

    // A synchronous exception taken at EL1 on the kernel stack (vector 4, SPSR mode EL1h) after the foundation
    // markers, with the expected syndrome class and the fault probe address in FAR where the scenario names one.
    private static bool ValidateA64Exception(string root, BootRequest request, string output)
    {
        var expected = request.A64Fault;
        if (expected is null || !A64Foundation(root, request, output) ||
            !MarkersInOrder(output, A64FoundationOrder(root)[^1], $"[TEST-BEGIN] {expected.Trigger}", "[EXCEPTION]",
                $"[PANIC] {expected.Panic}"))
        {
            return false;
        }
        var frame = Regex.Match(output,
            @"\[EXCEPTION\] kind=(\d+) class=(0x[0-9A-F]{16}) esr=(0x[0-9A-F]{16}) elr=(0x[0-9A-F]{16}) far=(0x[0-9A-F]{16}) spsr=(0x[0-9A-F]{16}) sp=(0x[0-9A-F]{16}) stack=(\w+)");
        var stack = Regex.Match(output, @"Kernel stack: (0x[0-9A-F]{16})-(0x[0-9A-F]{16})");
        var probe = Regex.Match(output, @"\[FAULT-PROBE\] address=(0x[0-9A-F]{16})");
        if (!frame.Success || !stack.Success || (expected.Probe && !probe.Success))
        {
            return false;
        }
        ulong Hex(Match match, int group) => Convert.ToUInt64(match.Groups[group].Value[2..], 16);
        var exceptionClass = (ulong)expected.Class;
        var sp = Hex(frame, 7);
        return frame.Groups[1].Value == "4" && Hex(frame, 2) == exceptionClass && ((Hex(frame, 3) >> 26) & 0x3F) == exceptionClass &&
            Hex(frame, 4) != 0 && (Hex(frame, 6) & 0xF) == 5 && sp > Hex(stack, 1) && sp <= Hex(stack, 2) &&
            frame.Groups[8].Value == "kernel" && (!expected.FaultAddress || Hex(frame, 5) == FAULT_PROBE) &&
            (!expected.Probe || Hex(frame, 5) == Hex(probe, 1));
    }

    private static bool SuiteReady(string root, BootRequest request, ProcessResult result)
    {
        var output = result.Output;
        switch (request.Suite)
        {
            case BootSuite.CoreClrMemory:
                return MarkersInOrder(output, BootExpectations.Read(root, BootExpectations.CORECLR_MEMORY).Markers);
            case BootSuite.CoreClrStorage:
                return MarkersInOrder(output, BootExpectations.Read(root, BootExpectations.CORECLR_STORAGE).Markers);
            case BootSuite.RuntimeBoot:
                return RuntimeBootProtocol.Validate(output, result.ExitCode, result.TimedOut);
            case BootSuite.RuntimeConfig:
                var cpuMarker = request.CpuModel == "max" ? "features=513; avx-hardware=1" :
                    request.CpuModel == "Nehalem" ? "features=1; avx-hardware=0" : "features=0; avx-hardware=0";
                return MarkersInOrder(output, BootExpectations.Read(root, BootExpectations.RUNTIME_CONFIG).Markers) &&
                    output.Contains("[MINIPAL-CPU] " + cpuMarker, StringComparison.Ordinal);
            default:
                return true;
        }
    }

    private static bool ValidateScheduler(string output)
    {
        if (!MarkersInOrder(output, "[TEST-PASS] Memory.VirtualMappings", "[TEST-BEGIN] Scheduler.Preemption",
                "[TEST-PASS] Cpu.Timer", "[TEST-PASS] Scheduler.Preemption", "[TEST-PASS] Scheduler.RegisterState",
                "[TEST-PASS] Boot.Hello"))
        {
            return false;
        }
        var dispatches = Regex.Matches(output, @"^(A|B): (\d+)\r?$", RegexOptions.Multiline);
        var counts = new int[2];
        foreach (Match dispatch in dispatches)
        {
            var worker = dispatch.Groups[1].Value == "A" ? 0 : 1;
            if (int.Parse(dispatch.Groups[2].Value) != ++counts[worker])
            {
                return false;
            }
        }
        ulong Number(string label)
        {
            var match = Regex.Match(output, Regex.Escape(label) + @": (\d+)");
            return match.Success ? ulong.Parse(match.Groups[1].Value) : 0;
        }
        return counts[0] >= 3 && counts[1] >= 3 &&
            dispatches[0].Groups[1].Value == "A" && dispatches[1].Groups[1].Value == "B" &&
            Number("Context switches") == (ulong)dispatches.Count + 1 &&
            Number("Timer ticks") >= Number("Context switches") &&
            Number("Worker A iterations") > 0 && Number("Worker B iterations") > 0;
    }

    // The ARM64 isolation markers in order after preemption, and exactly the expected faults, each taken at EL0t.
    private static bool ValidateA64Users(string root, string output)
    {
        var users = BootExpectations.Read(root, BootExpectations.ARM64_USERS);
        var markers = new List<string> { "[TEST-PASS] Scheduler.RegisterState", "[TEST-BEGIN] User.Isolation" };
        markers.AddRange(users.Markers);
        markers.Add("[TEST-SUMMARY] faults=");
        markers.Add("[TEST-PASS] Boot.Hello");
        var faults = Regex.Matches(output,
            @"(?m)^\[USER-FAULT\] id=(\d+) vector=(\d+) error=(0x[0-9A-F]{16}) address=(0x[0-9A-F]{16}) elr=(0x[0-9A-F]{16}) spsr=(0x[0-9A-F]{16}) esr=(0x[0-9A-F]{16})\r?$");
        return MarkersInOrder(output, markers.ToArray()) && SummaryAccounts(output, faults) &&
            faults.All(m => (Convert.ToUInt64(m.Groups[6].Value[2..], 16) & 0x1F) == 0 &&
                m.Groups[3].Value == m.Groups[7].Value &&
                Convert.ToUInt64(m.Groups[7].Value[2..], 16) >> 26 == ulong.Parse(m.Groups[2].Value));
    }

    private static bool ValidateUsers(string root, string output)
    {
        var markers = new List<string> { "[TEST-PASS] Scheduler.RegisterState", "[TEST-BEGIN] User.Isolation" };
        markers.AddRange(BootExpectations.Read(root, BootExpectations.X64_USERS).Markers);
        markers.Add("[TEST-SUMMARY] faults=");
        markers.Add("[TEST-PASS] Boot.Hello");
        if (!MarkersInOrder(output, markers.ToArray()))
        {
            return false;
        }
        return ValidateUserFaults(output);
    }

    private static bool ValidateException(string output, FaultExpectation expected)
    {
        if (!MarkersInOrder(output, "[TEST-PASS] Memory.VirtualMappings", $"[TEST-BEGIN] {expected.Trigger}",
                "[EXCEPTION]", $"[PANIC] {expected.Panic}"))
        {
            return false;
        }
        var frame = Regex.Match(output,
            @"\[EXCEPTION\] vector=(\d+) error=(0x[0-9A-F]{16}) rip=(0x[0-9A-F]{16}) cs=(0x[0-9A-F]{16}) rflags=(0x[0-9A-F]{16}) rsp=(0x[0-9A-F]{16}) ss=(0x[0-9A-F]{16}) cr2=(0x[0-9A-F]{16}) stack=(kernel|emergency)");
        if (!frame.Success || int.Parse(frame.Groups[1].Value) != expected.Vector)
        {
            return false;
        }
        ulong Hex(int group) => Convert.ToUInt64(frame.Groups[group].Value[2..], 16);
        if (Hex(2) != expected.Error || Hex(3) == 0 || Hex(4) != 8 || (Hex(5) & 2) == 0 || Hex(7) != 0x10)
        {
            return false;
        }
        if (expected.Vector == 8)
        {
            return Hex(6) == 1 && frame.Groups[9].Value == "emergency";
        }
        var stack = Regex.Match(output, @"Kernel stack: (0x[0-9A-F]{16})-(0x[0-9A-F]{16})");
        if (!stack.Success)
        {
            return false;
        }
        var low = Convert.ToUInt64(stack.Groups[1].Value[2..], 16);
        var high = Convert.ToUInt64(stack.Groups[2].Value[2..], 16);
        var expectedAddress = FAULT_PROBE;
        if (expected.Probe)
        {
            var probe = Regex.Match(output, @"\[FAULT-PROBE\] address=(0x[0-9A-F]{16})");
            if (!probe.Success)
            {
                return false;
            }
            expectedAddress = Convert.ToUInt64(probe.Groups[1].Value[2..], 16);
        }
        return Hex(6) >= low && Hex(6) < high && frame.Groups[9].Value == "kernel" &&
            (expected.Vector != 14 || Hex(8) == expectedAddress);
    }

    #endregion
}
