using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WitOS.Dev;

internal static class RuntimeConfigProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    public static async Task PrepareAsync(string root, RuntimeExperiment.SourceLock pin)
    {
        var output = Path.Combine(root, "artifacts", "runtime-config", "source");
        Directory.CreateDirectory(output);
        await RuntimeStartupSources.PrepareAsync(root, pin);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        async Task<string> Read(string path)
        {
            var item = pin.Sources.Single(s => s.Path == path);
            var file = await RuntimeExperiment.FetchAsync(client, Path.Combine(root, ".tools", "runtime-audit"),
                "runtime", pin.RuntimeCommit, path, item.Sha256);
            return (await File.ReadAllTextAsync(file)).Replace("\r\n", "\n");
        }
        static string ReplaceOne(string source, string before, string after)
        {
            var first = source.IndexOf(before, StringComparison.Ordinal);
            if (first < 0 || source.IndexOf(before, first + before.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("Pinned runtime correction anchor changed.");
            return source.Replace(before, after, StringComparison.Ordinal);
        }
        static string Slice(string source, string first, string next)
        {
            var start = source.IndexOf(first, StringComparison.Ordinal);
            var end = source.IndexOf(next, StringComparison.Ordinal);
            if (start < 0 || end <= start || source.IndexOf(first, start + first.Length, StringComparison.Ordinal) >= 0 ||
                source.IndexOf(next, end + next.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("Pinned GC configuration slice anchors changed.");
            return source[start..end];
        }
        const string allocPath = "src/coreclr/nativeaot/Runtime/allocheap.cpp";
        const string dispatchPath = "src/coreclr/runtime/CachedInterfaceDispatch.cpp";
        const string dispatchAotPath = "src/coreclr/nativeaot/Runtime/CachedInterfaceDispatch_Aot.cpp";
        var alloc = ReplaceOne(await Read(allocPath), "        delete pCur;\n    }\n}",
            "        delete pCur;\n    }\n    m_lock.Destroy();\n}");
        await File.WriteAllTextAsync(Path.Combine(output, "allocheap.witos.cpp"), alloc);
        var dispatch = await Read(dispatchPath);
        if (dispatch.Split("static CrstStatic g_sListLock;", StringSplitOptions.None).Length != 2)
            throw new InvalidDataException("Pinned interface dispatch lock declaration changed.");
        var dispatchPrefix = dispatch[..dispatch.IndexOf("#include", StringComparison.Ordinal)] +
            "#include \"common.h\"\n#include <minipal/mutex.h>\n#include \"CachedInterfaceDispatchPal.h\"\n#include \"CachedInterfaceDispatch.h\"\nstatic CrstStatic g_sListLock;\n";
        await File.WriteAllTextAsync(Path.Combine(output, "dispatch.shared.slice.cpp"), dispatchPrefix +
            Slice(dispatch, "bool InterfaceDispatch_Initialize()", "PCODE InterfaceDispatch_UpdateDispatchCellCache("));
        var dispatchAot = await Read(dispatchAotPath);
        var dispatchEnd = dispatchAot.IndexOf("FCIMPL4(PCODE, RhpUpdateDispatchCellCache", StringComparison.Ordinal);
        if (dispatchEnd < 0) throw new InvalidDataException("Pinned AOT dispatch prefix changed.");
        await File.WriteAllTextAsync(Path.Combine(output, "dispatch.aot.slice.cpp"), dispatchAot[..dispatchEnd]);
        const string startupPath = "src/coreclr/nativeaot/Runtime/startup.cpp";
        var startup = ReplaceOne(await Read(startupPath), "    atexit(&OnProcessExit);",
            "    if (atexit(&OnProcessExit) != 0) return false;");
        await File.WriteAllTextAsync(Path.Combine(output, "startup.witos.cpp"), startup);
        const string rhPath = "src/coreclr/nativeaot/Runtime/RhConfig.cpp";
        const string gcPath = "src/coreclr/gc/gcconfig.cpp";
        const string eePath = "src/coreclr/nativeaot/Runtime/gcenv.ee.cpp";
        var rh = await Read(rhPath);
        rh = ReplaceOne(rh, "        *value = PalCopyTCharAsChar(buffer);\n        return true;",
            "        char* converted = PalCopyTCharAsChar(buffer);\n        if (!converted) return false;\n        *value = converted;\n        return true;");
        rh = ReplaceOne(rh, "    NewArrayHolder<TCHAR> newBuffer {new (nothrow) TCHAR[bufferLen]};",
            "    NewArrayHolder<TCHAR> newBuffer {new (nothrow) TCHAR[bufferLen]};\n    if (newBuffer == nullptr) return false;");
        rh = ReplaceOne(rh, "    if (actualLen >= bufferLen)", "    if (actualLen == 0 || actualLen >= bufferLen)");
        rh = ReplaceOne(rh, "    *value = PalCopyTCharAsChar(newBuffer);",
            "    char* converted = PalCopyTCharAsChar(newBuffer);\n    if (!converted) return false;\n    *value = converted;");
        await File.WriteAllTextAsync(Path.Combine(output, "rhconfig.witos.cpp"), rh);
        var gc = await Read(gcPath);
        var cut = gc.IndexOf("// Parse an integer index or range", StringComparison.Ordinal);
        if (cut < 0 || !gc[..cut].Contains("void GCConfig::Initialize()", StringComparison.Ordinal))
            throw new InvalidDataException("GCConfig prefix boundary changed.");
        await File.WriteAllTextAsync(Path.Combine(output, "gcconfig.slice.cpp"), gc[..cut]);
        var ee = await Read(eePath);
        var selected = ee[..ee.IndexOf("#include", StringComparison.Ordinal)] +
            "#include \"common.h\"\n#include \"gcenv.h\"\n#include \"gcenv.ee.h\"\n#include \"RhConfig.h\"\n\n" +
            Slice(ee, "bool GCToEEInterface::GetBooleanConfigValue(", "void GCToEEInterface::LogErrorToHost(") +
            Slice(ee, "bool GCToEEInterface::GetStringConfigValue(", "void GCToEEInterface::TriggerClientBridgeProcessing(");
        await File.WriteAllTextAsync(Path.Combine(output, "gcenv.config.slice.cpp"), selected);
        await File.WriteAllTextAsync(Path.Combine(output, "provenance.json"), JsonSerializer.Serialize(new
        {
            pin.RuntimeCommit,
            scope = "Whole RhConfig with explicit allocation-failure checks; unchanged GCConfig prefix and four unchanged GCToEE configuration methods. Startup checks atexit failure; whole AllocHeap destroys its Crst; selected dispatch initialization/allocation method bodies remain unchanged; full dispatch dependencies retained outside this probe. No collector/lifecycle substitutes.",
            inputs = pin.Sources.Where(s => s.Path == rhPath || s.Path == gcPath || s.Path == eePath || s.Path == startupPath || s.Path == allocPath || s.Path == dispatchPath || s.Path == dispatchAotPath || s.Path == "src/coreclr/nativeaot/Runtime/allocheap.h"),
            generated = new[] { "allocheap.witos.cpp", "dispatch.shared.slice.cpp", "dispatch.aot.slice.cpp", "startup.witos.cpp", "rhconfig.witos.cpp", "gcconfig.slice.cpp", "gcenv.config.slice.cpp" }
                .Select(p => new { file = p, sha256 = Hash(Path.Combine(output, p)) })
        }, Json));
    }

    public static async Task VerifyArchiveAsync(string root, string obj, string msvc, string minipalArchive, string memoryObject, string stackObject)
    {
        var output = Path.Combine(root, "artifacts", "runtime-config");
        var archive = Path.Combine(obj, "witos-config", "WitOS.ConfigProbe.lib");
        var listing = await Processes.RunAsync(Path.Combine(msvc, "lib.exe"), ["/nologo", "/list", archive], root);
        if (listing.ExitCode != 0 || listing.TimedOut) throw new InvalidDataException("Configuration archive listing failed.");
        var members = listing.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var all = JsonSerializer.Deserialize<JsonElement[]>(await File.ReadAllTextAsync(Path.Combine(obj, "compile_commands.json")), Json)!;
        var commands = all.Where(c => c.GetProperty("output").GetString()!.Replace('\\', '/').Contains("/WitOS.ConfigProbe.dir/", StringComparison.Ordinal)).ToArray();
        string[] names = ["rhconfig.witos.cpp", "gcconfig.slice.cpp", "gcenv.config.slice.cpp", "runtime_config.cpp", "pal_init.witos.cpp", "allocheap.witos.cpp", "dispatch.shared.slice.cpp", "dispatch.aot.slice.cpp", "runtime_allocator.cpp", "startup.objects.slice.cpp", "runtime_instance.cpp", "runtime_barrier.cpp", "runtime_time.cpp", "runtime_crt.cpp", "runtime_stack.cpp", "runtime_cpu.cpp"];
        if (members.Length != names.Length || commands.Length != names.Length)
            throw new InvalidDataException("Configuration probe must contain exactly the selected sixteen objects.");
        foreach (var name in names)
        {
            var command = commands.Single(c => Path.GetFileName(c.GetProperty("file").GetString()!) == name);
            var compile = command.GetProperty("command").GetString()!;
            if (!compile.Contains("-DNO_STRESS_LOG", StringComparison.Ordinal) && !compile.Contains("/DNO_STRESS_LOG", StringComparison.Ordinal))
                throw new InvalidDataException("Configuration probe must match the WitOS NO_STRESS_LOG profile.");
            NativeObject.VerifyArchive(archive, Path.GetFullPath(command.GetProperty("output").GetString()!, command.GetProperty("directory").GetString()!));
        }
        var stackCommand = commands.Single(c => Path.GetFileName(c.GetProperty("file").GetString()!) == "runtime_stack.cpp");
        if (!stackCommand.GetProperty("command").GetString()!.Contains("/Gs4096", StringComparison.Ordinal))
            throw new InvalidDataException("Compiler stack-probe threshold missing.");
        var stackSymbols = await Processes.RunAsync(Path.Combine(msvc, "dumpbin.exe"),
            ["/symbols", Path.GetFullPath(stackCommand.GetProperty("output").GetString()!, stackCommand.GetProperty("directory").GetString()!)], root);
        if (stackSymbols.ExitCode != 0 || stackSymbols.TimedOut || !System.Text.RegularExpressions.Regex.IsMatch(stackSymbols.Output, @"UNDEF[^\r\n]*\b__chkstk\b"))
            throw new InvalidDataException("Compiler did not emit the real __chkstk dependency.");
        File.Copy(stackObject, Path.Combine(output, "chkstk.obj"), overwrite: true);
        File.Copy(archive, Path.Combine(output, "WitOS.ConfigProbe.lib"), overwrite: true);
        File.Copy(minipalArchive, Path.Combine(output, "WitOS.Minipal.lib"), overwrite: true);
        File.Copy(memoryObject, Path.Combine(output, "crt_memory.witos.obj"), overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(output, "archive-report.json"), JsonSerializer.Serialize(new
        {
            archiveSha256 = Hash(archive), minipalSha256 = Hash(minipalArchive), memorySha256 = Hash(memoryObject), stackSha256 = Hash(stackObject), members, commands, guestManagedRuntime = false,
            localInputs = new[] { "tests/User.X64/runtime_cpu.cpp", "src/Kernel.Arch.X64/user_runtime_cpu_fixture.asm", "src/Kernel.Arch.X64/minipal_cpu.witos.cpp", "src/Kernel.Arch.X64/minipal_cpu.witos.h", "tests/User.X64/runtime_stack.cpp", "src/Kernel.Arch.X64/user_runtime_stack_fixture.asm", "src/Kernel.Arch.X64/chkstk.asm", "tests/User.X64/runtime_crt.cpp", "src/Runtime.NativeAot/crt_memory.witos.c", "tests/User.X64/runtime_time.cpp", "src/Runtime.NativeAot/minipal_time.witos.cpp", "src/Runtime.NativeAot/minipal_time.witos.h", "tests/User.X64/runtime_config.cpp", "tests/User.X64/runtime_instance.cpp", "tests/User.X64/runtime_barrier.cpp", "artifacts/runtime-config/source/startup.objects.slice.cpp", "artifacts/runtime-config/source/threadstore.witos.cpp", "artifacts/runtime-config/source/thread.witos.cpp", "artifacts/runtime-config/include/stressLog.h", "tests/User.X64/runtime_allocator.cpp", "tests/User.X64/protocol.h", "artifacts/runtime-config/source/allocheap.witos.cpp", "artifacts/runtime-config/source/dispatch.shared.slice.cpp", "artifacts/runtime-config/source/dispatch.aot.slice.cpp",
                "src/Runtime.NativeAot/runtime-overlay.cmake", "src/Runtime.NativeAot/config-probe/CMakeLists.txt",
                "src/Runtime.NativeAot/crt_config.witos.cpp", "src/Runtime.NativeAot/pal_init.witos.cpp", "src/Runtime.NativeAot/pal_environment.witos.h",
                "src/System.Native/tls.h", "src/Kernel/include/witos/user_abi.h",
                "artifacts/runtime-config/source/rhconfig.witos.cpp", "artifacts/runtime-config/source/gcconfig.slice.cpp",
                "artifacts/runtime-config/source/gcenv.config.slice.cpp" }
                .Select(p => new { path = p, sha256 = Hash(Path.Combine(root, p)) })
        }, Json));
        Console.WriteLine("[SOURCE-PASS] Configuration probe: sixteen exact source objects; collector and thread attachment excluded.");
    }

    public static async Task BuildImageAsync(string root, string output, string msvc)
    {
        // The runtime-config command refreshes source/objects first. Ordinary
        // boot builds never silently use a cached upstream configuration object.
        var archive = Path.Combine(root, "artifacts", "runtime-config", "WitOS.ConfigProbe.lib");
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "artifacts", "runtime-config", "archive-report.json")));
        if (Hash(archive) != report.RootElement.GetProperty("archiveSha256").GetString())
            throw new InvalidDataException("Configuration probe archive differs from its verified build.");
        foreach (var input in report.RootElement.GetProperty("localInputs").EnumerateArray())
            if (Hash(Path.Combine(root, input.GetProperty("path").GetString()!)) != input.GetProperty("sha256").GetString())
                throw new InvalidDataException("Configuration source inputs changed after archive verification; rerun runtime-config.");
        var minipalArchive = Path.Combine(root, "artifacts", "runtime-config", "WitOS.Minipal.lib");
        if (Hash(minipalArchive) != report.RootElement.GetProperty("minipalSha256").GetString())
            throw new InvalidDataException("Minipal archive changed after source verification.");
        var memoryObject = Path.Combine(root, "artifacts", "runtime-config", "crt_memory.witos.obj");
        if (Hash(memoryObject) != report.RootElement.GetProperty("memorySha256").GetString())
            throw new InvalidDataException("Native memory object changed after archive verification.");
        var stackObject = Path.Combine(root, "artifacts", "runtime-config", "chkstk.obj");
        if (Hash(stackObject) != report.RootElement.GetProperty("stackSha256").GetString())
            throw new InvalidDataException("Stack probe object changed after archive verification.");
        var stackTest = Path.Combine(output, "runtime_stack_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/Fo" + stackTest, Path.Combine(root, "src", "Kernel.Arch.X64", "user_runtime_stack_fixture.asm")], root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var sdkVersion = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Include", sdkVersion);
        var crt = Path.Combine(output, "crt_config.witos.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo", "/c", "/TP", "/std:c++17", "/W4", "/WX", "/GS-", "/Zl", "/Oi", "/O1", "/GR-", "/DNDEBUG",
                $"/I{Path.Combine(vc, "include")}", $"/I{Path.Combine(sdk, "ucrt")}", $"/Fo{crt}",
                Path.Combine(root, "src", "Runtime.NativeAot", "crt_config.witos.cpp")], root);
        string[] shared = ["native_start.obj", "environment_pal_environment.witos.obj", "native_new.witos.obj",
            "native_error.obj", "native_environment.obj", "dynamic_image.obj", "dynamic_thread.obj",
            "dynamic_tls.witos.obj", "dynamic_tls_metadata.obj", "gcenv.witos.obj", "pal_pal.witos.obj", "pal_pal_error.witos.obj", "pal_pal_memory.witos.obj", "module_pal_module.witos.obj", "crst.witos.obj", "mutex.witos.obj"];
        var image = Path.Combine(output, "RuntimeConfigFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo", "/subsystem:native", "/entry:wit_native_start", "/nodefaultlib", "/machine:x64", "/fixed:no", "/dynamicbase",
                "/incremental:no", "/Brepro", "/opt:ref", "/include:_tls_used", "/merge:.CRT=.rdata", "/base:0x180000000",
                $"/out:{image}", $"/map:{Path.Combine(output, "RuntimeConfigFixture.map")}", archive, minipalArchive, memoryObject, stackObject, stackTest, crt, .. shared.Select(p => Path.Combine(output, p))], root);
        var bytes = await File.ReadAllBytesAsync(image);
        using var reader = new PEReader(new MemoryStream(bytes, writable: false));
        var h = reader.PEHeaders.PEHeader ?? throw new InvalidDataException("Configuration PE missing.");
        if (h.ImportTableDirectory.Size != 0 || h.DelayImportTableDirectory.Size != 0 || h.LoadConfigTableDirectory.Size != 0 ||
            reader.PEHeaders.CorHeader is not null || h.ThreadLocalStorageTableDirectory.Size != 40 || h.BaseRelocationTableDirectory.Size == 0)
            throw new InvalidDataException("Configuration image violates the import-free static TLS profile.");
        var text = new StringBuilder("/* Real upstream native configuration probe; generated. */\nstatic const unsigned char wit_runtime_config_image[] = {\n");
        for (var i = 0; i < bytes.Length; i += 16)
            text.AppendLine("    " + string.Join(", ", bytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        text.AppendLine("};");
        var raw = (byte[])bytes.Clone();
        // Controlled fixture: retain identical code/sections, omit only the
        // PE32+ TLS directory so the kernel supplies no compiler GS page.
        var tlsDirectory = checked(BitConverter.ToInt32(raw, 0x3c) + 24 + 112 + 9 * 8);
        raw.AsSpan(tlsDirectory, 8).Clear();
        using (var plain = new PEReader(new MemoryStream(raw, writable: false)))
            if (plain.PEHeaders.PEHeader!.ThreadLocalStorageTableDirectory.Size != 0 ||
                plain.PEHeaders.PEHeader.ThreadLocalStorageTableDirectory.RelativeVirtualAddress != 0)
                throw new InvalidDataException("Raw configuration fixture retained a compiler TLS directory.");
        await File.WriteAllBytesAsync(Path.Combine(output, "RuntimeConfigRawFixture.pe"), raw);
        text.AppendLine("static const unsigned char wit_runtime_config_raw_image[] = {");
        for (var i = 0; i < raw.Length; i += 16)
            text.AppendLine("    " + string.Join(", ", raw.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        text.AppendLine("};");
        var map = await File.ReadAllTextAsync(Path.Combine(output, "RuntimeConfigFixture.map"));
        foreach (var symbol in new[] { "__chkstk", "wit_chkstk_end" }) {
            var match = System.Text.RegularExpressions.Regex.Match(map, @"\b" + symbol + @"\s+([0-9a-fA-F]{16})\b");
            if (!match.Success) throw new InvalidDataException("Stack probe marker missing from map: " + symbol);
            var rva = Convert.ToUInt64(match.Groups[1].Value, 16) - h.ImageBase;
            text.AppendLine($"#define WIT_STACK_PROBE_{(symbol == "__chkstk" ? "BEGIN" : "END")} 0x{rva:X}U");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "runtime_config_image.h"), text.ToString(), Encoding.ASCII);
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "runtime-config", "image-report.json"), JsonSerializer.Serialize(new
        {
            guestManagedRuntime = false, imageBytes = bytes.Length, imageSha256 = Hash(image), unwindEntries = h.ExceptionTableDirectory.Size / 12,
            rawImageSha256 = Hash(Path.Combine(output, "RuntimeConfigRawFixture.pe")), archiveSha256 = Hash(archive), crtSha256 = Hash(crt), sharedObjects = shared.Select(p => new { file = p, sha256 = Hash(Path.Combine(output, p)) })
        }, Json));
        await RuntimeCpuImage.BuildAsync(root, output, msvc, archive, minipalArchive, memoryObject, crt);
        Console.WriteLine($"RuntimeConfigFixture: {bytes.Length} bytes, real upstream configuration methods, no OS/CRT imports.");
    }
}
