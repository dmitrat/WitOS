using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using WitOS.Dev.Host;

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
    /// The bindings of the guest's Windows message catalogue, whose LocalFree the STL's system_category uses.
    /// </summary>
    public const string DIAGNOSTICS = "src/Runtime.Pal.Win32/X64/native_diagnostics.asm";

    /// <summary>
    /// The bindings of the Win32 functions only the STL's sources call: FormatMessageA and GetLocaleInfoEx.
    /// </summary>
    public const string STL_BINDINGS = "src/Runtime.Pal.Win32/X64/native_stl.asm";

    #endregion

    #region Functions

    /// <summary>
    /// Compiles and links the fixture and writes host_runtime_image.h.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <param name="support">The guest's native objects the runtimes stand on: the entry, the native heap, exception
    /// dispatch and unwinding, GS, memory routines, last error, the message catalogue and the STL's Win32
    /// functions.</param>
    internal static async Task BuildAsync(string root, string output, string msvc, IReadOnlyCollection<string> support)
    {
        var stl = await StlSources.PrepareAsync(root);
        var cl = Path.Combine(msvc, "cl.exe");
        var ml = Path.Combine(msvc, "ml64.exe");
        var includes = NativeCxxExceptionImage.Includes(msvc);
        // The host compiles against the pinned STL headers, ahead of the toolset's.
        string[] guest = ["/I" + Path.Combine(stl, "stl", "inc"), .. includes, "/I" + Path.Combine(root, "src/Kernel/include"),
            "/I" + Path.Combine(root, "src/Runtime.Native"), "/I" + output];
        var objects = new List<string>(support);
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
        foreach (var file in NativeCxxExceptionImage.RUNTIME.Concat(NativeCxxExceptionImage.GUEST)
                     .Append(NativeCxxExceptionImage.ISA).Append("tests/User.X64/cxx_exceptions.cpp")
                     .Append("tests/User.X64/cxx_exceptions_guest.cpp").Append(NativeCxxExceptionImage.RUNTIME_SCENARIOS))
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
        foreach (var file in NativeCrtImage.RUNTIME.Append(NativeCrtImage.GUEST_PLATFORM).Append(NativeCrtImage.SCENARIOS)
                     .Append("tests/User.X64/crt_scenarios_guest.cpp"))
        {
            await Compile("ucrt_", [.. NativeCrtImage.OPTIONS, .. includes, "/I" + Path.Combine(root, "src/Kernel/include"),
                "/I" + Path.Combine(root, "src/Runtime.Native"), "/I" + Path.Combine(root, "src/Runtime.Crt"), "/I" + output],
                Path.Combine(root, file));
        }

        // The separately compiled STL sources (P6.4.i1) with the STL's own options, and their scenarios against the
        // trace msvcp140 prints on Windows.
        await File.WriteAllTextAsync(Path.Combine(output, "stl_trace.h"),
            "/* Generated from NativeStlImage.WINDOWS_TRACE. */\n" +
            $"#define WIT_STL_TRACE \"{NativeStlImage.WINDOWS_TRACE}\"\n");
        foreach (var file in NativeStlImage.SOURCES)
        {
            await Compile("stl_", [.. StlSources.CompileOptions(root, stl), .. includes], Path.Combine(stl, file));
        }
        foreach (var file in new[] { NativeStlImage.SCENARIOS, STL_GUEST, MAIN })
        {
            await Compile("stl_", ["/std:c++17", "/GS-", "/EHsc", "/Zl", "/O1", "/W4", "/WX", .. guest],
                Path.Combine(root, file));
        }

        foreach (var (file, name) in new[] { (NativeCxxExceptionImage.GUARD, "cxx_guard_dispatch.obj"),
                     (DIAGNOSTICS, "host_native_diagnostics.obj"), (STL_BINDINGS, "host_native_stl.obj") })
        {
            var obj = Path.Combine(output, name);
            await Processes.RequireSuccessAsync(ml, ["/nologo", "/c", "/Fo" + obj, Path.Combine(root, file)], root);
            objects.Add(obj);
        }
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

    #endregion
}
