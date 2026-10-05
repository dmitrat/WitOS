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
        const string revision = "b82454cad0aaaae3db2cf18fbf2cccc36e201ccc";
        const string digest = "290569807f98c8d73d4eff11b5469654daf1f76b444d6fa9ea2ede708acf4779";
        if (RuntimeExperiment.ReadLock(root).RuntimeCommit != revision)
            throw new InvalidDataException("CoreCLR mapper header pin mismatch.");
        using var client = new HttpClient();
        var header = await RuntimeExperiment.FetchAsync(client, Path.Combine(root, ".tools/runtime-audit"), "runtime", revision, "src/coreclr/minipal/minipal.h", digest);
        var pin = RuntimeExperiment.ReadLock(root);
        foreach (var item in pin.Sources.Where(item => item.Path.EndsWith(".h", StringComparison.Ordinal)))
            await RuntimeExperiment.FetchAsync(client, Path.Combine(root, ".tools/runtime-audit"), "runtime", pin.RuntimeCommit, item.Path, item.Sha256);
        var verified = Path.Combine(root, ".tools/runtime-audit/runtime", revision);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        await RuntimeConfigProbe.PrepareAsync(root, RuntimeExperiment.ReadLock(root));
        await RuntimeUnwindReference.PrepareAsync(root);
        var unwind = Path.Combine(root, "artifacts/runtime-unwind");
        var objects = new List<string>();
        // The host runtime fixture (P6.4.i) shares the guest's native support with the mapper: the native heap,
        // exception dispatch and unwinding with the dynamic function tables it consults, GS, memory routines and last
        // error, but not the mapper's tests.
        var support = new List<string>();
        async Task<string> Native(string file)
        {
            var obj = Path.Combine(output, Path.GetFileName(file) + ".obj");
            var c = file.EndsWith(".c", StringComparison.Ordinal);
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/c",c?"/TC":"/TP",c?"/std:c17":"/std:c++17","/GS-","/GR-","/EHs-c-","/Zl","/Oi","/O1","/DTARGET_AMD64","/DHOST_AMD64","/DHOST_64BIT","/DTARGET_64BIT","/DHOST_WINDOWS","/DTARGET_WINDOWS","/DNDEBUG","/DNOMINMAX","/DWITOS_DYNAMIC_CODE","/W4","/WX",
                "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",version,"ucrt"),"/I"+Path.Combine(sdk,"Include",version,"um"),"/I"+Path.Combine(sdk,"Include",version,"shared"),"/I"+unwind,"/I"+Path.Combine(verified,"src/coreclr/nativeaot/Runtime"),"/I"+Path.Combine(verified,"src/coreclr/nativeaot/Runtime/inc"),"/I"+Path.Combine(verified,"src/coreclr/nativeaot/Runtime/windows"),"/I"+Path.Combine(verified,"src/coreclr/gc/env"),"/I"+Path.Combine(verified,"src/native"),"/I"+Path.Combine(root,"artifacts/runtime-config/include"),"/I"+Path.Combine(root,"src/Runtime.NativeAot"),"/I"+Path.Combine(root,"src/Runtime.CoreClr"),"/I"+Path.GetDirectoryName(header),"/I"+Path.Combine(root,"src/Kernel/include"),
                "/I"+Path.Combine(root,"src/Runtime.Native"),"/I"+Path.Combine(root,"tests/User.X64"),"/Fo"+obj,Path.Combine(root,file)], root);
            return obj;
        }
        foreach (var file in new[] { "tests/User.X64/coreclr_mapper.cpp", "tests/User.X64/module_unwind.cpp", "tests/User.X64/module_foreign_unwind.cpp", "tests/User.X64/coreclr_dynamic_unwind.cpp" })
            objects.Add(await Native(file));
        foreach (var file in new[]{"src/Runtime.CoreClr/module_functions.witos.cpp","src/Runtime.CoreClr/doublemapping.witos.cpp","src/Runtime.CoreClr/function_tables.witos.cpp","src/Runtime.CoreClr/function_tables_guest.witos.cpp","src/Runtime.CoreClr/dynamic_unwind_guest.witos.cpp",
            "src/Runtime.Native/library.c","src/Runtime.Native/library_lifecycle.c","src/Runtime.Native/path.c","src/Runtime.Native/current_directory.c","src/Runtime.Native/file.c",
            "src/Runtime.NativeAot/crt_memory.witos.c","src/Runtime.NativeAot/crt_config.witos.cpp",
            "src/Runtime.Native/tls_metadata.c","src/Runtime.Native/image.c","src/Runtime.NativeAot/unwind_checked.witos.cpp","src/Runtime.NativeAot/unwind_validation.witos.cpp",
            "src/Runtime.NativeAot/unwind_scope.witos.cpp","src/Runtime.NativeAot/unwind_guest.witos.cpp","src/Runtime.NativeAot/native_exception.witos.cpp","src/Runtime.NativeAot/seh_scope.witos.cpp",
            "src/Runtime.NativeAot/seh_validation.witos.cpp","src/Runtime.NativeAot/seh_security.witos.cpp","src/Runtime.NativeAot/security_handler.witos.cpp",
            "src/Runtime.NativeAot/security_cookie.witos.cpp","src/Runtime.NativeAot/failfast_exception.witos.cpp","src/Runtime.NativeAot/pal_error.witos.cpp","src/Runtime.NativeAot/native_new.witos.cpp",
            "src/Runtime.NativeAot/X64/native_exception_x64.cpp","artifacts/runtime-unwind/unwinder.checked.cpp"})
        {
            var obj = await Native(file);
            objects.Add(obj);
            support.Add(obj);
        }
        support.Add(await Native("src/Runtime.NativeAot/native_diagnostics.witos.cpp"));
        support.Add(await Native("src/Runtime.NativeAot/native_stl.witos.cpp"));
        var entry = Path.Combine(output, "coreclr_mapper_start.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/I" + output, "/Fo" + entry, Path.Combine(root, "src/Runtime.Native/X64/native_start.asm")], root);
        objects.Add(entry);
        support.Add(entry);
        var frameObject = Path.Combine(output, "coreclr_jit_frame.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + frameObject, Path.Combine(root, "tests/User.X64/coreclr_jit_frame.asm")], root);
        objects.Add(frameObject);
        var bindings = Path.Combine(output, "coreclr_unwind_bindings.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + bindings, Path.Combine(root, "src/Runtime.CoreClr/X64/coreclr_unwind_bindings.asm")], root);
        objects.Add(bindings);
        support.Add(bindings);
        foreach (var (directory, name) in new[] { ("src/Runtime.Pal.Win32/X64", "native_exception"), ("src/Runtime.NativeAot/X64", "security_cookie"), ("src/Runtime.NativeAot/X64", "unwind_consolidation") })
        {
            var obj = Path.Combine(output, "coreclr-" + name + ".obj");
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/I" + output, "/Fo" + obj, Path.Combine(root, directory, name + ".asm")], root);
            objects.Add(obj);
            support.Add(obj);
        }
        objects.Add(Path.Combine(output, "native_error.obj"));
        support.Add(Path.Combine(output, "native_error.obj"));
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
        await File.WriteAllTextAsync(Path.Combine(output, "coreclr-mapper-image.json"), System.Text.Json.JsonSerializer.Serialize(new { headerSha256 = digest, imageSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), profile = "native VMToOS adapter probe; GS/EH disabled fixture, not source-built guest CoreCLR" }));
        await HostRuntimeImage.BuildAsync(root, output, msvc, support);
    }

    #endregion
}
