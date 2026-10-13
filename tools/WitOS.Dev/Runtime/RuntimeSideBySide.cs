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
/// both frameworks, and an application built for .NET 8 runs on 8.0 through them.
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
    /// The application built for .NET 8 that the scenario runs.
    /// </summary>
    public const string APP = "tests/Runtime.Witos/SideBySide/App.cs";

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
        ["src/native/libs/System.Native/pal_networkchange.c"] = "src.native.libs.System.Native.pal_networkchange.c"
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

        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_SIDE_BY_SIDE_SCENARIO, architecture: architecture);
        await BootScenarioRunner.RunAsync(root, image,
            new BootRequest($"{architecture.Name}-{KernelImageBuilder.RUNTIME_SIDE_BY_SIDE_SCENARIO}-{MEMORY_MIB}", MEMORY_MIB, 900,
                ExpectedOutcome.Success)
            {
                Architecture = architecture,
                Suite = BootSuite.Release,
                RequiredLines = [PassedLine(pin, architecture), "[DOTNET] dotnet exited with 0", architecture.RootTaskPassedLine]
            });
        Console.WriteLine($".NET {pin.ProductVersion} beside .NET {RuntimeWitos.ReadLock(root).ProductVersion} on {name}: " +
            $"an application built for .NET 8 passed its {APP_CHECKS} checks on 8.0 in the guest.");
    }

    /// <summary>
    /// The scenario's package: the .NET 10 install of runtime-witos with .NET 8's shared framework beside its own, and under
    /// /app the application the 8.0 tree's C# compiler builds against 8.0's reference pack, with a runtimeconfig that asks
    /// for the 8.0 framework, which tests/User/dotnet_init.c starts through /dotnet/dotnet.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var pin = RuntimeWitos.ReadLock(root, LOCK);
        var tree = Tree(root);
        var app = Path.Combine(output, "app");
        if (Directory.Exists(app))
            Directory.Delete(app, recursive: true);
        Directory.CreateDirectory(app);
        await RuntimeWitos.CompileAsync(tree, pin.TargetFramework, Path.Combine(app, "App.dll"), "exe", [Path.Combine(root, APP)], []);
        await File.WriteAllTextAsync(Path.Combine(app, "App.runtimeconfig.json"),
            $"{{\n  \"runtimeOptions\": {{\n    \"tfm\": \"{pin.TargetFramework}\",\n" +
            $"    \"framework\": {{ \"name\": \"Microsoft.NETCore.App\", \"version\": \"{pin.FrameworkReference}\" }}\n  }}\n}}\n");
        var init = Path.Combine(output, "dotnet_init.elf");
        await Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            .. Sysroot.DriverOptions(root, architecture), "-std=c11", "-O2", "-Wall", "-Wextra", "-Werror", "-DDOTNET_APP=\"/app/App.dll\"",
            Path.Combine(root, RuntimeWitos.DOTNET_PROGRAM), "-o", init
        ], root);
        var framework = await RuntimeWitos.FrameworkFilesAsync(tree, output, architecture, pin.ProductVersion, pin.TargetFramework);
        return
        [
            ("bin/init", init),
            .. await RuntimeWitos.InstallFilesAsync(root, output, architecture),
            .. framework.Select(file => ($"dotnet/shared/Microsoft.NETCore.App/{pin.ProductVersion}/{file.Name}", file.Source)),
            .. Directory.GetFiles(app).Order(StringComparer.Ordinal).Select(file => ($"app/{Path.GetFileName(file)}", file))
        ];
    }

    /// <summary>
    /// The application's line when every check passed on the branch's framework.
    /// </summary>
    /// <param name="pin">The branch's pin.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The line.</returns>
    public static string PassedLine(RuntimePin pin, KernelArchitecture architecture) =>
        $"[SIDE-BY-SIDE] App.dll on .NET {pin.ProductVersion}, {(architecture == KernelArchitecture.X64 ? "X64" : "Arm64")}: " +
        $"{APP_CHECKS} of {APP_CHECKS} checks passed";

    private static string Tree(string root) => Path.Combine(root, "artifacts", "runtime-witos", "release-8.0");

    private static string ProbeDirectory(string root, KernelArchitecture architecture) =>
        Path.Combine(root, "artifacts", "runtime-witos", "tryrun-release-8.0", architecture.Name);

    #endregion
}
