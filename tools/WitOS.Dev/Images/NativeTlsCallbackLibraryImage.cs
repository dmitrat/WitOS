using WitOS.Dev.Host;

namespace WitOS.Dev.Images;

/// <summary>
/// Builds the event sink and the library with PE TLS callbacks that record the loader's call order (P6.4).
/// </summary>
internal static class NativeTlsCallbackLibraryImage
{
    #region Constants

    /// <summary>
    /// The order Windows produces for the scenario of tests/WitOS.Dev.Tests/Native/LibraryTlsCallbacks.c: both TLS
    /// callbacks before the entry point for every reason; a new thread is attached before its body and detached with
    /// its final TLS; a thread already running at the load gets no attach, a template-initialized TLS block and a
    /// detach at its exit; the unloading thread runs the process detach. The guest must produce the same trace.
    /// </summary>
    public const string WINDOWS_ORDER = "M1 M2 A1:731@T B1:731@T E1:731@T M3 T:731 A2:731@N B2:731@N E2:731@N N:731 " +
        "A3:9@N B3:9@N E3:9@N M4 P:731 A3:731@P B3:731@P E3:731@P M5 A0:5@T B0:5@T E0:5@T M6";

    /// <summary>
    /// The same scenario with the library linked without an entry point: Windows runs its TLS callbacks for the
    /// process attach alone, never for a thread or the detach, while every thread still has its TLS block.
    /// </summary>
    public const string WINDOWS_NOENTRY_ORDER = "M1 M2 A1:731@T B1:731@T M3 T:731 N:731 M4 P:731 M5 M6";

    /// <summary>
    /// The order of C++ thread_local objects in tests/WitOS.Dev.Tests/Native/LibraryTlsObjects.c, the same with the
    /// WitOS dynamic TLS support and the MSVC CRT: the loading thread constructs in the load, before DllMain; a new
    /// thread constructs in its attach before its body; the thread that predates the load constructs on first access;
    /// each thread destroys newest first at its detach, and the unloading thread destroys its own objects at the
    /// process detach.
    /// </summary>
    public const string WINDOWS_OBJECTS_ORDER = "M1 M2 C1:1@T C2:2@T M3 T M4 C1:3@N C2:4@N N D2:4@N D1:3@N M5 P C1:5@P " +
        "C2:6@P D2:6@P D1:5@P M6 D2:2@T D1:1@T M7";

    #endregion

    #region Fields

    private static readonly string[] COMMON = ["/nologo", "/dll", "/nodefaultlib", "/machine:x64", "/subsystem:native",
        "/fixed:no", "/dynamicbase", "/incremental:no", "/Brepro", "/base:0x180000000"];

    #endregion

    #region Functions

    /// <summary>
    /// A Windows order without the thread that predates the load, which the guest does not admit yet (P6.4.d).
    /// </summary>
    /// <param name="order">Order of the Windows reference.</param>
    /// <returns>The order the guest must produce.</returns>
    public static string GuestOrder(string order) => string.Join(' ', order.Split(' ')
        .Where(token => token != "P" && !token.StartsWith("P:", StringComparison.Ordinal) &&
            !token.EndsWith("@P", StringComparison.Ordinal)));

