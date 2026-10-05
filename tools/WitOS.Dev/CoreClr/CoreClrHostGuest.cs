using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.NativeAot;
using WitOS.Dev.NativeAot.Acceptance;

namespace WitOS.Dev.CoreClr;

/// <summary>
/// Builds the upstream .NET host for the guest (P6.4.j): hostfxr and hostpolicy from the verified pinned runtime
/// checkout, compiled unchanged with upstream's options against the pinned STL, with WitOS's own PAL objects in place
/// of the Windows PAL, and linked strictly, without default libraries, over the WitOS C++ runtime, UCRT subset, STL
/// sources and the guest's native support. Each image's unresolved externals must equal the recorded expectation, which
/// every slice of P6.4.j reduces until none remain; a link with none is not yet a host that runs in the guest.
/// </summary>
internal static class CoreClrHostGuest
{
    #region Constants

    /// <summary>
    /// The expected unresolved externals of each image, by image name.
    /// </summary>
    public const string EXPECTED = "experiments/CoreClrHost/guest-link.json";

    /// <summary>
    /// hostmisc as upstream's CMake lists it for Windows, without pal.windows.cpp and longfile.windows.cpp: WitOS's PAL
    /// objects replace the Windows PAL, the only user of the long-path helpers.
    /// </summary>
    public static readonly string[] HOSTMISC = ["hostmisc/trace.cpp", "hostmisc/utils.cpp", "fxr/fx_ver.cpp"];

    /// <summary>
    /// libhostcommon, which both images link.
    /// </summary>
    public static readonly string[] HOSTCOMMON = ["json_parser.cpp", "host_startup_info.cpp", "roll_forward_option.cpp",
        "fx_definition.cpp", "fx_reference.cpp", "version_compatibility_range.cpp", "runtime_config.cpp", "bundle/info.cpp",
        "bundle/reader.cpp", "bundle/header.cpp"];

    /// <summary>
    /// The sources of hostfxr itself.
    /// </summary>
    public static readonly string[] HOSTFXR = ["fxr/standalone/hostpolicy_resolver.cpp", "fxr/command_line.cpp",
        "fxr/corehost_init.cpp", "fxr/hostfxr.cpp", "fxr/fx_muxer.cpp", "fxr/fx_resolver.cpp", "fxr/fx_resolver.messages.cpp",
        "fxr/framework_info.cpp", "fxr/host_context.cpp", "fxr/install_info.cpp", "fxr/sdk_info.cpp", "fxr/sdk_resolver.cpp"];

    /// <summary>
    /// The sources of hostpolicy itself.
    /// </summary>
    public static readonly string[] HOSTPOLICY = ["hostpolicy/standalone/coreclr_resolver.cpp", "hostpolicy/args.cpp",
        "hostpolicy/breadcrumbs.cpp", "hostpolicy/coreclr.cpp", "hostpolicy/deps_entry.cpp", "hostpolicy/deps_format.cpp",
        "hostpolicy/deps_resolver.cpp", "hostpolicy/hostpolicy_context.cpp", "hostpolicy/hostpolicy.cpp",
        "hostpolicy/hostpolicy_init.cpp", "hostpolicy/shared_store.cpp", "hostpolicy/version.cpp", "bundle/dir_utils.cpp",
        "bundle/extractor.cpp", "bundle/file_entry.cpp", "bundle/manifest.cpp", "bundle/runner.cpp"];

    /// <summary>
    /// WitOS's corehost PAL: the actual pal:: signatures over the guest's native backends, and the backends beyond the
    /// guest's native support.
    /// </summary>
    public static readonly string[] PAL = ["src/Runtime.CoreClr/host_files.witos.cpp", "src/Runtime.CoreClr/host_paths.witos.cpp",
        "src/Runtime.CoreClr/host_directory.witos.cpp", "src/Runtime.CoreClr/host_environment.witos.cpp",
        "src/Runtime.CoreClr/host_library.witos.cpp", "src/Runtime.CoreClr/host_library_discovery.witos.cpp",
        "src/Runtime.Native/file_view.c", "src/Runtime.Native/directory.c"];

    #endregion

    #region Functions

    /// <summary>
    /// Builds and links the guest host as one recorded attempt.
    /// </summary>
    /// <param name="root">Repository root.</param>
    internal static Task RunAsync(string root) =>
        RuntimeBootAttempt.RunInDirectoryAsync(Path.Combine(root, "artifacts/coreclr-host-guest"), "coreclr-host-guest",
            attempt => RunAsync(root, attempt));

    #endregion

    #region Tools

