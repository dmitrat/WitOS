using WitOS.Dev.Host;

namespace WitOS.Dev.Images;

/// <summary>
/// Compiles guest fixture sources with the one PAL profile: import-free, no CRT, Windows ABI headers and the WitOS
/// native and runtime include directories.
/// </summary>
internal static class PalFixtureCompiler
{
    #region Fields

    private static readonly string[] OPTIONS = ["/nologo", "/c", "/W4", "/WX", "/GS-", "/Zl", "/Oi", "/O1", "/GR-"];

    private static readonly string[] TARGET_DEFINES =
        ["/DHOST_64BIT", "/DHOST_WINDOWS", "/DTARGET_WINDOWS", "/DHOST_AMD64", "/DTARGET_AMD64", "/DTARGET_64BIT", "/DNDEBUG"];

    private static readonly string[] HEADER_DEFINES = ["/DWIN32_LEAN_AND_MEAN", "/DNOMINMAX"];

    #endregion

    #region Functions

    /// <summary>
    /// Compiles each source into &lt;prefix&gt;&lt;file&gt;.obj in the output directory.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory; with upstream headers it already holds pal-source.</param>
    /// <param name="msvc">Compiler directory.</param>
    /// <param name="objectPrefix">Prefix of the object files.</param>
    /// <param name="sources">Repository-relative C and C++ sources.</param>
    /// <param name="upstreamHeaders">Whether to add the pinned upstream NativeAOT include directories.</param>
    /// <param name="defines">Further preprocessor symbols.</param>
    /// <param name="functionSections">Sources compiled with /Gy.</param>
    /// <returns>The object files in source order.</returns>
    public static async Task<List<string>> CompileAsync(string root, string output, string msvc, string objectPrefix,
        IEnumerable<string> sources, bool upstreamHeaders, IEnumerable<string>? defines = null,
        IEnumerable<string>? functionSections = null)
    {
        var profile = Profile(root, output, msvc, upstreamHeaders, defines ?? []);
        var sectioned = new HashSet<string>(functionSections ?? [], StringComparer.Ordinal);
        var objects = new List<string>();
        foreach (var source in sources)
        {
            var obj = Path.Combine(output, objectPrefix + Path.GetFileNameWithoutExtension(source) + ".obj");
            var c = source.EndsWith(".c", StringComparison.Ordinal);
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
                [.. profile, c ? "/TC" : "/TP", c ? "/std:c17" : "/std:c++17",
                    .. sectioned.Contains(source) ? new[] { "/Gy" } : [], $"/Fo{obj}", Path.Combine(root, source)], root);
            objects.Add(obj);
        }
        return objects;
    }

    private static List<string> Profile(string root, string output, string msvc, bool upstreamHeaders,
        IEnumerable<string> defines)
    {
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var sdkVersion = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits", "10", "Include", sdkVersion);
        var profile = new List<string>(OPTIONS);
        profile.AddRange(TARGET_DEFINES);
        profile.AddRange(defines.Select(define => "/D" + define));
        profile.AddRange(HEADER_DEFINES);
        profile.AddRange([$"/I{Path.Combine(vc, "include")}", $"/I{Path.Combine(sdk, "ucrt")}",
            $"/I{Path.Combine(sdk, "um")}", $"/I{Path.Combine(sdk, "shared")}"]);
        if (upstreamHeaders)
        {
            var stage = Path.Combine(output, "pal-source");
            var runtime = Path.Combine(stage, "src", "coreclr", "nativeaot", "Runtime");
            profile.AddRange([$"/I{runtime}", $"/I{Path.Combine(runtime, "inc")}", $"/I{Path.Combine(runtime, "windows")}",
                $"/I{Path.Combine(stage, "src", "coreclr", "gc", "env")}", $"/I{Path.Combine(stage, "src", "native")}"]);
        }
        profile.AddRange([$"/I{Path.Combine(root, "src", "Runtime.NativeAot")}", $"/I{Path.Combine(root, "src", "Runtime.Native")}",
            $"/I{Path.Combine(root, "src", "Kernel", "include")}", $"/I{Path.Combine(root, "tests", "User.X64")}"]);
        return profile;
    }

    #endregion
}
