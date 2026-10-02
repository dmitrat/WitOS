using WitOS.Dev.Host;

namespace WitOS.Dev.Images;

/// <summary>
/// Builds the native library fixture that carries static TLS for the guest loader tests.
/// </summary>
internal static class NativeTlsLibraryImage
{
    #region Functions

    /// <summary>
    /// Builds the static TLS library fixture.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <returns>Library path.</returns>
    internal static async Task<string> BuildAsync(string root, string output, string msvc)
    {
        Directory.CreateDirectory(output);
        var code = Path.Combine(output, "dll-tls.obj");
        var metadata = Path.Combine(output, "dll-tls-metadata.obj");
        foreach (var input in new[] { ("tests/User.X64/library_tls.c", code), ("src/Runtime.Native/tls_metadata.c", metadata) })
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/TC", "/std:c17", "/W4", "/WX", "/GS-", "/Zl", "/O2", "/I" + Path.Combine(root, "src/Kernel/include"), "/Fo" + input.Item2, Path.Combine(root, input.Item1)], root);
        var dll = Path.Combine(output, "statictls.dll");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/dll", "/entry:StaticTlsEntry", "/include:_tls_used", "/nodefaultlib", "/machine:x64", "/subsystem:native", "/fixed:no", "/dynamicbase", "/incremental:no", "/Brepro", "/base:0x180000000", "/out:" + dll, code, metadata, Path.Combine(output, "WitLibraryFixture.lib")], root);
        return dll;
    }

    #endregion
}
