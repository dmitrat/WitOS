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
            if (args.Length > 1 && args[0] != "fingerprint")
                throw new ArgumentException("Use a single command; only fingerprint takes options. Run help for the command list.");
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
                case "format":
                    await SourceFormat.RunAsync(root, check: false);
                    break;
                case "format-check":
                    await SourceFormat.RunAsync(root, check: true);
                    break;
                case "fingerprint":
                    await ImageFingerprint.RunAsync(root, args[1..]);
                    break;
                case "runtime-port":
                    var portImage = await BuildAsync(root, "runtime-port");
                    await BootAsync(root, portImage, "runtime-port-256", 256, 60, ExpectedOutcome.Success);
                    break;
                case "runtime-seh":
                    await RuntimeSehReference.RunAsync(root,await Toolchain.FindMsvcAsync(root));
                    break;
                case "runtime-failfast":
                    await RuntimeFailFastReference.RunAsync(root,await Toolchain.FindMsvcAsync(root));
                    break;
                case "runtime-gp":
                    await RuntimeGpReference.RunAsync(root,await Toolchain.FindMsvcAsync(root));
                    break;
                case "runtime-exception":
                    await RuntimeExceptionReference.RunAsync(root,await Toolchain.FindMsvcAsync(root));
                    break;
                case "runtime-unwind":
                    await RuntimeExperiment.AuditAsync(root);
                    await RuntimeUnwindReference.RunAsync(root,await Toolchain.FindMsvcAsync(root));
                    break;
                case "runtime-gc-policy":
                    await RuntimeGcPolicy.ExistingAsync(root);
                    break;
                case "runtime-encoding":
                    await RuntimeEncodingReference.RunAsync(root, await Toolchain.FindMsvcAsync(root));
                    break;
                case "runtime-boot-run":
                    await RuntimeBootAttempt.RunAsync(root,command,async attempt=>{
                        var preparedImage=await BuildAsync(root,"runtime-boot");
                        await BootRuntimeMatrixAsync(root,preparedImage,attempt);
                    });
                    break;
                case "runtime-boot":
                    await RuntimeBootAttempt.RunAsync(root,command,async attempt=>{
                        await RuntimeSourceBuild.RunAsync(root);
                        var runtimeImage=await BuildAsync(root,"runtime-boot");
                        await BootRuntimeMatrixAsync(root,runtimeImage,attempt);
                    });
                    break;
                case "runtime-config":
                    await RuntimeSourceBuild.RunAsync(root);
                    var configImage = await BuildAsync(root, "runtime-config");
                    await BootAsync(root, configImage, "runtime-config-128", 128, 60, ExpectedOutcome.Success, runtimeConfig: true);
                    await BootAsync(root, configImage, "runtime-config-512", 512, 60, ExpectedOutcome.Success, runtimeConfig: true);
                    await BootAsync(root, configImage, "runtime-config-intel", 256, 60, ExpectedOutcome.Success, runtimeConfig: true, cpuModel: "Nehalem");
                    await BootAsync(root, configImage, "runtime-config-avx", 256, 60, ExpectedOutcome.Success, runtimeConfig: true, cpuModel: "max");
                    break;
                case "coreclr-functions":
                    await CoreClrFunctionTableReference.RunAsync(root);
                    break;
                case "coreclr-storage":
                    var storageImage=await BuildAsync(root,"coreclr-storage");
                    await BootAsync(root,storageImage,"coreclr-storage-128",128,120,ExpectedOutcome.Success,coreclrStorage:true);
                    await BootAsync(root,storageImage,"coreclr-storage-512",512,120,ExpectedOutcome.Success,coreclrStorage:true);
                    break;
                case "coreclr-memory":
                    var codeImage = await BuildAsync(root, "coreclr-memory");
                    await BootAsync(root,codeImage,"coreclr-memory-128",128,60,ExpectedOutcome.Success,coreclrMemory:true);
                    await BootAsync(root,codeImage,"coreclr-memory-512",512,60,ExpectedOutcome.Success,coreclrMemory:true);
                    break;
                case "coreclr-host-files":
                    await CoreClrHostFilePal.RunAsync(root);
                    break;
                case "coreclr-host":
                    await CoreClrHostReference.RunAsync(root);
                    break;
                case "coreclr-source":
                    await CoreClrExperiment.RunAsync(root);
                    break;
                case "runtime-audit":
                    await RuntimeExperiment.AuditAsync(root);
                    break;
                case "runtime-probe":
                    await RuntimeExperiment.ProbeAsync(root);
                    break;
                case "runtime-readiness":
                case "runtime-source":
                    await RuntimeSourceBuild.RunAsync(root);
                    break;
                case "runtime-target":
                    await RuntimeTargetExperiment.RunAsync(root);
                    break;
                case "help":
                    Console.WriteLine("WitOS development tool\nUsage: dotnet run --project tools/WitOS.Dev -- <command>\n\n  doctor  Check compiler, QEMU and firmware\n  setup   Download and verify pinned QEMU into .tools\n  build   Build the x64 UEFI image (no VM)\n  run     Build and boot headlessly in QEMU\n  test    Test boot, physical pages, CPU exceptions and timeout handling\n  format  Apply the repository style to files listed in build/format.json\n  format-check  Verify the repository style without changing files\n  fingerprint [--compare <file>] [scenario...]  Hash code/data sections of built images, ignoring debug records\n  coreclr-memory  Test owned executable memory backend (not guest CoreCLR)\n  coreclr-storage  Test unchanged assembly delivery and readonly guest IO (not guest CoreCLR)\n  coreclr-source  Build pinned CoreCLR/JIT Windows reference and inventory platform imports\n  coreclr-host  Build upstream Windows host and verify standard runtimeconfig/deps binding\n  coreclr-host-files  Verify pinned hosting PAL file contracts with a hosted syscall model\n  runtime-audit  Verify pinned NativeAOT sources and package provenance\n  runtime-probe  Publish and execute a hosted NativeAOT dependency probe\n  runtime-target  Inspect NativeAOT objects and test native-host bootstrap / strict link boundaries\n  runtime-port  Build pinned GC memory adapter and execute guest checks in QEMU\n  runtime-source  Build full upstream native libraries and verify the WitOS source overlay\n  runtime-boot-run  Rebuild kernel and boot the last hash-verified runtime image\n  runtime-boot  Build and execute the full guest runtime/GC workload\n  runtime-readiness  Build source runtime and audit minimal standard-CoreLib executable startup\n  runtime-unwind  Compare the pinned AMD64 unwinder with Windows (hosted)\n  runtime-exception  Verify Windows exception/VEH reference semantics (hosted)\n  runtime-gp  Verify Windows x64 general-protection translation (hosted)\n  runtime-failfast  Verify Windows fail-fast debugger record/context (hosted)\n  runtime-seh  Verify compiler scope tables and real filter/finally ABI (hosted)\n  runtime-gc-policy  Audit write-watch exclusion in existing source-built GC objects\n  runtime-encoding  Compare UTF conversions with Windows APIs (hosted)\n  runtime-config  Build upstream configuration/startup sources and execute their guest probe");
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

    // A fixed build id and output directory let fingerprint builds compare
    // images across commits without the Git revision string shifting data.
    internal static async Task<string> BuildAsync(string root, string scenario, string? outputDirectory = null,
        string? fixedBuildId = null)
    {
        var msvc = await Toolchain.FindMsvcAsync(root);
        var output = outputDirectory ?? Path.Combine(root, "artifacts", "x64", scenario);
        Directory.CreateDirectory(output);
        var buildId = fixedBuildId ?? await BuildIdAsync(root);
        await File.WriteAllTextAsync(Path.Combine(output, "build_info.h"), $"#define WITOS_BUILD_ID \"{buildId}\"\n", Encoding.ASCII);

        await UserImage.BuildAsync(root, output, msvc);
        if (scenario == "coreclr-memory") await CoreClrMemoryImage.BuildAsync(root,output,msvc);
        if (scenario == "runtime-config") await RuntimeConfigProbe.BuildImageAsync(root, output, msvc);
        if(scenario=="runtime-boot"){
            var driver=Path.Combine(root,"artifacts/runtime-readiness/guest-driver/WitOS.NativeAotBoot.pe");
            var bytes=await File.ReadAllBytesAsync(driver);
            using(var evidence=System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(driver)!,"image.json")))){
                var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                if(hash!=evidence.RootElement.GetProperty("sha256").GetString())throw new InvalidDataException("Prepared runtime image hash changed; rebuild runtime-boot.");
                await File.WriteAllTextAsync(Path.Combine(output,"runtime_report.h"),$"#define WIT_RUNTIME_ABRUPT_REPORT_RVA {evidence.RootElement.GetProperty("abruptReportRva").GetUInt32()}U\n",Encoding.ASCII);
                await File.WriteAllTextAsync(Path.Combine(output,"runtime-input.json"),System.Text.Json.JsonSerializer.Serialize(new {
                    file=driver,sha256=hash,fileBytes=bytes.Length,buildEvidence=evidence.RootElement.Clone()
                },new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            }

            await File.WriteAllBytesAsync(Path.Combine(output,"runtime-image.pe"),bytes);
            var header=new StringBuilder("static const unsigned char wit_runtime_boot_image[] = {\n");
            for(var i=0;i<bytes.Length;i+=16)header.AppendLine("    "+string.Join(", ",bytes.Skip(i).Take(16).Select(b=>$"0x{b:X2}"))+",");
            header.AppendLine("};");await File.WriteAllTextAsync(Path.Combine(output,"runtime_boot_image.h"),header.ToString(),Encoding.ASCII);
        }

        var bootPackage=await BootPackage.BuildAsync(root,output,scenario=="coreclr-storage",scenario=="coreclr-memory");
        if(scenario=="coreclr-storage")await CoreClrStorageImage.BuildAsync(root,output,msvc);
        string[] sources = ["src/Boot.Uefi/storage.c","src/Kernel/package.c","src/Kernel/storage.c","src/Kernel/files.c","src/Kernel.Arch.X64/user_files.c","src/Kernel.Arch.X64/user_library.c","src/Kernel.Arch.X64/user_library_readers.c","src/Kernel.Arch.X64/user_library_tls.c","src/Kernel.Arch.X64/user_library_lifecycle.c","src/Kernel.Arch.X64/user_file_tests.c","src/Kernel.Arch.X64/user_code.c","src/Kernel/virtual_gap.c","src/Kernel.Arch.X64/user_code_tests.c","src/Kernel.Arch.X64/user_runtime_boot_tests.c","src/Kernel.Arch.X64/user_exception.c","src/Kernel.Arch.X64/user_runtime_unwind_tests.c","src/Kernel.Arch.X64/user_stack_lease.c","src/Kernel.Arch.X64/user_suspend_tests.c","src/Kernel.Arch.X64/user_suspend.c","src/Kernel.Arch.X64/user_thread_context.c","src/Kernel.Arch.X64/user_cpu_context.c","src/Kernel.Arch.X64/user_thread_name.c","src/Kernel.Arch.X64/user_console.c","src/Kernel.Arch.X64/user_apc.c", "src/Kernel.Arch.X64/user_objects.c","src/Kernel.Arch.X64/user_reference.c","src/Boot.Uefi/entropy.c", "src/Kernel/random.c", "src/Boot.Uefi/entry.c", "src/Boot.Uefi/image.c", "src/Kernel/kernel.c", "src/Kernel/memory.c", "src/Kernel/memory_tests.c", "src/Kernel.Arch.X64/platform.c", "src/Kernel.Arch.X64/clock.c", "src/Kernel.Arch.X64/cpu_cache.c", "src/Kernel.Arch.X64/cpu_cache_tests.c", "src/Kernel.Arch.X64/exceptions.c", "src/Kernel.Arch.X64/stacks.c", "src/Kernel.Arch.X64/paging.c", "src/Kernel.Arch.X64/scheduler.c", "src/Kernel/handles.c", "src/Kernel.Arch.X64/user_space.c", "src/Kernel.Arch.X64/user.c", "src/Kernel.Arch.X64/user_thread.c", "src/Kernel.Arch.X64/user_tls_tests.c", "src/Kernel.Arch.X64/user_dynamic_tls_tests.c", "src/Kernel.Arch.X64/user_process_exit_tests.c", "src/Kernel.Arch.X64/user_pal_tests.c", "src/Kernel.Arch.X64/user_pal_service_tests.c", "src/Kernel.Arch.X64/user_pal_background_tests.c", "src/Kernel.Arch.X64/user_pal_error_tests.c", "src/Kernel.Arch.X64/user_pal_module_tests.c", "src/Kernel.Arch.X64/user_pal_environment_tests.c", "src/Kernel.Arch.X64/user_runtime_config_tests.c", "src/Kernel.Arch.X64/user_tests.c", "src/Kernel.Arch.X64/user_memory_tests.c", "src/Kernel.Arch.X64/user_thread_tests.c", "src/Kernel/events.c", "src/Kernel.Arch.X64/user_wait.c", "src/Kernel.Arch.X64/user_pressure.c", "src/Kernel.Arch.X64/user_pressure_tests.c", "src/Kernel.Arch.X64/user_wait_tests.c", "src/Kernel.Arch.X64/user_wait_any_tests.c", "src/Kernel/pe.c", "src/Kernel/pe_imports.c", "src/Kernel/pe_exports.c", "src/Kernel.Arch.X64/user_image.c", "src/Kernel.Arch.X64/user_image_tests.c", "src/Kernel.Arch.X64/user_bootstrap_tests.c", "src/Kernel.Arch.X64/user_gc_tests.c"];
        var objects = new List<string>();
        foreach (var source in sources)
        {
            var obj = Path.Combine(output, source=="src/Boot.Uefi/storage.c" ? "boot_storage.obj" : Path.GetFileNameWithoutExtension(source)+".obj");
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
                "runtime-config" => "WITOS_TEST_RUNTIME_CONFIG",
                "runtime-boot" => "WITOS_TEST_RUNTIME_BOOT",
                "coreclr-memory" => "WITOS_TEST_CORECLR_MEMORY",
                "coreclr-storage" => "WITOS_TEST_CORECLR_STORAGE",
                _ => null
            };
            if (define is not null) arguments.Add($"/D{define}=1");
            arguments.Add(Path.Combine(root, source));
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), arguments, root);
        }

        foreach (var assembly in new[] { "entry", "context", "user_entry", "chkstk" })
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
        FatImage.Create(disk, await File.ReadAllBytesAsync(efi), bootPackage);
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
        await BootAsync(root, image, "boot-intel", 256, 60, ExpectedOutcome.Success, cpuModel: "Nehalem");
        await BootAsync(root, image, "no-rng", 256, 60, ExpectedOutcome.EntropyUnavailable);
        await BootAsync(root, image, "no-hpet", 256, 60, ExpectedOutcome.ClockUnavailable);
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
        Console.WriteLine("PASS: all 20 kernel integration scenarios.");
    }

    private static async Task BootRuntimeMatrixAsync(string root,string image,RuntimeBootAttempt attempt)
    {
        var directory=Path.GetDirectoryName(image)!;
        string Hash(string path)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        var capturedInput=attempt.Snapshot(Path.Combine(directory,"runtime-input.json"));
        attempt.Snapshot(Path.Combine(directory,"runtime-image.pe"));
        using var input=System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(capturedInput));
        var referenceReport=attempt.Snapshot(Path.Combine(root,"artifacts/runtime-readiness/readiness.json"));
        var referenceLog=attempt.Snapshot(Path.Combine(root,"artifacts/runtime-readiness/hosted.log"));
        var referenceImage=attempt.Snapshot(Path.Combine(root,"artifacts/runtime-readiness/reference/NativeAotBoot.exe"));
        using var reference=System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(referenceReport));
        var proof=reference.RootElement;
        var managedObjectHash=RuntimeBootProtocol.SharedManagedObjectHash(input.RootElement);
        if(!proof.TryGetProperty("hostedSemanticAcceptance",out var semantic)||!semantic.GetBoolean()||!proof.GetProperty("hostedPassed").GetBoolean()||
            proof.GetProperty("managedObjectSha256").GetString()!=managedObjectHash||
            proof.GetProperty("referenceImageSha256").GetString()!=Hash(referenceImage)||proof.GetProperty("hostedLogSha256").GetString()!=Hash(referenceLog)||
            !RuntimeBootProtocol.ValidateHostedLog(await File.ReadAllTextAsync(referenceLog)))
            throw new InvalidDataException("Hosted semantic evidence or shared managed object changed; rebuild runtime-source.");
        var logs=Path.Combine(attempt.RunDirectory,"logs");
        await BootAsync(root,image,"runtime-boot-128",128,120,ExpectedOutcome.Success,runtimeBoot:true,logDirectory:logs);
        await BootAsync(root,image,"runtime-boot-512",512,120,ExpectedOutcome.Success,runtimeBoot:true,logDirectory:logs);
        await BootAsync(root,image,"runtime-boot-intel",256,120,ExpectedOutcome.Success,cpuModel:"Nehalem",runtimeBoot:true,logDirectory:logs);
        await BootAsync(root,image,"runtime-boot-avx",256,120,ExpectedOutcome.Success,cpuModel:"max",runtimeBoot:true,logDirectory:logs);

        var names=new[]{"runtime-boot-128","runtime-boot-512","runtime-boot-intel","runtime-boot-avx"};
        attempt.Publish(new {
            guestManagedExecution=true,integrationCyclesPerExecution=RuntimeBootProtocol.IntegrationCycles,executionsPerProfile=RuntimeBootProtocol.ExecutionBases.Length,threadStoreAudit=true,collectorExecution=true,managedStackOverflowContained=true,managedThreadCapacityRecovery=true,managedThreadApis=true,managedThreadsPerExecution=28,parkedManagedRoots=true,managedFinalization=true,probeFinalizersPerExecution=56,managedExceptionUnwind=true,managedExceptionRoundsPerExecution=88,hardwareFaultTranslation=true,hardwareFaultsPerExecution=36,nativeFaultContainment=true,abruptWorkerContainment=true,abruptWorkerCasesPerProfile=8,orderlyThreadCompletion=true,gcInitializationFailure=true,managedOomRecovery=true,managedOomFailuresPerExecution=4,workerLifecycle=true,workerDrivenCollection=true,hijackObserved=true,activeServiceFrameRejected=true,collectionDuringThreadExit=true,workersPerExecution=RuntimeBootProtocol.WorkersPerExecution,imageRelocations=RuntimeBootProtocol.ImageBases.Length,profiles=4,
            runtimeImageSha256=input.RootElement.GetProperty("sha256").GetString(),kernelDiskSha256=Hash(image),
            hostedReference=new { semanticPassed=true,sharedManagedObjectSha256=managedObjectHash,
                report=referenceReport,reportSha256=Hash(referenceReport),log=referenceLog,logSha256=Hash(referenceLog),image=referenceImage,imageSha256=Hash(referenceImage) },
            workload="Standard CoreLib: four combined hardware/managed EH, compacting GC, finalization/resurrection and Thread/Monitor/TLS/quota-recovery cycles; locked ThreadStore audit and stale observer checks; 43 orderly workers; base A/B/A/B; actual failure components, exit 42 and full component resource reclamation. Hosted reference uses the exact same managed object with an explicitly different OS thread quota.",
            logs=names.Select(name=>{var file=Path.Combine(logs,name+".serial.log");return new{file,sha256=Hash(file)};})
        });
    }

    private enum ExpectedOutcome { Success, InvalidBootInfo, InvalidMap, Exception, Timeout, ClockUnavailable, EntropyUnavailable }
    private sealed record FaultExpectation(int Vector, ulong Error, string Trigger, string Panic, bool Probe = false);

    private static async Task BootAsync(string root, string image, string name, int memoryMiB, int timeoutSeconds, ExpectedOutcome expected, FaultExpectation? fault = null, bool runtimeConfig = false, string cpuModel = "qemu64", bool runtimeBoot = false, string? logDirectory = null, bool coreclrMemory = false, bool coreclrStorage = false)
    {
        Toolchain.RequireQemu(root);
        var logs=logDirectory??Path.Combine(root,"artifacts","logs");Directory.CreateDirectory(logs);
        var serialPath=Path.Combine(logs,name+".serial.log");
        if(File.Exists(serialPath))File.Delete(serialPath);
        var firmwareState = Path.Combine(Path.GetDirectoryName(image)!, name + ".vars.fd");
        File.Copy(Toolchain.FirmwareVariables(root), firmwareState, overwrite: true);
        using var qmpControl = new QemuControl();
        var arguments = new List<string>
        {
            "-machine", expected == ExpectedOutcome.ClockUnavailable ? "q35,hpet=off" : "q35,hpet=on", "-accel", "tcg,thread=single", "-cpu", cpuModel, "-smp", "1", "-m", memoryMiB.ToString(),
            "-display", "none", "-monitor", "none", "-qmp", qmpControl.Argument, "-serial", "file:"+QemuPath(serialPath), "-nic", "none", "-no-reboot",
            "-drive", $"if=pflash,unit=0,format=raw,readonly=on,file={QemuPath(Toolchain.Firmware(root))}",
            "-drive", $"if=pflash,unit=1,format=raw,file={QemuPath(firmwareState)}",
            "-drive", $"if=none,id=boot,format=raw,readonly=on,file={QemuPath(image)}",
            "-device", "virtio-blk-pci,drive=boot,bootindex=1",
            "-device", "isa-debug-exit,iobase=0xf4,iosize=0x04"
        };
        if(expected!=ExpectedOutcome.EntropyUnavailable)
            arguments.AddRange(["-object","rng-builtin,id=entropy0","-device","virtio-rng-pci,rng=entropy0"]);
        Console.WriteLine($"Booting {name} ({memoryMiB} MiB, {cpuModel}, TCG, no networking)...");
        ProcessResult monitorResult;
        try { monitorResult=await Processes.RunWithFilesAsync(Toolchain.Qemu(root),arguments,root,timeoutSeconds,Path.Combine(logs,name+".stdout.log"),Path.Combine(logs,name+".stderr.log"),qmpControl.QuitAsync); }
        finally { await File.WriteAllTextAsync(Path.Combine(logs,name+".monitor.log"),qmpControl.Transcript); }
        var serial=File.Exists(serialPath)?await BoundedCapture.ReadFileAsync(serialPath):"";
        var result=monitorResult with {Output=serial};
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
        var counterFrequency = Regex.Match(result.Output, @"HPET frequency: (\d+)");
        var foundationReady = result.Output.Contains("[TEST-PASS] Cpu.SuspendedDeadlineState",StringComparison.Ordinal) && result.Output.Contains("[TEST-PASS] Cpu.ContextSanitization",StringComparison.Ordinal) && result.Output.Contains("[TEST-PASS] Cpu.ContextStateProfile",StringComparison.Ordinal) && result.Output.Contains("[TEST-PASS] Random.BootSeedConsumed",StringComparison.Ordinal) &&
            result.Output.Contains("[TEST-PASS] Random.ChaCha20Vector",StringComparison.Ordinal) && validMemory && counterFrequency.Success && counterFrequency.Groups[1].Value == "100000000" && MarkersInOrder(result.Output,
            "[BOOT] ExitBootServices OK", KernelAbi.Banner(root), "[TEST-PASS] Boot.Contract",
            "[TEST-PASS] Cpu.KernelStack", "[TEST-PASS] Cpu.ExceptionTables",
            "[TEST-PASS] Memory.KernelPaging", "[TEST-PASS] Memory.StackGuards",
            "[TEST-PASS] Clock.Counter64", "[TEST-PASS] Clock.IrqIndependent",
            "[TEST-PASS] Memory.PhysicalPages", "[TEST-PASS] Memory.Exhaustion",
            "[TEST-PASS] Memory.InvalidMaps", "[TEST-PASS] Memory.VirtualMappings");
        // The kernel console emits CRLF; match the banner as one exact line.
        var bannerReady = Regex.IsMatch(result.Output, "^" + Regex.Escape(KernelAbi.Banner(root)) + @"\r?$", RegexOptions.Multiline);
        var schedulerReady = ValidateScheduler(result.Output);
        var usersReady = ValidateUsers(result.Output, runtimeConfig ? 66 : 51, runtimeBoot ? RuntimeBootProtocol.StackFaultsPerProfile : coreclrMemory ? 9 : coreclrStorage ? 7 : 0);
        var helloReady = hello > result.Output.IndexOf("[TEST-PASS] Scheduler.RegisterState", StringComparison.Ordinal);
        var exception = result.Output.Contains("[EXCEPTION]", StringComparison.Ordinal);
        var booted = bannerReady && foundationReady && schedulerReady && usersReady && helloReady && !panic && !exception;
        var bootDiagnostics = $"banner={bannerReady} foundation={foundationReady} scheduler={schedulerReady} users={usersReady} hello={helloReady} panic={panic} exception={exception}";
        if(coreclrMemory)booted=booted&&MarkersInOrder(result.Output,"[TEST-PASS] Code.VMToOSMapper","[TEST-PASS] Code.VMToOSMapperRollback","[TEST-PASS] Code.DynamicFrameUnwind","[TEST-PASS] Code.ForeignDynamicUnwind","[TEST-PASS] Code.DynamicExceptionDispatch","[TEST-PASS] Code.DynamicTargetUnwind","[TEST-PASS] Code.CoreClrCollidedDispatch","[TEST-PASS] Code.CollidedContextRejection","[TEST-PASS] Code.DynamicUnwindRejection","[TEST-PASS] Code.ModuleUnwind","[TEST-PASS] Code.ForeignModuleUnwind","[TEST-PASS] Code.SparseViewsAndLateCommit","[TEST-PASS] Code.SparseCommitRollback","[TEST-PASS] Code.OwnershipAndAtomicProtection",
            "[TEST-PASS] Code.PublicationAndExecution","[TEST-PASS] Code.WriteAndNxFaults","[TEST-PASS] Code.AliasesAndLifetime","[TEST-PASS] Code.Teardown");
        if(coreclrStorage)booted=booted&&MarkersInOrder(result.Output,"[TEST-PASS] Storage.AssemblyBytes","[TEST-PASS] Storage.AtomicReadAndSeek","[TEST-PASS] Storage.HandlesAndQuotas","[TEST-PASS] Storage.NamespaceQueries","[TEST-PASS] Storage.FileViews","[TEST-PASS] Storage.CoreHostPalFiles","[TEST-PASS] Storage.NativePaths","[TEST-PASS] Storage.NativeDirectories","[TEST-PASS] Storage.NativeLibraries","[TEST-PASS] Storage.LibraryRollback","[TEST-PASS] Storage.LibraryDependencies","[TEST-PASS] Storage.LibraryReaders","[TEST-PASS] Storage.LibraryLifecycle","[TEST-PASS] Storage.LibraryShutdown","[TEST-PASS] Storage.LibraryThreadNotifications","[TEST-PASS] Storage.LibraryStaticTls","[TEST-PASS] Storage.FileViewRollback","[TEST-PASS] Storage.Isolation","[TEST-PASS] Storage.Teardown");
        if(runtimeBoot)booted=booted&&RuntimeBootProtocol.Validate(result.Output,result.ExitCode,result.TimedOut);
        if (runtimeConfig)
            booted = booted && MarkersInOrder(result.Output, "[TEST-PASS] User.RuntimeConfigCrt",
                "[TEST-PASS] User.RhConfigPrecedence", "[TEST-PASS] User.RhConfigStrings", "[TEST-PASS] User.GcConfigValues",
                "[TEST-PASS] User.GcConfigRefresh", "[TEST-PASS] User.RuntimeConfigThreads",
                "[TEST-PASS] User.PalInitPrerequisites", "[TEST-PASS] User.PalInitPolicy",
                "[TEST-PASS] User.PalInitLifecycle", "[TEST-PASS] User.RuntimeAllocHeap", "[TEST-PASS] User.InterfaceDispatchInit", "[TEST-PASS] User.RuntimeInstanceStartup", "[TEST-PASS] User.RuntimeThreadRecord",
                "[TEST-PASS] User.ThreadStoreTlsPrerequisite", "[TEST-PASS] User.GcProcessWriteBarrier",
                "[TEST-PASS] User.ProcessBarrierWithoutTls", "[TEST-PASS] User.MinipalTime", "[TEST-PASS] User.MinipalTimeWithoutTls", "[TEST-PASS] User.RuntimeRandomTls", "[TEST-PASS] User.CrtMemoryAndStrings", "[TEST-PASS] User.CrtUnsignedLong", "[TEST-PASS] User.CompilerStackProbe", "[TEST-PASS] User.CompilerStackProbeWithoutTls", "[TEST-PASS] User.CompilerStackProbeGuard", "[TEST-PASS] User.MinipalCpuFeatures", "[TEST-PASS] User.MinipalCpuWithoutTls", "[TEST-PASS] User.AvxDisabled", "[TEST-PASS] User.NativeClockBindings", "[TEST-PASS] User.NativeClockAtomicCopy", "[TEST-PASS] User.FatalDiagnosticOutput", "[TEST-PASS] User.FatalCrtExit", "[TEST-PASS] User.FatalDiagnosticRejection", "[TEST-PASS] User.GcAffinityParsing", "[TEST-PASS] User.GcAffinityBeforeTlsConstructors", "[TEST-PASS] User.NativeMathLog", "[TEST-PASS] User.NativeMathBeforeTlsConstructors", "[TEST-PASS] User.NativeSecureFormatting", "[TEST-PASS] User.NativeFormattingBeforeTlsConstructors", "[TEST-PASS] User.SecurityCookieAbi", "[TEST-PASS] User.SecurityCookieFailClosed", "[TEST-PASS] User.CryptographicRandom", "[TEST-PASS] User.RandomAtomicCopyAndEarlyCookie", "[TEST-PASS] User.NativeVirtualMemory", "[TEST-PASS] User.NativeMemoryWithoutCompilerTls", "[TEST-PASS] User.NativeCloseAndSleep", "[TEST-PASS] User.NativeServicesWithoutCompilerTls", "[TEST-PASS] User.NativeThreadReferences", "[TEST-PASS] User.ThreadReferencesWithoutCompilerTls", "[TEST-PASS] User.AlertableObjectWaits", "[TEST-PASS] User.ApcWithoutCompilerTls", "[TEST-PASS] User.NativeConsoleBindings", "[TEST-PASS] User.NativeUtfConversions", "[TEST-PASS] User.NativeProcessorAtomicCopy", "[TEST-PASS] User.NativeModuleNames", "[TEST-PASS] User.NativeModuleNamesWithoutTls", "[TEST-PASS] User.AnonymousModuleIdentity", "[TEST-PASS] User.NativeThreadNames", "[TEST-PASS] User.NativeThreadNamesWithoutTls", "[TEST-PASS] User.NativeDiagnosticServices", "[TEST-PASS] User.NativeDiagnosticsWithoutTls", "[TEST-PASS] User.NativeMtaLifecycle", "[TEST-PASS] User.NativeMtaProcessCleanup", "[TEST-PASS] User.NativeMtaPrerequisites", "[TEST-PASS] User.GcOptionalMemoryPolicy", "[TEST-PASS] User.GcWriteWatchFailClosed", "[TEST-PASS] User.GcArchitecturalBreakpoint", "[TEST-PASS] User.NativeContextStorage", "[TEST-PASS] User.NativeContextProfileWithoutTls", "[TEST-PASS] User.NativeContextInvalidOutput", "[TEST-PASS] User.RegisterContextSnapshots", "[TEST-PASS] User.RegisterSnapshotsWithoutTls", "[TEST-PASS] User.ThreadSuspension", "[TEST-PASS] User.SuspensionWithoutTls", "[TEST-PASS] User.SuspendedIdleBudget", "[TEST-PASS] User.ContextSetAndRestore", "[TEST-PASS] User.ContextRestoreWithoutTls", "[TEST-PASS] User.PalContextMapping", "[TEST-PASS] User.PalContextFailClosed", "[TEST-PASS] User.StackLeaseLifetime", "[TEST-PASS] User.StackLeaseWithoutTls", "[TEST-PASS] User.StackLeaseRawExit", "[TEST-PASS] User.NativeUnwindScope", "[TEST-PASS] User.NativeUnwindScopeRejection", "[TEST-PASS] User.RuntimeUnwindMetadataRejection", "[TEST-PASS] User.ArchivedNativeUnwinder", "[TEST-PASS] User.NativeUnwindFailureAndGs", "[TEST-PASS] User.NativeForeignUnwind", "[TEST-PASS] User.ExceptionDeliveryAndContinue", "[TEST-PASS] User.ExceptionFailureContainment", "[TEST-PASS] User.NativeVectoredHandlers", "[TEST-PASS] User.NativeVectoredFailure", "[TEST-PASS] User.NativeExceptionFrameSearch", "[TEST-PASS] User.NativeRaiseException", "[TEST-PASS] User.NativeNoncontinuableException", "[TEST-PASS] User.NativeRaiseFailFastException", "[TEST-PASS] User.CompilerSehTargetUnwind", "[TEST-PASS] User.CompilerLocalUnwind", "[TEST-PASS] User.CompilerNestedSehCallbacks", "[TEST-PASS] User.ExceptionScopeTransfer", "[TEST-PASS] User.CompilerCollidedUnwind", "[TEST-PASS] User.CompilerGsSeh", "[TEST-PASS] User.CompilerGsSehValidation", "[TEST-PASS] User.CompilerGsSehAligned", "[TEST-PASS] User.NativeGeneralProtection", "[TEST-PASS] User.NativeGeneralProtectionUnsupported", "[TEST-PASS] User.NativeThreadCreationAndRollback", "[TEST-PASS] User.Isolation");
        if (runtimeConfig) {
            var cpuMarker = cpuModel == "max" ? "features=513; avx-hardware=1" :
                cpuModel == "Nehalem" ? "features=1; avx-hardware=0" : "features=0; avx-hardware=0";
            booted = booted && result.Output.Contains("[MINIPAL-CPU] " + cpuMarker, StringComparison.Ordinal);
        }
        var failedBeforeContract = !result.TimedOut && result.ExitCode == 35 && exitedFirmware >= 0 && contract < 0 && hello < 0;
        var passed = expected switch
        {
            ExpectedOutcome.EntropyUnavailable => !result.TimedOut && result.ExitCode==35 && exitedFirmware<0 && contract<0 && hello<0 &&
                result.Output.Contains("[PANIC] UEFI RNG unavailable",StringComparison.Ordinal),
            ExpectedOutcome.ClockUnavailable => !result.TimedOut && result.ExitCode == 35 && hello < 0 &&
                MarkersInOrder(result.Output, "[TEST-PASS] Boot.Contract", "[TEST-PASS] Memory.KernelPaging",
                    "[PANIC] Unsupported q35 HPET") && !result.Output.Contains("[EXCEPTION]", StringComparison.Ordinal),
            ExpectedOutcome.Success => !result.TimedOut && result.ExitCode == 33 && booted,
            ExpectedOutcome.InvalidBootInfo => failedBeforeContract && result.Output.Contains("[PANIC] Invalid WitBootInfo", StringComparison.Ordinal),
            ExpectedOutcome.InvalidMap => failedBeforeContract && result.Output.Contains("[PANIC] Invalid memory map", StringComparison.Ordinal),
            ExpectedOutcome.Exception => !result.TimedOut && result.ExitCode == 35 && foundationReady && hello < 0 &&
                fault is not null && ValidateException(result.Output, fault),
            ExpectedOutcome.Timeout => result.TimedOut && booted,
            _ => false
        };
        if (!passed)
            throw new InvalidOperationException($"{name}: expected {expected}, got exit={result.ExitCode}, timeout={result.TimedOut}. Checks: {bootDiagnostics}. Logs: {logs}\n{result.Error}");
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

    private static bool ValidateUsers(string output, int expectedFaults, int followingFaults = 0)
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
            "NativeThreadIdExhaustion", "ThreadPreemptionAndTls", "ThreadJoinAndReuse", "ThreadJoinCycle", "ThreadCapacity",
            "ThreadCreationRollback", "ThreadFault", "ThreadGuardLow", "ThreadGuardHigh",
            "ThreadBadReturn", "ThreadProcessExit",
            "WaitQueueSemantics", "WaitClockDomains", "WaitResourceLimits", "WaitSignalState", "WaitClockAndIdle",
            "WaitAutoWake", "WaitManualWake", "WaitCloseAndReuse", "WaitHandoff", "WaitDeadlineOrder",
            "WaitExitCleanup", "WaitIdleBudget", "WaitRights", "WaitActiveTimeout", "WaitJoinChain",
            "ImageHeadersAndBounds", "ImageUnsupportedFeatures", "ImageSectionsAndEntry", "ImageRelocationValidation",
            "ImageRelocatedExecution", "ImageRelocationDirections", "ImagePreferredExecution", "ImageZeroFillAndPrivate",
            "ImageGapMapping", "ImageAllocationRollback", "ImageWriteCode", "ImageWriteReadOnly", "ImageWriteHeaders",
            "ImageNxData", "ImageEndBoundary", "ImageGapFault",
            "BootstrapUnwindMetadata", "BootstrapUnwindRejection", "BootstrapNativeEntry", "BootstrapImageDescriptor",
            "BootstrapOrderAndRunOnce", "BootstrapRollback", "BootstrapMainFailure", "BootstrapValidation",
            "BootstrapEmptyList", "BootstrapDescriptorProtection", "BootstrapInitializerFault",
            "GcMemoryContract", "GcMemoryOwnership", "GcMemoryRelocation",
            "GcReserveProtection", "GcDecommitProtection", "GcMemoryNx",
            "CpuCacheDiscovery", "GcEnvironmentInit", "GcMemoryInformation", "GcInformationBuffers", "GcPhysicalPressure",
            "GcEventState", "GcEventCapacity", "GcEventManual", "GcEventAuto",
            "GcEventClose", "GcEventContention", "GcEventFailFast",
            "GcClockContract", "GcTimedWait", "GcTimedSignal", "GcTimeArithmetic", "GcClockIsolation",
            "GcThreadIdentity", "GcMutexRecursive", "GcMutexBlocking", "GcMutexStress",
            "GcMutexCapacity", "GcCrst", "GcMutexFailFast", "GcMemoryReset", "GcResetProtection",
            "NativeHeap", "NativeHeapReuse", "NativeHeapFailure", "NativeHeapThreads", "NativeHeapFailFast", "NativeHeapProtection",
            "CompilerTlsValidation", "CompilerTlsRollback", "CompilerTlsThreads", "CompilerTlsIsolation",
            "DynamicTlsLifecycle", "DynamicTlsExplicitExit", "DynamicTlsDestructorOrder", "DynamicTlsFailFast", "DynamicTlsFaultIsolation",
            "PalThreadSnapshot", "PalThreadBuffers", "PalThreadSwitching", "PalExpiredSleep", "PalStackGuards",
            "PalMemory", "PalMemoryRollback", "PalEventState", "PalEventHandoff", "PalWaitTime", "PalCloseCancellation", "PalMemoryProtection", "PalFreeFailFast",
            "WaitAnyValidation", "WaitAnyAutoReset", "WaitAnyManualAndReuse", "WaitAnyClose", "WaitAnyDeadline", "WaitAnySnapshot", "WaitAnySingleAndMixed",
            "MemoryPressurePolicy", "MemoryPressureWaitAndReuse", "MemoryPressureCapacity", "MemoryPressurePhysical",
            "PalModuleDiscovery", "PalModuleInvalidBounds",
            "PalEnvironment", "PalEnvironmentValidation", "PalEnvironmentBlocks", "PalUtf8Copy", "PalEnvironmentThreads",
            "NativeProcessExitOrder", "NativeProcessExitCapacity", "NativeProcessExitThreads", "NativeProcessExitFailFast", "NativeProcessExitFault", "NativeThreadExitNotify", "NativeThreadExitDetached", "NativeThreadExitFailFast", "NativeProcessAbruptExit",
            "PalBackgroundLifecycle", "PalBackgroundCapacity", "PalBackgroundRollback", "DetachedLastExit", "PalBackgroundIsolation",
            "NativeLastError", "PalErrorCodes", "LastErrorBindingProtection",
            "BadReturn", "TimerBudget", "PreemptionState",
            "ZeroFillAndStaleHandles", "Teardown", "Isolation"
        ];
        var markers = new List<string> { "[TEST-PASS] Scheduler.RegisterState", "[TEST-BEGIN] User.Isolation" };
        markers.AddRange(checks.Select(name => $"[TEST-PASS] User.{name}"));
        markers.Add("[TEST-PASS] Boot.Hello");
        if (!MarkersInOrder(output, markers.ToArray())) return false;
        return ValidateUserFaults(output, expectedFaults, followingFaults);
    }

    internal static bool ValidateUserFaults(string output, int legacyFaults, int followingFaults)
    {
        var boundary=output.IndexOf("[TEST-PASS] User.Isolation",StringComparison.Ordinal);
        var faults=Regex.Matches(output,
            @"(?m)^\[USER-FAULT\] id=(\d+) vector=(\d+) error=(0x[0-9A-F]{16}) address=(0x[0-9A-F]{16}) cs=(0x[0-9A-F]{16})\r?$");
        return boundary>=0&&faults.Count==legacyFaults+followingFaults&&
            Regex.Matches(output,@"(?m)^\[USER-FAULT\]").Count==faults.Count&&
            faults.Count(m=>m.Index<boundary)==legacyFaults&&
            faults.All(m=>Convert.ToUInt64(m.Groups[5].Value[2..],16)==0x33);
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
