using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace WitOS.Dev;

internal static class DevTool
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length > 1)
                throw new ArgumentException("Use a single command: doctor, setup, build, run, test, runtime-audit, runtime-probe, runtime-target.");
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The current development host is Windows x64 with Visual Studio C++ tools. The guest does not use Windows.");

            var root = FindRoot();
            var command = args.Length == 0 ? "help" : args[0];
            switch (command)
            {
                case "doctor":
                    Console.WriteLine($"Root: {root}");
                    Console.WriteLine($"MSVC: {await Toolchain.FindMsvcAsync(root)}");
                    Toolchain.RequireQemu(root);
                    var version = await Processes.RunAsync(Toolchain.Qemu(root), ["--version"], root);
                    if (version.ExitCode != 0 || version.TimedOut)
                        throw new InvalidOperationException($"QEMU could not start. {version.Error}");
                    Console.WriteLine(version.Output.Trim());
                    Console.WriteLine($"Firmware: {Toolchain.Firmware(root)}");
                    break;
                case "setup":
                    await Toolchain.SetupAsync(root);
                    break;
                case "build":
                    await BuildAsync(root, "boot");
                    break;
                case "run":
                    var image = await BuildAsync(root, "boot");
                    await BootAsync(root, image, "boot-256", 256, 60, ExpectedOutcome.Success);
                    break;
                case "test":
                    await TestAsync(root);
                    break;
                case "runtime-audit":
                    await RuntimeExperiment.AuditAsync(root);
                    break;
                case "runtime-probe":
                    await RuntimeExperiment.ProbeAsync(root);
                    break;
                case "runtime-target":
                    await RuntimeTargetExperiment.RunAsync(root);
                    break;
                case "help":
                    Console.WriteLine("WitOS development tool\nUsage: dotnet run --project tools/WitOS.Dev -- <command>\n\n  doctor  Check compiler, QEMU and firmware\n  setup   Download and verify pinned QEMU into .tools\n  build   Build the x64 UEFI image (no VM)\n  run     Build and boot headlessly in QEMU\n  test    Test boot, physical pages, CPU exceptions and timeout handling\n  runtime-audit  Verify pinned NativeAOT sources and package provenance\n  runtime-probe  Publish and execute a hosted NativeAOT dependency probe\n  runtime-target  Inspect NativeAOT objects and test native-host bootstrap / strict link boundaries");
                    break;
                default:
                    throw new ArgumentException($"Unknown command: {command}. Use help.");
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}");
            return 1;
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WitOS.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Run this command inside the WitOS repository.");
    }

    private static async Task<string> BuildAsync(string root, string scenario)
    {
        var msvc = await Toolchain.FindMsvcAsync(root);
        var output = Path.Combine(root, "artifacts", "x64", scenario);
        Directory.CreateDirectory(output);
        var buildId = await BuildIdAsync(root);
        await File.WriteAllTextAsync(Path.Combine(output, "build_info.h"), $"#define WITOS_BUILD_ID \"{buildId}\"\n", Encoding.ASCII);

        await UserImage.BuildAsync(root, output, msvc);

        string[] sources = ["src/Boot.Uefi/entry.c", "src/Boot.Uefi/image.c", "src/Kernel/kernel.c", "src/Kernel/memory.c", "src/Kernel/memory_tests.c", "src/Kernel.Arch.X64/platform.c", "src/Kernel.Arch.X64/exceptions.c", "src/Kernel.Arch.X64/stacks.c", "src/Kernel.Arch.X64/paging.c", "src/Kernel.Arch.X64/scheduler.c", "src/Kernel/handles.c", "src/Kernel.Arch.X64/user_space.c", "src/Kernel.Arch.X64/user.c", "src/Kernel.Arch.X64/user_tests.c", "src/Kernel.Arch.X64/user_memory_tests.c", "src/Kernel.Arch.X64/user_thread_tests.c", "src/Kernel/events.c", "src/Kernel.Arch.X64/user_wait.c", "src/Kernel.Arch.X64/user_wait_tests.c"];
        var objects = new List<string>();
        foreach (var source in sources)
        {
            var obj = Path.Combine(output, Path.GetFileNameWithoutExtension(source) + ".obj");
            objects.Add(obj);
            var arguments = new List<string>
            {
                "/nologo", "/c", "/TC", "/std:c17", "/W4", "/WX", "/GS-", "/Zl", "/Oi", "/Od", "/Zi",
                $"/I{Path.Combine(root, "src", "Kernel", "include")}", $"/I{Path.Combine(root, "tests", "User.X64")}", $"/I{output}",
                $"/Fo{obj}", $"/Fd{Path.Combine(output, "compiler.pdb")}"
            };
            var define = scenario switch
            {
                "invalid-boot-info" => "WITOS_TEST_INVALID_BOOTINFO",
                "overlapping-map" => "WITOS_TEST_OVERLAPPING_MAP",
                "breakpoint" => "WITOS_TEST_BREAKPOINT",
                "divide-error" => "WITOS_TEST_DIVIDE_ERROR",
                "invalid-opcode" => "WITOS_TEST_INVALID_OPCODE",
                "general-protection" => "WITOS_TEST_GENERAL_PROTECTION",
                "page-fault" => "WITOS_TEST_PAGE_FAULT",
                "double-fault" => "WITOS_TEST_DOUBLE_FAULT",
                "write-code" => "WITOS_TEST_WRITE_CODE",
                "execute-data" => "WITOS_TEST_EXECUTE_DATA",
                "guard-low" => "WITOS_TEST_GUARD_LOW",
                "guard-high" => "WITOS_TEST_GUARD_HIGH",
                "readonly-alias" => "WITOS_TEST_READONLY_ALIAS",
                "unmapped-alias" => "WITOS_TEST_UNMAPPED_ALIAS",
                "timeout" => "WITOS_TEST_HANG",
                _ => null
            };
            if (define is not null) arguments.Add($"/D{define}=1");
            arguments.Add(Path.Combine(root, source));
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), arguments, root);
        }

        foreach (var assembly in new[] { "entry", "context", "user_entry" })
        {
            var assemblyObject = Path.Combine(output, $"x64_{assembly}.obj");
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
                ["/nologo", "/c", "/Zi", $"/Fo{assemblyObject}", Path.Combine(root, "src", "Kernel.Arch.X64", assembly + ".asm")], root);
            objects.Add(assemblyObject);
        }

        var efi = Path.Combine(output, "BOOTX64.EFI");
        var linkArgs = new List<string>
        {
            "/nologo", "/subsystem:efi_application", "/entry:efi_main", "/nodefaultlib", "/machine:x64",
            "/fixed:no", "/dynamicbase:no", "/incremental:no", "/debug:full", "/Brepro",
            $"/out:{efi}", $"/pdb:{Path.Combine(output, "WitOS.pdb")}", $"/map:{Path.Combine(output, "WitOS.map")}"
        };
        linkArgs.AddRange(objects);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), linkArgs, root);

        using (var file = File.OpenRead(efi))
        using (var pe = new PEReader(file))
        {
            var header = pe.PEHeaders.PEHeader;
            if (header is null || (int)header.Subsystem != 10 ||
                pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || header.ImportTableDirectory.Size != 0)
                throw new InvalidOperationException("Output must be an x64 EFI image with no imported OS/CRT functions.");
        }

        var disk = Path.Combine(output, "WitOS-x64.img");
        FatImage.Create(disk, await File.ReadAllBytesAsync(efi));
        await File.WriteAllTextAsync(Path.Combine(output, "build.txt"),
            $"Build: {buildId}\nScenario: {scenario}\nCompiler: {msvc}\nQEMU: {Toolchain.QemuVersion}\n");
        Console.WriteLine($"Built {scenario}: {disk}");
        return disk;
    }

    private static async Task<string> BuildIdAsync(string root)
    {
        // Trust only this explicitly selected workspace; do not change global Git settings.
        string[] prefix = ["-c", $"safe.directory={root.Replace('\\', '/')}"];
        var commit = await Processes.RunAsync("git", [.. prefix, "rev-parse", "--short=12", "HEAD"], root);
        var id = commit.Output.Trim();
        if (commit.ExitCode != 0 || !Regex.IsMatch(id, "^[0-9a-f]{7,40}$"))
            return "uncommitted";
        var state = await Processes.RunAsync("git", [.. prefix, "status", "--porcelain"], root);
        if (state.ExitCode != 0 || state.TimedOut)
            throw new InvalidOperationException("Could not determine Git worktree state for build identification.");
        return id + (state.Output.Length == 0 ? "" : "-dirty");
    }

    private static async Task TestAsync(string root)
    {
        Toolchain.RequireQemu(root);
        var image = await BuildAsync(root, "boot");
        await BootAsync(root, image, "boot-128", 128, 60, ExpectedOutcome.Success);
        await BootAsync(root, image, "boot-512", 512, 60, ExpectedOutcome.Success);
        var panic = await BuildAsync(root, "invalid-boot-info");
        await BootAsync(root, panic, "invalid-boot-info", 256, 60, ExpectedOutcome.InvalidBootInfo);
        var overlap = await BuildAsync(root, "overlapping-map");
        await BootAsync(root, overlap, "overlapping-map", 256, 60, ExpectedOutcome.InvalidMap);

        (string Name, FaultExpectation Fault)[] faults =
        [
            ("breakpoint", new(3, 0, "Cpu.Breakpoint", "Breakpoint")),
            ("divide-error", new(0, 0, "Cpu.DivideError", "Divide error")),
            ("invalid-opcode", new(6, 0, "Cpu.InvalidOpcode", "Invalid opcode")),
            ("general-protection", new(13, 0xFFF8, "Cpu.GeneralProtection", "General protection")),
            ("page-fault", new(14, 0, "Cpu.PageFault", "Page fault")),
            ("double-fault", new(8, 0, "Cpu.DoubleFault", "Double fault")),
            ("write-code", new(14, 3, "Memory.WriteCode", "Page fault", true)),
            ("execute-data", new(14, 17, "Memory.ExecuteData", "Page fault", true)),
            ("guard-low", new(14, 2, "Memory.GuardLow", "Page fault", true)),
            ("guard-high", new(14, 2, "Memory.GuardHigh", "Page fault", true)),
            ("readonly-alias", new(14, 3, "Memory.ReadOnlyAlias", "Page fault", true)),
            ("unmapped-alias", new(14, 0, "Memory.UnmappedAlias", "Page fault", true))
        ];
        foreach (var (name, fault) in faults)
        {
            var faultImage = await BuildAsync(root, name);
            await BootAsync(root, faultImage, name, 256, 60, ExpectedOutcome.Exception, fault);
        }
        var timeout = await BuildAsync(root, "timeout");
        await BootAsync(root, timeout, "timeout", 256, 15, ExpectedOutcome.Timeout);
        Console.WriteLine("PASS: all 17 kernel integration scenarios.");
    }

    private enum ExpectedOutcome { Success, InvalidBootInfo, InvalidMap, Exception, Timeout }
    private sealed record FaultExpectation(int Vector, ulong Error, string Trigger, string Panic, bool Probe = false);

    private static async Task BootAsync(string root, string image, string name, int memoryMiB, int timeoutSeconds, ExpectedOutcome expected, FaultExpectation? fault = null)
    {
        Toolchain.RequireQemu(root);
        var firmwareState = Path.Combine(Path.GetDirectoryName(image)!, name + ".vars.fd");
        File.Copy(Toolchain.FirmwareVariables(root), firmwareState, overwrite: true);
        var arguments = new[]
        {
            "-machine", "q35", "-accel", "tcg,thread=single", "-cpu", "qemu64", "-smp", "1", "-m", memoryMiB.ToString(),
            "-display", "none", "-monitor", "none", "-serial", "stdio", "-nic", "none", "-no-reboot",
            "-drive", $"if=pflash,unit=0,format=raw,readonly=on,file={QemuPath(Toolchain.Firmware(root))}",
            "-drive", $"if=pflash,unit=1,format=raw,file={QemuPath(firmwareState)}",
            "-drive", $"if=none,id=boot,format=raw,readonly=on,file={QemuPath(image)}",
            "-device", "virtio-blk-pci,drive=boot,bootindex=1",
            "-device", "isa-debug-exit,iobase=0xf4,iosize=0x04"
        };
        Console.WriteLine($"Booting {name} ({memoryMiB} MiB, TCG, no networking)...");
        var result = await Processes.RunAsync(Toolchain.Qemu(root), arguments, root, timeoutSeconds);
        var logs = Path.Combine(root, "artifacts", "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, name + ".serial.log"), result.Output);
        await File.WriteAllTextAsync(Path.Combine(logs, name + ".stderr.log"), result.Error);
        await File.WriteAllTextAsync(Path.Combine(logs, name + ".result.txt"),
            $"ExitCode={result.ExitCode}\nTimedOut={result.TimedOut}\nExpected={expected}\n");
        Console.Write(result.Output);

        var exitedFirmware = result.Output.IndexOf("[BOOT] ExitBootServices OK", StringComparison.Ordinal);
        var contract = result.Output.IndexOf("[TEST-PASS] Boot.Contract", StringComparison.Ordinal);
        var hello = result.Output.IndexOf("[TEST-PASS] Boot.Hello", StringComparison.Ordinal);
        var panic = result.Output.Contains("[PANIC]", StringComparison.Ordinal);
        var memory = Regex.Match(result.Output, @"Usable memory: (\d+) MiB");
        var validMemory = memory.Success && int.TryParse(memory.Groups[1].Value, out var usable) && usable > 0 && usable < memoryMiB;
        var foundationReady = validMemory && MarkersInOrder(result.Output,
            "[BOOT] ExitBootServices OK", "[TEST-PASS] Boot.Contract",
            "[TEST-PASS] Cpu.KernelStack", "[TEST-PASS] Cpu.ExceptionTables",
            "[TEST-PASS] Memory.KernelPaging", "[TEST-PASS] Memory.StackGuards",
            "[TEST-PASS] Memory.PhysicalPages", "[TEST-PASS] Memory.Exhaustion",
            "[TEST-PASS] Memory.InvalidMaps", "[TEST-PASS] Memory.VirtualMappings");
        var booted = foundationReady && ValidateScheduler(result.Output) && ValidateUsers(result.Output) && hello > result.Output.IndexOf("[TEST-PASS] Scheduler.RegisterState", StringComparison.Ordinal) &&
            !panic && !result.Output.Contains("[EXCEPTION]", StringComparison.Ordinal);
        var failedBeforeContract = !result.TimedOut && result.ExitCode == 35 && exitedFirmware >= 0 && contract < 0 && hello < 0;
        var passed = expected switch
        {
            ExpectedOutcome.Success => !result.TimedOut && result.ExitCode == 33 && booted,
            ExpectedOutcome.InvalidBootInfo => failedBeforeContract && result.Output.Contains("[PANIC] Invalid WitBootInfo", StringComparison.Ordinal),
            ExpectedOutcome.InvalidMap => failedBeforeContract && result.Output.Contains("[PANIC] Invalid memory map", StringComparison.Ordinal),
            ExpectedOutcome.Exception => !result.TimedOut && result.ExitCode == 35 && foundationReady && hello < 0 &&
                fault is not null && ValidateException(result.Output, fault),
            ExpectedOutcome.Timeout => result.TimedOut && booted,
            _ => false
        };
        if (!passed)
            throw new InvalidOperationException($"{name}: expected {expected}, got exit={result.ExitCode}, timeout={result.TimedOut}. Logs: {logs}\n{result.Error}");
        Console.WriteLine($"PASS: {name} (exit={result.ExitCode}, timeout={result.TimedOut}).");
    }

    private static bool MarkersInOrder(string output, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = output.IndexOf(marker, StringComparison.Ordinal);
            if (current <= previous)
                return false;
            previous = current;
        }
        return true;
    }

    private static bool ValidateScheduler(string output)
    {
        if (!MarkersInOrder(output, "[TEST-PASS] Memory.VirtualMappings", "[TEST-BEGIN] Scheduler.Preemption",
                "[TEST-PASS] Cpu.Timer", "[TEST-PASS] Scheduler.Preemption", "[TEST-PASS] Scheduler.RegisterState", "[TEST-PASS] Boot.Hello"))
            return false;
        var dispatches = Regex.Matches(output, @"^(A|B): (\d+)\r?$", RegexOptions.Multiline);
        var counts = new int[2];
        foreach (Match dispatch in dispatches)
        {
            var worker = dispatch.Groups[1].Value == "A" ? 0 : 1;
            if (int.Parse(dispatch.Groups[2].Value) != ++counts[worker]) return false;
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

    private static bool ValidateUsers(string output)
    {
        string[] checks =
        [
            "Ring3", "AbiAndHandles", "PrivateMemory", "PeerMemory",
            "KernelRead", "KernelWrite", "PrivilegedCli", "PrivilegedPort", "Nx",
            "GuardLow", "GuardHigh", "WriteCode", "WriteInfo", "NullRead",
            "InvalidOpcode",
            "MemoryReservedFault", "MemoryDecommittedFault", "MemoryReleasedFault",
            "MemoryReadOnlyFault", "MemoryNoAccessFault", "MemoryNxFault",
            "MemorySparseAndPrivate", "MemoryQuotaRollback", "MemoryReservationErrors", "MemoryPhysicalOom",
            "MemoryLifecycle",
            "ThreadPreemptionAndTls", "ThreadJoinAndReuse", "ThreadJoinCycle", "ThreadCapacity",
            "ThreadCreationRollback", "ThreadFault", "ThreadGuardLow", "ThreadGuardHigh",
            "ThreadBadReturn", "ThreadProcessExit",
            "WaitQueueSemantics", "WaitResourceLimits", "WaitSignalState", "WaitClockAndIdle",
            "WaitAutoWake", "WaitManualWake", "WaitCloseAndReuse", "WaitHandoff", "WaitDeadlineOrder",
            "WaitExitCleanup", "WaitIdleBudget", "WaitRights", "WaitActiveTimeout", "WaitJoinChain",
            "BadReturn", "TimerBudget", "PreemptionState",
            "ZeroFillAndStaleHandles", "Teardown", "Isolation"
        ];
        var markers = new List<string> { "[TEST-PASS] Scheduler.RegisterState", "[TEST-BEGIN] User.Isolation" };
        markers.AddRange(checks.Select(name => $"[TEST-PASS] User.{name}"));
        markers.Add("[TEST-PASS] Boot.Hello");
        if (!MarkersInOrder(output, markers.ToArray())) return false;
        var faults = Regex.Matches(output,
            @"\[USER-FAULT\] id=(\d+) vector=(\d+) error=(0x[0-9A-F]{16}) address=(0x[0-9A-F]{16}) cs=(0x[0-9A-F]{16})");
        return faults.Count == 21 && faults.All(match =>
            Convert.ToUInt64(match.Groups[5].Value[2..], 16) == 0x33);
    }

    private static bool ValidateException(string output, FaultExpectation expected)
    {
        if (!MarkersInOrder(output, "[TEST-PASS] Memory.VirtualMappings", $"[TEST-BEGIN] {expected.Trigger}",
                "[EXCEPTION]", $"[PANIC] {expected.Panic}"))
            return false;
        var frame = Regex.Match(output,
            @"\[EXCEPTION\] vector=(\d+) error=(0x[0-9A-F]{16}) rip=(0x[0-9A-F]{16}) cs=(0x[0-9A-F]{16}) rflags=(0x[0-9A-F]{16}) rsp=(0x[0-9A-F]{16}) ss=(0x[0-9A-F]{16}) cr2=(0x[0-9A-F]{16}) stack=(kernel|emergency)");
        if (!frame.Success || int.Parse(frame.Groups[1].Value) != expected.Vector)
            return false;
        ulong Hex(int group) => Convert.ToUInt64(frame.Groups[group].Value[2..], 16);
        if (Hex(2) != expected.Error || Hex(3) == 0 || Hex(4) != 8 || (Hex(5) & 2) == 0 || Hex(7) != 0x10)
            return false;
        if (expected.Vector == 8)
            return Hex(6) == 1 && frame.Groups[9].Value == "emergency";
        var stack = Regex.Match(output, @"Kernel stack: (0x[0-9A-F]{16})-(0x[0-9A-F]{16})");
        if (!stack.Success)
            return false;
        var low = Convert.ToUInt64(stack.Groups[1].Value[2..], 16);
        var high = Convert.ToUInt64(stack.Groups[2].Value[2..], 16);
        var expectedAddress = 0x0000400000000000UL;
        if (expected.Probe)
        {
            var probe = Regex.Match(output, @"\[FAULT-PROBE\] address=(0x[0-9A-F]{16})");
            if (!probe.Success) return false;
            expectedAddress = Convert.ToUInt64(probe.Groups[1].Value[2..], 16);
        }
        return Hex(6) >= low && Hex(6) < high && frame.Groups[9].Value == "kernel" &&
            (expected.Vector != 14 || Hex(8) == expectedAddress);
    }

    // Escape QEMU's comma-separated key/value syntax independently of shell quoting.
    private static string QemuPath(string path) => path.Replace('\\', '/').Replace(",", ",,");
}
