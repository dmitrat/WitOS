using WitOS.Dev.Host;

namespace WitOS.Dev.Images;

/// <summary>
/// Builds the C++ scenarios of tests/User.X64/cxx_exceptions.cpp and cxx_runtime.cpp against vcruntime and against the
/// WitOS C++ runtime in src/Runtime.Cxx (P6.4.e, P6.4.g).
/// </summary>
internal static class NativeCxxExceptionImage
{
    #region Constants

    /// <summary>
    /// The trace vcruntime produces on Windows. The WitOS runtime must produce it on Windows' dispatcher and in the guest:
    /// catch parameters are built in the search phase, before the frames in between unwind; an exception leaving a catch
    /// destroys the old object while unwinding, before the next catch runs; a rethrow keeps the object;
    /// uncaught_exceptions is 1 in a destructor during unwinding and 0 inside the catch. The runtime scenarios (G) add
    /// allocation, arrays whose construction throws, std::exception messages, thread-safe statics that retry after a
    /// throwing initializer, a GS-protected frame and a guarded indirect call.
    /// </summary>
    public const string WINDOWS_TRACE = "S1:7 S2:5 drop:5 copy:6 S3:6 drop:6 drop:6 S4:1 +:1 +:2 +:3 -:3 -:2 -:1 S5:30 +:61 " +
        "-:61 S6:60 drop:60 S7:71 drop:71 +:81 -:81 drop:80 S8:82 drop:82 S9:22 S9adjust:4 S10:100 drop:100 S11ok:0 " +
        "S11:111 S11ok:2 S11:113 S12:121 drop:121 S12after:120 unwinding:1 S13:130 S13caught:0 copy:140 S14:140 drop:140 " +
        "drop:140 +:151 -:151 S15:150 drop:150 S16:160 drop:160 +:170 +:171 copy:172 -:171 -:170 S17:172 drop:172 " +
        "drop:172 +:182 -:182 drop:181 S18:183 S19:190 S19right:382 S20:603 S21inner:0 S21:210 drop:210 live:0 G1:42 +:1 " +
        "+:2 +:3 G2:2 -:3 -:2 -:1 G3:bad_allocation G4:1 G5:runtime_failure +:4 G6:4 G6again:4 G7first:1 G7:72 +:5 -:5 " +
        "G8:80 G9:90 t:1 t:2 ~t:2 ~t:1 G10:3";

    /// <summary>
    /// Sources of the WitOS C++ runtime.
    /// </summary>
    public static readonly string[] RUNTIME = ["src/Runtime.Cxx/frame_handler.cpp", "src/Runtime.Cxx/throw.cpp",
        "src/Runtime.Cxx/type_info.cpp", "src/Runtime.Cxx/new.cpp", "src/Runtime.Cxx/vector.cpp",
        "src/Runtime.Cxx/statics.cpp", "src/Runtime.Cxx/exception.cpp"];

    /// <summary>
    /// The runtime's assembly: the Control Flow Guard dispatch.
    /// </summary>
    public const string GUARD = "src/Runtime.Cxx/X64/guard_dispatch.asm";

    /// <summary>
    /// The runtime's ISA detection for the STL's vectorized algorithms (P6.4.i).
    /// </summary>
    public const string ISA = "src/Runtime.Cxx/X64/isa.cpp";

    /// <summary>
    /// The runtime's platform source on Windows.
    /// </summary>
    public const string WINDOWS_PLATFORM = "src/Runtime.Cxx/platform_windows.cpp";

    /// <summary>
    /// The runtime's sources in the guest only: its platform functions and the GS handler over the guest's GS check.
    /// </summary>
    public static readonly string[] GUEST = ["src/Runtime.Cxx/platform_witos.cpp", "src/Runtime.Cxx/gs_witos.cpp"];

    /// <summary>
    /// The runtime scenarios, built with /guard:cf and, where the GS check exists, /GS.
    /// </summary>
    public const string RUNTIME_SCENARIOS = "tests/User.X64/cxx_runtime.cpp";

    #endregion

    #region Functions