    // Upstream's warning policy for native code (eng/native/configurecompiler.cmake), as its Windows host build passes it.
    private static readonly string[] WARNINGS = ["/W4", "/WX", "/wd4065", "/wd4100", "/wd4127", "/wd4131", "/wd4189",
        "/wd4200", "/wd4201", "/wd4206", "/wd4239", "/wd4245", "/wd4291", "/wd4310", "/wd4324", "/wd4366", "/wd4456",
        "/wd4457", "/wd4458", "/wd4459", "/wd4463", "/wd4505", "/wd4702", "/wd4706", "/wd4733", "/wd4815", "/wd4838",
        "/wd4918", "/wd4960", "/wd4961", "/wd5105", "/wd5205", "/we4007", "/we4013", "/we4102", "/we4551", "/we4640",
        "/we4806", "/we4055", "/we4146", "/we4242", "/we4244", "/we4267", "/we4302", "/we4308", "/we4509", "/we4510",
        "/we4532", "/we4533", "/we4610", "/we4611", "/we4700", "/we4701", "/we4703", "/we4789", "/we4995", "/we4996",
        "/w34092", "/w34121", "/w34125", "/w34130", "/w34132", "/w34212", "/w34530", "/w35038", "/w44177"];

    // Upstream's code generation for the host, without the static CRT (-MT), whole-program optimization, PDBs and the
    // parallel build: the guest links no default library.
    private static readonly string[] CODE = ["/GR-", "/O2", "/Ob2", "/Oi", "/Oy-", "/Zp8", "/Gy", "/GS", "/fp:precise",
        "/EHsc", "/Zc:strictStrings", "/Zc:wchar_t", "/Zc:inline", "/Zc:forScope", "/source-charset:utf-8", "/guard:cf",
        "/guard:ehcont", "/Zl"];

    private static readonly string[] DEFINES = ["/DHOST_64BIT", "/DHOST_AMD64", "/DHOST_WINDOWS", "/DNDEBUG",
        "/DTARGET_64BIT", "/DTARGET_AMD64", "/DTARGET_WINDOWS", "/DURTBLDENV_FRIENDLY=Retail", "/D_FILE_OFFSET_BITS=64",
        "/D_TIME_BITS=64", "/DWIN32", "/D_WINDOWS", "/DWITOS_HOST_FILES"];

    private static async Task RunAsync(string root, RuntimeBootAttempt attempt)
    {
        var output = Path.Combine(attempt.RunDirectory, "build");
        Directory.CreateDirectory(output);
        var msvc = await Toolchain.FindMsvcAsync(root);
        var pin = RuntimeExperiment.ReadLock(root);
        var checkout = await RuntimeSourceCheckout.PrepareAsync(root, pin);
        var source = await PrepareSourcesAsync(root, output, checkout, pin);
        var stl = await StlSources.PrepareAsync(root);

        // The guest's native support and runtimes, as the host runtime fixture links them.
        await UserImage.PrepareAbiAsync(root, output, msvc);
        var support = await CoreClrMemoryImage.BuildSupportAsync(root, output, msvc);
        var runtimes = await HostRuntimeImage.CompileRuntimesAsync(root, output, msvc);

        var corehost = Path.Combine(source, "src/native/corehost");
        string[] includes = ["/I" + Path.Combine(stl, "stl", "inc"), .. NativeCxxExceptionImage.Includes(msvc),
            "/I" + Path.Combine(output, "generated"), "/I" + corehost, "/I" + Path.Combine(corehost, "hostmisc"),
            "/I" + Path.Combine(corehost, "fxr"), "/I" + Path.Combine(corehost, "json"),
            "/I" + Path.Combine(source, "src/native/external"), "/I" + Path.Combine(source, "src/native"),
            "/I" + Path.Combine(source, "src/native/libs/System.IO.Compression.Native"),
            "/I" + Path.Combine(source, "src/native/libs/Common")];
        var cl = Path.Combine(msvc, "cl.exe");
        async Task<List<string>> Compile(string prefix, IEnumerable<string> files, string directory, string[] options)
        {
            var objects = new List<string>();
            foreach (var file in files)
            {
                var obj = Path.Combine(output, prefix + file.Replace('/', '_') + ".obj");
                await Processes.RequireSuccessAsync(cl, ["/nologo", "/c", file.EndsWith(".c", StringComparison.Ordinal) ? "/TC" : "/TP",
                    .. options, "/Fo" + obj, Path.Combine(directory, file)], root);
                objects.Add(obj);
            }
            return objects;
        }
        string[] host = [.. CODE, .. WARNINGS, .. DEFINES, .. includes];
        var hostmisc = await Compile("hostmisc_", HOSTMISC, corehost, host);
        var hostcommon = await Compile("hostcommon_", HOSTCOMMON, corehost, [.. host, "/DEXPORT_SHARED_API"]);
        var hostfxr = await Compile("hostfxr_", HOSTFXR, corehost,
            [.. host, "/DEXPORT_SHARED_API", "/Dhostfxr_EXPORTS", "/I" + Path.Combine(corehost, "fxr/standalone")]);
        var hostpolicy = await Compile("hostpolicy_", HOSTPOLICY, corehost,
            [.. host, "/DEXPORT_SHARED_API", "/Dhostpolicy_EXPORTS", "/I" + Path.Combine(corehost, "hostpolicy/standalone")]);
        // WitOS's PAL compiles against the same corrected pal.h and pinned STL; its C backends as the support's are.
        var pal = await Compile("pal_", PAL.Where(file => file.EndsWith(".cpp", StringComparison.Ordinal)), root,
            ["/std:c++17", "/GS-", "/GR-", "/EHsc", "/O2", "/W4", "/WX", "/Zl", .. DEFINES, .. includes,
                "/I" + Path.Combine(root, "src/Runtime.Native"), "/I" + Path.Combine(root, "src/Kernel/include")]);
        foreach (var file in PAL.Where(file => file.EndsWith(".c", StringComparison.Ordinal)))
            pal.Add(await CompileNativeAsync(root, output, msvc, file));
        // The compiler's stack probe for frames larger than a page, from the x64 layer.
        var probe = Path.Combine(output, "chkstk.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + probe,
            Path.Combine(root, "src/Kernel.Arch.X64/chkstk.asm")], root);
        pal.Add(probe);

