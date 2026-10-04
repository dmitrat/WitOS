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

    #endregion

    #region Functions

    /// <summary>
    /// Builds the sink and the callback library.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>Paths of the sink and the callback library.</returns>
    internal static async Task<(string Sink, string Library)> BuildAsync(string root, string output, string msvc)
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
        string[] common = ["/nologo", "/dll", "/nodefaultlib", "/machine:x64", "/subsystem:native", "/fixed:no", "/dynamicbase",
            "/incremental:no", "/Brepro", "/base:0x180000000"];
        var sink = Path.Combine(output, "tlssink.dll");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            [.. common, "/noentry", "/out:" + sink, await Compile("tests/User.X64/library_tls_sink.c", "tlssink")], root);
        var library = Path.Combine(output, "tlscallbacks.dll");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            [.. common, "/entry:CallbackEntry", "/include:_tls_used", "/merge:.CRT=.rdata", "/out:" + library,
                await Compile("tests/User.X64/library_tls_callbacks.c", "tlscallbacks"), Path.Combine(output, "tlssink.lib")],
            root);
        return (sink, library);
    }

    #endregion
}