    /// <summary>
    /// Builds the scenarios with the normal CRT and vcruntime.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>Path of the executable.</returns>
    internal static async Task<string> BuildReferenceAsync(string root, string output, string msvc)
    {
        Directory.CreateDirectory(output);
        var exe = Path.Combine(output, "cxx-exceptions-reference.exe");
        var reference = Path.Combine(output, "reference");
        Directory.CreateDirectory(reference);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/MD", "/EHsc", "/O2", "/GS-",
            "/W4", "/WX", "/std:c++17", "/DCXX_REFERENCE", .. Includes(msvc), "/Fo" + reference + "/",
            Path.Combine(root, "tests/User.X64/cxx_exceptions.cpp"),
            Path.Combine(root, "tests/WitOS.Dev.Tests/Native/CxxExceptionsHost.cpp")], root);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/MD", "/EHsc", "/O2", "/GS",
            "/guard:cf", "/W4", "/WX", "/std:c++17", .. Includes(msvc), "/Fo" + reference + "/",
            Path.Combine(root, RUNTIME_SCENARIOS)], root);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/guard:cf", "/out:" + exe,
            .. new[] { "cxx_exceptions", "CxxExceptionsHost", "cxx_runtime" }.Select(name => Path.Combine(reference, name + ".obj")),
            .. Libraries(msvc)], root);
        return exe;
    }

    /// <summary>
    /// Builds the scenarios with the WitOS C++ runtime and no CRT or vcruntime, over kernel32 and ntdll.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>Path of the executable.</returns>
    internal static async Task<string> BuildWindowsAsync(string root, string output, string msvc)
    {
        var objects = Path.Combine(output, "witos");
        Directory.CreateDirectory(objects);
        string[] sources = [.. RUNTIME, WINDOWS_PLATFORM, "src/Runtime.Native/library_dynamic_tls.cpp",
            "tests/User.X64/cxx_exceptions.cpp",
            "tests/WitOS.Dev.Tests/Native/CxxExceptionsHost.cpp"];
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/Zl", "/EHsc", "/O2", "/GS-",
            "/W4", "/WX", "/std:c++17", .. Includes(msvc), "/I" + Path.Combine(root, "src/Runtime.Native"),
            "/Fo" + objects + "/", .. sources.Select(source => Path.Combine(root, source))], root);
        // No GS on Windows: the guest's GS check carries __GSHandlerCheck_EH4, and the compiler's cookie check needs an
        // assembly ABI the reference build does not exercise either.
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/Zl", "/EHsc", "/O2", "/GS-",
            "/guard:cf", "/W4", "/WX", "/std:c++17", .. Includes(msvc), "/Fo" + objects + "/",
            Path.Combine(root, RUNTIME_SCENARIOS)], root);
        var guard = Path.Combine(objects, "guard_dispatch.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + guard,
            Path.Combine(root, GUARD)], root);
        var exe = Path.Combine(output, "cxx-exceptions-witos.exe");
        // The static TLS directory of library_dynamic_tls.cpp carries the runtime's per-thread state.
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/nodefaultlib",
            "/entry:CxxTestStart", "/subsystem:console", "/include:_tls_used", "/out:" + exe,
            .. sources.Select(source => Path.Combine(objects, Path.GetFileNameWithoutExtension(source) + ".obj")),
            Path.Combine(objects, "cxx_runtime.obj"), guard, "/LIBPATH:" + SdkLibraries(), "kernel32.lib", "ntdll.lib"],
            root);
        return exe;
    }

    /// <summary>
    /// The include directories of the MSVC toolset and the Windows SDK, UCRT's among them.
    /// </summary>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>The /I options.</returns>
    internal static string[] Includes(string msvc)
    {
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        return ["/I" + Path.Combine(vc, "include"), "/I" + Path.Combine(sdk, "Include", version, "ucrt"),
            "/I" + Path.Combine(sdk, "Include", version, "um"), "/I" + Path.Combine(sdk, "Include", version, "shared")];
    }

    /// <summary>
    /// The library directories of the MSVC toolset, UCRT and the Windows SDK.
    /// </summary>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>The /LIBPATH options.</returns>
    internal static string[] Libraries(string msvc)
    {
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var ucrt = Path.Combine(Directory.GetParent(SdkLibraries())!.Parent!.FullName, "ucrt", "x64");
        return ["/LIBPATH:" + Path.Combine(vc, "lib/x64"), "/LIBPATH:" + ucrt, "/LIBPATH:" + SdkLibraries()];
    }

    /// <summary>
    /// The Windows SDK's x64 library directory.
    /// </summary>
    /// <returns>Directory path.</returns>
    internal static string SdkLibraries() => Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.FullName;

    #endregion
}
