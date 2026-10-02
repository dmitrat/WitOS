using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.NativeAot.References;
using WitOS.Dev.Pe;
namespace WitOS.Dev.NativeAot;

internal static class RuntimeCpuImage
{
    #region Functions

    public static async Task BuildAsync(string root, string output, string msvc, string archive, string minipal, string memory, string crt, string clockObject, string clockBinding, string fatalObject, string affinityObject, string mathObject, string logObject, NativePlatformObjects securityObjects)
    {
        var entry = Path.Combine(output, "runtime_cpu_entry.obj");
        var assembly = Path.Combine(output, "runtime_cpu_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo", "/c", "/TP", "/std:c++17", "/W4", "/WX", "/GS-", "/Zl", "/O1", "/GR-",
                "/I" + Path.Combine(root, "src", "System.Native"), "/I" + Path.Combine(root, "src", "Kernel", "include"),
                "/I" + Path.Combine(root, "tests", "User.X64"), "/Fo" + entry,
                Path.Combine(root, "tests", "User.X64", "runtime_cpu_entry.cpp")], root);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/I" + output, "/Fo" + assembly, Path.Combine(root, "src", "Kernel.Arch.X64", "user_runtime_cpu_fixture.asm")], root);
        var clockTest = Path.Combine(output, "runtime_clock_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/Fo" + clockTest, Path.Combine(root, "src", "Kernel.Arch.X64", "user_runtime_clock_fixture.asm")], root);
        var mathTest = Path.Combine(output, "runtime_math_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/Fo" + mathTest, Path.Combine(root, "src", "Kernel.Arch.X64", "user_runtime_math_fixture.asm")], root);
        var formatAssembly = Path.Combine(output, "native_format.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/Fo" + formatAssembly, Path.Combine(root, "src/Kernel.Arch.X64/native_format.asm")], root);
        var securityTest = Path.Combine(output, "runtime_security_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/Fo" + securityTest, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_security_fixture.asm")], root);
        var randomTest = Path.Combine(output, "runtime_random_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/Fo" + randomTest, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_random_fixture.asm")], root);
        var memoryTest = Path.Combine(output, "runtime_memory_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/Fo" + memoryTest, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_memory_fixture.asm")], root);
        var servicesTest = Path.Combine(output, "runtime_services_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/Fo" + servicesTest, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_services_fixture.asm")], root);
        var consoleTest = Path.Combine(output, "runtime_console_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/Fo" + consoleTest, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_console_fixture.asm")], root);
        var moduleTest = Path.Combine(output, "runtime_module_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + moduleTest, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_module_fixture.asm")], root);
        var diagnosticTest = Path.Combine(output, "runtime_diagnostics_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + diagnosticTest, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_diagnostics_fixture.asm")], root);
        string[] shared = ["gcenv.witos.obj", "process_crt_exit.witos.obj", "process_library_lifecycle.obj", "native_start.obj", "native_error.obj", "dynamic_thread.obj", "dynamic_image.obj", "dynamic_tls.witos.obj", "dynamic_tls_metadata.obj", "pal_pal_error.witos.obj"];
        // Only the original eighteen platform objects belong to the older CPU fixture.
        // Later UTF/module/name/diagnostic/COM services and their heap/bindings are
        // exercised only by the separate thread/console fixture. Keep unused import
        // slots out of the older CPU image and its bounded plain-unwind budget.
        var cpuPlatformObjects = securityObjects.Cpu;
        var path = Path.Combine(output, "RuntimeCpuFixture.pe");
        var mapPath = Path.Combine(output, "RuntimeCpuFixture.map");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo", "/subsystem:native", "/entry:wit_native_start", "/nodefaultlib", "/machine:x64", "/fixed:no", "/dynamicbase",
                "/incremental:no", "/Brepro", "/opt:ref", "/include:_tls_used", "/merge:.CRT=.rdata", "/base:0x180000000",
                "/out:" + path, "/map:" + mapPath, entry, assembly, clockTest, mathTest, formatAssembly, clockObject, clockBinding, fatalObject, affinityObject, mathObject, logObject, archive, minipal, memory, crt,
                securityTest, randomTest, memoryTest, servicesTest, .. cpuPlatformObjects, .. shared.Select(p => Path.Combine(output, p))], root);
        var bytes = await File.ReadAllBytesAsync(path);
        using var pe = new PEReader(new MemoryStream(bytes, writable: false));
        var h = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("CPU fixture PE header missing.");
        if (h.ImportTableDirectory.Size != 0 || h.DelayImportTableDirectory.Size != 0 || h.LoadConfigTableDirectory.Size != 0 ||
            pe.PEHeaders.CorHeader is not null || h.ThreadLocalStorageTableDirectory.Size != 40 || h.BaseRelocationTableDirectory.Size == 0)
            throw new InvalidDataException("CPU fixture violates the import-free TLS profile.");
        var raw = (byte[])bytes.Clone();
        raw.AsSpan(BitConverter.ToInt32(raw, 0x3c) + 24 + 112 + 9 * 8, 8).Clear();
        await File.WriteAllBytesAsync(Path.Combine(output, "RuntimeCpuRawFixture.pe"), raw);
        var header = new StringBuilder("/* Native CPU runtime fixture; generated. */\n");
        foreach (var (name, data) in new[] { ("wit_runtime_cpu_image", bytes), ("wit_runtime_cpu_raw_image", raw) })
        {
            header.AppendLine("static const unsigned char " + name + "[] = {");
            for (var i = 0; i < data.Length; i += 16)
                header.AppendLine("    " + string.Join(", ", data.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
            header.AppendLine("};");
        }
        var symbol = Regex.Match(await File.ReadAllTextAsync(mapPath), @"\bwit_cpu_avx_probe\s+([0-9a-fA-F]{16})\b");
        if (!symbol.Success)
            throw new InvalidDataException("AVX probe missing from CPU map.");
        header.AppendLine($"#define WIT_CPU_AVX_RVA 0x{Convert.ToUInt64(symbol.Groups[1].Value, 16) - h.ImageBase:X}U");
        await File.WriteAllTextAsync(Path.Combine(output, "runtime_cpu_image.h"), header.ToString(), Encoding.ASCII);
        static string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "runtime-config", "cpu-image-report.json"), JsonSerializer.Serialize(new
        {
            guestManagedRuntime = false,
            imageBytes = bytes.Length,
            imageSha256 = Hash(path),
            unwindEntries = h.ExceptionTableDirectory.Size / 12,
            inputs = new[] { entry, assembly, clockTest, mathTest, formatAssembly, clockObject, clockBinding, fatalObject, affinityObject, mathObject, logObject, archive, minipal, memory, crt }.Concat(cpuPlatformObjects).Append(securityTest).Append(randomTest).Append(memoryTest).Append(servicesTest).Concat(shared.Select(p => Path.Combine(output, p))).Select(p => new { file = p, sha256 = Hash(p) }),
            sources = new[] { "src/Runtime.NativeAot/native_diagnostics.witos.cpp", "src/Runtime.NativeAot/native_heap.witos.h", "tests/User.X64/runtime_diagnostics.cpp", "src/Kernel.Arch.X64/user_runtime_diagnostics_fixture.asm", "src/Runtime.NativeAot/pal_thread_name.witos.cpp", "tests/User.X64/runtime_thread_names.cpp", "src/Kernel.Arch.X64/user_thread_name.c", "src/Kernel/include/witos/thread_name.h", "src/Runtime.NativeAot/native_module.witos.cpp", "src/Kernel.Arch.X64/native_module.asm", "tests/User.X64/runtime_module_names.cpp", "src/Kernel.Arch.X64/user_runtime_module_fixture.asm", "tests/User.X64/runtime_encoding.cpp", "src/Runtime.NativeAot/native_encoding.witos.cpp", "src/Runtime.NativeAot/native_encoding.witos.h", "src/Kernel.Arch.X64/native_encoding.asm", "tests/User.X64/runtime_console.cpp", "src/Runtime.NativeAot/native_console.witos.cpp", "src/Runtime.NativeAot/native_processor.witos.cpp", "src/Kernel.Arch.X64/user_runtime_console_fixture.asm", "src/Kernel/include/witos/console_info.h", "tests/User.X64/runtime_object_wait.cpp", "src/Runtime.NativeAot/native_wait.witos.cpp", "src/Kernel.Arch.X64/native_wait.asm", "src/Kernel/include/witos/wait_objects.h", "tests/User.X64/runtime_services.cpp", "src/Runtime.NativeAot/native_services.witos.cpp", "src/Kernel.Arch.X64/native_services.asm", "src/Kernel.Arch.X64/user_runtime_services_fixture.asm", "tests/User.X64/runtime_memory.cpp", "src/Runtime.NativeAot/native_memory.witos.cpp", "src/Kernel.Arch.X64/native_memory.asm", "src/Kernel.Arch.X64/user_runtime_memory_fixture.asm", "tests/User.X64/runtime_random.cpp", "src/Runtime.NativeAot/native_random.witos.cpp", "src/Kernel.Arch.X64/native_random.asm", "src/Kernel.Arch.X64/user_runtime_random_fixture.asm", "tests/User.X64/runtime_security.cpp", "src/Kernel.Arch.X64/user_runtime_security_fixture.asm", "src/Runtime.NativeAot/security_cookie.witos.cpp", "src/Runtime.NativeAot/security_handler.witos.cpp", "src/Kernel.Arch.X64/security_cookie.asm", "src/System.Native/native_security.h", "tests/User.X64/runtime_format.cpp", "src/Runtime.NativeAot/native_format.witos.cpp", "src/Runtime.NativeAot/format_fixed.witos.cpp", "src/Runtime.NativeAot/format_fixed.witos.h", "src/Kernel.Arch.X64/native_format.asm", "src/Kernel.Arch.X64/user_runtime_math_fixture.asm", "tests/User.X64/runtime_math.cpp", "tests/User.X64/math_log_vectors.h", "src/Runtime.NativeAot/native_math.witos.cpp", "src/Runtime.NativeAot/math_bits.witos.h", "tests/User.X64/runtime_affinity.cpp", "src/Runtime.NativeAot/gc_affinity.witos.cpp", "src/Runtime.NativeAot/crt_exit.witos.cpp", "src/System.Native/library_lifecycle.c", "src/System.Native/library_lifecycle.h", "tests/User.X64/runtime_fatal.cpp", "src/Runtime.NativeAot/fatal.witos.cpp", "src/System.Native/diagnostics.h", "src/Kernel.Arch.X64/user_runtime_clock_fixture.asm", "tests/User.X64/runtime_clock.cpp", "tests/User.X64/runtime_cpu_entry.cpp", "tests/User.X64/runtime_cpu.cpp", "src/Kernel.Arch.X64/user_runtime_cpu_fixture.asm" }
                .Select(p => new { file = p, sha256 = Hash(Path.Combine(root, p)) })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"RuntimeCpuFixture: {bytes.Length} bytes, real minipal CPU backend, no OS/CRT imports.");
        var threadEntry = Path.Combine(output, "runtime_thread_entry.obj");
        var threadAssembly = Path.Combine(output, "runtime_reference_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo","/c","/TP","/std:c++17","/W4","/WX","/GS-","/Zl","/O1","/GR-",
             "/I"+Path.Combine(root,"src/System.Native"),"/I"+Path.Combine(root,"src/Kernel/include"),"/I"+Path.Combine(root,"tests/User.X64"),
             "/Fo"+threadEntry,Path.Combine(root,"tests/User.X64/runtime_thread_entry.cpp")], root);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + threadAssembly, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_reference_fixture.asm")], root);
        var threadPath = Path.Combine(output, "RuntimeThreadFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo","/subsystem:native","/entry:wit_native_start","/nodefaultlib","/machine:x64","/fixed:no","/dynamicbase","/incremental:no","/Brepro","/opt:ref","/include:_tls_used","/merge:.CRT=.rdata","/base:0x180000000","/out:"+threadPath,
             threadEntry,threadAssembly,consoleTest,moduleTest,diagnosticTest,archive,minipal,memory,crt,..securityObjects.Thread,..shared.Select(p=>Path.Combine(output,p))], root);
        var threadBytes = await File.ReadAllBytesAsync(threadPath);
        using var threadPe = new PEReader(new MemoryStream(threadBytes, writable: false));
        var th = threadPe.PEHeaders.PEHeader ?? throw new InvalidDataException("Thread fixture PE missing.");
        if (th.ImportTableDirectory.Size != 0 || th.DelayImportTableDirectory.Size != 0 || th.LoadConfigTableDirectory.Size != 0 || threadPe.PEHeaders.CorHeader is not null || th.ThreadLocalStorageTableDirectory.Size != 40)
            throw new InvalidDataException("Thread fixture violates native import-free TLS profile.");
        var threadRaw = (byte[])threadBytes.Clone();
        threadRaw.AsSpan(BitConverter.ToInt32(threadRaw, 0x3c) + 24 + 112 + 9 * 8, 8).Clear();
        var threadHeader = new StringBuilder("/* Native thread reference fixture; generated. */\n");
        foreach (var (name, data) in new[] { ("wit_runtime_threads_image", threadBytes), ("wit_runtime_threads_raw_image", threadRaw) })
        {
            threadHeader.AppendLine("static const unsigned char " + name + "[] = {");
            for (var i = 0; i < data.Length; i += 16)
                threadHeader.AppendLine("    " + string.Join(", ", data.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
            threadHeader.AppendLine("};");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "runtime_threads_image.h"), threadHeader.ToString(), Encoding.ASCII);
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts/runtime-config/thread-image-report.json"), JsonSerializer.Serialize(new
        {
            guestManagedRuntime = false,
            imageBytes = threadBytes.Length,
            unwindEntries = th.ExceptionTableDirectory.Size / 12,
            imageSha256 = Hash(threadPath),
            inputs = new[] { threadEntry, threadAssembly, consoleTest, moduleTest, diagnosticTest, archive, minipal, memory, crt }.Concat(securityObjects.Thread).Concat(shared.Select(p => Path.Combine(output, p))).Select(p => new { file = p, sha256 = Hash(p) })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"RuntimeThreadFixture: {threadBytes.Length} bytes, native thread references; no guest managed runtime.");
        var comEntry = Path.Combine(output, "runtime_com_entry.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo","/c","/TP","/std:c++17","/W4","/WX","/GS-","/Zl","/O1","/GR-",
             "/I"+Path.Combine(root,"src/System.Native"),"/I"+Path.Combine(root,"src/Kernel/include"),
             "/Fo"+comEntry,Path.Combine(root,"tests/User.X64/runtime_com_entry.cpp")], root);
        var comAssembly = Path.Combine(output, "runtime_com_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + comAssembly, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_com_fixture.asm")], root);
        var contextAssembly = Path.Combine(output, "runtime_context_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + contextAssembly, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_context_fixture.asm")], root);
        var captureAssembly = Path.Combine(output, "runtime_capture_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + captureAssembly, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_capture_fixture.asm")], root);
        var suspendAssembly = Path.Combine(output, "runtime_suspend_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + suspendAssembly, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_suspend_fixture.asm")], root);
        var comPlatformObjects = securityObjects.Com; // Keep production GS context storage separate from its probe.
        var comPath = Path.Combine(output, "RuntimeComFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo","/subsystem:native","/entry:wit_native_start","/nodefaultlib","/machine:x64","/fixed:no","/dynamicbase","/incremental:no","/Brepro","/opt:ref","/include:_tls_used","/merge:.CRT=.rdata","/base:0x180000000","/out:"+comPath,
             comEntry,comAssembly,contextAssembly,captureAssembly,suspendAssembly,archive,minipal,memory,crt,..comPlatformObjects,..shared.Select(p=>Path.Combine(output,p))], root);
        var comBytes = await File.ReadAllBytesAsync(comPath);
        using var comPe = new PEReader(new MemoryStream(comBytes, writable: false));
        var ch = comPe.PEHeaders.PEHeader ?? throw new InvalidDataException("COM fixture PE missing.");
        if (ch.ImportTableDirectory.Size != 0 || ch.DelayImportTableDirectory.Size != 0 || ch.LoadConfigTableDirectory.Size != 0 || comPe.PEHeaders.CorHeader is not null || ch.ThreadLocalStorageTableDirectory.Size != 40)
            throw new InvalidDataException("COM fixture violates native import-free TLS profile.");
        var comRaw = (byte[])comBytes.Clone();
        comRaw.AsSpan(BitConverter.ToInt32(comRaw, 0x3c) + 24 + 112 + 9 * 8, 8).Clear();
        var comHeader = new StringBuilder("/* Native MTA lifecycle fixture; generated. */\n");
        foreach (var (name, data) in new[] { ("wit_runtime_com_image", comBytes), ("wit_runtime_com_raw_image", comRaw) })
        {
            comHeader.AppendLine("static const unsigned char " + name + "[] = {");
            for (var i = 0; i < data.Length; i += 16)
                comHeader.AppendLine("    " + string.Join(", ", data.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
            comHeader.AppendLine("};");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "runtime_com_image.h"), comHeader.ToString(), Encoding.ASCII);
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts/runtime-config/com-image-report.json"), JsonSerializer.Serialize(new
        {
            guestManagedRuntime = false,
            imageBytes = comBytes.Length,
            unwindEntries = ch.ExceptionTableDirectory.Size / 12,
            imageSha256 = Hash(comPath),
            contextStorageEvidence = "Source-equivalent GS-disabled probe; production object retains GS handlers and is not yet guest-executable.",
            inputs = new[] { comEntry, comAssembly, contextAssembly, captureAssembly, suspendAssembly, archive, minipal, memory, crt }.Concat(comPlatformObjects).Concat(shared.Select(p => Path.Combine(output, p))).Select(p => new { file = p, sha256 = Hash(p) })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"RuntimeComFixture: {comBytes.Length} bytes; native MTA lifecycle, no managed runtime attachment.");
        var mutationEntry = Path.Combine(output, "runtime_context_set_entry.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo","/c","/TP","/std:c++17","/W4","/WX","/GS-","/Zl","/O1","/GR-",
             "/I"+Path.Combine(root,"src/System.Native"),"/I"+Path.Combine(root,"src/Kernel/include"),
             "/Fo"+mutationEntry,Path.Combine(root,"tests/User.X64/runtime_context_set_entry.cpp")], root);
        var mutationAssembly = Path.Combine(output, "runtime_context_set_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + mutationAssembly, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_context_set_fixture.asm")], root);
        var mutationStack = Path.Combine(root, "artifacts/runtime-config/chkstk.obj");
        using var productionReport = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "artifacts/runtime-source/witos-unwind-objects.json")));
        var productionContexts = productionReport.RootElement.GetProperty("contextObjects").EnumerateArray()
            .Concat(productionReport.RootElement.GetProperty("objects").EnumerateArray().Where(o => Path.GetFileName(o.GetProperty("file").GetString()) == "unwind_scope.witos.cpp.obj"))
            .Select(o => { var file = o.GetProperty("file").GetString()!; if (Hash(file) != o.GetProperty("sha256").GetString()) throw new InvalidDataException("Protected context object changed after archive verification."); return file; }).ToArray();
        if (productionContexts.Length != 3)
            throw new InvalidDataException("Expected actual protected context/storage/scope objects.");
        var mutationPath = Path.Combine(output, "RuntimeContextMutationFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo","/subsystem:native","/entry:wit_native_start","/nodefaultlib","/machine:x64","/fixed:no","/dynamicbase","/incremental:no","/Brepro","/opt:ref","/include:_tls_used","/merge:.CRT=.rdata","/base:0x180000000","/out:"+mutationPath,
             mutationEntry,mutationAssembly,mutationStack,..productionContexts,archive,minipal,memory,crt,..comPlatformObjects,..shared.Select(p=>Path.Combine(output,p))], root);
        var mutationBytes = await File.ReadAllBytesAsync(mutationPath);
        using var mutationPe = new PEReader(new MemoryStream(mutationBytes, writable: false));
        var mh = mutationPe.PEHeaders.PEHeader ?? throw new InvalidDataException("COM fixture PE missing.");
        if (mh.ImportTableDirectory.Size != 0 || mh.DelayImportTableDirectory.Size != 0 || mh.LoadConfigTableDirectory.Size != 0 || mutationPe.PEHeaders.CorHeader is not null || mh.ThreadLocalStorageTableDirectory.Size != 40)
            throw new InvalidDataException("COM fixture violates native import-free TLS profile.");
        var mutationRaw = (byte[])mutationBytes.Clone();
        mutationRaw.AsSpan(BitConverter.ToInt32(mutationRaw, 0x3c) + 24 + 112 + 9 * 8, 8).Clear();
        var mutationHeader = new StringBuilder("/* Native MTA lifecycle fixture; generated. */\n");
        foreach (var (name, data) in new[] { ("wit_runtime_context_mutation_image", mutationBytes), ("wit_runtime_context_mutation_raw_image", mutationRaw) })
        {
            mutationHeader.AppendLine("static const unsigned char " + name + "[] = {");
            for (var i = 0; i < data.Length; i += 16)
                mutationHeader.AppendLine("    " + string.Join(", ", data.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
            mutationHeader.AppendLine("};");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "runtime_context_mutation_image.h"), mutationHeader.ToString(), Encoding.ASCII);
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts/runtime-config/context-mutation-image-report.json"), JsonSerializer.Serialize(new
        {
            guestManagedRuntime = false,
            imageBytes = mutationBytes.Length,
            unwindEntries = mh.ExceptionTableDirectory.Size / 12,
            imageSha256 = Hash(mutationPath),
            contextStorageEvidence = "Actual byte-verified production GS context/storage/scope objects; runtime unwind loader profile required.",
            productionContexts = productionContexts.Select(p => new { file = p, sha256 = Hash(p) }),
            inputs = new[] { mutationEntry, mutationAssembly, mutationStack, archive, minipal, memory, crt }.Concat(comPlatformObjects).Concat(shared.Select(p => Path.Combine(output, p))).Select(p => new { file = p, sha256 = Hash(p) })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"RuntimeContextMutationFixture: {mutationBytes.Length} bytes; checked context mutation/restore, no managed runtime attachment.");
        var unwindEntry = Path.Combine(output, "runtime_unwind_entry.obj");
        var protectedFrame = Path.Combine(output, "runtime_unwind_protected.obj");
        foreach (var (sourceName, target, protection) in new[] { ("runtime_unwind_entry.cpp", unwindEntry, "/GS-"), ("runtime_unwind_protected.cpp", protectedFrame, "/GS") })
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/c","/TP","/std:c++17","/W4","/WX",protection,"/Zl","/O2","/GR-","/EHs-c-",
                "/I"+Path.Combine(root,"src/System.Native"),"/I"+Path.Combine(root,"src/Kernel/include"),"/Fo"+target,Path.Combine(root,"tests/User.X64",sourceName)], root);
        var frameSymbols = NativeObject.Inspect(protectedFrame);
        if (!frameSymbols.UndefinedExternals.Contains("__security_check_cookie") || !frameSymbols.UndefinedExternals.Contains("__GSHandlerCheck"))
            throw new InvalidDataException("Guest protected frame lost real GS check/handler dependencies.");
        var unwindAssembly = Path.Combine(output, "runtime_unwind_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + unwindAssembly, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_unwind_fixture.asm")], root);
        using var unwindReport = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "artifacts/runtime-source/witos-unwind-objects.json")));
        var unwindObjects = unwindReport.RootElement.GetProperty("objects").EnumerateArray().Select(o =>
        {
            var file = o.GetProperty("file").GetString()!;
            if (Hash(file) != o.GetProperty("sha256").GetString())
                throw new InvalidDataException("Production unwind object changed after archive verification.");
            return file;
        }).ToArray();
        if (unwindObjects.Length != 6)
            throw new InvalidDataException("Missing actual production unwind objects.");
        var exceptionObjects = unwindReport.RootElement.GetProperty("exceptionObjects").EnumerateArray().Select(o =>
        {
            var file = o.GetProperty("file").GetString()!;
            if (Hash(file) != o.GetProperty("sha256").GetString())
                throw new InvalidDataException("Production exception object changed after verification.");
            return file;
        }).ToArray();
        if (exceptionObjects.Length != 7)
            throw new InvalidDataException("Missing actual exception adapter objects.");
        var exceptionAssembly = Path.Combine(output, "runtime_exception_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/I" + output, "/Fo" + exceptionAssembly, Path.Combine(root, "src/Kernel.Arch.X64/user_runtime_exception_fixture.asm")], root);
        var gpAssembly = Path.Combine(output, "gp_reference_fixture.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + gpAssembly, Path.Combine(root, "src/Kernel.Arch.X64/gp_reference_fixture.asm")], root);
        var sehGsObjects = await RuntimeSehReference.BuildProtectedFrameAsync(root, msvc, output);
        var unwindPath = Path.Combine(output, "RuntimeUnwindFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo","/subsystem:native","/entry:wit_native_start","/nodefaultlib","/machine:x64","/fixed:no","/dynamicbase","/incremental:no","/Brepro","/opt:ref","/include:_tls_used","/merge:.CRT=.rdata","/base:0x180000000","/out:"+unwindPath,
             unwindEntry,protectedFrame,unwindAssembly,exceptionAssembly,gpAssembly,..sehGsObjects,mutationStack,..unwindObjects,..exceptionObjects,..productionContexts.Where(p=>Path.GetFileName(p)!="unwind_scope.witos.cpp.obj"),archive,minipal,memory,crt,..comPlatformObjects,..shared.Select(p=>Path.Combine(output,p))], root);
        var unwindBytes = await File.ReadAllBytesAsync(unwindPath);
        using var unwindPe = new PEReader(new MemoryStream(unwindBytes, writable: false));
        var uh = unwindPe.PEHeaders.PEHeader ?? throw new InvalidDataException("Unwind fixture PE missing.");
        if (uh.ImportTableDirectory.Size != 0 || uh.DelayImportTableDirectory.Size != 0 || uh.LoadConfigTableDirectory.Size != 0 || unwindPe.PEHeaders.CorHeader is not null || uh.ThreadLocalStorageTableDirectory.Size != 40)
            throw new InvalidDataException("Unwind fixture violates import-free native TLS profile.");
        var unwindHeader = new StringBuilder("/* Actual source-archive unwind objects with compiler GS metadata. */\nstatic const unsigned char wit_runtime_unwind_image[] = {\n");
        for (var i = 0; i < unwindBytes.Length; i += 16)
            unwindHeader.AppendLine("    " + string.Join(", ", unwindBytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        unwindHeader.AppendLine("};");
        var unwindRaw = (byte[])unwindBytes.Clone();
        unwindRaw.AsSpan(BitConverter.ToInt32(unwindRaw, 0x3c) + 24 + 112 + 9 * 8, 8).Clear();
        unwindHeader.AppendLine("static const unsigned char wit_runtime_unwind_raw_image[] = {");
        for (var i = 0; i < unwindRaw.Length; i += 16)
            unwindHeader.AppendLine("    " + string.Join(", ", unwindRaw.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        unwindHeader.AppendLine("};");
        await File.WriteAllTextAsync(Path.Combine(output, "runtime_unwind_image.h"), unwindHeader.ToString(), Encoding.ASCII);
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts/runtime-config/unwind-image-report.json"), JsonSerializer.Serialize(new
        {
            guestManagedRuntime = false,
            imageBytes = unwindBytes.Length,
            mappedBytes = uh.SizeOfImage,
            unwindEntries = uh.ExceptionTableDirectory.Size / 12,
            imageSha256 = Hash(unwindPath),
            productionObjects = unwindObjects.Concat(exceptionObjects).Concat(productionContexts.Where(p => Path.GetFileName(p) != "unwind_scope.witos.cpp.obj")).Select(p => new { file = p, sha256 = Hash(p) }),
            inputs = new[] { unwindEntry, protectedFrame, unwindAssembly, exceptionAssembly, gpAssembly, mutationStack, archive, minipal, memory, crt }.Concat(sehGsObjects).Concat(comPlatformObjects).Concat(shared.Select(p => Path.Combine(output, p))).Select(p => new { file = p, sha256 = Hash(p) })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"RuntimeUnwindFixture: {unwindBytes.Length} bytes, {uh.ExceptionTableDirectory.Size / 12} unwind records; actual archived GS-protected unwinder.");
    }

    #endregion
}
