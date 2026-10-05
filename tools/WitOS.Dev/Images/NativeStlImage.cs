using WitOS.Dev.Host;

namespace WitOS.Dev.Images;

/// <summary>
/// Builds the scenarios of tests/User.X64/stl_scenarios.cpp against the toolset's STL and against the separately
/// compiled sources of the pinned microsoft/STL with the WitOS C++ runtime and UCRT subset (P6.4.i).
/// </summary>
internal static class NativeStlImage
{
    #region Constants

    /// <summary>
    /// The trace msvcp140 produces for the scenarios on Windows; the pinned STL sources must produce it without any
    /// Visual C++ or C runtime library on Windows and in the guest: the messages of the throw helpers, generic error
    /// messages and the mapping of Windows errors, std::uncaught_exception during unwinding and the vectorized
    /// algorithms and string searches (checks, mismatches with plain loops, a hash of the results); threads, contended
    /// and recursive mutexes, condition variables with one and many waiters, the legacy C thread functions, the steady
    /// clock and a notification at a detached thread's exit.
    /// </summary>
    public const string WINDOWS_TRACE = "x1:string_too_long x2:vector_too_long x3:invalid_string_position " +
        "x4:invalid_vector_subscript x5:invalid_bitset_char x6:bad_function_call x7:runtime_helper x8:bad_allocation " +
        "x9:bitset_overflow x10:invalid_argument:_invalid_argument " +
        "x11:resource_deadlock_would_occur:_resource_deadlock_would_occur x12:16,generic g:0=success " +
        "g:1=operation_not_permitted g:2=no_such_file_or_directory g:4=interrupted g:13=permission_denied " +
        "g:17=file_exists g:22=invalid_argument g:34=result_out_of_range g:42=illegal_byte_sequence " +
        "g:100=address_in_use g:138=timed_out g:140=operation_would_block g:9999=unknown_error " +
        "g2:no_such_file_or_directory g3:not_enough_memory w:2=2,generic w:5=13,generic w:8=12,generic " +
        "w:87=22,generic w:183=17,generic w:1460=138,generic w:12345=12345,system w2:system u:1,0 " +
        "v1:3195,0,751218269 v2:3197,0,879267265 v4:3197,0,79119382 v8:3200,0,556037538 s1:2000,0,656027640 " +
        "s2:2000,0,336263857 t1:500500,1,0,1,1,1 t2:900 t3:125250,500 t4:3 t5:1,1 t6:1,0,1 " +
        "t7:resource_deadlock_would_occur:_resource_deadlock_would_occur t8:0,0,42,7 t9:1,1,1 t10:1";

    /// <summary>
    /// The separately compiled sources of the pinned STL that the host needs: the throw helpers, system error
    /// messages, std::uncaught_exception and the vectorized algorithms (P6.4.i1); mutexes, condition variables,
    /// threads and their clocks (P6.4.i2).
    /// </summary>
    public static readonly string[] SOURCES = ["stl/src/xthrow.cpp", "stl/src/thread0.cpp", "stl/src/syserror.cpp",
        "stl/src/syserror_import_lib.cpp", "stl/src/uncaught_exception.cpp", "stl/src/vector_algorithms.cpp",
        "stl/src/cond.cpp", "stl/src/mutex.cpp", "stl/src/cthread.cpp", "stl/src/xnotify.cpp", "stl/src/xtime.cpp"];

    /// <summary>
    /// The guest's native memory routines, which the separately compiled sources call.
    /// </summary>
    public const string MEMORY = "src/Runtime.NativeAot/crt_memory.witos.c";

    /// <summary>
    /// The scenarios, shared by every build.
    /// </summary>
    public const string SCENARIOS = "tests/User.X64/stl_scenarios.cpp";

    /// <summary>
    /// The Windows harness of the scenarios.
    /// </summary>
    public const string HOST = "tests/WitOS.Dev.Tests/Native/StlScenariosHost.cpp";

    #endregion

    #region Functions