    /// <summary>
    /// Builds the sink and the callback library, with and without its entry point.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>Paths of the sink, the callback library and its variant without an entry point.</returns>
    internal static async Task<(string Sink, string Library, string NoEntry)> BuildAsync(string root, string output,
        string msvc)
    {
        Directory.CreateDirectory(output);
        async Task<string> Compile(string source, string name)
        {
            var obj = Path.Combine(output, name + ".obj");
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
                ["/nologo", "/c", "/TC", "/std:c17", "/W4", "/WX", "/GS-", "/Zl", "/O2", "/Fo" + obj, Path.Combine(root, source)],
                root);
            return obj;
        }
        var sink = Path.Combine(output, "tlssink.dll");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            [.. COMMON, "/noentry", "/out:" + sink, await Compile("tests/User.X64/library_tls_sink.c", "tlssink")], root);
        var callbacks = await Compile("tests/User.X64/library_tls_callbacks.c", "tlscallbacks");
        var library = Path.Combine(output, "tlscallbacks.dll");
        var noEntry = Path.Combine(output, "tlsnoentry.dll");
        foreach (var (path, entry) in new[] { (library, "/entry:CallbackEntry"), (noEntry, "/noentry") })
        {
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
                [.. COMMON, entry, "/include:_tls_used", "/merge:.CRT=.rdata", "/out:" + path, callbacks,
                    Path.Combine(output, "tlssink.lib")], root);
        }
        return (sink, library, noEntry);
    }

    /// <summary>
    /// Builds the library with C++ thread_local objects, which links the WitOS dynamic TLS support, against the sink
    /// that <see cref="BuildAsync"/> built in the same directory.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory of <see cref="BuildAsync"/>.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>Path of the object library.</returns>
    internal static async Task<string> BuildObjectsAsync(string root, string output, string msvc)
    {
        var library = Path.Combine(output, "tlsobjects.dll");
        await LinkDynamicTlsAsync(root, output, msvc, library,
            [await CompileDynamicTlsAsync(root, output, msvc, "tests/User.X64/library_tls_objects.cpp", "tlsobjects")],
            Path.Combine(output, "tlssink.lib"));
        return library;
    }

    /// <summary>
    /// Builds a fixture of tests/WitOS.Dev.Tests/Native/LibraryTlsBoundsFixture.cpp, which links the WitOS dynamic TLS
    /// support, against the sink that <see cref="BuildAsync"/> built in the same directory.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory of <see cref="BuildAsync"/>.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <param name="initializers">Whether the fixture fills the initializer table instead of the destructor registry.</param>
    /// <param name="beyond">Whether the fixture goes one past the bound.</param>
    /// <returns>Path of the fixture.</returns>
    internal static async Task<string> BuildBoundsAsync(string root, string output, string msvc, bool initializers,
        bool beyond)
    {
        var name = "tlsbounds-" + (initializers ? "initializers" : "destructors") + (beyond ? "-beyond" : "");
        string[] defines = [.. initializers ? ["/DINITIALIZERS"] : Array.Empty<string>(),
            .. beyond ? ["/DBEYOND"] : Array.Empty<string>()];
        var library = Path.Combine(output, name + ".dll");
        await LinkDynamicTlsAsync(root, output, msvc, library,
            [await CompileDynamicTlsAsync(root, output, msvc, "tests/WitOS.Dev.Tests/Native/LibraryTlsBoundsFixture.cpp",
                name, defines)], Path.Combine(output, "tlssink.lib"));
        return library;
    }

    /// <summary>
    /// Compiles C++ for a DLL with the WitOS dynamic TLS support: no CRT, RTTI or exceptions.
    /// </summary>
    private static async Task<string> CompileDynamicTlsAsync(string root, string output, string msvc, string source,
        string name, params string[] defines)
    {
        var obj = Path.Combine(output, name + ".obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo", "/c", "/TP", "/std:c++17", "/W4", "/WX", "/GS-", "/Zl", "/O2", "/GR-", "/EHs-c-", .. defines,
                "/I" + Path.Combine(root, "src/Runtime.Native"), "/Fo" + obj, Path.Combine(root, source)], root);
        return obj;
    }

    /// <summary>
    /// Links a DLL with the WitOS dynamic TLS support, its entry point and TLS directory.
    /// </summary>
    private static async Task LinkDynamicTlsAsync(string root, string output, string msvc, string library,
        string[] objects, params string[] imports)
    {
        var support = await CompileDynamicTlsAsync(root, output, msvc, "src/Runtime.Native/library_dynamic_tls.cpp",
            "library-dynamic-tls");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            [.. COMMON, "/entry:wit_library_dll_entry", "/include:_tls_used", "/merge:.CRT=.rdata", "/out:" + library,
                support, .. objects, .. imports], root);
    }

    #endregion
}
