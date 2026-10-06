using WitOS.Dev.Host;

namespace WitOS.Dev.Images;

/// <summary>
/// Builds the scenarios of tests/User.X64/crt_scenarios.cpp against UCRT and against the WitOS UCRT subset in
/// src/Runtime.Crt, and the in-process comparison of tests/WitOS.Dev.Tests/Native/CrtDifferential.cpp (P6.4.h).
/// </summary>
internal static class NativeCrtImage
{
    #region Constants

    /// <summary>
    /// The trace UCRT produces for the scenarios on Windows. The WitOS subset must produce it without UCRT on Windows
    /// and in the guest: formatting with UCRT's options and legacy wide specifiers and its buffer contracts, narrow
    /// strings in the C and UTF-8 locales, the standard streams, wide strings, C-locale case, Unicode white space and
    /// digits in parsing, error messages, calendar time, locale data, the heap, ceilf, the floating-point environment
    /// and the mathematics.
    /// </summary>
    public const string WINDOWS_TRACE = "o1:36 o2:2 o3:0 o4:28 o5:46 o6:10 o7:0 o8:0 o9:17 o10:1 o11:42 o12:2 o13:0 " +
        "o14:13 o15:0 o16:10 " +
        "f1:93:0:wide|narrow|N|w|n|___42|ff___|010|+7|0000000000001234|%|-0042|005|-1234567890123|abcdef012345 " +
        "f2:88:0:[_____1][2___][0003][0ab_____][44][4464][18446744073709551615][0XBEEF][long][pr][___pad] " +
        "f3:-1:0:12345 f4:-1:0:abc f5:-1:0:123 f6:5:0:12345## f7:3:0:123 f8:8 f9:19:0:[(null)][(null)][\\u00E9] " +
        "f10:10:0:[\\u00E9\\u00FF][\\u00C4][\\u0080] u1:17:0:[\\u00E9\\u20AC\\uD83D\\uDE00][\\u00E9\\u20AC][____\\u00E9] " +
        "u2:-1:42: u3:2:42:[] s1:7 s2:-1 s3:1 s4:0 s5:1 s6:2 s7:1 s8:14 s9:0 s10:1 s11:0 s12:1 s13:113 s14:81 " +
        "s15:201 s16:-123 s17:2147483647 s18:34 s19:31 s20:5 s21:34 s22:4294967295 s23:4294967295 s24:34 s25:17 e1:0 " +
        "e2:0:34:No_such_file_or_directory e3:0:34:Permiss e4:0:34:Unknown_error e5:0:34:timed_out t1:0 " +
        "t2:125,9,4,14,16,21,6,276,0, t3:28:0:Sat_Oct__4_14:16:21_2025_GMT " +
        "t4:73:0:2025-10-04T14:16:21|277|39|39|40|2025|6|02PM|Saturday,_October_04,_2025|4 t5:0:34: t6:22 t7:-1 t8:0 " +
        "t9:364 m1:1 m2:1 m3:1 m4:1 m5:1 m6:12 m7:1 l1:1,0,4,65001,129,616,32768, l2:1 c1:2 c2:-1 c3:2147483648 " +
        "fp1:0,524831,512,6,0,5,1,0,0,524319, " +
        "fp2:3,1024,3,-3,3,3,3,4,4161823309824,1,34,1,33,1,34,4389103109956,4391908128068,4398696307643," +
        "4399589009408,4392389789696, " +
        "n1:65:0:0xff|+42|18446744073709551615|0000000000001234|___ab|wd__|q|%|end n2:C,C n3:.,0,127,. " +
        "n4:1,0,0,1,129,72,0, " +
        "n5::Sun:Sunday:Mon:Monday:Tue:Tuesday:Wed:Wednesday:Thu:Thursday:Fri:Friday:Sat:Saturday " +
        "n6::Jan:January:Feb:February:Mar:March:Apr:April:May:May:Jun:June:Jul:July:Aug:August:Sep:September:Oct:October:Nov:November:Dec:December,85,134 " +
        "n7:66:10/04/25_14:16:21|14:16:21|Saturday|Saturday,_October_04,_2025|Oct " +
        "n8:34:0:10/04/25_14:16:21|14:16:21|October n9:26,26,6,2,8, n10:4,3,7,1, n11:750,6,1,0,-1,";

    /// <summary>
    /// The lines the scenarios print on standard output and standard error, in text mode.
    /// </summary>
    public const string STANDARD_OUTPUT = "[CRT-STDOUT] wide narrow 42\r\n.\r\n?\r\n[CRT-FWRITE]\r\n[CRT-FPUTS]\r\n";

    /// <summary>
    /// The line the scenarios print on standard error.
    /// </summary>
    public const string STANDARD_ERROR = "[CRT-STDERR] err\r\n";

    /// <summary>
    /// Sources of the WitOS UCRT subset.
    /// </summary>
    public static readonly string[] RUNTIME = ["src/Runtime.Crt/format.cpp", "src/Runtime.Crt/stdio.cpp",
        "src/Runtime.Crt/locale.cpp", "src/Runtime.Crt/string.cpp", "src/Runtime.Crt/time.cpp",
        "src/Runtime.Crt/heap.cpp", "src/Runtime.Crt/runtime.cpp", "src/Runtime.Crt/errno.cpp",
        "src/Runtime.Crt/thread.cpp", "src/Runtime.Crt/fenv.cpp", "src/Runtime.Crt/math.cpp"];

