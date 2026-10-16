using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// .NET 8 LTS beside .NET 10 (plan step N3, RFC 0015 section 7). The witos target is backported to dotnet/runtime's
/// release/8.0 with a pin and a patch set of its own (build/runtime/release-8.0.lock.json, patches/runtime/release-8.0).
/// The Linux host builds from it what the shared framework of .NET 8 needs: CoreLib, CoreCLR's runtime and JIT, the
/// libraries with their native part, and hostpolicy. 10.0's dotnet and hostfxr serve both versions, so the install holds
/// both frameworks. The host picks one by an application's runtimeconfig and rollForward (N3.3): the application built for
/// .NET 8 runs on 8.0, the one built for .NET 10 on 10.0, and the .NET 8 one rolls forward to 10.0 when its runtimeconfig
/// or dotnet's option asks for the latest major version.
/// </summary>
internal static class RuntimeSideBySide
{
    #region Constants

    /// <summary>
    /// The release branch of .NET 8 LTS.
    /// </summary>
    public const string BRANCH = "release/8.0";

    /// <summary>
    /// The branch's pin: the commit and the bytes of every upstream file its patch set changes.
    /// </summary>
    public const string LOCK = "build/runtime/release-8.0.lock.json";

    /// <summary>
    /// The branch's patch set under patches/.
    /// </summary>
    public const string PATCH_SET = "runtime/release-8.0";

    /// <summary>
    /// The application the scenario builds for .NET 8 and for .NET 10.
    /// </summary>
    public const string APP = "tests/Runtime.Witos/SideBySide";

    /// <summary>
    /// The checks the application runs.
    /// </summary>
    public const int APP_CHECKS = 10;

    private const int MEMORY_MIB = 512;

    #endregion

    #region Fields