        // Both images link everything below the host; each library is linked without an entry until its startup exists.
        string[] below = [.. hostmisc, .. hostcommon, .. pal, .. runtimes, .. support.Objects, .. support.Adapters, support.Entry];
        var actual = new SortedDictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["hostfxr"] = await LinkAsync(root, msvc, output, "hostfxr", [.. hostfxr, .. below]),
            ["hostpolicy"] = await LinkAsync(root, msvc, output, "hostpolicy", [.. hostpolicy, .. below])
        };
        var options = new JsonSerializerOptions { WriteIndented = true };
        var report = Path.Combine(attempt.RunDirectory, "guest-link.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(actual, options));
        var expected = JsonSerializer.Deserialize<SortedDictionary<string, string[]>>(await File.ReadAllTextAsync(Path.Combine(root, EXPECTED)))!;
        var differences = new StringBuilder();
        foreach (var image in actual.Keys.Union(expected.Keys))
        {
            var now = actual.GetValueOrDefault(image, []);
            var before = expected.GetValueOrDefault(image, []);
            foreach (var symbol in now.Except(before))
                differences.AppendLine($"{image}: new unresolved {symbol}");
            foreach (var symbol in before.Except(now))
                differences.AppendLine($"{image}: no longer unresolved {symbol}");
        }
        if (differences.Length > 0)
            throw new InvalidDataException($"The guest host's unresolved externals differ from {EXPECTED} (actual: {report}):\n{differences}");
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        attempt.Publish(new
        {
            guestHostExecuted = false,
            runtimeCommit = pin.RuntimeCommit,
            unresolved = actual.ToDictionary(item => item.Key, item => item.Value.Length),
            expectedSha256 = Hash(Path.Combine(root, EXPECTED)),
            sources = new { hostmisc = HOSTMISC, hostcommon = HOSTCOMMON, hostfxr = HOSTFXR, hostpolicy = HOSTPOLICY, pal = PAL },
            scope = "Upstream hostfxr and hostpolicy compiled for the guest and linked strictly; unresolved externals " +
                "inventory, not a host that runs in the guest"
        });
        foreach (var (image, symbols) in actual)
            Console.WriteLine($"[CORECLR-HOST-GUEST] {image}: {symbols.Length} unresolved externals, as expected.");
    }

    // A copy of the pinned corehost tree with upstream patches applied to the copies (sources in hostmisc include
    // pal.h from their own directory first), and the headers upstream's build generates.
    private static async Task<string> PrepareSourcesAsync(string root, string output, string checkout, UpstreamSourceLock pin)
    {
        var source = Path.Combine(output, "source");
        var corehost = Path.Combine(source, "src/native/corehost");
        if (Directory.Exists(source))
            Directory.Delete(source, recursive: true);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(checkout, "src/native/corehost"), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Path.Combine(checkout, "src/native/corehost"), file);
            if (relative.StartsWith("test" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            var copy = Path.Combine(corehost, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        foreach (var directory in new[] { "src/native/external/rapidjson", "src/native/minipal", "src/native/libs/System.IO.Compression.Native", "src/native/libs/Common" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(checkout, directory), "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(source, directory, Path.GetRelativePath(Path.Combine(checkout, directory), file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
        }
        // pal.h: the hash-verified canonical bytes with the WitOS correction, which must equal the checkout's copy.
        var generated = Path.Combine(output, "generated");
        Directory.CreateDirectory(generated);
        var canonical = await CoreClrHostFilePal.PrepareAsync(root, generated);
        var checkoutPal = (await File.ReadAllTextAsync(Path.Combine(corehost, "hostmisc/pal.h"))).Replace("\r\n", "\n");
        if (checkoutPal != (await File.ReadAllTextAsync(canonical)).Replace("\r\n", "\n"))
            throw new InvalidDataException("The runtime checkout's pal.h differs from its hash-verified pin.");
        File.Copy(Path.Combine(generated, "pal.h"), Path.Combine(corehost, "hostmisc/pal.h"), overwrite: true);
        File.Delete(Path.Combine(generated, "pal.h"));
        // The version headers upstream's MSBuild generates, with the local-build file version upstream's Arcade uses.
        var version = pin.RuntimeVersion.Split('.');
        await File.WriteAllTextAsync(Path.Combine(generated, "runtime_version.h"),
            "#define RuntimeAssemblyMajorVersion " + version[0] + "\n#define RuntimeAssemblyMinorVersion " + version[1] + "\n\n" +
            "#define RuntimeFileMajorVersion 42\n#define RuntimeFileMinorVersion 42\n#define RuntimeFileBuildVersion 42\n" +
            "#define RuntimeFileRevisionVersion 42424\n\n" +
            "#define RuntimeProductMajorVersion " + version[0] + "\n#define RuntimeProductMinorVersion " + version[1] + "\n" +
            "#define RuntimeProductPatchVersion " + version[2] + "\n\n#define RuntimeProductVersion " + pin.RuntimeVersion + "\n");
        await File.WriteAllTextAsync(Path.Combine(generated, "_version.h"),
            "#include <Windows.h>\n#define VER_PRODUCTVERSION 42,42,42,42424\n" +
            "#define VER_PRODUCTVERSION_STR \"" + pin.RuntimeVersion + " @Commit: " + pin.RuntimeCommit + "\"\n" +
            "#define VER_FILEVERSION 42,42,42,42424\n" +
            "#define VER_FILEVERSION_STR \"42,42,42,42424 @Commit: " + pin.RuntimeCommit + "\"\n");
        return source;
    }

    // The guest's C backends with the support's options.
    private static async Task<string> CompileNativeAsync(string root, string output, string msvc, string file)
    {
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var obj = Path.Combine(output, "pal_" + Path.GetFileName(file) + ".obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/TC", "/std:c17", "/GS-",
            "/Zl", "/Oi", "/O1", "/W4", "/WX", "/DNDEBUG", "/I" + Path.Combine(vc, "include"),
            "/I" + Path.Combine(sdk, "Include", version, "ucrt"), "/I" + Path.Combine(root, "src/Kernel/include"),
            "/I" + Path.Combine(root, "src/Runtime.Native"), "/Fo" + obj, Path.Combine(root, file)], root);
        return obj;
    }

    // Links one library strictly and returns its unresolved externals, decorated, in ordinal order.
    private static async Task<string[]> LinkAsync(string root, string msvc, string output, string name, string[] objects)
    {
        var image = Path.Combine(output, name + ".dll");
        var result = await Processes.RunAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/dll", "/noentry", "/nodefaultlib",
            "/machine:x64", "/subsystem:native", "/fixed:no", "/dynamicbase", "/incremental:no", "/Brepro", "/opt:ref",
            "/out:" + image, .. objects], root, 600);
        var log = result.Output + result.Error;
        await File.WriteAllTextAsync(Path.Combine(output, name + ".link.log"), log);
        if (result.TimedOut)
            throw new TimeoutException($"Linking {name} timed out.");
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in UNRESOLVED.Matches(log))
            unresolved.Add(match.Groups["decorated"].Success ? match.Groups["decorated"].Value : match.Groups["name"].Value);
        // Any other link failure is not an inventory.
        var other = log.Split('\n').Where(line => line.Contains(" error LNK", StringComparison.Ordinal) &&
            !line.Contains("LNK2001", StringComparison.Ordinal) && !line.Contains("LNK2019", StringComparison.Ordinal) &&
            !line.Contains("LNK1120", StringComparison.Ordinal)).ToArray();
        if (other.Length > 0 || (result.ExitCode != 0 && unresolved.Count == 0))
            throw new InvalidDataException($"Linking {name} failed beyond unresolved externals:\n{string.Join('\n', other)}\n{log}");
        return [.. unresolved];
    }

    // "unresolved external symbol name" or "unresolved external symbol "undecorated" (decorated)".
    private static readonly Regex UNRESOLVED = new(
        @"error LNK20(?:01|19): unresolved external symbol (?:""[^""]*"" \((?<decorated>[^)\s]+)\)|(?<name>\S+))",
        RegexOptions.Compiled);

    #endregion
}
