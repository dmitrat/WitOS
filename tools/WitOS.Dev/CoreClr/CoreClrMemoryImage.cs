using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.NativeAot;
using WitOS.Dev.NativeAot.References;
namespace WitOS.Dev.CoreClr;

/// <summary>
/// Builds the guest image that tests the owned executable-memory backend for CoreCLR (not guest CoreCLR).
/// </summary>
internal static class CoreClrMemoryImage
{
    #region Functions

    /// <summary>
    /// Compiles and links the CoreCLR memory test image.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    internal static async Task BuildAsync(string root, string output, string msvc)
    {
        var support = await BuildSupportAsync(root, output, msvc);
        var objects = new List<string>();
        foreach (var file in new[] { "tests/User.X64/coreclr_mapper.cpp", "tests/User.X64/module_unwind.cpp", "tests/User.X64/module_foreign_unwind.cpp", "tests/User.X64/coreclr_dynamic_unwind.cpp" })
            objects.Add(await CompileAsync(root, output, msvc, file));
        objects.AddRange(support.Objects);
        objects.Add(support.Entry);
        var frameObject = Path.Combine(output, "coreclr_jit_frame.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + frameObject, Path.Combine(root, "tests/User.X64/coreclr_jit_frame.asm")], root);
        objects.Add(frameObject);
        var image = Path.Combine(output, "CoreClrMapperFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/subsystem:native", "/entry:wit_native_start", "/nodefaultlib", "/machine:x64", "/fixed:no", "/dynamicbase", "/incremental:no", "/Brepro", "/base:0x180000000", "/include:_tls_used", "/out:" + image, .. objects], root);
        var bytes = await File.ReadAllBytesAsync(image);
        using var pe = new PEReader(new MemoryStream(bytes));
        var h = pe.PEHeaders.PEHeader!;
        if (pe.PEHeaders.CorHeader is not null || h.ImportTableDirectory.Size != 0 || h.DelayImportTableDirectory.Size != 0)
            throw new InvalidDataException("VMToOS fixture acquired OS imports or managed header.");
        var text = new StringBuilder("static const unsigned char wit_coreclr_mapper_image[]={\n");
        for (var i = 0; i < bytes.Length; i += 16)
            text.AppendLine(string.Join(",", bytes.Skip(i).Take(16).Select(v => $"0x{v:X2}")) + ",");
        text.AppendLine("};");
        await File.WriteAllTextAsync(Path.Combine(output, "coreclr_mapper_image.h"), text.ToString(), Encoding.ASCII);
        await File.WriteAllTextAsync(Path.Combine(output, "coreclr-mapper-image.json"), System.Text.Json.JsonSerializer.Serialize(new { headerSha256 = DIGEST, imageSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), profile = "native VMToOS adapter probe; GS/EH disabled fixture, not source-built guest CoreCLR" }));
        // The runtimes compile once for the fixture and the libraries it loads (P6.4.j3c): the C++ library and the .NET
        // host's, which must leave no symbol unresolved.
        var runtimes = await HostRuntimeImage.CompileRuntimesAsync(root, output, msvc);
        string[] library = [.. await BuildLibrarySupportAsync(root, output, msvc, support), .. runtimes];
        await HostRuntimeImage.BuildLibraryAsync(root, output, msvc, library);
        var host = Path.Combine(output, "host");
        var unresolved = await CoreClrHostGuest.BuildAsync(root, host, msvc, library);
        if (unresolved.Values.Any(symbols => symbols.Length > 0))
            throw new InvalidDataException("The guest host's libraries left symbols unresolved: " +
                string.Join(", ", unresolved.Values.SelectMany(symbols => symbols)));
        await HostRuntimeImage.BuildAsync(root, output, msvc, [.. support.Objects, .. support.Adapters, support.Entry], runtimes,
            Path.Combine(host, "source/src/native/corehost"));
    }

    /// <summary>
    /// Compiles the guest's native support for the mapper and the C++ modules: the native heap, exception dispatch and
    /// unwinding with the dynamic function tables they consult, GS, memory routines, last error, libraries, paths and
    /// files, the Win32 adapters and the process entry. The output directory must hold user_abi.inc and
    /// native_error.obj (<see cref="UserImage.PrepareAbiAsync"/>).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>The support objects.</returns>
    internal static async Task<GuestSupport> BuildSupportAsync(string root, string output, string msvc)
    {
        await PrepareHeadersAsync(root);
        var objects = new List<string>();
        foreach (var file in new[]{"src/Runtime.CoreClr/module_functions.witos.cpp","src/Runtime.CoreClr/doublemapping.witos.cpp","src/Runtime.CoreClr/function_tables.witos.cpp","src/Runtime.CoreClr/function_tables_guest.witos.cpp","src/Runtime.CoreClr/dynamic_unwind_guest.witos.cpp",
            "src/Runtime.Native/library.c","src/Runtime.Native/library_lifecycle.c","src/Runtime.Native/path.c","src/Runtime.Native/current_directory.c","src/Runtime.Native/file.c",
            "src/Runtime.NativeAot/crt_memory.witos.c","src/Runtime.NativeAot/crt_config.witos.cpp",
            "src/Runtime.Native/tls_metadata.c","src/Runtime.Native/image.c","src/Runtime.NativeAot/unwind_checked.witos.cpp","src/Runtime.NativeAot/unwind_validation.witos.cpp",
            "src/Runtime.NativeAot/unwind_scope.witos.cpp","src/Runtime.NativeAot/unwind_guest.witos.cpp","src/Runtime.NativeAot/native_exception.witos.cpp","src/Runtime.NativeAot/seh_scope.witos.cpp",
            "src/Runtime.NativeAot/seh_validation.witos.cpp","src/Runtime.NativeAot/seh_security.witos.cpp","src/Runtime.NativeAot/security_handler.witos.cpp",
            "src/Runtime.NativeAot/security_cookie.witos.cpp","src/Runtime.NativeAot/failfast_exception.witos.cpp","src/Runtime.NativeAot/pal_error.witos.cpp","src/Runtime.NativeAot/native_new.witos.cpp",
            "src/Runtime.NativeAot/X64/native_exception_x64.cpp","artifacts/runtime-unwind/unwinder.checked.cpp"})
            objects.Add(await CompileAsync(root, output, msvc, file));
        var bindings = Path.Combine(output, "coreclr_unwind_bindings.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + bindings, Path.Combine(root, "src/Runtime.CoreClr/X64/coreclr_unwind_bindings.asm")], root);
        objects.Add(bindings);
        foreach (var (directory, name) in new[] { ("src/Runtime.Pal.Win32/X64", "native_exception"), ("src/Runtime.NativeAot/X64", "security_cookie"), ("src/Runtime.NativeAot/X64", "unwind_consolidation") })
        {
            var obj = Path.Combine(output, "coreclr-" + name + ".obj");
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/I" + output, "/Fo" + obj, Path.Combine(root, directory, name + ".asm")], root);
            objects.Add(obj);
        }
        objects.Add(Path.Combine(output, "native_error.obj"));
        var adapters = new List<string>();
        foreach (var file in HostRuntimeImage.ADAPTERS)
            adapters.Add(await CompileAsync(root, output, msvc, file));
        adapters.Add(await CompileAsync(root, output, msvc, HostRuntimeImage.ENVIRONMENT, "/DUNICODE", "/D_UNICODE"));
        var entry = Path.Combine(output, "coreclr_mapper_start.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/I" + output, "/Fo" + entry, Path.Combine(root, "src/Runtime.Native/X64/native_start.asm")], root);
        return new GuestSupport(objects, adapters, entry);
    }

    /// <summary>
    /// The guest's native support as a C++ library links it (P6.4.j3c): the support and the adapters without the
    /// process's compiler TLS, the library's dynamic TLS and startup in its place, and the system-call primitives
    /// without the process entry.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <param name="support">The support <see cref="BuildSupportAsync"/> compiled into the same directory.</param>
    /// <returns>The library's support objects.</returns>
    internal static async Task<IReadOnlyList<string>> BuildLibrarySupportAsync(string root, string output, string msvc,
        GuestSupport support)
    {
        var process = new[] { HostRuntimeImage.PROCESS_TLS, HostRuntimeImage.PROCESS_TLS_DIRECTORY }
            .Select(file => Path.Combine(output, Path.GetFileName(file) + ".obj")).ToArray();
        var objects = support.Objects.Concat(support.Adapters)
            .Where(obj => !process.Contains(obj, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var file in HostRuntimeImage.LIBRARY_STARTUP)
            objects.Add(await CompileAsync(root, output, msvc, file));
        var transport = Path.Combine(output, "library_transport.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/DWITOS_NATIVE_TRANSPORT_ONLY",
            "/I" + output, "/Fo" + transport, Path.Combine(root, "src/Runtime.Native/X64/native_start.asm")], root);
        objects.Add(transport);
        return objects;
    }

    #endregion

    #region Tools

    private const string REVISION = "b82454cad0aaaae3db2cf18fbf2cccc36e201ccc";
    private const string DIGEST = "290569807f98c8d73d4eff11b5469654daf1f76b444d6fa9ea2ede708acf4779";

    // The hash-verified upstream headers the support compiles against.
    private static async Task PrepareHeadersAsync(string root)
    {
        if (RuntimeExperiment.ReadLock(root).RuntimeCommit != REVISION)
            throw new InvalidDataException("CoreCLR mapper header pin mismatch.");
        using var client = new HttpClient();
        await RuntimeExperiment.FetchAsync(client, Path.Combine(root, ".tools/runtime-audit"), "runtime", REVISION, "src/coreclr/minipal/minipal.h", DIGEST);
        var pin = RuntimeExperiment.ReadLock(root);
        foreach (var item in pin.Sources.Where(item => item.Path.EndsWith(".h", StringComparison.Ordinal)))
            await RuntimeExperiment.FetchAsync(client, Path.Combine(root, ".tools/runtime-audit"), "runtime", pin.RuntimeCommit, item.Path, item.Sha256);
        await RuntimeConfigProbe.PrepareAsync(root, RuntimeExperiment.ReadLock(root));
        await RuntimeUnwindReference.PrepareAsync(root);
    }

    internal static async Task<string> CompileAsync(string root, string output, string msvc, string file, params string[] options)
    {
        var verified = Path.Combine(root, ".tools/runtime-audit/runtime", REVISION);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var unwind = Path.Combine(root, "artifacts/runtime-unwind");
        var minipal = Path.Combine(verified, "src/coreclr/minipal");
        var obj = Path.Combine(output, Path.GetFileName(file) + ".obj");
        var c = file.EndsWith(".c", StringComparison.Ordinal);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/c",c?"/TC":"/TP",c?"/std:c17":"/std:c++17","/GS-","/GR-","/EHs-c-","/Zl","/Oi","/O1","/DTARGET_AMD64","/DHOST_AMD64","/DHOST_64BIT","/DTARGET_64BIT","/DHOST_WINDOWS","/DTARGET_WINDOWS","/DNDEBUG","/DNOMINMAX","/DWITOS_DYNAMIC_CODE","/W4","/WX",.. options,
            "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",version,"ucrt"),"/I"+Path.Combine(sdk,"Include",version,"um"),"/I"+Path.Combine(sdk,"Include",version,"shared"),"/I"+unwind,"/I"+Path.Combine(verified,"src/coreclr/nativeaot/Runtime"),"/I"+Path.Combine(verified,"src/coreclr/nativeaot/Runtime/inc"),"/I"+Path.Combine(verified,"src/coreclr/nativeaot/Runtime/windows"),"/I"+Path.Combine(verified,"src/coreclr/gc/env"),"/I"+Path.Combine(verified,"src/native"),"/I"+Path.Combine(root,"artifacts/runtime-config/include"),"/I"+Path.Combine(root,"src/Runtime.NativeAot"),"/I"+Path.Combine(root,"src/Runtime.CoreClr"),"/I"+minipal,"/I"+Path.Combine(root,"src/Kernel/include"),
            "/I"+Path.Combine(root,"src/Runtime.Native"),"/I"+Path.Combine(root,"tests/User.X64"),"/Fo"+obj,Path.Combine(root,file)], root);
        return obj;
    }

    #endregion
}

/// <summary>
/// The guest's native support for a module: objects every module links, the Win32 adapters only the C++ modules
/// link, and the process entry, which a library does not link.
/// </summary>
/// <param name="Objects">Objects every module links.</param>
/// <param name="Adapters">The compiled Win32 adapters.</param>
/// <param name="Entry">The process entry.</param>
internal sealed record GuestSupport(IReadOnlyList<string> Objects, IReadOnlyList<string> Adapters, string Entry);