    /// <summary>
    /// The subset's platform source on Windows.
    /// </summary>
    public const string WINDOWS_PLATFORM = "src/Runtime.Crt/platform_windows.cpp";

    /// <summary>
    /// The subset's platform source in the guest.
    /// </summary>
    public const string GUEST_PLATFORM = "src/Runtime.Crt/platform_witos.cpp";

    /// <summary>
    /// The scenarios, shared by every build.
    /// </summary>
    public const string SCENARIOS = "tests/User.X64/crt_scenarios.cpp";

    /// <summary>
    /// The Windows harness of the scenarios.
    /// </summary>
    public const string HOST = "tests/WitOS.Dev.Tests/Native/CrtScenariosHost.cpp";

    /// <summary>
    /// The in-process comparison with UCRT.
    /// </summary>
    public const string DIFFERENTIAL = "tests/WitOS.Dev.Tests/Native/CrtDifferential.cpp";

    /// <summary>
    /// The options the subset compiles with: no C runtime, exceptions, RTTI or GS, like C.
    /// </summary>
    public static readonly string[] OPTIONS = ["/Zl", "/GS-", "/GR-", "/EHs-c-", "/O2", "/W4", "/WX", "/std:c++17"];

    #endregion

    #region Functions

    /// <summary>
    /// Builds the scenarios with UCRT.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>Path of the executable.</returns>
    internal static async Task<string> BuildReferenceAsync(string root, string output, string msvc)
    {
        var objects = Path.Combine(output, "crt-reference");
        Directory.CreateDirectory(objects);
        var exe = Path.Combine(output, "crt-reference.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/MD", "/EHsc", "/O2",
            "/W4", "/WX", "/std:c++17", "/DCRT_REFERENCE", .. NativeCxxExceptionImage.Includes(msvc), "/Fo" + objects + "/",
            Path.Combine(root, SCENARIOS), Path.Combine(root, HOST)], root);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/out:" + exe,
            .. new[] { SCENARIOS, HOST }.Select(source => Path.Combine(objects, Path.GetFileNameWithoutExtension(source) + ".obj")),
            .. NativeCxxExceptionImage.Libraries(msvc), "kernel32.lib"], root);
        return exe;
    }

    /// <summary>
    /// Builds the scenarios with the WitOS UCRT subset over kernel32, without any C runtime.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>Path of the executable.</returns>
    internal static async Task<string> BuildWindowsAsync(string root, string output, string msvc)
    {
        var objects = Path.Combine(output, "crt-witos");
        Directory.CreateDirectory(objects);
        // The static TLS directory of library_dynamic_tls.cpp carries the subset's per-thread errno.
        string[] sources = [SCENARIOS, HOST, .. RUNTIME, WINDOWS_PLATFORM, "src/Runtime.Native/library_dynamic_tls.cpp"];
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", .. OPTIONS,
            .. NativeCxxExceptionImage.Includes(msvc), "/I" + Path.Combine(root, "src/Runtime.Crt"),
            "/I" + Path.Combine(root, "src/Runtime.Native"), "/Fo" + objects + "/",
            .. sources.Select(source => Path.Combine(root, source))], root);
        var mathematics = await CrtMathSources.CompileAsync(root, objects, msvc);
        var exe = Path.Combine(output, "crt-witos.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/nodefaultlib",
            "/entry:CrtTestStart", "/subsystem:console", "/include:_tls_used", "/out:" + exe,
            .. sources.Select(source => Path.Combine(objects, Path.GetFileNameWithoutExtension(source) + ".obj")),
            .. mathematics, "/LIBPATH:" + NativeCxxExceptionImage.SdkLibraries(), "kernel32.lib"], root);
        return exe;
    }

    /// <summary>
    /// Builds the in-process comparison: the subset without its exported names, next to UCRT.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>Path of the executable.</returns>
    internal static async Task<string> BuildDifferentialAsync(string root, string output, string msvc)
    {
        var objects = Path.Combine(output, "crt-differential");
        Directory.CreateDirectory(objects);
        string[] sources = [DIFFERENTIAL, .. RUNTIME, WINDOWS_PLATFORM];
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/MD", "/EHsc", "/O2",
            "/W4", "/WX", "/std:c++17", "/DWITCRT_REFERENCE", .. NativeCxxExceptionImage.Includes(msvc),
            "/I" + Path.Combine(root, "src/Runtime.Crt"), "/Fo" + objects + "/",
            .. sources.Select(source => Path.Combine(root, source))], root);
        var mathematics = await CrtMathSources.CompileAsync(root, objects, msvc);
        var exe = Path.Combine(output, "crt-differential.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/out:" + exe,
            .. sources.Select(source => Path.Combine(objects, Path.GetFileNameWithoutExtension(source) + ".obj")),
            .. mathematics, .. NativeCxxExceptionImage.Libraries(msvc), "kernel32.lib"], root);
        return exe;
    }

    #endregion
}
