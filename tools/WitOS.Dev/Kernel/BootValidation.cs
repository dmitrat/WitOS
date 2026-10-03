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

    #endregion

    #region Fields

    private static readonly string[] FOUNDATION_TESTS =
    [
        "[TEST-PASS] Cpu.SuspendedDeadlineState", "[TEST-PASS] Cpu.ContextSanitization",
        "[TEST-PASS] Cpu.ContextStateProfile", "[TEST-PASS] Random.BootSeedConsumed",
        "[TEST-PASS] Random.ChaCha20Vector"
    ];

    // The kernel banner from the ABI headers is inserted after the first marker.
    // Unmapped address that the page-fault and data-abort scenarios read.
    private const ulong FAULT_PROBE = 0x0000400000000000UL;

    // Kernel foundation of an architecture without user mode yet (ARM64 in phase A), in order: firmware handoff,
    // boot contract, exception vectors, allocator, kernel paging, randomness, clock and the foundation self-tests.
    private static readonly string[] A64_FOUNDATION_ORDER =
    [
        "[BOOT] ExitBootServices OK", "[TEST-PASS] Boot.Contract", "[TEST-PASS] Cpu.KernelStack",
        "[TEST-PASS] Cpu.ExceptionTables", "Free physical pages: ", "[TEST-PASS] Memory.KernelPaging",
        "[TEST-PASS] Memory.StackGuards", "[TEST-PASS] Random.BootSeedConsumed", "[TEST-PASS] Random.ChaCha20Vector",
        "[TEST-PASS] Clock.Counter64", "[TEST-PASS] Clock.IrqIndependent", "[TEST-PASS] Memory.PhysicalPages",
        "[TEST-PASS] Memory.Exhaustion", "[TEST-PASS] Memory.InvalidMaps", "[TEST-PASS] Memory.VirtualMappings"
    ];

    private static readonly string[] FOUNDATION_ORDER =
    [
        "[BOOT] ExitBootServices OK", "[TEST-PASS] Boot.Contract", "[TEST-PASS] Cpu.KernelStack",
        "[TEST-PASS] Cpu.ExceptionTables", "[TEST-PASS] Memory.KernelPaging", "[TEST-PASS] Memory.StackGuards",
        "[TEST-PASS] Clock.Counter64", "[TEST-PASS] Clock.IrqIndependent", "[TEST-PASS] Memory.PhysicalPages",
        "[TEST-PASS] Memory.Exhaustion", "[TEST-PASS] Memory.InvalidMaps", "[TEST-PASS] Memory.VirtualMappings"
    ];

    private static readonly string[] CORECLR_MEMORY_MARKERS =
    [
        "[TEST-PASS] Code.VMToOSMapper", "[TEST-PASS] Code.VMToOSMapperRollback",
        "[TEST-PASS] Code.DynamicFrameUnwind", "[TEST-PASS] Code.ForeignDynamicUnwind",
        "[TEST-PASS] Code.DynamicExceptionDispatch", "[TEST-PASS] Code.DynamicTargetUnwind",
        "[TEST-PASS] Code.CoreClrCollidedDispatch", "[TEST-PASS] Code.CollidedContextRejection",
        "[TEST-PASS] Code.DynamicUnwindRejection", "[TEST-PASS] Code.ModuleUnwind",
        "[TEST-PASS] Code.ForeignModuleUnwind", "[TEST-PASS] Code.SparseViewsAndLateCommit",
        "[TEST-PASS] Code.SparseCommitRollback", "[TEST-PASS] Code.OwnershipAndAtomicProtection",
        "[TEST-PASS] Code.PublicationAndExecution", "[TEST-PASS] Code.WriteAndNxFaults",
        "[TEST-PASS] Code.AliasesAndLifetime", "[TEST-PASS] Code.Teardown"
    ];

    private static readonly string[] CORECLR_STORAGE_MARKERS =
    [
        "[TEST-PASS] Storage.AssemblyBytes", "[TEST-PASS] Storage.AtomicReadAndSeek",
        "[TEST-PASS] Storage.HandlesAndQuotas", "[TEST-PASS] Storage.NamespaceQueries",
        "[TEST-PASS] Storage.FileViews", "[TEST-PASS] Storage.CoreHostPalFiles", "[TEST-PASS] Storage.NativePaths",
        "[TEST-PASS] Storage.NativeDirectories", "[TEST-PASS] Storage.NativeLibraries",
        "[TEST-PASS] Storage.LibraryRollback", "[TEST-PASS] Storage.LibraryDependencies",
        "[TEST-PASS] Storage.LibraryReaders", "[TEST-PASS] Storage.LibraryLifecycle",
        "[TEST-PASS] Storage.LibraryShutdown", "[TEST-PASS] Storage.LibraryThreadNotifications",
        "[TEST-PASS] Storage.LibraryStaticTls", "[TEST-PASS] Storage.FileViewRollback",
        "[TEST-PASS] Storage.Isolation", "[TEST-PASS] Storage.Teardown"
    ];

    private static readonly string[] RUNTIME_CONFIG_MARKERS =
    [
        "[TEST-PASS] User.RuntimeConfigCrt", "[TEST-PASS] User.RhConfigPrecedence", "[TEST-PASS] User.RhConfigStrings",
        "[TEST-PASS] User.GcConfigValues", "[TEST-PASS] User.GcConfigRefresh", "[TEST-PASS] User.RuntimeConfigThreads",
        "[TEST-PASS] User.PalInitPrerequisites", "[TEST-PASS] User.PalInitPolicy", "[TEST-PASS] User.PalInitLifecycle",
        "[TEST-PASS] User.RuntimeAllocHeap", "[TEST-PASS] User.InterfaceDispatchInit",
        "[TEST-PASS] User.RuntimeInstanceStartup", "[TEST-PASS] User.RuntimeThreadRecord",
        "[TEST-PASS] User.ThreadStoreTlsPrerequisite", "[TEST-PASS] User.GcProcessWriteBarrier",
        "[TEST-PASS] User.ProcessBarrierWithoutTls", "[TEST-PASS] User.MinipalTime",
        "[TEST-PASS] User.MinipalTimeWithoutTls", "[TEST-PASS] User.RuntimeRandomTls",
        "[TEST-PASS] User.CrtMemoryAndStrings", "[TEST-PASS] User.CrtUnsignedLong",
        "[TEST-PASS] User.CompilerStackProbe", "[TEST-PASS] User.CompilerStackProbeWithoutTls",
        "[TEST-PASS] User.CompilerStackProbeGuard", "[TEST-PASS] User.MinipalCpuFeatures",
        "[TEST-PASS] User.MinipalCpuWithoutTls", "[TEST-PASS] User.AvxDisabled",
        "[TEST-PASS] User.NativeClockBindings", "[TEST-PASS] User.NativeClockAtomicCopy",
        "[TEST-PASS] User.FatalDiagnosticOutput", "[TEST-PASS] User.FatalCrtExit",
        "[TEST-PASS] User.FatalDiagnosticRejection", "[TEST-PASS] User.GcAffinityParsing",
        "[TEST-PASS] User.GcAffinityBeforeTlsConstructors", "[TEST-PASS] User.NativeMathLog",
        "[TEST-PASS] User.NativeMathBeforeTlsConstructors", "[TEST-PASS] User.NativeSecureFormatting",
        "[TEST-PASS] User.NativeFormattingBeforeTlsConstructors", "[TEST-PASS] User.SecurityCookieAbi",
        "[TEST-PASS] User.SecurityCookieFailClosed", "[TEST-PASS] User.CryptographicRandom",
        "[TEST-PASS] User.RandomAtomicCopyAndEarlyCookie", "[TEST-PASS] User.NativeVirtualMemory",
        "[TEST-PASS] User.NativeMemoryWithoutCompilerTls", "[TEST-PASS] User.NativeCloseAndSleep",
        "[TEST-PASS] User.NativeServicesWithoutCompilerTls", "[TEST-PASS] User.NativeThreadReferences",
        "[TEST-PASS] User.ThreadReferencesWithoutCompilerTls", "[TEST-PASS] User.AlertableObjectWaits",
        "[TEST-PASS] User.ApcWithoutCompilerTls", "[TEST-PASS] User.NativeConsoleBindings",
        "[TEST-PASS] User.NativeUtfConversions", "[TEST-PASS] User.NativeProcessorAtomicCopy",
        "[TEST-PASS] User.NativeModuleNames", "[TEST-PASS] User.NativeModuleNamesWithoutTls",
        "[TEST-PASS] User.AnonymousModuleIdentity", "[TEST-PASS] User.NativeThreadNames",
        "[TEST-PASS] User.NativeThreadNamesWithoutTls", "[TEST-PASS] User.NativeDiagnosticServices",
        "[TEST-PASS] User.NativeDiagnosticsWithoutTls", "[TEST-PASS] User.NativeMtaLifecycle",
        "[TEST-PASS] User.NativeMtaProcessCleanup", "[TEST-PASS] User.NativeMtaPrerequisites",
        "[TEST-PASS] User.GcOptionalMemoryPolicy", "[TEST-PASS] User.GcWriteWatchFailClosed",
        "[TEST-PASS] User.GcArchitecturalBreakpoint", "[TEST-PASS] User.NativeContextStorage",
        "[TEST-PASS] User.NativeContextProfileWithoutTls", "[TEST-PASS] User.NativeContextInvalidOutput",
        "[TEST-PASS] User.RegisterContextSnapshots", "[TEST-PASS] User.RegisterSnapshotsWithoutTls",
        "[TEST-PASS] User.ThreadSuspension", "[TEST-PASS] User.SuspensionWithoutTls",
        "[TEST-PASS] User.SuspendedIdleBudget", "[TEST-PASS] User.ContextSetAndRestore",
        "[TEST-PASS] User.ContextRestoreWithoutTls", "[TEST-PASS] User.PalContextMapping",
        "[TEST-PASS] User.PalContextFailClosed", "[TEST-PASS] User.StackLeaseLifetime",
        "[TEST-PASS] User.StackLeaseWithoutTls", "[TEST-PASS] User.StackLeaseRawExit",
        "[TEST-PASS] User.NativeUnwindScope", "[TEST-PASS] User.NativeUnwindScopeRejection",
        "[TEST-PASS] User.RuntimeUnwindMetadataRejection", "[TEST-PASS] User.ArchivedNativeUnwinder",
        "[TEST-PASS] User.NativeUnwindFailureAndGs", "[TEST-PASS] User.NativeForeignUnwind",
        "[TEST-PASS] User.ExceptionDeliveryAndContinue", "[TEST-PASS] User.ExceptionFailureContainment",
        "[TEST-PASS] User.NativeVectoredHandlers", "[TEST-PASS] User.NativeVectoredFailure",
        "[TEST-PASS] User.NativeExceptionFrameSearch", "[TEST-PASS] User.NativeRaiseException",
        "[TEST-PASS] User.NativeNoncontinuableException", "[TEST-PASS] User.NativeRaiseFailFastException",
        "[TEST-PASS] User.CompilerSehTargetUnwind", "[TEST-PASS] User.CompilerLocalUnwind",
        "[TEST-PASS] User.CompilerNestedSehCallbacks", "[TEST-PASS] User.ExceptionScopeTransfer",
        "[TEST-PASS] User.CompilerCollidedUnwind", "[TEST-PASS] User.CompilerGsSeh",
        "[TEST-PASS] User.CompilerGsSehValidation", "[TEST-PASS] User.CompilerGsSehAligned",
        "[TEST-PASS] User.NativeGeneralProtection", "[TEST-PASS] User.NativeGeneralProtectionUnsupported",
        "[TEST-PASS] User.NativeThreadCreationAndRollback", "[TEST-PASS] User.Isolation"
    ];

    private static readonly string[] USER_CHECKS =
    [
        "Ring3", "AbiAndHandles", "PrivateMemory", "PeerMemory", "KernelRead", "KernelWrite", "PrivilegedCli",
        "PrivilegedPort", "Nx", "GuardLow", "GuardHigh", "WriteCode", "WriteInfo", "NullRead", "InvalidOpcode",
        "MemoryReservedFault", "MemoryDecommittedFault", "MemoryReleasedFault", "MemoryReadOnlyFault",
        "MemoryNoAccessFault", "MemoryNxFault", "MemorySparseAndPrivate", "MemoryQuotaRollback",
        "MemoryReservationErrors", "MemoryPhysicalOom", "MemoryLifecycle", "NativeThreadIdExhaustion",
        "ThreadPreemptionAndTls", "ThreadJoinAndReuse", "ThreadJoinCycle", "ThreadCapacity", "ThreadCreationRollback",
        "ThreadFault", "ThreadGuardLow", "ThreadGuardHigh", "ThreadBadReturn", "ThreadProcessExit",
        "WaitQueueSemantics", "WaitClockDomains", "WaitResourceLimits", "WaitSignalState", "WaitClockAndIdle",
        "WaitAutoWake", "WaitManualWake", "WaitCloseAndReuse", "WaitHandoff", "WaitDeadlineOrder", "WaitExitCleanup",
        "WaitIdleBudget", "WaitRights", "WaitActiveTimeout", "WaitJoinChain", "ImageHeadersAndBounds",
        "ImageUnsupportedFeatures", "ImageSectionsAndEntry", "ImageRelocationValidation", "ImageRelocatedExecution",
        "ImageRelocationDirections", "ImagePreferredExecution", "ImageZeroFillAndPrivate", "ImageGapMapping",
        "ImageAllocationRollback", "ImageWriteCode", "ImageWriteReadOnly", "ImageWriteHeaders", "ImageNxData",
        "ImageEndBoundary", "ImageGapFault", "BootstrapUnwindMetadata", "BootstrapUnwindRejection",
        "BootstrapNativeEntry", "BootstrapImageDescriptor", "BootstrapOrderAndRunOnce", "BootstrapRollback",
        "BootstrapMainFailure", "BootstrapValidation", "BootstrapEmptyList", "BootstrapDescriptorProtection",
        "BootstrapInitializerFault", "GcMemoryContract", "GcMemoryOwnership", "GcMemoryRelocation",
        "GcReserveProtection", "GcDecommitProtection", "GcMemoryNx", "CpuCacheDiscovery", "GcEnvironmentInit",
        "GcMemoryInformation", "GcInformationBuffers", "GcPhysicalPressure", "GcEventState", "GcEventCapacity",
        "GcEventManual", "GcEventAuto", "GcEventClose", "GcEventContention", "GcEventFailFast", "GcClockContract",
        "GcTimedWait", "GcTimedSignal", "GcTimeArithmetic", "GcClockIsolation", "GcThreadIdentity", "GcMutexRecursive",
        "GcMutexBlocking", "GcMutexStress", "GcMutexCapacity", "GcCrst", "GcMutexFailFast", "GcMemoryReset",
        "GcResetProtection", "NativeHeap", "NativeHeapReuse", "NativeHeapFailure", "NativeHeapThreads",
        "NativeHeapFailFast", "NativeHeapProtection", "CompilerTlsValidation", "CompilerTlsRollback",
        "CompilerTlsThreads", "CompilerTlsIsolation", "DynamicTlsLifecycle", "DynamicTlsExplicitExit",
        "DynamicTlsDestructorOrder", "DynamicTlsFailFast", "DynamicTlsFaultIsolation", "PalThreadSnapshot",
        "PalThreadBuffers", "PalThreadSwitching", "PalExpiredSleep", "PalStackGuards", "PalMemory",
        "PalMemoryRollback", "PalEventState", "PalEventHandoff", "PalWaitTime", "PalCloseCancellation",
        "PalMemoryProtection", "PalFreeFailFast", "WaitAnyValidation", "WaitAnyAutoReset", "WaitAnyManualAndReuse",
        "WaitAnyClose", "WaitAnyDeadline", "WaitAnySnapshot", "WaitAnySingleAndMixed", "MemoryPressurePolicy",
        "MemoryPressureWaitAndReuse", "MemoryPressureCapacity", "MemoryPressurePhysical", "PalModuleDiscovery",
        "PalModuleInvalidBounds", "PalEnvironment", "PalEnvironmentValidation", "PalEnvironmentBlocks", "PalUtf8Copy",
        "PalEnvironmentThreads", "NativeProcessExitOrder", "NativeProcessExitCapacity", "NativeProcessExitThreads",
        "NativeProcessExitFailFast", "NativeProcessExitFault", "NativeThreadExitNotify", "NativeThreadExitDetached",
        "NativeThreadExitFailFast", "NativeProcessAbruptExit", "PalBackgroundLifecycle", "PalBackgroundCapacity",
        "PalBackgroundRollback", "DetachedLastExit", "PalBackgroundIsolation", "NativeLastError", "PalErrorCodes",
        "LastErrorBindingProtection", "BadReturn", "TimerBudget", "PreemptionState", "ZeroFillAndStaleHandles",
        "Teardown", "Isolation"
    ];

    #endregion

    #region Functions

    /// <summary>
    /// Decides whether a boot produced the outcome its request requires.
    /// </summary>
    /// <param name="root">Repository root, used to read the expected kernel banner.</param>
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
        var foundationReady = FOUNDATION_TESTS.All(test => output.Contains(test, StringComparison.Ordinal)) &&
            validMemory &&
            counterFrequency.Success &&
            counterFrequency.Groups[1].Value == HPET_FREQUENCY &&
            MarkersInOrder(output, FoundationOrder(root));

        // The kernel console emits CRLF; match the banner as one exact line.
        var bannerReady = Regex.IsMatch(output, "^" + Regex.Escape(KernelAbi.Banner(root)) + @"\r?$", RegexOptions.Multiline);
        var schedulerReady = ValidateScheduler(output);
        var usersReady = ValidateUsers(output, LegacyFaults(request.Suite), FollowingFaults(request.Suite));
        var helloReady = hello > output.IndexOf("[TEST-PASS] Scheduler.RegisterState", StringComparison.Ordinal);
        var exception = output.Contains("[EXCEPTION]", StringComparison.Ordinal);
        var booted = request.Suite == BootSuite.Foundation
            ? FoundationReady(root, request, output)
            : bannerReady && foundationReady && schedulerReady && usersReady && helloReady && !panic && !exception;
        var diagnostics = $"banner={bannerReady} foundation={foundationReady} scheduler={schedulerReady} " +
            $"users={usersReady} hello={helloReady} panic={panic} exception={exception}";
        booted = booted && SuiteReady(request, result);

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
    /// Checks the number and placement of contained user faults around the isolation boundary.
    /// </summary>
    /// <param name="output">Serial log.</param>
    /// <param name="legacyFaults">Faults expected before the user isolation marker.</param>
    /// <param name="followingFaults">Faults expected after it.</param>
    /// <returns>True when every fault line is well formed and the counts match.</returns>
    public static bool ValidateUserFaults(string output, int legacyFaults, int followingFaults)
    {
        var boundary = output.IndexOf("[TEST-PASS] User.Isolation", StringComparison.Ordinal);
        var faults = Regex.Matches(output,
            @"(?m)^\[USER-FAULT\] id=(\d+) vector=(\d+) error=(0x[0-9A-F]{16}) address=(0x[0-9A-F]{16}) cs=(0x[0-9A-F]{16})\r?$");
        return boundary >= 0 && faults.Count == legacyFaults + followingFaults &&
            Regex.Matches(output, @"(?m)^\[USER-FAULT\]").Count == faults.Count &&
            faults.Count(m => m.Index < boundary) == legacyFaults &&
            faults.All(m => Convert.ToUInt64(m.Groups[5].Value[2..], 16) == 0x33);
    }

    #endregion

    #region Tools

    private static string[] FoundationOrder(string root)
        => [FOUNDATION_ORDER[0], KernelAbi.Banner(root), .. FOUNDATION_ORDER[1..]];

    private static int LegacyFaults(BootSuite suite) => suite == BootSuite.RuntimeConfig ? 66 : 51;

    private static int FollowingFaults(BootSuite suite) => suite switch
    {
        BootSuite.RuntimeBoot => RuntimeBootProtocol.StackFaultsPerProfile,
        BootSuite.CoreClrMemory => 9,
        BootSuite.CoreClrStorage => 7,
        _ => 0
    };

    // An ARM64 kernel foundation: the foundation markers, kernel-worker preemption, then Hello, without panic or
    // exception.
    private static bool FoundationReady(string root, BootRequest request, string output) =>
        A64Foundation(root, request, output) && ValidateScheduler(output) &&
        MarkersInOrder(output, A64_FOUNDATION_ORDER[^1], "Kernel initialized.", "[TEST-PASS] Boot.Hello") &&
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
            MarkersInOrder(output, A64_FOUNDATION_ORDER);
    }

    // A synchronous exception taken at EL1 on the kernel stack (vector 4, SPSR mode EL1h) after the foundation
    // markers, with the expected syndrome class and the fault probe address in FAR where the scenario names one.
    private static bool ValidateA64Exception(string root, BootRequest request, string output)
    {
        var expected = request.A64Fault;
        if (expected is null || !A64Foundation(root, request, output) ||
            !MarkersInOrder(output, A64_FOUNDATION_ORDER[^1], $"[TEST-BEGIN] {expected.Trigger}", "[EXCEPTION]",
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

    private static bool SuiteReady(BootRequest request, ProcessResult result)
    {
        var output = result.Output;
        switch (request.Suite)
        {
            case BootSuite.CoreClrMemory:
                return MarkersInOrder(output, CORECLR_MEMORY_MARKERS);
            case BootSuite.CoreClrStorage:
                return MarkersInOrder(output, CORECLR_STORAGE_MARKERS);
            case BootSuite.RuntimeBoot:
                return RuntimeBootProtocol.Validate(output, result.ExitCode, result.TimedOut);
            case BootSuite.RuntimeConfig:
                var cpuMarker = request.CpuModel == "max" ? "features=513; avx-hardware=1" :
                    request.CpuModel == "Nehalem" ? "features=1; avx-hardware=0" : "features=0; avx-hardware=0";
                return MarkersInOrder(output, RUNTIME_CONFIG_MARKERS) &&
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

    private static bool ValidateUsers(string output, int expectedFaults, int followingFaults)
    {
        var markers = new List<string> { "[TEST-PASS] Scheduler.RegisterState", "[TEST-BEGIN] User.Isolation" };
        markers.AddRange(USER_CHECKS.Select(name => $"[TEST-PASS] User.{name}"));
        markers.Add("[TEST-PASS] Boot.Hello");
        if (!MarkersInOrder(output, markers.ToArray()))
        {
            return false;
        }
        return ValidateUserFaults(output, expectedFaults, followingFaults);
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