    /// <summary>
    /// The branch's patch set: each upstream path and the name of its patch, its path with '/' as '.'. It is the release/10.0
    /// set less what 8.0's shared framework does not build (NativeAOT, ILC, the static apphost, the PAL suite) and plus
    /// what 8.0 shapes otherwise (N3.2).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PATCHES = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // The build and the platform's identity: what 10.0 says in eng/RuntimeIdentifier.props, 8.0 says in
        // Directory.Build.props.
        ["Directory.Build.props"] = "Directory.Build.props",
        ["eng/build.sh"] = "eng.build.sh",
        ["src/libraries/Microsoft.NETCore.Platforms/src/runtime.json"] =
            "src.libraries.Microsoft.NETCore.Platforms.src.runtime.json",
        ["src/libraries/System.Private.CoreLib/src/System.Private.CoreLib.Shared.projitems"] =
            "src.libraries.System.Private.CoreLib.src.System.Private.CoreLib.Shared.projitems",
        ["src/libraries/System.Private.CoreLib/src/System/Environment.WitOS.cs"] =
            "src.libraries.System.Private.CoreLib.src.System.Environment.WitOS.cs",
        ["src/libraries/System.Private.CoreLib/src/System/OperatingSystem.cs"] =
            "src.libraries.System.Private.CoreLib.src.System.OperatingSystem.cs",
        // The native build against the sysroot and the configure's measured answers.
        ["eng/common/cross/toolchain.cmake"] = "eng.common.cross.toolchain.cmake",
        ["eng/native/configurecompiler.cmake"] = "eng.native.configurecompiler.cmake",
        ["eng/native/configureplatform.cmake"] = "eng.native.configureplatform.cmake",
        ["eng/native/tryrun.cmake"] = "eng.native.tryrun.cmake",
        // CoreCLR's native part.
        ["src/coreclr/CMakeLists.txt"] = "src.coreclr.CMakeLists.txt",
        ["src/coreclr/dlls/mscordac/CMakeLists.txt"] = "src.coreclr.dlls.mscordac.CMakeLists.txt",
        ["src/coreclr/inc/crosscomp.h"] = "src.coreclr.inc.crosscomp.h",
        ["src/coreclr/minipal/Unix/doublemapping.cpp"] = "src.coreclr.minipal.Unix.doublemapping.cpp",
        ["src/coreclr/pal/src/configure.cmake"] = "src.coreclr.pal.src.configure.cmake",
        ["src/coreclr/pal/src/init/pal.cpp"] = "src.coreclr.pal.src.init.pal.cpp",
        ["src/coreclr/vm/gcenv.ee.cpp"] = "src.coreclr.vm.gcenv.ee.cpp",
        ["src/native/external/libunwind.cmake"] = "src.native.external.libunwind.cmake",
        // The libraries and their native part.
        ["src/libraries/System.Net.Http/src/System.Net.Http.csproj"] =
            "src.libraries.System.Net.Http.src.System.Net.Http.csproj",
        ["src/libraries/System.Security.Cryptography/src/System.Security.Cryptography.csproj"] =
            "src.libraries.System.Security.Cryptography.src.System.Security.Cryptography.csproj",
        ["src/native/libs/CMakeLists.txt"] = "src.native.libs.CMakeLists.txt",
        ["src/native/libs/System.Globalization.Native/CMakeLists.txt"] =
            "src.native.libs.System.Globalization.Native.CMakeLists.txt",
        ["src/native/libs/System.IO.Compression.Native/CMakeLists.txt"] =
            "src.native.libs.System.IO.Compression.Native.CMakeLists.txt",
        ["src/native/libs/System.IO.Compression.Native/extra_libs.cmake"] =
            "src.native.libs.System.IO.Compression.Native.extra_libs.cmake",
        ["src/native/libs/System.Native/pal_networkchange.c"] = "src.native.libs.System.Native.pal_networkchange.c",
        // The mount table for DriveInfo.GetDrives() (C1.2), as in the 10.0 set.
        ["src/native/libs/System.Native/pal_mount.c"] = "src.native.libs.System.Native.pal_mount.c"
    };

    #endregion

    #region Functions

    /// <summary>
    /// Builds .NET 8's shared framework for witos and boots the install of both versions (the runtime-side-by-side
    /// command, on the .NET 10 install runtime-witos built).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the application built for .NET 8 passed its checks on 8.0 in the guest.</returns>
    public static async Task BuildAsync(string root, KernelArchitecture architecture)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("dotnet/runtime's release/8.0 builds for TargetOS=witos on a Linux host, as runtime-witos does.");
        var pin = RuntimeWitos.ReadLock(root, LOCK);
        var checkout = await RuntimeWitos.PrepareCheckoutAsync(root, pin, LOCK);
        var tree = await RuntimeWitos.PrepareTreeAsync(root, checkout, pin, Tree(root), PATCH_SET, PATCHES);
        var name = $"witos-{architecture.Name}";

        await RuntimeWitos.RunBuildAsync(tree, ["clr.corelib", "-os", "witos", "-arch", architecture.Name, "-c", "Release"], null);
        var coreclr = Path.Combine(tree, "artifacts", "bin", "coreclr", $"witos.{architecture.Name}.Release");
        var corelib = Path.Combine(coreclr, "IL", "System.Private.CoreLib.dll");
        if (!RuntimeWitos.HasUserString(corelib, "WITOS"))
            throw new InvalidDataException($"{corelib} does not name its platform WITOS.");

        // The native part, cross-compiled with the pinned clang and lld against the sysroot, after the configure's try_run
        // questions are collected for the guest to answer.
        var sysroot = await Sysroot.BuildAsync(root, architecture);
        var environment = RuntimeWitos.NativeEnvironment(root, sysroot);
        var probes = await RuntimeWitos.CollectProbesAsync(root, tree, architecture, environment, ProbeDirectory(root, architecture));
        await RuntimeWitos.RunBuildAsync(tree,
            ["clr.runtime+clr.jit+libs.native+host.native", "-os", "witos", "-arch", architecture.Name, "-c", "Release", "-cross"], environment);
        var hostpolicy = Path.Combine(tree, "artifacts", "bin", $"{name}.Release", "corehost", "libhostpolicy.so");
        if (!File.Exists(Path.Combine(coreclr, "libcoreclr.so")) || !File.Exists(Path.Combine(coreclr, "libclrjit.so")) || !File.Exists(hostpolicy))
            throw new InvalidDataException($"8.0's native build for {name} left no CoreCLR, JIT or hostpolicy in {tree}.");

        // The libraries take their Unix flavors through the tree's own identifier graph, which knows witos; 8.0's build
        // chooses each library's flavor by the SDK's bundled graph otherwise, which does not, and so takes the
        // platform-neutral ones.
        var graph = Path.Combine(tree, "src", "libraries", "Microsoft.NETCore.Platforms", "src", "runtime.json");
        await RuntimeWitos.RunBuildAsync(tree,
            ["libs.sfx", "-os", "witos", "-arch", architecture.Name, "-c", "Release", $"/p:RuntimeIdentifierGraphPath={graph}"], environment);
        var references = Path.Combine(tree, "artifacts", "bin", "microsoft.netcore.app.ref", "ref", pin.TargetFramework);
        var framework = await RuntimeWitos.FrameworkFilesAsync(tree, Path.Combine(root, "artifacts", architecture.Name, "release-8.0"),
            architecture, pin.ProductVersion, pin.TargetFramework);
        if (!File.Exists(Path.Combine(references, "System.Runtime.dll")) || framework.All(file => file.Name != "System.Console.dll"))
            throw new InvalidDataException($"8.0's shared framework for {name} left no System.Console or reference pack in {tree}.");
        Console.WriteLine($".NET {pin.ProductVersion} for {name}: CoreCLR, CoreLib, hostpolicy and {framework.Count} files of the shared framework ({tree}).");

        var answers = await RuntimeWitos.ReadAnswersAsync(RuntimeWitos.ObjectDirectory(tree, architecture), probes);
        var measured = await RuntimeWitos.MeasureProbesAsync(root, architecture, probes, ProbeDirectory(root, architecture));
        var report = Path.Combine(root, "artifacts", "runtime-witos", $"tryrun-release-8.0-{architecture.Name}.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(probes.ToDictionary(probe => probe,
            probe => new { tryrun = answers[probe], guest = measured[probe] }), new JsonSerializerOptions { WriteIndented = true }));
        var differing = probes.Where(probe => answers[probe] != measured[probe]).ToList();
        if (differing.Count > 0)
            throw new InvalidDataException($"8.0's tryrun.cmake answers {differing.Count} of {probes.Count} probes otherwise than {name}: " +
                string.Join(", ", differing.Select(probe => $"{probe} = {answers[probe]}, the guest {measured[probe]}")) + $" ({report}).");
        Console.WriteLine($"8.0's tryrun.cmake answers the {probes.Count} probes of its native configure as {name} does ({report}).");

        var ten = RuntimeWitos.ReadLock(root);
        await RuntimeApps.BootAsync(root, architecture, KernelImageBuilder.RUNTIME_SIDE_BY_SIDE_SCENARIO, RequiredLines(pin, ten),
            1800, memoryMiB: MEMORY_MIB);
        Console.WriteLine($".NET {pin.ProductVersion} beside .NET {ten.ProductVersion} on {name}: the host picked each application's framework " +
            "by its runtimeconfig and rollForward, and every run passed its checks in the guest.");
    }

    /// <summary>
    /// The scenario's package, beside the device services (<see cref="RuntimeApps"/>): the .NET 10 install of runtime-witos
    /// with .NET 8's shared framework beside its own, and the application under /app/net8, built by the stock SDK for
    /// net8.0 against 8.0's reference pack, and under /app/net10, built for net10.0 against 10.0's; /app/net8-latest holds
    /// the .NET 8 build with a runtimeconfig whose rollForward asks for the latest major version. tests/User/apps_init.c runs the
    /// commands: dotnet's list of runtimes, then each application with the major version its host must pick, the .NET 8
    /// one also through dotnet's own --roll-forward option.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var pin = RuntimeWitos.ReadLock(root, LOCK);
        var ten = RuntimeWitos.ReadLock(root);
        var tree = Tree(root);
        var eight = await BuildAppAsync(root, output, "net8", pin);
        var latest = Path.Combine(output, "net8-latest");
        Directory.CreateDirectory(latest);
        File.Copy(Path.Combine(eight, "App.dll"), Path.Combine(latest, "App.dll"), overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(latest, "App.runtimeconfig.json"), RuntimeConfig(pin, "LatestMajor"));
        var current = await BuildAppAsync(root, output, "net10", ten);
        string[] commands =
        [
            "/dotnet/dotnet --list-runtimes",
            $"/dotnet/dotnet /app/net8/App.dll {Major(pin)}",
            $"/dotnet/dotnet /app/net10/App.dll {Major(ten)}",
            $"/dotnet/dotnet /app/net8-latest/App.dll {Major(ten)}",
            $"/dotnet/dotnet exec --roll-forward LatestMajor /app/net8/App.dll {Major(ten)}"
        ];
        var framework = await RuntimeWitos.FrameworkFilesAsync(tree, output, architecture, pin.ProductVersion, pin.TargetFramework);
        IEnumerable<(string Name, string Source)> package =
        [
            .. await RuntimeApps.BuildPackageAsync(root, output, architecture, [], commands),
            .. framework.Select(file => ($"dotnet/shared/Microsoft.NETCore.App/{pin.ProductVersion}/{file.Name}", file.Source)),
            .. new[] { ("net8", eight), ("net8-latest", latest), ("net10", current) }.SelectMany(app =>
                Directory.GetFiles(app.Item2).Order(StringComparer.Ordinal).Select(file => ($"app/{app.Item1}/{Path.GetFileName(file)}", file)))
        ];
        return [.. package];
    }

    /// <summary>
    /// The lines the scenario requires: both frameworks in dotnet's list, and each run's line naming the framework its host
    /// picked, whose description a build of the tree may suffix (10.0.12-dev); apps_init's report requires every run to
    /// exit with 0, which the application does when every check passed.
    /// </summary>
    /// <param name="eight">The release/8.0 pin.</param>
    /// <param name="ten">The runtime's pin.</param>
    /// <returns>The lines.</returns>
    public static string[] RequiredLines(RuntimePin eight, RuntimePin ten)
    {
        string Run(RuntimePin built, RuntimePin runs) => $"[SIDE-BY-SIDE] App.dll for .NET {Major(built)}.0 on .NET {runs.ProductVersion}";
        return
        [
            $"Microsoft.NETCore.App {eight.ProductVersion} [/dotnet/shared/Microsoft.NETCore.App]",
            $"Microsoft.NETCore.App {ten.ProductVersion} [/dotnet/shared/Microsoft.NETCore.App]",
            Run(eight, eight), Run(ten, ten), Run(eight, ten)
        ];
    }

    // The application built for a release: its project's target framework of that release, built by the stock SDK against
    // the release's reference pack (which records the target framework in the assembly, as every SDK build does), with
    // the runtimeconfig that names the release's framework; the application's directory. The stock SDK is the one every
    // host has (CI's side-by-side job builds no .NET 10 tree), and the reference packs are upstream's.
    private static async Task<string> BuildAppAsync(string root, string output, string name, RuntimePin pin)
    {
        var directory = Path.Combine(output, name);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        var built = await RuntimeApps.BuildAsync(root, output, APP, "side-by-side", pin.TargetFramework);
        File.Copy(Path.Combine(built, "App.dll"), Path.Combine(directory, "App.dll"));
        await File.WriteAllTextAsync(Path.Combine(directory, "App.runtimeconfig.json"), RuntimeConfig(pin, null));
        return directory;
    }

    // A runtimeconfig.json for the release's framework, with a rollForward when one is given.
    private static string RuntimeConfig(RuntimePin pin, string? rollForward) =>
        $"{{\n  \"runtimeOptions\": {{\n    \"tfm\": \"{pin.TargetFramework}\",\n" +
        (rollForward is null ? "" : $"    \"rollForward\": \"{rollForward}\",\n") +
        $"    \"framework\": {{ \"name\": \"Microsoft.NETCore.App\", \"version\": \"{pin.FrameworkReference}\" }}\n  }}\n}}\n";

    private static string Major(RuntimePin pin) => pin.Version[..pin.Version.IndexOf('.')];

    private static string Tree(string root) => Path.Combine(root, "artifacts", "runtime-witos", "release-8.0");

    private static string ProbeDirectory(string root, KernelArchitecture architecture) =>
        Path.Combine(root, "artifacts", "runtime-witos", "tryrun-release-8.0", architecture.Name);

    #endregion
}
