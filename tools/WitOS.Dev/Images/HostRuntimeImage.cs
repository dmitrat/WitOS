using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using WitOS.Dev.CoreClr;
using WitOS.Dev.Host;
using WitOS.Dev.NativeAot;

namespace WitOS.Dev.Images;

/// <summary>
/// Builds the host runtime fixture (P6.4.i): the WitOS C++ runtime, the UCRT subset and the separately compiled sources
/// of the pinned microsoft/STL with their scenarios in one guest image, which the kernel loads with the full runtime
/// profile, as it will load the .NET host. Every scenario group must print the trace Windows prints.
/// </summary>
internal static class HostRuntimeImage
{
    #region Constants

    /// <summary>
    /// The fixture's entry: the scenario group of each mode.
    /// </summary>
    public const string MAIN = "tests/User.X64/host_runtime_main.cpp";

    /// <summary>
    /// The guest half of the STL scenarios.
    /// </summary>
    public const string STL_GUEST = "tests/User.X64/stl_scenarios_guest.cpp";

    /// <summary>
    /// The native heap at the full runtime profile's quotas.
    /// </summary>
    public const string HEAP = "tests/User.X64/heap_scenarios.cpp";

    /// <summary>
    /// A C++ library linked as the .NET host's libraries are (P6.4.j3c), which mode 27 loads from the boot package.
    /// </summary>
    public const string CXX_LIBRARY = "tests/User.X64/cxx_library.cpp";

    /// <summary>
    /// The fixture's side of mode 27.
    /// </summary>
    public const string CXX_LIBRARY_GUEST = "tests/User.X64/cxx_library_guest.cpp";

    /// <summary>
    /// Mode 28, which loads the .NET host's libraries from the boot package's .NET root (P6.4.j3c3).
    /// </summary>
    public const string HOST_LIBRARIES_GUEST = "tests/User.X64/host_libraries_guest.cpp";

    /// <summary>
    /// The process's compiler TLS, which a library replaces with <see cref="LIBRARY_STARTUP"/>.
    /// </summary>
    public const string PROCESS_TLS = "src/Runtime.NativeAot/tls.witos.cpp";

    /// <summary>
    /// The process's TLS directory, which a library takes from <see cref="LIBRARY_STARTUP"/> too.
    /// </summary>
    public const string PROCESS_TLS_DIRECTORY = "src/Runtime.Native/tls_metadata.c";

    /// <summary>
    /// A C++ library's dynamic TLS and startup, its entry point wit_library_cxx_entry (P6.4.j3c).
    /// </summary>
    public static readonly string[] LIBRARY_STARTUP = ["src/Runtime.Native/library_dynamic_tls.cpp",
        "src/Runtime.Native/library_startup.cpp"];

    /// <summary>
    /// WitOS's corehost PAL objects of P6.4.j2, the ones they call, and their guest checks.
    /// </summary>
    public static readonly string[] HOST_PAL = ["src/Runtime.CoreClr/host_strings.witos.cpp",
        "src/Runtime.CoreClr/host_trace.witos.cpp", "src/Runtime.CoreClr/host_install.witos.cpp",
        "src/Runtime.CoreClr/host_paths.witos.cpp", "src/Runtime.CoreClr/host_environment.witos.cpp",
        "src/Runtime.CoreClr/host_library.witos.cpp", "src/Runtime.CoreClr/host_library_discovery.witos.cpp",
        "tests/User.X64/host_pal_guest.cpp"];

    /// <summary>
    /// The guest's thread lifecycle and Win32 adapters the runtimes call, compiled with the guest's native support:
    /// compiler TLS and threads, the message catalogue, events and waits, handles, sleeping, thread creation, the
    /// clocks, UTF-8 conversion, the functions only the STL's sources call (native_stl) and those only the .NET host
    /// calls (native_host). The process's environment (<see cref="ENVIRONMENT"/>) is compiled as Unicode.
    /// </summary>
    public static readonly string[] ADAPTERS = [PROCESS_TLS, "src/Runtime.Native/thread.c",
        "src/Runtime.NativeAot/minipal_time.witos.cpp", "src/Runtime.NativeAot/native_diagnostics.witos.cpp",
        "src/Runtime.NativeAot/native_stl.witos.cpp", "src/Runtime.NativeAot/pal_events.witos.cpp",
        "src/Runtime.NativeAot/native_services.witos.cpp", "src/Runtime.NativeAot/native_wait.witos.cpp",
        "src/Runtime.NativeAot/native_thread_create.witos.cpp", "src/Runtime.NativeAot/native_thread_handles.witos.cpp",
        "src/Runtime.NativeAot/native_clock.witos.cpp", "src/Runtime.NativeAot/native_encoding.witos.cpp",
        "src/Runtime.NativeAot/native_host.witos.cpp"];

