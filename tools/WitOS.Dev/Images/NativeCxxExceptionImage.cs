using WitOS.Dev.Host;

namespace WitOS.Dev.Images;

/// <summary>
/// Builds the C++ exception scenarios of tests/User.X64/cxx_exceptions.cpp against vcruntime and against the WitOS C++
/// runtime in src/Runtime.Cxx (P6.4.e).
/// </summary>
internal static class NativeCxxExceptionImage
{
    #region Constants

    /// <summary>
    /// The trace vcruntime produces on Windows. The WitOS runtime must produce it on Windows' dispatcher and in the guest:
    /// catch parameters are built in the search phase, before the frames in between unwind; an exception leaving a catch
    /// destroys the old object while unwinding, before the next catch runs; a rethrow keeps the object;
    /// uncaught_exceptions is 1 in a destructor during unwinding and 0 inside the catch.
    /// </summary>
    public const string WINDOWS_TRACE = "S1:7 S2:5 drop:5 copy:6 S3:6 drop:6 drop:6 S4:1 +:1 +:2 +:3 -:3 -:2 -:1 S5:30 +:61 " +
        "-:61 S6:60 drop:60 S7:71 drop:71 +:81 -:81 drop:80 S8:82 drop:82 S9:22 S9adjust:4 S10:100 drop:100 S11ok:0 " +
        "S11:111 S11ok:2 S11:113 S12:121 drop:121 S12after:120 unwinding:1 S13:130 S13caught:0 copy:140 S14:140 drop:140 " +
        "drop:140 +:151 -:151 S15:150 drop:150 S16:160 drop:160 +:170 +:171 copy:172 -:171 -:170 S17:172 drop:172 " +
        "drop:172 +:182 -:182 drop:181 S18:183 S19:190 S19right:382 S20:603 S21inner:0 S21:210 drop:210 live:0";

    /// <summary>
    /// Sources of the WitOS C++ runtime.
    /// </summary>
    public static readonly string[] RUNTIME = ["src/Runtime.Cxx/frame_handler.cpp", "src/Runtime.Cxx/throw.cpp",
        "src/Runtime.Cxx/type_info.cpp"];

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
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/MD", "/EHsc", "/O2", "/GS-", "/W4",
            "/WX", "/std:c++17", "/DCXX_REFERENCE", .. Includes(msvc), "/Fo" + reference + "/", "/Fe" + exe,
            Path.Combine(root, "tests/User.X64/cxx_exceptions.cpp"),
            Path.Combine(root, "tests/WitOS.Dev.Tests/Native/CxxExceptionsHost.cpp"), "/link", .. Libraries(msvc)], root);
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
        string[] sources = [.. RUNTIME, "src/Runtime.Native/library_dynamic_tls.cpp", "tests/User.X64/cxx_exceptions.cpp",
            "tests/WitOS.Dev.Tests/Native/CxxExceptionsHost.cpp"];
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/Zl", "/EHsc", "/O2", "/GS-",
            "/W4", "/WX", "/std:c++17", .. Includes(msvc), "/I" + Path.Combine(root, "src/Runtime.Native"),
            "/Fo" + objects + "/", .. sources.Select(source => Path.Combine(root, source))], root);
        var exe = Path.Combine(output, "cxx-exceptions-witos.exe");
        // The static TLS directory of library_dynamic_tls.cpp carries the runtime's per-thread state.
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/nodefaultlib",
            "/entry:CxxTestStart", "/subsystem:console", "/include:_tls_used", "/out:" + exe,
            .. sources.Select(source => Path.Combine(objects, Path.GetFileNameWithoutExtension(source) + ".obj")),
            "/LIBPATH:" + SdkLibraries(), "kernel32.lib", "ntdll.lib"], root);
        return exe;
    }

    private static string[] Includes(string msvc)
    {
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        return ["/I" + Path.Combine(vc, "include"), "/I" + Path.Combine(sdk, "Include", version, "ucrt"),
            "/I" + Path.Combine(sdk, "Include", version, "um"), "/I" + Path.Combine(sdk, "Include", version, "shared")];
    }

    private static string[] Libraries(string msvc)
    {
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var ucrt = Path.Combine(Directory.GetParent(SdkLibraries())!.Parent!.FullName, "ucrt", "x64");
        return ["/LIBPATH:" + Path.Combine(vc, "lib/x64"), "/LIBPATH:" + ucrt, "/LIBPATH:" + SdkLibraries()];
    }

    private static string SdkLibraries() => Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.FullName;

    #endregion
}