    /// <summary>
    /// Builds the scenarios with the toolset's STL, vcruntime and UCRT.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>Path of the executable.</returns>
    internal static async Task<string> BuildReferenceAsync(string root, string output, string msvc)
    {
        var objects = Path.Combine(output, "stl-reference");
        Directory.CreateDirectory(objects);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/MD", "/EHsc", "/O2",
            "/W4", "/WX", "/std:c++17", "/DSTL_REFERENCE", .. NativeCxxExceptionImage.Includes(msvc),
            "/Fo" + objects + "/", Path.Combine(root, SCENARIOS), Path.Combine(root, HOST)], root);
        var exe = Path.Combine(output, "stl-reference.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/out:" + exe,
            .. new[] { SCENARIOS, HOST }.Select(source => Path.Combine(objects, Path.GetFileNameWithoutExtension(source) + ".obj")),
            .. NativeCxxExceptionImage.Libraries(msvc), "kernel32.lib"], root);
        return exe;
    }

    /// <summary>
    /// Builds the scenarios against the pinned STL headers and links its separately compiled sources with the WitOS
    /// C++ runtime, UCRT subset and native memory routines, over kernel32 only. memcpy is the guest's memmove: the
    /// guest's own memcpy shares its source with a second errno that the Windows platform of the subset replaces.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>Path of the executable.</returns>
    internal static async Task<string> BuildWindowsAsync(string root, string output, string msvc)
    {
        var stl = await StlSources.PrepareAsync(root);
        var objects = Path.Combine(output, "stl-witos");
        var cl = Path.Combine(msvc, "cl.exe");
        var includes = NativeCxxExceptionImage.Includes(msvc);
        var compiled = new List<string>();
        // Each group compiles into its own directory: the C++ runtime and the UCRT subset both have platform_windows.cpp.
        async Task Compile(string group, IEnumerable<string> options, IEnumerable<string> sources)
        {
            var directory = Path.Combine(objects, group);
            Directory.CreateDirectory(directory);
            var list = sources.ToArray();
            await Processes.RequireSuccessAsync(cl, ["/nologo", "/c", .. options, "/Fo" + directory + "/", .. list], root);
            compiled.AddRange(list.Select(source => Path.Combine(directory, Path.GetFileNameWithoutExtension(source) + ".obj")));
        }
        await Compile("stl", [.. StlSources.CompileOptions(root, stl), .. includes],
            SOURCES.Select(source => Path.Combine(stl, source)));
        // Everything else sees the pinned headers too, ahead of the toolset's.
        await Compile("cxx", ["/Zl", "/GS-", "/EHsc", "/O2", "/W4", "/WX", "/std:c++17",
                "/I" + Path.Combine(stl, "stl", "inc"), .. includes, "/I" + Path.Combine(root, "src/Runtime.Native")],
            NativeCxxExceptionImage.RUNTIME.Concat([NativeCxxExceptionImage.WINDOWS_PLATFORM,
                NativeCxxExceptionImage.ISA, "src/Runtime.Native/library_dynamic_tls.cpp", SCENARIOS, HOST])
                .Select(source => Path.Combine(root, source)));
        await Compile("crt", [.. NativeCrtImage.OPTIONS, .. includes, "/I" + Path.Combine(root, "src/Runtime.Crt")],
            NativeCrtImage.RUNTIME.Append(NativeCrtImage.WINDOWS_PLATFORM).Select(source => Path.Combine(root, source)));
        // The guest's own memory routines; Windows has no import library that exports them.
        await Compile("memory", ["/TC", "/std:c17", "/Zl", "/GS-", "/O2", "/W4", "/WX", .. includes],
            [Path.Combine(root, MEMORY)]);
        var guard = Path.Combine(objects, "guard_dispatch.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + guard,
            Path.Combine(root, NativeCxxExceptionImage.GUARD)], root);
        var exe = Path.Combine(output, "stl-witos.exe");
        // The static TLS directory of library_dynamic_tls.cpp carries the runtimes' per-thread state.
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/nodefaultlib",
            "/entry:StlTestStart", "/subsystem:console", "/include:_tls_used", "/alternatename:memcpy=memmove",
            "/out:" + exe, .. compiled, guard, "/LIBPATH:" + NativeCxxExceptionImage.SdkLibraries(), "kernel32.lib"],
            root);
        return exe;
    }

    #endregion
}