    /// <summary>
    /// The adapter of the process's environment, which the kernel keeps for every module (P6.4.j3a).
    /// </summary>
    public const string ENVIRONMENT = "src/Runtime.NativeAot/pal_environment.witos.cpp";

    /// <summary>
    /// The Win32 bindings of those adapters.
    /// </summary>
    public static readonly string[] BINDINGS = ["native_diagnostics", "native_stl", "native_services", "native_wait",
        "native_thread_create", "native_thread_handles", "native_clock", "native_encoding", "native_host",
        "native_environment"];

    #endregion

    #region Functions

    /// <summary>
    /// Compiles and links the fixture and writes host_runtime_image.h.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <param name="support">The guest's native objects the runtimes stand on: the entry, the native heap, exception
    /// dispatch and unwinding, GS, memory routines, last error and the compiled <see cref="ADAPTERS"/>.</param>
    /// <param name="runtimes">The objects of <see cref="CompileRuntimesAsync"/>.</param>
    /// <param name="hostSource">The corehost sources of the guest host's build, for hostfxr.h.</param>
    internal static async Task BuildAsync(string root, string output, string msvc, IReadOnlyCollection<string> support,
        IReadOnlyCollection<string> runtimes, string hostSource)
    {
        var stl = await StlSources.PrepareAsync(root);
        var cl = Path.Combine(msvc, "cl.exe");
        var includes = NativeCxxExceptionImage.Includes(msvc);
        // The host compiles against the pinned STL headers, ahead of the toolset's.
        string[] guest = ["/I" + Path.Combine(stl, "stl", "inc"), .. includes, "/I" + Path.Combine(root, "src/Kernel/include"),
            "/I" + Path.Combine(root, "src/Runtime.Native"), "/I" + output];
        var objects = new List<string>(support);
        objects.AddRange(runtimes);
        async Task Compile(string prefix, IEnumerable<string> options, string source)
        {
            var obj = Path.Combine(output, prefix + Path.GetFileName(source) + ".obj");
            await Processes.RequireSuccessAsync(cl, ["/nologo", "/c", "/TP", .. options, "/Fo" + obj, source], root);
            objects.Add(obj);
        }

        // C++ exceptions and the rest of the WitOS C++ runtime (P6.4.f, P6.4.g), against the trace vcruntime prints on
        // Windows. The runtime scenarios also use the guest's GS check and native heap.
        await File.WriteAllTextAsync(Path.Combine(output, "cxx_exception_trace.h"),
            "/* Generated from NativeCxxExceptionImage.WINDOWS_TRACE. */\n" +
            $"#define WIT_CXX_EXCEPTION_TRACE \"{NativeCxxExceptionImage.WINDOWS_TRACE}\"\n");
        foreach (var file in new[] { "tests/User.X64/cxx_exceptions.cpp", "tests/User.X64/cxx_exceptions_guest.cpp",
                     NativeCxxExceptionImage.RUNTIME_SCENARIOS })
        {
            string[] protection = file == NativeCxxExceptionImage.RUNTIME_SCENARIOS ? ["/GS", "/guard:cf"] : ["/GS-"];
            await Compile("cxx_", ["/std:c++17", .. protection, "/GR-", "/EHsc", "/Zl", "/O1", "/W4", "/WX", .. guest],
                Path.Combine(root, file));
        }

        // The UCRT subset (P6.4.h) on the native heap and the process console, against the trace UCRT prints on
        // Windows; the trace's \u escapes stay escapes in the C string.
        await File.WriteAllTextAsync(Path.Combine(output, "crt_trace.h"),
            "/* Generated from NativeCrtImage.WINDOWS_TRACE. */\n" +
            $"#define WIT_CRT_TRACE \"{NativeCrtImage.WINDOWS_TRACE.Replace("\\", "\\\\")}\"\n");
        foreach (var file in new[] { NativeCrtImage.SCENARIOS, "tests/User.X64/crt_scenarios_guest.cpp" })
        {
            await Compile("ucrt_", [.. NativeCrtImage.OPTIONS, .. includes, "/I" + Path.Combine(root, "src/Kernel/include"),
                "/I" + Path.Combine(root, "src/Runtime.Native"), "/I" + Path.Combine(root, "src/Runtime.Crt"), "/I" + output],
                Path.Combine(root, file));
        }

        // The STL scenarios against the trace msvcp140 prints on Windows.
        await File.WriteAllTextAsync(Path.Combine(output, "stl_trace.h"),
            "/* Generated from NativeStlImage.WINDOWS_TRACE. */\n" +
            $"#define WIT_STL_TRACE \"{NativeStlImage.WINDOWS_TRACE}\"\n");
        foreach (var file in new[] { NativeStlImage.SCENARIOS, STL_GUEST, MAIN })
        {
            await Compile("stl_", ["/std:c++17", "/GS-", "/EHsc", "/Zl", "/O1", "/W4", "/WX", .. guest,
                "/I" + Path.Combine(root, "src/Runtime.NativeAot")], Path.Combine(root, file));
        }

        // The rest of the corehost PAL (P6.4.j2) over the guest's adapters, against the corrected pinned pal.h.
        var corehost = Path.Combine(output, "corehost");
        await CoreClrHostFilePal.PrepareAsync(root, corehost);
        foreach (var file in HOST_PAL)
        {
            await Compile("pal_", ["/std:c++17", "/utf-8", "/GS-", "/EHsc", "/Zl", "/O1", "/W4", "/WX", "/DWITOS_HOST_FILES", .. guest,
                "/I" + corehost], Path.Combine(root, file));
        }
        foreach (var file in new[] { HEAP, CXX_LIBRARY_GUEST })
        {
            await Compile("heap_", ["/std:c++17", "/GS-", "/EHsc", "/Zl", "/O1", "/W4", "/WX", .. guest,
                "/I" + Path.Combine(root, "src/Runtime.NativeAot")], Path.Combine(root, file));
        }
        // The .NET host's libraries (P6.4.j3c3), against upstream's hostfxr.h and the pinned runtime version, under
        // which the boot package places them.
        var version = RuntimeExperiment.ReadLock(root).RuntimeVersion;
        await File.WriteAllTextAsync(Path.Combine(output, "host_libraries.h"),
            "/* Generated from the runtime pin. */\n" +
            $"#define WIT_HOST_RUNTIME_VERSION L\"{version}\"\n#define WIT_HOST_RUNTIME_VERSION_UTF8 \"{version}\"\n");
        await Compile("heap_", ["/std:c++17", "/GS-", "/EHsc", "/Zl", "/O1", "/W4", "/WX", .. guest, "/I" + hostSource],
            Path.Combine(root, HOST_LIBRARIES_GUEST));

        var image = Path.Combine(output, "HostRuntimeFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/subsystem:native",
            "/entry:wit_native_start", "/nodefaultlib", "/machine:x64", "/fixed:no", "/dynamicbase", "/incremental:no",
            "/Brepro", "/base:0x180000000", "/include:_tls_used", "/out:" + image, .. objects], root);
        var bytes = await File.ReadAllBytesAsync(image);
        using var pe = new PEReader(new MemoryStream(bytes));
        var h = pe.PEHeaders.PEHeader!;
        if (pe.PEHeaders.CorHeader is not null || h.ImportTableDirectory.Size != 0 || h.DelayImportTableDirectory.Size != 0)
            throw new InvalidDataException("Host runtime fixture acquired OS imports or a managed header.");
        var text = new StringBuilder("static const unsigned char wit_host_runtime_image[]={\n");
        for (var i = 0; i < bytes.Length; i += 16)
            text.AppendLine(string.Join(",", bytes.Skip(i).Take(16).Select(v => $"0x{v:X2}")) + ",");
        text.AppendLine("};");
        await File.WriteAllTextAsync(Path.Combine(output, "host_runtime_image.h"), text.ToString(), Encoding.ASCII);
        var pin = await StlSources.ReadPinAsync(root);
        await File.WriteAllTextAsync(Path.Combine(output, "host-runtime-image.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            imageSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            stlRevision = pin.Revision,
            stlSources = NativeStlImage.SOURCES,
            profile = "C++ runtime, UCRT subset and pinned STL sources under the full runtime profile; not the .NET host"
        }));
    }

    /// <summary>
    /// Compiles and links the C++ library of mode 27, cxxlib.dll, as the .NET host's libraries link: strictly, with
    /// the library startup as its entry point and no import.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <param name="objects">The library's support (CoreClrMemoryImage.BuildLibrarySupportAsync) and the runtimes.</param>
    /// <returns>The library's path.</returns>
    internal static async Task<string> BuildLibraryAsync(string root, string output, string msvc, IReadOnlyCollection<string> objects)
    {
        var stl = await StlSources.PrepareAsync(root);
        var source = Path.Combine(output, "cxxlib_" + Path.GetFileName(CXX_LIBRARY) + ".obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/TP", "/std:c++17", "/GS",
            "/EHsc", "/Zl", "/O1", "/W4", "/WX", "/I" + Path.Combine(stl, "stl", "inc"), .. NativeCxxExceptionImage.Includes(msvc),
            "/Fo" + source, Path.Combine(root, CXX_LIBRARY)], root);
        var library = Path.Combine(output, "cxxlib.dll");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/dll", "/entry:wit_library_cxx_entry",
            "/nodefaultlib", "/machine:x64", "/subsystem:native", "/fixed:no", "/dynamicbase", "/incremental:no", "/Brepro",
            "/include:_tls_used", "/out:" + library, .. objects, source], root);
        using var pe = new PEReader(new MemoryStream(await File.ReadAllBytesAsync(library)));
        var h = pe.PEHeaders.PEHeader!;
        if (pe.PEHeaders.CorHeader is not null || h.ImportTableDirectory.Size != 0 || h.DelayImportTableDirectory.Size != 0)
            throw new InvalidDataException("The C++ library acquired imports or a managed header.");
        return library;
    }

    /// <summary>
    /// Compiles what every guest C++ module links besides the guest's native support: the WitOS C++ runtime with its
    /// guest platform and processor level (P6.4.e-g), the UCRT subset over the guest (P6.4.h), the separately compiled
    /// sources of the pinned STL with the STL's own options (P6.4.i), the Win32 bindings of the adapters and the x64
    /// stack probe for frames larger than a page.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>The objects, in link order.</returns>
    internal static async Task<List<string>> CompileRuntimesAsync(string root, string output, string msvc)
    {
        var stl = await StlSources.PrepareAsync(root);
        var cl = Path.Combine(msvc, "cl.exe");
        var ml = Path.Combine(msvc, "ml64.exe");
        var includes = NativeCxxExceptionImage.Includes(msvc);
        string[] guest = ["/I" + Path.Combine(stl, "stl", "inc"), .. includes, "/I" + Path.Combine(root, "src/Kernel/include"),
            "/I" + Path.Combine(root, "src/Runtime.Native")];
        var objects = new List<string>();
        async Task Compile(string prefix, IEnumerable<string> options, string source)
        {
            var obj = Path.Combine(output, prefix + Path.GetFileName(source) + ".obj");
            await Processes.RequireSuccessAsync(cl, ["/nologo", "/c", "/TP", .. options, "/Fo" + obj, source], root);
            objects.Add(obj);
        }

        foreach (var file in NativeCxxExceptionImage.RUNTIME.Concat(NativeCxxExceptionImage.GUEST).Append(NativeCxxExceptionImage.ISA))
        {
            await Compile("cxx_", ["/std:c++17", "/GS-", "/GR-", "/EHsc", "/Zl", "/O1", "/W4", "/WX", .. guest],
                Path.Combine(root, file));
        }
        foreach (var file in NativeCrtImage.RUNTIME.Append(NativeCrtImage.GUEST_PLATFORM))
        {
            await Compile("ucrt_", [.. NativeCrtImage.OPTIONS, .. includes, "/I" + Path.Combine(root, "src/Kernel/include"),
                "/I" + Path.Combine(root, "src/Runtime.Native"), "/I" + Path.Combine(root, "src/Runtime.Crt")],
                Path.Combine(root, file));
        }
        foreach (var file in NativeStlImage.SOURCES)
        {
            await Compile("stl_", [.. StlSources.CompileOptions(root, stl), .. includes], Path.Combine(stl, file));
        }
        foreach (var (file, name) in BINDINGS.Select(binding => ($"src/Runtime.Pal.Win32/X64/{binding}.asm", $"host_{binding}.obj"))
                     .Prepend((NativeCxxExceptionImage.GUARD, "cxx_guard_dispatch.obj")).Append(("src/Kernel.Arch.X64/chkstk.asm", "chkstk.obj")))
        {
            var obj = Path.Combine(output, name);
            await Processes.RequireSuccessAsync(ml, ["/nologo", "/c", "/Fo" + obj, Path.Combine(root, file)], root);
            objects.Add(obj);
        }
        return objects;
    }

    #endregion
}
