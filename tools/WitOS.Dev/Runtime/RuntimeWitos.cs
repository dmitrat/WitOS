using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Upstream;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The Unix form of upstream .NET for TargetOS=witos (plan phase R, RFC 0015) on a Linux host: the pinned dotnet/runtime
/// checkout, a work tree with the witos patch set applied, the runtime's own build scripts and the measure of the patch
/// set against the FreeBSD and Haiku ports. Step R1.1 builds System.Private.CoreLib for witos-x64 and witos-arm64 and
/// checks the platform's name in it; step R1.2b builds NativeAOT's native part and its CoreLib against the system layer's
/// sysroot and measures in the guest the answers the native configure takes from eng/native/tryrun.cmake; step R1.3
/// builds the host's JITs and ILC with the pinned clang and compiles a program for witos into an ELF object; step R2.1
/// compiles it as a publish does, links it with the NativeAOT runtime and System.Native, and runs it in the guest.
/// </summary>
internal static class RuntimeWitos
{
    #region Constants

    /// <summary>
    /// The pin: the commit, the bytes of every upstream file the patch set changes and the ports' budget.
    /// </summary>
    public const string LOCK = "build/runtime/runtime.lock.json";

    /// <summary>
    /// The initial cache of the try_run measurement, which forgets tryrun.cmake's answers.
    /// </summary>
    public const string TRYRUN_MEASURE = "build/runtime/tryrun-measure.cmake";

    /// <summary>
    /// The first program ILC compiles for witos (R1.3).
    /// </summary>
    public const string PLATFORM_PROGRAM = "tests/Runtime.Witos/Platform.cs";

    /// <summary>
    /// The executable runtime-witos links from the first program (R2.1).
    /// </summary>
    public const string PLATFORM_EXECUTABLE = "platform";

    /// <summary>
    /// The M3 acceptance of NativeAOT's Unix form (R2.2): the directory of its sources, compiled together.
    /// </summary>
    public const string ACCEPTANCE_PROGRAM = "tests/Runtime.Witos/Acceptance";

    /// <summary>
    /// The executable runtime-witos links from the acceptance (R2.2).
    /// </summary>
    public const string ACCEPTANCE_EXECUTABLE = "acceptance";

    /// <summary>
    /// The CoreCLR load check (R3.1), the runtime-coreclr scenario's /bin/init.
    /// </summary>
    public const string CORECLR_PROGRAM = "tests/User/coreclr_init.c";

    /// <summary>
    /// The CoreCLR host check (R3.2), the runtime-corerun scenario's /bin/init, which starts corerun.
    /// </summary>
    public const string CORERUN_PROGRAM = "tests/User/corerun_init.c";

    /// <summary>
    /// The managed program corerun runs (R3.2).
    /// </summary>
    public const string CORERUN_ASSEMBLY = "tests/Runtime.Witos/CoreRun/CoreRun.cs";

    /// <summary>
    /// The managed check of System.Native for witos (R4): files, time, the environment and the recorded refusals.
    /// </summary>
    public const string SYSTEM_NATIVE_PROGRAM = "tests/Runtime.Witos/SystemNative/SystemNative.cs";

    /// <summary>
    /// The areas System.Native's check prints a line for (R4).
    /// </summary>
    public const int SYSTEM_NATIVE_AREAS = 12;

    /// <summary>
    /// The hosts check (R5), the runtime-dotnet scenario's /bin/init, which starts the dotnet muxer.
    /// </summary>
    public const string DOTNET_PROGRAM = "tests/User/dotnet_init.c";

    /// <summary>
    /// The program dotnet runs through hostfxr and hostpolicy (R5).
    /// </summary>
    public const string HOSTED_APP = "tests/Runtime.Witos/HostedApp/App.cs";

    /// <summary>
    /// The ordinary application of R6: an SDK project the stock SDK publishes without a RID.
    /// </summary>
    public const string PORTABLE_APP = "tests/Runtime.Witos/PortableApp";

    /// <summary>
    /// The checks the portable application runs (R6).
    /// </summary>
    public const int PORTABLE_APP_CHECKS = 12;

    /// <summary>
    /// The runner of CoreCLR's PAL suite (R7.1), the runtime-palsuite scenario's /bin/init.
    /// </summary>
    public const string PALSUITE_PROGRAM = "tests/User/palsuite_runner.c";

    /// <summary>
    /// The tests of upstream's paltestlist.txt the PAL suite leaves out, each with its reason (R7.1).
    /// </summary>
    public const string PALSUITE_SELECTION = "build/runtime/palsuite.json";

    /// <summary>
    /// The runner of slices of CoreCLR's src/tests (R7.2b), the runtime-coreclr-tests scenario's /bin/init.
    /// </summary>
    public const string CORECLR_TESTS_PROGRAM = "tests/User/coreclr_tests_runner.c";

    /// <summary>
    /// The merged test groups of src/tests the guest runs and the tests of them it leaves out, each with its reason (R7.2b).
    /// </summary>
    public const string CORECLR_TESTS_SELECTION = "build/runtime/coreclr-tests.json";

    /// <summary>
    /// The runner of slices of the libraries' tests (R7.3), the runtime-library-tests scenario's /bin/init.
    /// </summary>
    public const string LIBRARY_TESTS_PROGRAM = "tests/User/library_tests_runner.c";

    /// <summary>
    /// The library test projects the guest runs and the test methods it leaves out, each with its reason (R7.3).
    /// </summary>
    public const string LIBRARY_TESTS_SELECTION = "build/runtime/library-tests.json";

    /// <summary>
    /// The runs of the acceptance: four cycles of its eight probes (R2.2).
    /// </summary>
    public const int ACCEPTANCE_RUNS = 32;

    /// <summary>
    /// WitOS's part of a program's project that the SDK's targets build for witos (R2.3b).
    /// </summary>
    public const string PROGRAM_TARGETS = "build/runtime/witos.targets";

    /// <summary>
    /// The switches the SDK passes ILC for a NativeAOT publish (R2.1).
    /// </summary>
    public const string PUBLISH_SWITCHES = "build/runtime/nativeaot-publish.json";

    // The symbol ILC gives the program's Main: the assembly, the type and the method.
    private const string PLATFORM_MAIN = "Platform_Program__Main";

    // The coreclr_tests_runner.c line that starts a group, and a merged runner's line for each test it ran (R7.2b).
    private static readonly Regex TEST_GROUP_LINE = new(@"\[CORECLR-TESTS\] group (\S+)$", RegexOptions.Compiled);
    private static readonly Regex TEST_RESULT_LINE = new(@"(Passed|Failed) test: (.+)$", RegexOptions.Compiled);
    private static readonly Regex TEST_SKIPPED_LINE = new(@"Test executable '.*' not found, skipping\.", RegexOptions.Compiled);
    private static readonly Regex TEST_ISOLATED_LINE = new(@"\[CORECLR-TESTS\] test /tests/(\S+)/[^/\s]+: (ok|FAILED.*)$", RegexOptions.Compiled);
    private static readonly Regex SCRIPT_EXECUTABLE = new(@"^ExePath=(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    // The library_tests_runner.c line that starts a project, xunit's summary line and its line for a failed test (R7.3).
    private static readonly Regex LIBRARY_PROJECT_LINE = new(@"\[LIBRARY-TESTS\] project (\S+)$", RegexOptions.Compiled);
    private static readonly Regex LIBRARY_SUMMARY_LINE = new(@"\sTotal: (\d+), Errors: (\d+), Failed: (\d+), Skipped: (\d+)", RegexOptions.Compiled);
    private static readonly Regex LIBRARY_FAIL_LINE = new(@"\[FAIL\] (.+)$", RegexOptions.Compiled);
    private static readonly Regex SCRIPT_ARGUMENTS = new(@"CLRTestExecutionArguments=\(([^)'""]*)\); fi", RegexOptions.Compiled);
    private static readonly Regex SCRIPT_EXIT_CODE = new(@"^export CLRTestExpectedExitCode=(-?\d+)", RegexOptions.Compiled | RegexOptions.Multiline);
    // A variable a script exports for its test, at the start of a line (CLRTestEnvironmentVariable, R8.1); the script's own
    // exports for its arguments and SuperPMI are indented.
    private static readonly Regex SCRIPT_ENVIRONMENT = new(@"^export ([A-Za-z_][A-Za-z0-9_]*)=(.*)$", RegexOptions.Compiled | RegexOptions.Multiline);

    #endregion

    #region Fields

    /// <summary>
    /// CoreCLR's native images for witos (R3.1): the runtime, the JIT and the host that loads them.
    /// </summary>
    private static readonly string[] CORECLR_IMAGES = ["libcoreclr.so", "libclrjit.so", "corerun"];

    /// <summary>
    /// The witos patch set: each upstream path and the name of its patch in patches/runtime, its path with '/' as '.'.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PATCHES = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // The build and the platform's identity (R1.1, RFC 0015 section 3).
        ["eng/build.sh"] = "eng.build.sh",
        ["eng/RuntimeIdentifier.props"] = "eng.RuntimeIdentifier.props",
        ["src/libraries/System.Private.CoreLib/src/System.Private.CoreLib.Shared.projitems"] =
            "src.libraries.System.Private.CoreLib.src.System.Private.CoreLib.Shared.projitems",
        ["src/libraries/System.Private.CoreLib/src/System/OperatingSystem.cs"] =
            "src.libraries.System.Private.CoreLib.src.System.OperatingSystem.cs",
        ["src/libraries/System.Private.CoreLib/src/System/Environment.WitOS.cs"] =
            "src.libraries.System.Private.CoreLib.src.System.Environment.WitOS.cs",
        ["src/libraries/Microsoft.NETCore.Platforms/src/runtime.json"] =
            "src.libraries.Microsoft.NETCore.Platforms.src.runtime.json",
        ["src/libraries/Microsoft.NETCore.Platforms/src/PortableRuntimeIdentifierGraph.json"] =
            "src.libraries.Microsoft.NETCore.Platforms.src.PortableRuntimeIdentifierGraph.json",
        // The native build against the system layer's sysroot (R1.2b, RFC 0015 section 4).
        ["eng/common/cross/toolchain.cmake"] = "eng.common.cross.toolchain.cmake",
        ["eng/native/configureplatform.cmake"] = "eng.native.configureplatform.cmake",
        ["eng/native/configurecompiler.cmake"] = "eng.native.configurecompiler.cmake",
        ["eng/native/tryrun.cmake"] = "eng.native.tryrun.cmake",
        ["src/coreclr/gc/unix/gcenv.unix.cpp"] = "src.coreclr.gc.unix.gcenv.unix.cpp",
        ["src/native/libs/CMakeLists.txt"] = "src.native.libs.CMakeLists.txt",
        ["src/native/libs/System.Globalization.Native/CMakeLists.txt"] =
            "src.native.libs.System.Globalization.Native.CMakeLists.txt",
        ["src/native/corehost/apphost/static/CMakeLists.txt"] = "src.native.corehost.apphost.static.CMakeLists.txt",
        // ILC's target WitOS (R1.3).
        ["src/coreclr/tools/Common/TypeSystem/Common/TargetDetails.cs"] =
            "src.coreclr.tools.Common.TypeSystem.Common.TargetDetails.cs",
        ["src/coreclr/tools/Common/CommandLineHelpers.cs"] = "src.coreclr.tools.Common.CommandLineHelpers.cs",
        // NativeAOT's link for witos through the SDK's targets (R2.3b).
        ["src/coreclr/nativeaot/BuildIntegration/Microsoft.NETCore.Native.Unix.targets"] =
            "src.coreclr.nativeaot.BuildIntegration.Microsoft.NETCore.Native.Unix.targets",
        // CoreCLR's native part: the runtime, the JIT, the DAC and corerun (R3.1).
        ["src/coreclr/CMakeLists.txt"] = "src.coreclr.CMakeLists.txt",
        ["src/coreclr/dlls/mscordac/CMakeLists.txt"] = "src.coreclr.dlls.mscordac.CMakeLists.txt",
        ["src/coreclr/pal/src/thread/process.cpp"] = "src.coreclr.pal.src.thread.process.cpp",
        ["src/coreclr/vm/gcenv.ee.cpp"] = "src.coreclr.vm.gcenv.ee.cpp",
        ["src/coreclr/pal/src/init/pal.cpp"] = "src.coreclr.pal.src.init.pal.cpp",
        // The PAL suite's side-by-side test (R7.1): its second library's version script.
        ["src/coreclr/pal/tests/palsuite/exception_handling/pal_sxs/test1/CMakeLists.txt"] =
            "src.coreclr.pal.tests.palsuite.exception_handling.pal_sxs.test1.CMakeLists.txt",
        ["src/native/external/libunwind.cmake"] = "src.native.external.libunwind.cmake",
        // System.Native's profile for witos (R4.1): cryptography without OpenSSL.
        ["src/libraries/System.Security.Cryptography/src/System.Security.Cryptography.csproj"] =
            "src.libraries.System.Security.Cryptography.src.System.Security.Cryptography.csproj"
    };

    #endregion

    #region Functions

    /// <summary>
    /// Applies the patch set to the pinned tree, builds System.Private.CoreLib for TargetOS=witos (plan step R1.1), then
    /// NativeAOT's native part and its CoreLib against the sysroot, and requires that the guest answers every try_run
    /// probe of the native configure as tryrun.cmake does (plan step R1.2b).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">x64 or arm64.</param>
    public static async Task BuildAsync(string root, KernelArchitecture architecture)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("dotnet/runtime builds for TargetOS=witos on a Linux host (plan steps T2.1a, R1.1).");
        var pin = ReadLock(root);
        var checkout = await PrepareCheckoutAsync(root, pin);
        var tree = await PrepareTreeAsync(root, checkout, pin);

        await RunBuildAsync(tree, ["clr.corelib", "-os", "witos", "-arch", architecture.Name, "-c", "Release"], null);
        var output = Path.Combine(tree, "artifacts", "bin", "coreclr", $"witos.{architecture.Name}.Release");
        var corelib = Path.Combine(output, "IL", "System.Private.CoreLib.dll");
        if (!HasUserString(corelib, "WITOS"))
            throw new InvalidDataException($"{corelib} does not name its platform WITOS.");
        Console.WriteLine($"System.Private.CoreLib for witos-{architecture.Name}: OperatingSystem names WITOS ({corelib}).");

        // The native build cross-compiles with the pinned clang and lld against the sysroot, which the toolchain file
        // recognizes by its marker (ROOTFS_DIR).
        var sysroot = await Sysroot.BuildAsync(root, architecture);
        var environment = NativeEnvironment(root, sysroot);
        var probes = await CollectProbesAsync(root, tree, architecture, environment);
        await RunBuildAsync(tree,
            ["clr.nativeaotruntime+clr.nativeaotlibs+libs.native", "-os", "witos", "-arch", architecture.Name, "-c", "Release", "-cross"],
            environment);
        var objects = ObjectDirectory(tree, architecture);
        var offsets = Path.Combine(objects, "nativeaot", "Runtime", "Full", "AsmOffsets.cs");
        var aotCoreLib = Path.Combine(output, "aotsdk", "System.Private.CoreLib.dll");
        if (!File.Exists(offsets) || !File.Exists(Path.Combine(output, "aotsdk", "libRuntime.WorkstationGC.a")))
            throw new InvalidDataException($"NativeAOT's native build for witos-{architecture.Name} left no AsmOffsets.cs or runtime library.");
        if (!HasUserString(aotCoreLib, "WITOS"))
            throw new InvalidDataException($"{aotCoreLib} does not name its platform WITOS.");
        Console.WriteLine($"NativeAOT for witos-{architecture.Name}: the native runtime, AsmOffsets.cs and CoreLib ({Path.Combine(output, "aotsdk")}).");

        // CoreCLR's native part (R3.1): the runtime, the JIT and corerun, cross-compiled against the sysroot as dynamic
        // images for musl's dynamic linker; the guest loads the runtime and the JIT and resolves their entry points. The PAL
        // suite (R7.1) is upstream's paltests, one dynamic program of every test.
        await RunBuildAsync(tree, ["clr.runtime+clr.jit+clr.paltests", "-os", "witos", "-arch", architecture.Name, "-c", "Release", "-cross"],
            environment);
        foreach (var coreclrFile in CORECLR_IMAGES)
        {
            if (!File.Exists(Path.Combine(output, coreclrFile)))
                throw new InvalidDataException($"CoreCLR's native build for witos-{architecture.Name} left no {coreclrFile} in {output}.");
        }
        var coreclrImage = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_CORECLR_SCENARIO, architecture: architecture);
        await BootScenarioRunner.RunAsync(root, coreclrImage, new BootRequest($"{architecture.Name}-runtime-coreclr-256", 256, 300, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = [KernelArchitecture.CoreClrLoadedLine, architecture.RootTaskPassedLine]
        });
        Console.WriteLine($"CoreCLR for witos-{architecture.Name}: {string.Join(", ", CORECLR_IMAGES)}; the runtime and the JIT load in the guest ({output}).");

        // CoreCLR's PAL suite (R7.1): upstream's paltestlist.txt less the tests palsuite.json leaves out, a process a test.
        var palsuite = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_PALSUITE_SCENARIO, architecture: architecture);
        await BootScenarioRunner.RunAsync(root, palsuite, new BootRequest($"{architecture.Name}-runtime-palsuite-256", 256, 3600, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = [architecture.PalSuitePassedLine, architecture.RootTaskPassedLine]
        });
        Console.WriteLine($"CoreCLR for witos-{architecture.Name}: the PAL suite passed in the guest.");

        // The class libraries above CoreLib (R2.3a): upstream's shared framework for witos, which takes the libraries' Unix
        // flavors, and the reference pack programs compile against.
        await RunBuildAsync(tree, ["libs.sfx", "-os", "witos", "-arch", architecture.Name, "-c", "Release"], environment);
        var framework = FrameworkDirectory(root, tree, architecture);
        var references = Path.Combine(tree, "artifacts", "bin", "microsoft.netcore.app.ref", "ref", pin.TargetFramework);
        if (!File.Exists(Path.Combine(framework, "System.Console.dll")) || !File.Exists(Path.Combine(references, "System.Runtime.dll")))
            throw new InvalidDataException($"The shared framework for witos-{architecture.Name} left no System.Console or reference pack ({framework}).");
        Console.WriteLine($"The shared framework for witos-{architecture.Name}: {Directory.GetFiles(framework, "*.dll").Length} libraries ({framework}).");

        // CoreCLR's host on a managed program (R3.2): upstream's corerun with CoreCLR's CoreLib and the framework's
        // libraries runs tests/Runtime.Witos/CoreRun/CoreRun.cs, every method of which the JIT compiles into the double
        // mapping of a shared memory file.
        var corerunImage = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_CORERUN_SCENARIO, architecture: architecture);
        await BootScenarioRunner.RunAsync(root, corerunImage, new BootRequest($"{architecture.Name}-runtime-corerun-256", 256, 600, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = [architecture.CoreRunLine, architecture.RootTaskPassedLine]
        });
        Console.WriteLine($"CoreCLR for witos-{architecture.Name}: corerun runs a managed Main through the JIT.");

        // CoreCLR's M3 acceptance (R3.3): the probes NativeAOT's acceptance runs, every method JIT-compiled.
        var coreclrAcceptance = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_CORECLR_ACCEPTANCE_SCENARIO,
            architecture: architecture);
        await BootScenarioRunner.RunAsync(root, coreclrAcceptance,
            new BootRequest($"{architecture.Name}-runtime-coreclr-acceptance-256", 256, 1800, ExpectedOutcome.Success)
            {
                Architecture = architecture,
                Suite = BootSuite.Release,
                RequiredLines = [architecture.CoreClrAcceptancePassedLine, architecture.RootTaskPassedLine]
            });
        Console.WriteLine($"CoreCLR for witos-{architecture.Name}: the M3 acceptance passed its {ACCEPTANCE_RUNS} runs in the guest.");

        // System.Native for witos (R4): files of the package, time, the environment, invariant globalization and the
        // refusals the compatibility contract records, checked from managed code under corerun.
        var systemNative = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_SYSTEM_NATIVE_SCENARIO,
            architecture: architecture);
        await BootScenarioRunner.RunAsync(root, systemNative,
            new BootRequest($"{architecture.Name}-runtime-system-native-256", 256, 900, ExpectedOutcome.Success)
            {
                Architecture = architecture,
                Suite = BootSuite.Release,
                RequiredLines = [architecture.SystemNativePassedLine, architecture.RootTaskPassedLine]
            });
        Console.WriteLine($"CoreCLR for witos-{architecture.Name}: System.Native's {SYSTEM_NATIVE_AREAS} areas passed in the guest.");

        // The hosts (R5): upstream's dotnet muxer, hostfxr and hostpolicy for witos, built with no patch, run dotnet App.dll
        // from the shared-framework layout of a .NET install.
        await RunBuildAsync(tree, ["host.native", "-os", "witos", "-arch", architecture.Name, "-c", "Release", "-cross"], environment);
        var dotnet = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_DOTNET_SCENARIO, architecture: architecture);
        await BootScenarioRunner.RunAsync(root, dotnet, new BootRequest($"{architecture.Name}-runtime-dotnet-512", 512, 900, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = [architecture.DotnetLine, architecture.RootTaskPassedLine]
        });
        Console.WriteLine($"The hosts for witos-{architecture.Name}: dotnet App.dll ran through hostfxr and hostpolicy in the guest.");

        // R6: an ordinary application the stock SDK publishes without a RID runs unchanged through the same install.
        var portable = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_PORTABLE_APP_SCENARIO, architecture: architecture);
        await BootScenarioRunner.RunAsync(root, portable, new BootRequest($"{architecture.Name}-runtime-portable-app-512", 512, 900, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = [KernelArchitecture.PortableAppPassedLine, architecture.RootTaskPassedLine]
        });
        Console.WriteLine($"R6 on witos-{architecture.Name}: a portable application ran unchanged through dotnet in the guest.");

        // R7.2b: slices of src/tests, upstream's merged test runners built for witos, a process a group under corerun in
        // /dev/shm; whether each test passed is read from the log, since a merged runner returns 100 whatever its tests did.
        var selection = ReadCoreClrTestSelection(root);
        await RunTestsBuildAsync(tree, architecture, [.. selection.Groups, .. selection.Standalone], environment);
        var coreclrTests = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_CORECLR_TESTS_SCENARIO, architecture: architecture);
        var testsRequest = new BootRequest($"{architecture.Name}-runtime-coreclr-tests-512", 512, 3600, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = [architecture.CoreClrTestsPassedLine, architecture.RootTaskPassedLine]
        };
        await BootScenarioRunner.RunAsync(root, coreclrTests, testsRequest);
        Console.WriteLine(await ReportCoreClrTestsAsync(root, architecture, Path.Combine(root, "artifacts", "logs", testsRequest.Name + ".serial.log"),
            selection));

        // R7.3: slices of the libraries' tests, upstream's test projects built for witos as an archived test build (with
        // xunit's console runner and RunTests.sh), each run in the guest as RunTests.sh runs it, through dotnet.
        var libraries = ReadLibraryTestSelection(root);
        await RunLibraryTestsBuildAsync(tree, architecture, libraries.Projects, environment);
        var libraryTests = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_LIBRARY_TESTS_SCENARIO, architecture: architecture);
        var librariesRequest = new BootRequest($"{architecture.Name}-runtime-library-tests-512", 512, 3600, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = [architecture.LibraryTestsPassedLine, architecture.RootTaskPassedLine]
        };
        await BootScenarioRunner.RunAsync(root, libraryTests, librariesRequest);
        Console.WriteLine(await ReportLibraryTestsAsync(root, architecture,
            Path.Combine(root, "artifacts", "logs", librariesRequest.Name + ".serial.log"), libraries));

        // NativeAOT's runtime pack for the architecture (R2.3b), as upstream packs it: the SDK's targets take it for a
        // publish with PublishAot.
        DropPackages(tree, $"Microsoft.NETCore.App.Runtime.NativeAOT.witos-{architecture.Name}");
        await RunBuildAsync(tree,
            ["--projects", Path.Combine(tree, "src", "installer", "pkg", "sfx", "Microsoft.NETCore.App", "Microsoft.NETCore.App.Runtime.NativeAOT.sfxproj"),
                "-os", "witos", "-arch", architecture.Name, "-c", "Release", "-cross"],
            environment);

        var answers = await ReadAnswersAsync(objects, probes);
        var measured = await MeasureProbesAsync(root, architecture, probes);
        var report = Path.Combine(root, "artifacts", "runtime-witos", $"tryrun-{architecture.Name}.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(probes.ToDictionary(probe => probe,
            probe => new { tryrun = answers[probe], guest = measured[probe] }), new JsonSerializerOptions { WriteIndented = true }));
        var differing = probes.Where(probe => answers[probe] != measured[probe]).ToList();
        if (differing.Count > 0)
            throw new InvalidDataException($"tryrun.cmake answers {differing.Count} of {probes.Count} probes otherwise than witos-{architecture.Name}: " +
                string.Join(", ", differing.Select(probe => $"{probe} = {answers[probe]}, the guest {measured[probe]}")) + $" ({report}).");
        Console.WriteLine($"tryrun.cmake answers the {probes.Count} probes of the native configure as witos-{architecture.Name} does ({report}).");

        var ilc = await BuildCompilerAsync(root, tree);
        await PackCompilerAsync(root, tree);
        var version = PackageVersion(tree, architecture);
        Console.WriteLine($"NativeAOT's packages for witos-{architecture.Name}: {version} ({PackageFeed(tree)}).");
        var aotsdk = Path.Combine(output, "aotsdk");
        var directory = ProgramDirectory(root, architecture);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        var program = await CompileProgramAsync(root, tree, ilc, aotsdk, architecture, "Platform", [Path.Combine(root, PLATFORM_PROGRAM)],
            [Path.Combine(aotsdk, "System.Private.CoreLib.dll")], [], []);
        await RequireConstantMainAsync(root, tree, program, architecture);
        Console.WriteLine($"ILC for witos-{architecture.Name}: {PLATFORM_PROGRAM} is an ELF object whose Main returns the constant 0 ({program}).");
        var executable = await LinkProgramAsync(root, tree, program, aotsdk, architecture, PLATFORM_EXECUTABLE);
        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_PROGRAM_SCENARIO, architecture: architecture);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest($"{architecture.Name}-runtime-program-256", 256, 300, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = [architecture.RootTaskPassedLine]
        });
        Console.WriteLine($"NativeAOT on witos-{architecture.Name}: {PLATFORM_PROGRAM} ran in the guest as /bin/init and exited with 0 ({executable}).");

        // The M3 acceptance (R2.2), built by the SDK's targets (R2.3b): dotnet publish of a project with PublishAot for
        // witos-<arch>, against the shared framework (R2.3a); its output goes through System.Console, its native pages and
        // the thread's id through the libc as a direct P/Invoke.
        var published = await PublishProgramAsync(root, tree, architecture, "Acceptance", Path.Combine(root, ACCEPTANCE_PROGRAM), version,
            """
                <ItemGroup>
                  <DirectPInvoke Include="libc" />
                </ItemGroup>
            """);
        var acceptanceExecutable = Path.Combine(ProgramDirectory(root, architecture), ACCEPTANCE_EXECUTABLE);
        File.Copy(published, acceptanceExecutable, overwrite: true);
        image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_ACCEPTANCE_SCENARIO, architecture: architecture);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest($"{architecture.Name}-runtime-acceptance-256", 256, 900, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = [architecture.AcceptancePassedLine, architecture.RootTaskPassedLine]
        });
        Console.WriteLine($"NativeAOT on witos-{architecture.Name}: the M3 acceptance passed its {ACCEPTANCE_RUNS} runs in the guest ({acceptanceExecutable}).");

        // D1.1 (ADR 0025): the device manager, a NativeAOT service of the boot package the same targets publish.
        await RuntimeDevices.BuildAsync(root, tree, architecture, version);

        // N1: the .NET compatibility suite, an ordinary portable application, beside the services; N2.1: representative
        // NuGet libraries under NUnit and xUnit.
        await RuntimeCompatibility.RunAsync(root, architecture);
        await RuntimeLibraries.RunAsync(root, architecture);
        // N2.2, the libraries' own test suites, run for hours in the guest: runtime-nuget-suites runs them on this tree,
        // and CI a group a job.
        Console.WriteLine(await MeasureAsync(root, pin));
    }

    /// <summary>
    /// The package of a witos program (R2.1, R2.2): the program runtime-witos linked as /bin/init, musl's libc.so as the
    /// dynamic linker and the shared C++ runtime in /lib.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="name">The executable: PLATFORM_EXECUTABLE or ACCEPTANCE_EXECUTABLE.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static IReadOnlyList<(string Name, string Source)> ProgramPackage(string root, KernelArchitecture architecture, string name)
    {
        var executable = Path.Combine(ProgramDirectory(root, architecture), name);
        if (!File.Exists(executable))
            throw new InvalidOperationException($"No witos program in {executable}. Run: dotnet run --project tools/WitOS.Dev -- runtime-witos --arch {architecture.Name}");
        var lib = Path.Combine(Sysroot.Directory(root, architecture), "usr", "lib");
        return
        [
            ("bin/init", executable), (MuslLibc.InterpreterPath(architecture).TrimStart('/'), Path.Combine(lib, "libc.so")),
            ("lib/libc++.so.1", Path.Combine(lib, "libc++.so.1")), ("lib/libc++abi.so.1", Path.Combine(lib, "libc++abi.so.1")),
            ("lib/libunwind.so.1", Path.Combine(lib, "libunwind.so.1"))
        ];
    }

    /// <summary>
    /// The package of the CoreCLR load check (R3.1): tests/User/coreclr_init.c, which clang's driver builds against the
    /// sysroot, as /bin/init, CoreCLR's runtime and JIT for witos under /coreclr, musl's libc.so as the dynamic linker and
    /// the shared C++ runtime in /lib.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildCoreClrPackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var coreclr = Path.Combine(root, "artifacts", "runtime-witos", "src", "artifacts", "bin", "coreclr", $"witos.{architecture.Name}.Release");
        if (!File.Exists(Path.Combine(coreclr, "libcoreclr.so")))
            throw new InvalidOperationException($"No CoreCLR for witos in {coreclr}. Run: dotnet run --project tools/WitOS.Dev -- runtime-witos --arch {architecture.Name}");
        var sysroot = await Sysroot.BuildAsync(root, architecture);
        var program = Path.Combine(output, "coreclr_init.elf");
        await Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            .. Sysroot.DriverOptions(root, architecture), "-std=c11", "-O2", "-Wall", "-Wextra", "-Werror",
            Path.Combine(root, CORECLR_PROGRAM), "-o", program
        ], root);
        var lib = Path.Combine(sysroot, "usr", "lib");
        return
        [
            ("bin/init", program), (MuslLibc.InterpreterPath(architecture).TrimStart('/'), Path.Combine(lib, "libc.so")),
            ("lib/libc++.so.1", Path.Combine(lib, "libc++.so.1")), ("lib/libc++abi.so.1", Path.Combine(lib, "libc++abi.so.1")),
            ("lib/libunwind.so.1", Path.Combine(lib, "libunwind.so.1")),
            ("coreclr/libcoreclr.so", Path.Combine(coreclr, "libcoreclr.so")), ("coreclr/libclrjit.so", Path.Combine(coreclr, "libclrjit.so"))
        ];
    }

    /// <summary>
    /// The package of the CoreCLR host check (R3.2): corerun on tests/Runtime.Witos/CoreRun/CoreRun.cs.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static Task<IReadOnlyList<(string Name, string Source)>> BuildCoreRunPackageAsync(string root, string output,
        KernelArchitecture architecture) =>
        BuildCoreRunPackageAsync(root, output, architecture, "CoreRun", [Path.Combine(root, CORERUN_ASSEMBLY)]);

    /// <summary>
    /// The package of CoreCLR's M3 acceptance (R3.3): corerun on tests/Runtime.Witos/Acceptance, the program NativeAOT's
    /// acceptance compiles ahead of time, every method of which the JIT compiles here.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static Task<IReadOnlyList<(string Name, string Source)>> BuildCoreClrAcceptancePackageAsync(string root, string output,
        KernelArchitecture architecture) =>
        BuildCoreRunPackageAsync(root, output, architecture, "Acceptance",
            Directory.GetFiles(Path.Combine(root, ACCEPTANCE_PROGRAM), "*.cs").Order(StringComparer.Ordinal).ToArray());

    /// <summary>
    /// The package of System.Native's check (R4): corerun on tests/Runtime.Witos/SystemNative with the text files the
    /// libc's own checks read under /test.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildSystemNativePackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var files = await BuildCoreRunPackageAsync(root, output, architecture, "SystemNative", [Path.Combine(root, SYSTEM_NATIVE_PROGRAM)]);
        var test = Path.Combine(output, "test");
        Directory.CreateDirectory(Path.Combine(test, "dir"));
        (string Name, string Text)[] texts = [("hello.txt", "Hello, package!\nsecond line\n"), ("dir/a.txt", "a\n"), ("dir/b.txt", "bb\n")];
        foreach (var (name, text) in texts)
            await File.WriteAllTextAsync(Path.Combine(test, name), text);
        return [.. files, .. texts.Select(entry => ($"test/{entry.Name}", Path.Combine(test, entry.Name)))];
    }

    /// <summary>
    /// The package of the hosts check (R5): tests/User/dotnet_init.c as /bin/init; the shared-framework layout of a .NET
    /// install under /dotnet (the dotnet muxer, hostfxr under host/fxr/<version>, and under shared/Microsoft.NETCore.App/
    /// <version> hostpolicy, CoreCLR's runtime and JIT, CoreLib, every library of the framework with its native libraries
    /// and the framework's deps.json, which the tool writes as the shared framework's packaging does); /etc/dotnet/
    /// install_location naming it; and under /app the program the SDK's C# compiler builds against the reference pack with
    /// its runtimeconfig.json (invariant globalization, since the system layer has no ICU).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildDotnetPackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var app = Path.Combine(output, "app");
        Directory.CreateDirectory(app);
        await CompileAsync(root, Path.Combine(app, "App.dll"), "exe", [Path.Combine(root, HOSTED_APP)], []);
        await WriteRuntimeConfigAsync(root, Path.Combine(app, "App.runtimeconfig.json"));
        return await BuildInstallPackageAsync(root, output, architecture, app, "App.dll");
    }

    /// <summary>
    /// Compiles C# sources into an assembly as an SDK build compiles a program: the SDK's C# compiler of the witos tree
    /// against the reference pack the tree built (R5).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="assembly">The assembly to write.</param>
    /// <param name="target">The compiler's target: exe or library.</param>
    /// <param name="sources">The sources.</param>
    /// <param name="references">Assemblies beside the reference pack.</param>
    /// <returns>When the assembly is written.</returns>
    internal static async Task CompileAsync(string root, string assembly, string target, IEnumerable<string> sources,
        IEnumerable<string> references)
    {
        var tree = Path.Combine(root, "artifacts", "runtime-witos", "src");
        var pack = Path.Combine(tree, "artifacts", "bin", "microsoft.netcore.app.ref", "ref", ReadLock(root).TargetFramework);
        var sdks = Directory.GetDirectories(Path.Combine(tree, ".dotnet", "sdk"));
        if (sdks.Length != 1)
            throw new InvalidDataException($"The runtime's own .NET SDK is not one SDK: {string.Join(", ", sdks)}.");
        await RunToolAsync(Path.Combine(tree, ".dotnet", "dotnet"),
        [
            Path.Combine(sdks[0], "Roslyn", "bincore", "csc.dll"), "-nologo", "-noconfig", "-nostdlib", "-deterministic", "-optimize+",
            "-nullable:enable", $"-target:{target}", .. Directory.GetFiles(pack, "*.dll").Select(reference => $"-r:{reference}"),
            .. references.Select(reference => $"-r:{reference}"), $"-out:{assembly}", .. sources
        ], tree);
    }

    /// <summary>
    /// Writes a program's runtimeconfig.json for the pin's shared framework with invariant globalization, since the
    /// system layer has no ICU (R5).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="path">The file to write.</param>
    /// <returns>When the file is written.</returns>
    internal static Task WriteRuntimeConfigAsync(string root, string path)
    {
        var pin = ReadLock(root);
        return File.WriteAllTextAsync(path,
            $"{{\n  \"runtimeOptions\": {{\n    \"tfm\": \"{pin.TargetFramework}\",\n" +
            $"    \"framework\": {{ \"name\": \"Microsoft.NETCore.App\", \"version\": \"{pin.FrameworkReference}\" }},\n" +
            "    \"configProperties\": { \"System.Globalization.Invariant\": true }\n  }\n}\n");
    }

    /// <summary>
    /// The package of R6: the install of BuildDotnetPackageAsync with an ordinary application under /app, which the
    /// stock SDK (the one that runs this tool) publishes without a RID from a copy of tests/Runtime.Witos/PortableApp
    /// kept apart from the repository's build properties, as a developer's machine builds it; nothing of it is changed.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildPortableAppPackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var project = Path.Combine(output, "project");
        if (Directory.Exists(project))
            Directory.Delete(project, recursive: true);
        Directory.CreateDirectory(project);
        foreach (var file in Directory.GetFiles(Path.Combine(root, PORTABLE_APP)))
            File.Copy(file, Path.Combine(project, Path.GetFileName(file)));
        await File.WriteAllTextAsync(Path.Combine(project, "Directory.Build.props"), "<Project />\n");
        await File.WriteAllTextAsync(Path.Combine(project, "Directory.Build.targets"), "<Project />\n");
        var app = Path.Combine(output, "app");
        if (Directory.Exists(app))
            Directory.Delete(app, recursive: true);
        await Processes.RequireSuccessAsync("dotnet",
            ["publish", Path.Combine(project, "PortableApp.csproj"), "-c", "Release", "-o", app, "-nologo"], project);
        return await BuildInstallPackageAsync(root, output, architecture, app, "PortableApp.dll");
    }

    // An install of .NET under /dotnet with a program under /app (R5, R6): what both packages share.
    private static async Task<IReadOnlyList<(string Name, string Source)>> BuildInstallPackageAsync(string root, string output,
        KernelArchitecture architecture, string app, string program)
    {
        await Sysroot.BuildAsync(root, architecture);
        var initProgram = Path.Combine(output, "dotnet_init.elf");
        await Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            .. Sysroot.DriverOptions(root, architecture), "-std=c11", "-O2", "-Wall", "-Wextra", "-Werror",
            $"-DDOTNET_APP=\"/app/{program}\"", Path.Combine(root, DOTNET_PROGRAM), "-o", initProgram
        ], root);
        return
        [
            ("bin/init", initProgram),
            .. await InstallFilesAsync(root, output, architecture),
            .. Directory.GetFiles(app).Order(StringComparer.Ordinal).Select(file => ($"app/{Path.GetFileName(file)}", file))
        ];
    }

    // A .NET install as the shared framework's packaging lays it out (R5), with musl's libc.so as the dynamic linker and
    // the shared C++ runtime: /dotnet/dotnet, hostfxr, the shared framework and its deps.json, and the install location.
    internal static async Task<IReadOnlyList<(string Name, string Source)>> InstallFilesAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var tree = Path.Combine(root, "artifacts", "runtime-witos", "src");
        var coreclr = Path.Combine(tree, "artifacts", "bin", "coreclr", $"witos.{architecture.Name}.Release");
        var corehost = Path.Combine(tree, "artifacts", "bin", $"witos-{architecture.Name}.Release", "corehost");
        var framework = FrameworkDirectory(root, tree, architecture);
        var native = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(framework)!)!, "native");
        if (!File.Exists(Path.Combine(corehost, "dotnet")) || !File.Exists(Path.Combine(coreclr, "libcoreclr.so")))
            throw new InvalidOperationException($"No hosts or CoreCLR for witos in {tree}. Run: dotnet run --project tools/WitOS.Dev -- runtime-witos --arch {architecture.Name}");
        var version = ReadLock(root).ProductVersion;
        var installLocation = Path.Combine(output, "install_location");
        await File.WriteAllTextAsync(installLocation, "/dotnet\n");
        // The account database (N2.2): every process runs as uid and gid 0, which the libc reports, and a program asks its
        // user's name through getpwuid_r (Environment.UserName), which fails on a system without /etc/passwd.
        var passwd = Path.Combine(output, "passwd");
        await File.WriteAllTextAsync(passwd, "root:x:0:0:root:/:\n");
        var group = Path.Combine(output, "group");
        await File.WriteAllTextAsync(group, "root:x:0:\n");
        var sysroot = await Sysroot.BuildAsync(root, architecture);

        // The shared framework: what Microsoft.NETCore.App's runtime pack carries for the RID, and the deps.json that names it.
        var shared = new List<(string Name, string Source)>
        {
            ("libhostpolicy.so", Path.Combine(corehost, "libhostpolicy.so")), ("libcoreclr.so", Path.Combine(coreclr, "libcoreclr.so")),
            ("libclrjit.so", Path.Combine(coreclr, "libclrjit.so")),
            ("System.Private.CoreLib.dll", Path.Combine(coreclr, "IL", "System.Private.CoreLib.dll"))
        };
        shared.AddRange(Directory.GetFiles(framework, "*.dll").Order(StringComparer.Ordinal).Select(file => (Path.GetFileName(file), file)));
        // The libraries' native libraries alone: libs.pretest (R7.3) also copies the runtime and the hosts beside them.
        shared.AddRange(Directory.GetFiles(native, "libSystem.*.so").Order(StringComparer.Ordinal).Select(file => (Path.GetFileName(file), file)));
        var deps = Path.Combine(output, "Microsoft.NETCore.App.deps.json");
        await File.WriteAllTextAsync(deps, FrameworkDeps(architecture, version, shared));
        shared.Add(("Microsoft.NETCore.App.deps.json", deps));

        var lib = Path.Combine(sysroot, "usr", "lib");
        return
        [
            (MuslLibc.InterpreterPath(architecture).TrimStart('/'), Path.Combine(lib, "libc.so")),
            ("lib/libc++.so.1", Path.Combine(lib, "libc++.so.1")), ("lib/libc++abi.so.1", Path.Combine(lib, "libc++abi.so.1")),
            ("lib/libunwind.so.1", Path.Combine(lib, "libunwind.so.1")),
            // ICU (R4.3), which System.Globalization.Native loads for culture data.
            .. await Icu.PackageFilesAsync(root, architecture),
            ("etc/dotnet/install_location", installLocation), ("etc/passwd", passwd), ("etc/group", group),
            ("dotnet/dotnet", Path.Combine(corehost, "dotnet")),
            ($"dotnet/host/fxr/{version}/libhostfxr.so", Path.Combine(corehost, "libhostfxr.so")),
            .. shared.Select(file => ($"dotnet/shared/Microsoft.NETCore.App/{version}/{file.Name}", file.Source))
        ];
    }

    /// <summary>
    /// The package of the slices of the libraries' tests (R7.3): tests/User/library_tests_runner.c as /bin/init, the .NET
    /// install of R5, each project's output with xunit's console runner under /tests/&lt;assembly name&gt;, the names in
    /// /tests/projects and the test methods left out, one a line, in /tests/exclusions.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildLibraryTestsPackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var tree = Path.Combine(root, "artifacts", "runtime-witos", "src");
        var selection = ReadLibraryTestSelection(root);
        var files = new List<(string Name, string Source)>();
        var names = new List<string>();
        foreach (var project in selection.Projects)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var directory = LibraryTestDirectory(tree, name);
            names.Add(name);
            files.AddRange(Directory.EnumerateFiles(directory)
                .Where(file => !file.EndsWith(".pdb", StringComparison.Ordinal) && !file.EndsWith(".exe", StringComparison.Ordinal) &&
                    !file.EndsWith(".sh", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(file => ($"tests/{name}/{Path.GetFileName(file)}", file)));
        }
        var projects = Path.Combine(output, "library-tests-projects.txt");
        await File.WriteAllTextAsync(projects, string.Join("\n", names) + "\n");
        var exclusions = Path.Combine(output, "library-tests-exclusions.txt");
        await File.WriteAllTextAsync(exclusions, string.Concat(selection.Excluded.Keys.Select(method => method + "\n")));
        var sysroot = await Sysroot.BuildAsync(root, architecture);
        var init = Path.Combine(output, "library_tests_runner.elf");
        // The program is C: the driver's C++ library option would be an unused argument.
        await Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            .. Sysroot.DriverOptions(root, architecture).Where(option => option != "-stdlib=libc++"), "-std=c17", "-O2", "-Wall",
            "-Wextra", "-Werror", Path.Combine(root, LIBRARY_TESTS_PROGRAM), "-o", init
        ], root);
        return
        [
            ("bin/init", init), ("tests/projects", projects), ("tests/exclusions", exclusions),
            .. await InstallFilesAsync(root, output, architecture),
            .. files
        ];
    }

    /// <summary>
    /// The library test projects the guest runs and the test methods it leaves out with their reasons (R7.3).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The selection.</returns>
    public static LibraryTestSelection ReadLibraryTestSelection(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, LIBRARY_TESTS_SELECTION)));
        var projects = document.RootElement.GetProperty("projects").EnumerateArray().Select(project => project.GetString() ?? "").ToList();
        if (projects.Count == 0 || projects.Any(project => !project.StartsWith("src/libraries/", StringComparison.Ordinal) ||
                !project.EndsWith(".Tests.csproj", StringComparison.Ordinal)))
            throw new InvalidDataException($"{LIBRARY_TESTS_SELECTION} names no project or one that is not a library test project.");
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var method in document.RootElement.GetProperty("excluded").EnumerateObject())
        {
            var reason = method.Value.GetString();
            if (string.IsNullOrWhiteSpace(reason) || method.Name.Contains(' '))
                throw new InvalidDataException($"{LIBRARY_TESTS_SELECTION} leaves out {method.Name} without a reason or with a space in its name.");
            excluded.Add(method.Name, reason);
        }
        return new LibraryTestSelection(projects, excluded);
    }

    // A library test project's output (R7.3): the one directory under artifacts/bin/<name>/Release that holds the
    // RunTests.sh an archived test build writes for a .NET target framework; a project that also builds for .NET
    // Framework (net4*) has a second one, which is not for witos.
    private static string LibraryTestDirectory(string tree, string name)
    {
        var bin = Path.Combine(tree, "artifacts", "bin", name, "Release");
        var scripts = Directory.Exists(bin)
            ? Directory.GetFiles(bin, "RunTests.sh", SearchOption.AllDirectories)
                .Where(script => !Path.GetFileName(Path.GetDirectoryName(script)!).StartsWith("net4", StringComparison.Ordinal)).ToArray()
            : [];
        if (scripts.Length != 1)
            throw new InvalidOperationException($"No single archived test build of {name} in {bin}. Run: dotnet run --project tools/WitOS.Dev -- runtime-witos");
        return Path.GetDirectoryName(scripts[0])!;
    }

    // The shared framework's deps.json as the shared framework's packaging writes it (R5): one runtime-pack library for the
    // RID whose runtime assets are the assemblies, with their assembly and file versions, and whose native assets are the
    // native libraries; hostpolicy builds the trusted platform assemblies and the native search paths from it.
    private static string FrameworkDeps(KernelArchitecture architecture, string version, IReadOnlyList<(string Name, string Source)> files)
    {
        var rid = $"witos-{architecture.Name}";
        var library = $"Microsoft.NETCore.App.Runtime.{rid}/{version}";
        var target = $".NETCoreApp,Version=v{version[..version.IndexOf('.')]}.0";
        var runtime = new System.Text.Json.Nodes.JsonObject();
        var nativeAssets = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, source) in files)
        {
            if (name.EndsWith(".dll", StringComparison.Ordinal))
            {
                using var stream = File.OpenRead(source);
                using var reader = new PEReader(stream);
                var metadata = reader.GetMetadataReader();
                var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(source).FileVersion ?? "0.0.0.0";
                runtime[name] = new System.Text.Json.Nodes.JsonObject
                {
                    ["assemblyVersion"] = metadata.GetAssemblyDefinition().Version.ToString(),
                    ["fileVersion"] = fileVersion
                };
            }
            else
            {
                nativeAssets[name] = new System.Text.Json.Nodes.JsonObject { ["fileVersion"] = "0.0.0.0" };
            }
        }
        var document = new System.Text.Json.Nodes.JsonObject
        {
            ["runtimeTarget"] = new System.Text.Json.Nodes.JsonObject { ["name"] = $"{target}/{rid}", ["signature"] = "" },
            ["compilationOptions"] = new System.Text.Json.Nodes.JsonObject(),
            ["targets"] = new System.Text.Json.Nodes.JsonObject
            {
                [target] = new System.Text.Json.Nodes.JsonObject(),
                [$"{target}/{rid}"] = new System.Text.Json.Nodes.JsonObject
                {
                    [library] = new System.Text.Json.Nodes.JsonObject { ["runtime"] = runtime, ["native"] = nativeAssets }
                }
            },
            ["libraries"] = new System.Text.Json.Nodes.JsonObject
            {
                [library] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "package",
                    ["serviceable"] = true,
                    ["sha512"] = "",
                    ["path"] = $"microsoft.netcore.app.runtime.{rid}/{version}"
                }
            }
        };
        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // A corerun package: tests/User/corerun_init.c as /bin/init, naming the program; under /coreclr upstream's corerun,
    // CoreCLR's runtime and JIT, CoreLib, the framework's libraries the program reaches with System.Native, and the program,
    // which the SDK's C# compiler builds against the reference pack; musl's libc.so as the dynamic linker and the shared
    // C++ runtime in /lib.
    private static async Task<IReadOnlyList<(string Name, string Source)>> BuildCoreRunPackageAsync(string root, string output,
        KernelArchitecture architecture, string name, string[] sources)
    {
        var tree = Path.Combine(root, "artifacts", "runtime-witos", "src");
        var coreclr = Path.Combine(tree, "artifacts", "bin", "coreclr", $"witos.{architecture.Name}.Release");
        var framework = FrameworkDirectory(root, tree, architecture);
        var native = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(framework)!)!, "native");
        var references = Path.Combine(tree, "artifacts", "bin", "microsoft.netcore.app.ref", "ref", ReadLock(root).TargetFramework);
        if (!File.Exists(Path.Combine(coreclr, "corerun")) || !File.Exists(Path.Combine(framework, "System.Console.dll")))
            throw new InvalidOperationException($"No CoreCLR or shared framework for witos in {tree}. Run: dotnet run --project tools/WitOS.Dev -- runtime-witos --arch {architecture.Name}");
        var sdks = Directory.GetDirectories(Path.Combine(tree, ".dotnet", "sdk"));
        if (sdks.Length != 1)
            throw new InvalidDataException($"The runtime's own .NET SDK is not one SDK: {string.Join(", ", sdks)}.");
        var assembly = Path.Combine(output, name + ".dll");
        await RunToolAsync(Path.Combine(tree, ".dotnet", "dotnet"),
        [
            Path.Combine(sdks[0], "Roslyn", "bincore", "csc.dll"), "-nologo", "-noconfig", "-nostdlib", "-deterministic", "-optimize+",
            "-unsafe", "-nullable:enable", "-target:exe", .. Directory.GetFiles(references, "*.dll").Select(reference => $"-r:{reference}"),
            $"-out:{assembly}", .. sources
        ], tree);
        var sysroot = await Sysroot.BuildAsync(root, architecture);
        var program = Path.Combine(output, "corerun_init.elf");
        await Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            .. Sysroot.DriverOptions(root, architecture), "-std=c11", "-O2", "-Wall", "-Wextra", "-Werror",
            $"-DCORERUN_ASSEMBLY=\"/coreclr/{name}.dll\"", Path.Combine(root, CORERUN_PROGRAM), "-o", program
        ], root);
        var lib = Path.Combine(sysroot, "usr", "lib");
        return
        [
            ("bin/init", program), (MuslLibc.InterpreterPath(architecture).TrimStart('/'), Path.Combine(lib, "libc.so")),
            ("lib/libc++.so.1", Path.Combine(lib, "libc++.so.1")), ("lib/libc++abi.so.1", Path.Combine(lib, "libc++abi.so.1")),
            ("lib/libunwind.so.1", Path.Combine(lib, "libunwind.so.1")),
            ("coreclr/corerun", Path.Combine(coreclr, "corerun")),
            ("coreclr/libcoreclr.so", Path.Combine(coreclr, "libcoreclr.so")), ("coreclr/libclrjit.so", Path.Combine(coreclr, "libclrjit.so")),
            ("coreclr/System.Private.CoreLib.dll", Path.Combine(coreclr, "IL", "System.Private.CoreLib.dll")),
            ("coreclr/libSystem.Native.so", Path.Combine(native, "libSystem.Native.so")),
            ($"coreclr/{name}.dll", assembly),
            .. FrameworkClosure(framework, assembly).Select(library => ($"coreclr/{library}.dll", Path.Combine(framework, library + ".dll")))
        ];
    }

    // The framework's libraries a program reaches through its references and theirs (R3.3): corerun's TPA list is every
    // assembly of its directory, and the package carries those alone; CoreLib, which every one references, comes apart.
    private static IReadOnlyList<string> FrameworkClosure(string framework, string program)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>([program]);
        while (pending.Count > 0)
        {
            using var stream = File.OpenRead(pending.Dequeue());
            using var reader = new PEReader(stream);
            var metadata = reader.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var library = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                var path = Path.Combine(framework, library + ".dll");
                if (File.Exists(path) && found.Add(library))
                    pending.Enqueue(path);
            }
        }
        return [.. found];
    }

    /// <summary>
    /// The package of CoreCLR's PAL suite (R7.1): tests/User/palsuite_runner.c as /bin/init, the build's paltests
    /// directory under /paltests (paltests and the programs and libraries of the tests built apart, without the scripts
    /// and the list), the selection, upstream's paltestlist.txt less the tests palsuite.json leaves out, in
    /// /paltests/selection, musl's libc.so as the dynamic linker and the shared C++ runtime in /lib.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildPalSuitePackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var paltests = Path.Combine(root, "artifacts", "runtime-witos", "src", "artifacts", "bin", "coreclr",
            $"witos.{architecture.Name}.Release", "paltests");
        var list = Path.Combine(paltests, "paltestlist.txt");
        if (!File.Exists(Path.Combine(paltests, "paltests")) || !File.Exists(list))
            throw new InvalidOperationException($"No PAL suite for witos in {paltests}. Run: dotnet run --project tools/WitOS.Dev -- runtime-witos --arch {architecture.Name}");
        var tests = (await File.ReadAllLinesAsync(list)).Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        var excluded = PalSuiteExclusions(root);
        var unknown = excluded.Keys.Where(test => !tests.Contains(test, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
            throw new InvalidDataException($"{PALSUITE_SELECTION} leaves out tests paltestlist.txt does not name: {string.Join(", ", unknown)}.");
        var selection = Path.Combine(output, "palsuite-selection.txt");
        await File.WriteAllTextAsync(selection, string.Join("\n", tests.Where(test => !excluded.ContainsKey(test))) + "\n");
        var sysroot = await Sysroot.BuildAsync(root, architecture);
        var init = Path.Combine(output, "palsuite_runner.elf");
        // The program is C: the driver's C++ library option would be an unused argument.
        await Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            .. Sysroot.DriverOptions(root, architecture).Where(option => option != "-stdlib=libc++"), "-std=c17", "-O2", "-Wall",
            "-Wextra", "-Werror", $"-DPALSUITE_EXCLUDED={excluded.Count}", Path.Combine(root, PALSUITE_PROGRAM), "-o", init
        ], root);
        var lib = Path.Combine(sysroot, "usr", "lib");
        return
        [
            ("bin/init", init), ("paltests/selection", selection),
            .. Directory.EnumerateFiles(paltests, "*", SearchOption.AllDirectories)
                .Where(file => !file.EndsWith(".sh", StringComparison.Ordinal) && file != list)
                .Order(StringComparer.Ordinal)
                .Select(file => ("paltests/" + Path.GetRelativePath(paltests, file).Replace('\\', '/'), file)),
            (MuslLibc.InterpreterPath(architecture).TrimStart('/'), Path.Combine(lib, "libc.so")),
            ("lib/libc++.so.1", Path.Combine(lib, "libc++.so.1")), ("lib/libc++abi.so.1", Path.Combine(lib, "libc++abi.so.1")),
            ("lib/libunwind.so.1", Path.Combine(lib, "libunwind.so.1"))
        ];
    }

    /// <summary>
    /// The package of the slices of CoreCLR's src/tests (R7.2b): tests/User/coreclr_tests_runner.c as /bin/init, corerun,
    /// CoreCLR, CoreLib, System.Native and the libraries the groups reach under /coreclr, from the framework or from the
    /// test build's own dependencies (xunit's assertions), as upstream's Core_Root carries them, each group's directory of
    /// the test build under /tests, the groups' names in /tests/groups and the tests left out, a name and a
    /// reason a line, in /tests/exclusions.txt, musl's libc.so as the dynamic linker and the shared C++ runtime in /lib.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildCoreClrTestsPackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var tree = Path.Combine(root, "artifacts", "runtime-witos", "src");
        var coreclr = Path.Combine(tree, "artifacts", "bin", "coreclr", $"witos.{architecture.Name}.Release");
        var framework = FrameworkDirectory(root, tree, architecture);
        var native = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(framework)!)!, "native");
        var tests = TestsDirectory(tree, architecture);
        var dependencies = Path.Combine(tree, "artifacts", "TargetingPack");
        var selection = ReadCoreClrTestSelection(root);
        var files = new List<(string Name, string Source)>();
        var assemblies = new List<string>();
        foreach (var group in selection.Groups)
        {
            var directory = Path.Combine(tests, group);
            var assembly = Path.Combine(directory, Path.GetFileName(group) + ".dll");
            if (!File.Exists(assembly))
                throw new InvalidOperationException($"No test group {group} for witos in {tests}. Run: dotnet run --project tools/WitOS.Dev -- runtime-witos --arch {architecture.Name}");
            foreach (var file in Directory.EnumerateFiles(directory).Where(file => !file.EndsWith(".pdb", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                files.Add(($"tests/{group}/{Path.GetFileName(file)}", file));
                if (file.EndsWith(".dll", StringComparison.Ordinal))
                    assemblies.Add(file);
            }
        }
        var libraries = ReferenceClosure([framework, dependencies], assemblies);
        var isolated = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, directory, executable, arguments, environment) in IsolatedTests(tests, selection))
        {
            names.Add(name);
            foreach (var file in Directory.EnumerateFiles(directory).Where(file => !file.EndsWith(".pdb", StringComparison.Ordinal) &&
                         !file.EndsWith(".sh", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                files.Add(($"tests/{name}/{Path.GetFileName(file)}", file));
                if (file.EndsWith(".dll", StringComparison.Ordinal))
                    assemblies.Add(file);
            }
            if (!selection.Excluded.ContainsKey(name))
                isolated.Add($"{(environment.Length > 0 ? environment + " " : "")}/tests/{name}/{executable}{(arguments.Length > 0 ? " " + arguments : "")}");
        }
        // A merged test is named as its group prints it (with "::"); any other name must be a process-isolated test.
        var unknown = selection.Excluded.Keys.Where(name => !name.Contains("::", StringComparison.Ordinal) && !names.Contains(name)).ToList();
        if (unknown.Count > 0)
            throw new InvalidDataException($"{CORECLR_TESTS_SELECTION} leaves out tests the groups do not have: {string.Join(", ", unknown)}.");
        var groups = Path.Combine(output, "coreclr-tests-groups.txt");
        await File.WriteAllTextAsync(groups, string.Join("\n", selection.Groups) + "\n");
        var standalone = Path.Combine(output, "coreclr-tests-standalone.txt");
        await File.WriteAllTextAsync(standalone, string.Join("\n", isolated) + "\n");
        var exclusions = Path.Combine(output, "coreclr-tests-exclusions.txt");
        await File.WriteAllTextAsync(exclusions, string.Concat(selection.Excluded.Select(test => $"{test.Key},{test.Value.Replace(',', ';')}\n")));
        var sysroot = await Sysroot.BuildAsync(root, architecture);
        var init = Path.Combine(output, "coreclr_tests_runner.elf");
        // The program is C: the driver's C++ library option would be an unused argument.
        await Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            .. Sysroot.DriverOptions(root, architecture).Where(option => option != "-stdlib=libc++"), "-std=c17", "-O2", "-Wall",
            "-Wextra", "-Werror", Path.Combine(root, CORECLR_TESTS_PROGRAM), "-o", init
        ], root);
        var lib = Path.Combine(sysroot, "usr", "lib");
        return
        [
            ("bin/init", init), ("tests/groups", groups), ("tests/standalone", standalone), ("tests/exclusions.txt", exclusions),
            (MuslLibc.InterpreterPath(architecture).TrimStart('/'), Path.Combine(lib, "libc.so")),
            ("lib/libc++.so.1", Path.Combine(lib, "libc++.so.1")), ("lib/libc++abi.so.1", Path.Combine(lib, "libc++abi.so.1")),
            ("lib/libunwind.so.1", Path.Combine(lib, "libunwind.so.1")),
            ("coreclr/corerun", Path.Combine(coreclr, "corerun")),
            ("coreclr/libcoreclr.so", Path.Combine(coreclr, "libcoreclr.so")), ("coreclr/libclrjit.so", Path.Combine(coreclr, "libclrjit.so")),
            ("coreclr/System.Private.CoreLib.dll", Path.Combine(coreclr, "IL", "System.Private.CoreLib.dll")),
            ("coreclr/libSystem.Native.so", Path.Combine(native, "libSystem.Native.so")),
            .. libraries.Select(library => ($"coreclr/{Path.GetFileName(library)}", library)),
            .. files
        ];
    }

    // The process-isolated tests of the standalone groups (R7.2b): every test directory beside a group's own, under the
    // group's parent directory, that holds the script of a test (<name>.sh) and its assembly, which the script names; a
    // test is named by its directory's path under the test build. Each script expects 100, as the guest does, and may give
    // the test default arguments and environment variables (R8.1), which the guest passes on.
    private static IEnumerable<(string Name, string Directory, string Executable, string Arguments, string Environment)> IsolatedTests(
        string tests, CoreClrTestSelection selection)
    {
        foreach (var group in selection.Standalone)
        {
            var parent = Path.Combine(tests, Path.GetDirectoryName(group)!);
            var own = Path.Combine(tests, group);
            if (!Directory.Exists(own))
                throw new InvalidOperationException($"No test group {group} for witos in {tests}.");
            foreach (var script in Directory.EnumerateFiles(parent, "*.sh", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var directory = Path.GetDirectoryName(script)!;
                if (directory == own || Path.GetFileNameWithoutExtension(script) != Path.GetFileName(directory))
                    continue;
                var text = File.ReadAllText(script);
                var executable = SCRIPT_EXECUTABLE.Match(text);
                var expected = SCRIPT_EXIT_CODE.Match(text);
                if (!executable.Success || !expected.Success || expected.Groups[1].Value != "100")
                    throw new InvalidDataException($"{script} names no assembly or expects another exit code than 100.");
                var arguments = SCRIPT_ARGUMENTS.Match(text);
                var environment = new List<string>();
                foreach (Match variable in SCRIPT_ENVIRONMENT.Matches(text))
                {
                    var (name, value) = (variable.Groups[1].Value, variable.Groups[2].Value.TrimEnd());
                    if (name is "CLRTestExpectedExitCode" or "TestExclusionListPath")
                        continue;
                    // The guest has no shell: a value is given as it is written.
                    if (value.Length == 0 || value.IndexOfAny(['$', '"', '\'', ' ', '`', '\\']) >= 0)
                        throw new InvalidDataException($"{script} exports {name} as {value}, which the guest cannot give without a shell.");
                    environment.Add($"{name}={value}");
                }
                yield return (Path.GetRelativePath(tests, directory).Replace('\\', '/'), directory, executable.Groups[1].Value.Trim(),
                    arguments.Success ? arguments.Groups[1].Value.Trim() : "", string.Join(' ', environment));
            }
        }
    }

    // The libraries assemblies reach through their references and theirs (R7.2b), each from the first directory that has
    // it; a reference none of them has is the assembly's neighbour or CoreLib, which come apart.
    private static IReadOnlyList<string> ReferenceClosure(IReadOnlyList<string> directories, IEnumerable<string> roots)
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var pending = new Queue<string>(roots);
        while (pending.Count > 0)
        {
            using var stream = File.OpenRead(pending.Dequeue());
            using var reader = new PEReader(stream);
            if (!reader.HasMetadata)
                continue;
            var metadata = reader.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var library = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if (found.ContainsKey(library))
                    continue;
                var path = directories.Select(directory => Path.Combine(directory, library + ".dll")).FirstOrDefault(File.Exists);
                if (path is null)
                    continue;
                found[library] = path;
                pending.Enqueue(path);
            }
        }
        return [.. found.Values];
    }

    /// <summary>
    /// The groups of src/tests the guest runs and the tests of them it leaves out with their reasons (R7.2b).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The selection.</returns>
    public static CoreClrTestSelection ReadCoreClrTestSelection(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, CORECLR_TESTS_SELECTION)));
        var groups = document.RootElement.GetProperty("groups").EnumerateArray().Select(group => group.GetString() ?? "").ToList();
        var standalone = document.RootElement.GetProperty("standalone").EnumerateArray().Select(group => group.GetString() ?? "").ToList();
        if (groups.Count == 0 || groups.Concat(standalone).Any(group => group.Length == 0 || group.Contains('\\') ||
                group.EndsWith(".csproj", StringComparison.Ordinal) || !group.Contains('/')))
            throw new InvalidDataException($"{CORECLR_TESTS_SELECTION} names no group or a group that is not a project path without its extension.");
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var test in document.RootElement.GetProperty("excluded").EnumerateObject())
        {
            var reason = test.Value.GetString();
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidDataException($"{CORECLR_TESTS_SELECTION} leaves out {test.Name} without a reason.");
            excluded.Add(test.Name, reason);
        }
        return new CoreClrTestSelection(groups, standalone, excluded);
    }

    /// <summary>
    /// The tests the PAL suite leaves out (R7.1) and the reason for each.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The test names of paltestlist.txt and their reasons.</returns>
    public static IReadOnlyDictionary<string, string> PalSuiteExclusions(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, PALSUITE_SELECTION)));
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var test in document.RootElement.GetProperty("excluded").EnumerateObject())
        {
            var reason = test.Value.GetString();
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidDataException($"{PALSUITE_SELECTION} leaves out {test.Name} without a reason.");
            excluded.Add(test.Name, reason);
        }
        return excluded;
    }

    /// <summary>
    /// The package of the try_run measurement: tests/User/tryrun_init.c, which clang's driver builds against the sysroot,
    /// as /bin/init, the probes the configure kept under /tryrun with their names in /tryrun/probes, musl's libc.so as
    /// the dynamic linker and the shared C++ runtime in /lib.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildProbeProgramsAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var directory = ProbeDirectory(root, architecture);
        var probes = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];
        if (probes.Count == 0)
            throw new InvalidOperationException($"No try_run probe in {directory}. Run: dotnet run --project tools/WitOS.Dev -- runtime-witos --arch {architecture.Name}");
        var sysroot = Sysroot.Directory(root, architecture);
        var init = Path.Combine(output, "tryrun_init.elf");
        // The program is C: the driver's C++ library option would be an unused argument.
        await Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            .. Sysroot.DriverOptions(root, architecture).Where(option => option != "-stdlib=libc++"), "-std=c17", "-O2", "-Wall",
            "-Wextra", "-Werror", Path.Combine(root, "tests", "User", "tryrun_init.c"), "-o", init
        ], root);
        var list = Path.Combine(output, "tryrun-probes.txt");
        await File.WriteAllTextAsync(list, string.Join("\n", probes) + "\n");
        var lib = Path.Combine(sysroot, "usr", "lib");
        return
        [
            ("bin/init", init), ("tryrun/probes", list), .. probes.Select(probe => ($"tryrun/{probe}", Path.Combine(directory, probe))),
            (MuslLibc.InterpreterPath(architecture).TrimStart('/'), Path.Combine(lib, "libc.so")),
            ("lib/libc++.so.1", Path.Combine(lib, "libc++.so.1")), ("lib/libc++abi.so.1", Path.Combine(lib, "libc++abi.so.1")),
            ("lib/libunwind.so.1", Path.Combine(lib, "libunwind.so.1"))
        ];
    }

    /// <summary>
    /// Reads the pin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    public static RuntimePin ReadLock(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, LOCK)));
        var lockRoot = document.RootElement;
        string Text(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";
        var budget = lockRoot.GetProperty("budget");
        return new RuntimePin(Text(lockRoot, "repository"), Text(lockRoot, "tag"), Text(lockRoot, "version"),
            Text(lockRoot, "commit"),
            lockRoot.GetProperty("sources").EnumerateArray().Select(source => (Text(source, "path"), Text(source, "sha256")))
                .ToArray(),
            new RuntimeBudget(budget.GetProperty("freebsd").GetProperty("coreclr").GetInt32(),
                budget.GetProperty("freebsd").GetProperty("nativeLibraries").GetInt32(),
                budget.GetProperty("haiku").GetProperty("coreclr").GetInt32(),
                budget.GetProperty("haiku").GetProperty("nativeLibraries").GetInt32()));
    }

    /// <summary>
    /// The area of the runtime a patched path belongs to, as the ports' budget counts it.
    /// </summary>
    /// <param name="path">Upstream path.</param>
    /// <returns>coreclr, nativeLibraries, hosts, libraries or build.</returns>
    public static string Area(string path) =>
        path.StartsWith("src/coreclr/", StringComparison.Ordinal) ? "coreclr"
        : path.StartsWith("src/native/libs/", StringComparison.Ordinal) ? "nativeLibraries"
        : path.StartsWith("src/native/corehost/", StringComparison.Ordinal) ? "hosts"
        : path.StartsWith("src/libraries/", StringComparison.Ordinal) ? "libraries"
        : "build";

    /// <summary>
    /// Reads the probes of TryRunResults.cmake: each try_run variable and the executable CMake kept for it, in the order
    /// the file lists them.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <returns>Each variable and its executable.</returns>
    public static IReadOnlyList<(string Variable, string Executable)> ParseTryRunResults(string text)
    {
        var executables = Regex.Matches(text, @"^# Executable\s*:\s*(\S+)\s*$", RegexOptions.Multiline).Select(match => match.Groups[1].Value).ToList();
        var variables = Regex.Matches(text, @"^set\(\s*([A-Z0-9_]+_EXITCODE)\s*$", RegexOptions.Multiline).Select(match => match.Groups[1].Value).ToList();
        if (executables.Count != variables.Count ||
            executables.Zip(variables).Any(pair => !Path.GetFileName(pair.First).EndsWith("-" + pair.Second, StringComparison.Ordinal)))
            throw new InvalidDataException("TryRunResults.cmake does not pair each try_run variable with its executable.");
        return variables.Zip(executables).ToList();
    }

    /// <summary>
    /// Reads the guest's answers from the serial log of the measurement, whose user lines the kernel marks [USER]: a
    /// probe that exited answers its status, one that a signal ended FAILED_TO_RUN, as CMake records a probe that could
    /// not run.
    /// </summary>
    /// <param name="lines">The serial log.</param>
    /// <returns>Each probe's answer.</returns>
    public static IReadOnlyDictionary<string, string> ParseGuestAnswers(IEnumerable<string> lines)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"^(?:\[USER\] )?\[TRYRUN\] ([A-Z0-9_]+_EXITCODE) (exit|signal) ([0-9]+)\s*$");
            if (match.Success && !answers.TryAdd(match.Groups[1].Value, match.Groups[2].Value == "exit" ? match.Groups[3].Value : "FAILED_TO_RUN"))
                throw new InvalidDataException($"The guest answered {match.Groups[1].Value} twice.");
        }
        return answers;
    }

    #endregion

    #region Tools

    // The pinned checkout under .tools/upstream: the commit alone, fetched shallow, never built in and never changed.
    private static async Task<string> PrepareCheckoutAsync(string root, RuntimePin pin)
    {
        var checkout = Path.Combine(root, ".tools", "upstream", $"runtime-witos-{pin.Version}");
        if (!Directory.Exists(Path.Combine(checkout, ".git")))
        {
            if (Directory.Exists(checkout) && Directory.EnumerateFileSystemEntries(checkout).Any())
                throw new InvalidOperationException($"{checkout} exists without Git metadata; refusing to overwrite it.");
            Directory.CreateDirectory(checkout);
            Console.WriteLine($"Fetching dotnet/runtime {pin.Tag} ({pin.Commit[..12]})...");
            await GitAsync(checkout, ["init", "-q"]);
            await GitAsync(checkout, ["remote", "add", "origin", pin.Repository + ".git"]);
            await GitAsync(checkout, ["fetch", "-q", "--depth=1", "origin", pin.Commit], 1800);
            await GitAsync(checkout, ["checkout", "-q", "--detach", pin.Commit], 1800);
        }
        var revision = (await GitAsync(checkout, ["rev-parse", "HEAD"])).Trim();
        var remote = (await GitAsync(checkout, ["remote", "get-url", "origin"])).Trim().TrimEnd('/');
        if (revision != pin.Commit || (remote != pin.Repository && remote != pin.Repository + ".git"))
            throw new InvalidDataException($"{checkout} is not {pin.Repository} at {pin.Commit}; it was not changed.");
        if (!string.IsNullOrWhiteSpace(await GitAsync(checkout, ["status", "--porcelain", "--untracked-files=normal"])))
            throw new InvalidDataException($"{checkout} has local changes; refusing to use it.");
        // Every file the patch set changes has the pinned bytes in the clean checkout, which a Linux host writes as git
        // stores them; a file WitOS adds does not exist upstream.
        foreach (var (path, sha256) in pin.Sources)
        {
            var file = Path.Combine(checkout, path);
            var bytes = File.Exists(file) ? await File.ReadAllBytesAsync(file) : [];
            if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != sha256)
                throw new InvalidDataException($"{LOCK} does not pin {path} as dotnet/runtime {pin.Tag} holds it.");
        }
        return checkout;
    }

    // The work tree under artifacts/runtime-witos/src: a worktree of the checkout, returned to the commit on every run with
    // the build's own caches kept, then patched.
    private static async Task<string> PrepareTreeAsync(string root, string checkout, RuntimePin pin)
    {
        var tree = Path.Combine(root, "artifacts", "runtime-witos", "src");
        // A tree of another checkout, an earlier pin's (R8.1), goes whole with that pin's build caches and SDK: its
        // worktree cannot reach the new commit, and the runtime's own SDK is one SDK.
        var worktree = Path.Combine(tree, ".git");
        if (Directory.Exists(tree) && (!File.Exists(worktree) ||
                !(await File.ReadAllTextAsync(worktree)).Contains($"/{Path.GetFileName(checkout)}/", StringComparison.Ordinal)))
        {
            Console.WriteLine($"The work tree belongs to another pin; building dotnet/runtime {pin.Tag} from a new one.");
            Directory.Delete(tree, recursive: true);
        }
        if (!Directory.Exists(tree))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tree)!);
            await GitAsync(checkout, ["worktree", "prune"]);
            await GitAsync(checkout, ["worktree", "add", "-q", "--detach", tree, pin.Commit], 1800);
        }
        await GitAsync(tree, ["checkout", "-q", "--force", "--detach", pin.Commit], 1800);
        await GitAsync(tree, ["clean", "-ffdqx", "-e", "/artifacts/", "-e", "/.dotnet/"], 600);
        foreach (var (path, output) in PATCHES)
        {
            var file = Path.Combine(tree, path);
            var text = File.Exists(file) ? (await File.ReadAllTextAsync(file)).Replace("\r\n", "\n") : "";
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, UpstreamPatches.Apply(root, "runtime", path, output, text));
        }
        Console.WriteLine($"Applied the witos patch set: {PATCHES.Count} files of dotnet/runtime {pin.Tag}.");
        return tree;
    }

    // The runtime's own build script in the work tree.
    // The merged test groups of src/tests for witos (R7.2b): managed only, without the Core_Root layout, which the guest's
    // package takes the place of. The test build restores for witos-<arch>, which the SDK's own identifier graph does not
    // know, so it takes the witos tree's graph, as a program's project does (R2.3b).
    private static async Task RunTestsBuildAsync(string tree, KernelArchitecture architecture, IReadOnlyList<string> groups,
        IReadOnlyDictionary<string, string> environment)
    {
        var testsEnvironment = new Dictionary<string, string>(environment, StringComparer.Ordinal)
        {
            ["RuntimeIdentifierGraphPath"] = Path.Combine(tree, "src", "libraries", "Microsoft.NETCore.Platforms", "src", "PortableRuntimeIdentifierGraph.json")
        };
        var build = await Processes.RunAsync(Path.Combine(tree, "src", "tests", "build.sh"),
            [architecture.Name, "Release", "-os", "witos", "-skipnative", "-skipgeneratelayout", .. groups.Select(group => $"-test:{group}.csproj")],
            tree, 3600, BuildEnvironment(testsEnvironment));
        if (build.TimedOut || build.ExitCode != 0)
            throw new InvalidOperationException($"src/tests/build.sh for witos-{architecture.Name} failed (exit {build.ExitCode}, timeout={build.TimedOut}).\n" +
                $"{Tail(build.Output)}\n{Tail(build.Error)}");
    }

    // What the groups' tests did (R7.2b): each group's "Passed test" and "Failed test" lines between the runner's group
    // markers, written to a report beside the tree; a failed test fails runtime-witos.
    private static async Task<string> ReportCoreClrTestsAsync(string root, KernelArchitecture architecture, string log, CoreClrTestSelection selection)
    {
        var results = new SortedDictionary<string, (int Passed, List<string> Failed)>(StringComparer.Ordinal);
        var isolated = new SortedDictionary<string, string>(StringComparer.Ordinal);
        int skipped = 0;
        bool skipping = false;
        string? group = null;
        foreach (var raw in await File.ReadAllLinesAsync(log))
        {
            var line = raw.TrimEnd('\r');
            var marker = TEST_GROUP_LINE.Match(line);
            if (marker.Success)
            {
                group = marker.Groups[1].Value;
                results[group] = (0, []);
                continue;
            }
            var single = TEST_ISOLATED_LINE.Match(line);
            if (single.Success)
            {
                isolated[single.Groups[1].Value] = single.Groups[2].Value;
                group = null;
                continue;
            }
            if (TEST_SKIPPED_LINE.IsMatch(line))
            {
                skipping = true; /* a process-isolated test the merged runner cannot start reports itself passed */
                continue;
            }
            var result = TEST_RESULT_LINE.Match(line);
            if (result.Success && group is not null)
            {
                var (passed, failed) = results[group];
                if (result.Groups[1].Value == "Failed")
                    failed.Add(result.Groups[2].Value.Trim());
                else if (skipping)
                    ++skipped;
                else
                    results[group] = (passed + 1, failed);
                skipping = false;
            }
        }
        var missing = selection.Groups.Where(name => !results.ContainsKey(name)).ToList();
        var report = Path.Combine(root, "artifacts", "runtime-witos", $"coreclr-tests-{architecture.Name}.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            groups = results.ToDictionary(entry => entry.Key, entry => new { passed = entry.Value.Passed, failed = entry.Value.Failed }),
            isolated,
            skippedInGroups = skipped,
            excluded = selection.Excluded.Count
        }, new JsonSerializerOptions { WriteIndented = true }));
        var failures = results.SelectMany(entry => entry.Value.Failed.Select(test => $"{entry.Key}: {test}"))
            .Concat(isolated.Where(entry => entry.Value != "ok").Select(entry => $"{entry.Key}: {entry.Value}")).ToList();
        if (missing.Count > 0 || failures.Count > 0 || results.Values.Any(entry => entry.Passed == 0))
            throw new InvalidDataException($"CoreCLR's tests on witos-{architecture.Name}: {failures.Count} failed" +
                (missing.Count > 0 ? $", groups that did not run: {string.Join(", ", missing)}" : "") +
                (failures.Count > 0 ? $": {string.Join("; ", failures.Take(40))}" : "") + $" ({report}).");
        return $"CoreCLR's tests on witos-{architecture.Name}: {results.Values.Sum(entry => entry.Passed)} passed in {results.Count} groups, " +
            $"{isolated.Count} process-isolated tests passed, {skipped} process-isolated tests of the groups not started, " +
            $"{selection.Excluded.Count} left out ({report}).";
    }

    // The library test projects for witos (R7.3): each built by the tree's SDK as an archived test build, which bundles
    // xunit's console runner and writes RunTests.sh; the restore asks for witos, so the tree's identifier graph is given.
    private static async Task RunLibraryTestsBuildAsync(string tree, KernelArchitecture architecture, IReadOnlyList<string> projects,
        IReadOnlyDictionary<string, string> environment)
    {
        var testsEnvironment = new Dictionary<string, string>(environment, StringComparer.Ordinal)
        {
            ["RuntimeIdentifierGraphPath"] = Path.Combine(tree, "src", "libraries", "Microsoft.NETCore.Platforms", "src", "PortableRuntimeIdentifierGraph.json")
        };
        // Upstream builds the testhost and the tests' shared dependencies before a test project (libs.pretest).
        await RunBuildAsync(tree, ["libs.pretest", "-os", "witos", "-arch", architecture.Name, "-c", "Release"], testsEnvironment);
        foreach (var project in projects)
        {
            var build = await Processes.RunAsync(Path.Combine(tree, ".dotnet", "dotnet"),
                ["build", Path.Combine(tree, project), "-c", "Release", "/p:TargetOS=witos", $"/p:TargetArchitecture={architecture.Name}",
                    "/p:ArchiveTests=true"],
                tree, 3600, BuildEnvironment(testsEnvironment));
            if (build.TimedOut || build.ExitCode != 0)
                throw new InvalidOperationException($"The test build of {project} for witos-{architecture.Name} failed (exit {build.ExitCode}, timeout={build.TimedOut}).\n" +
                    $"{Tail(build.Output)}\n{Tail(build.Error)}");
        }
    }

    // What the projects' tests did (R7.3): xunit's summary line of each project and its "[FAIL]" lines between the
    // runner's project markers, written to a report beside the tree; a failed test or a project without a summary fails
    // runtime-witos.
    private static async Task<string> ReportLibraryTestsAsync(string root, KernelArchitecture architecture, string log, LibraryTestSelection selection)
    {
        var summaries = new SortedDictionary<string, (int Total, int Errors, int Failed, int Skipped)>(StringComparer.Ordinal);
        var failures = new List<string>();
        string? project = null;
        foreach (var raw in await File.ReadAllLinesAsync(log))
        {
            var line = raw.TrimEnd('\r');
            var marker = LIBRARY_PROJECT_LINE.Match(line);
            if (marker.Success)
            {
                project = marker.Groups[1].Value;
                continue;
            }
            var summary = LIBRARY_SUMMARY_LINE.Match(line);
            if (summary.Success && project is not null)
            {
                summaries[project] = (int.Parse(summary.Groups[1].Value), int.Parse(summary.Groups[2].Value),
                    int.Parse(summary.Groups[3].Value), int.Parse(summary.Groups[4].Value));
                continue;
            }
            var failure = LIBRARY_FAIL_LINE.Match(line);
            if (failure.Success && project is not null)
                failures.Add($"{project}: {failure.Groups[1].Value.Trim()}");
        }
        var names = selection.Projects.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        var missing = names.Where(name => !summaries.ContainsKey(name)).ToList();
        var report = Path.Combine(root, "artifacts", "runtime-witos", $"library-tests-{architecture.Name}.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            projects = summaries.ToDictionary(entry => entry.Key, entry => new
            {
                total = entry.Value.Total,
                errors = entry.Value.Errors,
                failed = entry.Value.Failed,
                skipped = entry.Value.Skipped
            }),
            failures,
            excluded = selection.Excluded.Count
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (missing.Count > 0 || failures.Count > 0 || summaries.Values.Any(entry => entry.Errors > 0 || entry.Failed > 0))
            throw new InvalidDataException($"The libraries' tests on witos-{architecture.Name}: {failures.Count} failed" +
                (missing.Count > 0 ? $", projects without a summary: {string.Join(", ", missing)}" : "") +
                (failures.Count > 0 ? $": {string.Join("; ", failures.Take(40))}" : "") + $" ({report}).");
        var passed = summaries.Values.Sum(entry => entry.Total - entry.Skipped);
        return $"The libraries' tests on witos-{architecture.Name}: {passed} passed in {summaries.Count} projects, " +
            $"{summaries.Values.Sum(entry => entry.Skipped)} skipped, {selection.Excluded.Count} methods left out ({report}).";
    }

    private static string TestsDirectory(string tree, KernelArchitecture architecture) =>
        Path.Combine(tree, "artifacts", "tests", "coreclr", $"witos.{architecture.Name}.Release");

    private static async Task RunBuildAsync(string tree, string[] arguments, IReadOnlyDictionary<string, string>? environment)
    {
        var build = await Processes.RunAsync(Path.Combine(tree, "build.sh"), arguments, tree, 3600, BuildEnvironment(environment));
        if (build.TimedOut || build.ExitCode != 0)
            throw new InvalidOperationException($"build.sh {arguments[0]} failed (exit {build.ExitCode}, timeout={build.TimedOut}).\n" +
                $"{Tail(build.Output)}\n{Tail(build.Error)}");
    }

    // A build of the pinned tree runs without NuGet's audit (R8.1): the pin fixes upstream's dependencies, while the audit
    // reads the advisories published up to the day of the restore, which must not decide whether the pin builds; upstream
    // sets no NuGetAudit, so the variable is the property. Microsoft.DiaSymReader.Native, a Windows library of upstream's
    // externals that WitOS neither builds into anything nor ships, gained high-severity advisories after the pin, and the
    // build treats the warning as an error.
    private static Dictionary<string, string> BuildEnvironment(IReadOnlyDictionary<string, string>? environment) =>
        new(environment ?? new Dictionary<string, string>(), StringComparer.Ordinal) { ["NuGetAudit"] = "false" };

    // What upstream's native build runs with: the pinned clang and its LLVM tools first on the path, as the compilers,
    // and the sysroot for a cross build (none for the host).
    private static Dictionary<string, string> NativeEnvironment(string root, string? sysroot)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Path.Combine(Toolchain.ClangDirectory(root), "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["CLR_CC"] = Toolchain.Clang(root),
            ["CLR_CXX"] = Path.Combine(Toolchain.ClangDirectory(root), "bin", "clang++")
        };
        if (sysroot is not null)
            environment["ROOTFS_DIR"] = sysroot;
        return environment;
    }

    // The host's JITs and ILC (R1.3): upstream's build of the x64 Linux host, the only host the pinned LLVM release
    // serves, by the pinned clang and lld as an ordinary Linux build; ILC is the published compiler beside its JITs.
    private static async Task<string> BuildCompilerAsync(string root, string tree)
    {
        await RunBuildAsync(tree, ["clr.alljits+clr.tools", "-os", "linux", "-arch", "x64", "-c", "Release"], NativeEnvironment(root, null));
        var published = Path.Combine(tree, "artifacts", "bin", "coreclr", "linux.x64.Release", "ilc-published");
        var ilc = Path.Combine(published, "ilc");
        if (!File.Exists(ilc) || !File.Exists(Path.Combine(published, "libclrjit_unix_x64_x64.so")) ||
            !File.Exists(Path.Combine(published, "libclrjit_universal_arm64_x64.so")))
            throw new InvalidDataException($"The host build left no ILC with its x64 and ARM64 JITs in {published}.");
        return ilc;
    }

    // A program: the SDK's C# compiler builds it against the references (the witos CoreLib alone for the first program,
    // R1.3; the reference pack for the acceptance, R2.3a), and ILC compiles it for the architecture with the NativeAOT
    // class libraries and the libraries given (the shared framework's), with the arguments a publish passes (R2.1): those
    // Microsoft.NETCore.Native.targets gives a program by default, the libraries witos builds as direct P/Invokes, the
    // program's own (extra), and the SDK's switches. The object must be a relocatable ELF object for the architecture.
    private static async Task<string> CompileProgramAsync(string root, string tree, string ilc, string sdk, KernelArchitecture architecture,
        string name, string[] sources, string[] references, string[] libraries, string[] extra)
    {
        var directory = ProgramDirectory(root, architecture);
        var sdks = Directory.GetDirectories(Path.Combine(tree, ".dotnet", "sdk"));
        if (sdks.Length != 1)
            throw new InvalidDataException($"The runtime's own .NET SDK is not one SDK: {string.Join(", ", sdks)}.");
        var assembly = Path.Combine(directory, name + ".dll");
        await RunToolAsync(Path.Combine(tree, ".dotnet", "dotnet"),
        [
            Path.Combine(sdks[0], "Roslyn", "bincore", "csc.dll"), "-nologo", "-noconfig", "-nostdlib", "-deterministic", "-optimize+",
            "-unsafe", "-nullable:enable", "-target:exe", .. references.Select(reference => $"-r:{reference}"), $"-out:{assembly}",
            .. sources
        ], tree);
        var program = Path.Combine(directory, name + ".o");
        using var switches = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, PUBLISH_SWITCHES)));
        await RunToolAsync(ilc,
        [
            assembly, $"-o:{program}", $"-r:{Path.Combine(sdk, "*.dll")}", .. libraries.Select(library => $"-r:{library}"),
            "--targetos:witos", $"--targetarch:{architecture.Name}",
            "--dehydrate", "-O", "-g", "--export-dynamic-symbol:DotNetRuntimeDebugHeader",
            "--initassembly:System.Private.CoreLib", "--initassembly:System.Private.StackTraceMetadata",
            "--initassembly:System.Private.TypeLoader", "--initassembly:System.Private.Reflection.Execution",
            "--directpinvoke:System.Native", "--directpinvoke:System.IO.Compression.Native", .. extra,
            .. switches.RootElement.GetProperty("switches").EnumerateObject()
                .SelectMany(entry => new[] { $"--feature:{entry.Name}={entry.Value.GetString()}", $"--runtimeknob:{entry.Name}={entry.Value.GetString()}" }),
            .. switches.RootElement.GetProperty("featuresOnly").EnumerateObject().Select(entry => $"--feature:{entry.Name}={entry.Value.GetString()}"),
            $"--runtimeknob:RUNTIME_IDENTIFIER=witos-{architecture.Name}", "--stacktracedata", "--scanreflection",
            "--methodbodyfolding:generic", "--resilient", "--generateunmanagedentrypoints:System.Private.CoreLib,HIDDEN"
        ], tree);

        var header = new byte[20];
        await using (var stream = File.OpenRead(program))
            await stream.ReadExactlyAsync(header);
        var machine = architecture == KernelArchitecture.X64 ? 62 : 183;
        if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F' || header[4] != 2 ||
            header[5] != 1 || BitConverter.ToUInt16(header, 16) != 1 || BitConverter.ToUInt16(header, 18) != machine)
            throw new InvalidDataException($"{program} is not a relocatable ELF64 object for {architecture.Name} (machine {machine}).");
        return program;
    }

    // The first program's Main must be the code the JIT folds it to (R1.3): the return register zeroed, nothing called.
    private static async Task RequireConstantMainAsync(string root, string tree, string program, KernelArchitecture architecture)
    {
        var main = await Processes.RunAsync(Path.Combine(Toolchain.ClangDirectory(root), "bin", "llvm-objdump"),
            ["-d", $"--disassemble-symbols={PLATFORM_MAIN}", program], tree, 600);
        var zeroed = architecture == KernelArchitecture.X64 ? @"\bxorl\s+%eax, %eax" : @"\bmov\s+w0, wzr";
        var call = architecture == KernelArchitecture.X64 ? @"\bcall" : @"\bblr?\s";
        if (main.ExitCode != 0 || !main.Output.Contains($"<{PLATFORM_MAIN}>:", StringComparison.Ordinal) ||
            !Regex.IsMatch(main.Output, zeroed) || Regex.IsMatch(main.Output, call))
            throw new InvalidDataException($"{PLATFORM_MAIN} of {program} is not the constant 0 for witos-{architecture.Name}:\n{main.Output}\n{main.Error}");
    }

    // The link (R2.1): clang's driver with the sysroot's options links the object with System.Native and the NativeAOT
    // runtime in the order Microsoft.NETCore.Native.Unix.targets gives them (the managed code, the native shims, then the
    // runtime), so that every dependency follows its dependents.
    private static async Task<string> LinkProgramAsync(string root, string tree, string program, string sdk, KernelArchitecture architecture,
        string name)
    {
        var executable = Path.Combine(ProgramDirectory(root, architecture), name);
        var native = Path.Combine(tree, "artifacts", "bin", "native", $"{ReadLock(root).TargetFramework}-witos-Release-{architecture.Name}");
        // The runtime libraries in the targets' order, with the vxsort support library where the runtime built one: x64
        // in 10.0, ARM64 too since .NET 11 (R8.2).
        string[] runtime =
        [
            "libbootstrapper.o", "libRuntime.WorkstationGC.a", "libeventpipe-disabled.a",
            .. File.Exists(Path.Combine(sdk, "libRuntime.VxsortDisabled.a")) ? (string[])["libRuntime.VxsortDisabled.a"] : [],
            "libstandalonegc-disabled.a", "libaotminipal.a", "libstdc++compat.a"
        ];
        await RunToolAsync(Toolchain.Clang(root),
        [
            "--driver-mode=g++", .. Sysroot.DriverOptions(root, architecture), program, Path.Combine(native, "libSystem.Native.a"),
            .. runtime.Select(library => Path.Combine(sdk, library)), "-lpthread", "-ldl", "-lm", "-o", executable
        ], tree);
        return executable;
    }

    // The ILCompiler packages of the Linux host (R2.3b), as upstream packs them from the witos tree: the targets package
    // Microsoft.DotNet.ILCompiler and runtime.linux-x64.Microsoft.DotNet.ILCompiler with the host's ILC, which knows
    // witos (R1.3). Their project resolves the host's own libraries, so the host's native libraries and shared framework
    // are built first.
    private static async Task PackCompilerAsync(string root, string tree)
    {
        var environment = NativeEnvironment(root, null);
        await RunBuildAsync(tree, ["libs.native+libs.sfx", "-os", "linux", "-arch", "x64", "-c", "Release"], environment);
        var project = Path.Combine(tree, "src", "installer", "pkg", "projects", "Microsoft.DotNet.ILCompiler", "Microsoft.DotNet.ILCompiler.pkgproj");
        DropPackages(tree, "Microsoft.DotNet.ILCompiler");
        DropPackages(tree, "runtime.linux-x64.Microsoft.DotNet.ILCompiler");
        await RunBuildAsync(tree, ["--projects", project, "-os", "linux", "-arch", "x64", "-c", "Release"], environment);
        await RunBuildAsync(tree, ["--projects", project, "-os", "linux", "-arch", "x64", "-c", "Release", "/p:PackageTargetRuntime=linux-x64"],
            environment);
    }

    // The packages' feed: what the witos tree's build packed.
    private static string PackageFeed(string tree) => Path.Combine(tree, "artifacts", "packages", "Release", "Shipping");

    // A package of the feed goes before it is packed again: the packaging skips a package whose file is there, though
    // what it packs changed (the patched targets, a rebuilt runtime).
    private static void DropPackages(string tree, string id)
    {
        var feed = PackageFeed(tree);
        if (!Directory.Exists(feed))
            return;
        foreach (var package in Directory.GetFiles(feed, id + ".*.nupkg"))
        {
            var version = Path.GetFileName(package)[(id.Length + 1)..];
            if (char.IsAsciiDigit(version[0]))
                File.Delete(package);
        }
    }

    // The one version of the three packages a witos publish needs, read from the feed.
    internal static string PackageVersion(string tree, KernelArchitecture architecture)
    {
        const string TARGETS = "Microsoft.DotNet.ILCompiler.";
        var feed = PackageFeed(tree);
        var versions = Directory.GetFiles(feed, TARGETS + "*.nupkg").Select(Path.GetFileName)
            .Where(name => !name!.EndsWith(".symbols.nupkg", StringComparison.Ordinal))
            .Select(name => name![TARGETS.Length..^".nupkg".Length]).ToArray();
        if (versions.Length != 1)
            throw new InvalidDataException($"The feed {feed} holds not one Microsoft.DotNet.ILCompiler: {string.Join(", ", versions)}.");
        var version = versions[0];
        foreach (var package in new[] { "runtime.linux-x64.Microsoft.DotNet.ILCompiler", $"Microsoft.NETCore.App.Runtime.NativeAOT.witos-{architecture.Name}" })
        {
            if (!File.Exists(Path.Combine(feed, $"{package}.{version}.nupkg")))
                throw new InvalidDataException($"The feed {feed} holds no {package} {version}.");
        }
        return version;
    }

    // A program built by the SDK's targets (R2.3b): a project of its own under artifacts, apart from the repository's SDK
    // pin and build properties, whose sources are the program's directory, published with PublishAot for witos-<arch>
    // by the witos tree's SDK from the feed of the packages the tree built, through build/runtime/witos.targets with the
    // sysroot's link options. The packages keep one version from build to build, so their copies in the project's NuGet
    // cache are dropped first. Returns the published executable.
    internal static async Task<string> PublishProgramAsync(string root, string tree, KernelArchitecture architecture, string name,
        string sources, string version, string items)
    {
        var project = Path.Combine(root, "artifacts", "runtime-witos", "publish", architecture.Name, name);
        if (Directory.Exists(project))
            Directory.Delete(project, recursive: true);
        Directory.CreateDirectory(project);
        var sdk = Path.GetFileName(Directory.GetDirectories(Path.Combine(tree, ".dotnet", "sdk")).Single());
        await File.WriteAllTextAsync(Path.Combine(project, "global.json"), $"{{ \"sdk\": {{ \"version\": \"{sdk}\" }} }}\n");
        await File.WriteAllTextAsync(Path.Combine(project, "Directory.Build.props"), "<Project />\n");
        await File.WriteAllTextAsync(Path.Combine(project, "Directory.Build.targets"), "<Project />\n");
        await File.WriteAllTextAsync(Path.Combine(project, "nuget.config"), $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="witos" value="{{SecurityElement.Escape(PackageFeed(tree))}}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>

            """);
        string Property(string value) => SecurityElement.Escape(value)!.Replace(";", "%3B", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(project, name + ".csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{{ReadLock(root).TargetFramework}}</TargetFramework>
                <AssemblyName>{{name}}</AssemblyName>
                <RuntimeIdentifier>witos-{{architecture.Name}}</RuntimeIdentifier>
                <PublishAot>true</PublishAot>
                <InvariantGlobalization>true</InvariantGlobalization>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <Nullable>enable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <WitOSPackageVersion>{{Property(version)}}</WitOSPackageVersion>
                <WitOSRuntimeGraph>{{Property(Path.Combine(tree, "src", "libraries", "Microsoft.NETCore.Platforms", "src", "PortableRuntimeIdentifierGraph.json"))}}</WitOSRuntimeGraph>
                <WitOSSysroot>{{Property(Sysroot.Directory(root, architecture))}}</WitOSSysroot>
                <WitOSClang>{{Property(Toolchain.Clang(root))}}</WitOSClang>
                <WitOSLinkerOptions>{{string.Join(";", Sysroot.LinkOptions(root, architecture).Select(SecurityElement.Escape))}}</WitOSLinkerOptions>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="{{SecurityElement.Escape(Path.Combine(sources, "*.cs"))}}" />
              </ItemGroup>
            {{items}}
              <Import Project="{{SecurityElement.Escape(Path.Combine(root, PROGRAM_TARGETS))}}" />
            </Project>

            """);
        var cache = Path.Combine(root, "artifacts", "runtime-witos", "nuget");
        foreach (var package in new[] { "microsoft.dotnet.ilcompiler", "runtime.linux-x64.microsoft.dotnet.ilcompiler",
            $"microsoft.netcore.app.runtime.nativeaot.witos-{architecture.Name}" })
        {
            var copy = Path.Combine(cache, package, version);
            if (Directory.Exists(copy))
                Directory.Delete(copy, recursive: true);
        }
        var environment = NativeEnvironment(root, null);
        environment["NUGET_PACKAGES"] = cache;
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_NOLOGO"] = "1";
        var output = Path.Combine(project, "publish");
        var publish = await Processes.RunAsync(Path.Combine(tree, ".dotnet", "dotnet"),
            ["publish", Path.Combine(project, name + ".csproj"), "-c", "Release", "-o", output, "-nologo"], project, 1800, environment);
        if (publish.TimedOut || publish.ExitCode != 0)
            throw new InvalidOperationException($"dotnet publish of {name} for witos-{architecture.Name} failed (exit {publish.ExitCode}, " +
                $"timeout={publish.TimedOut}).\n{Tail(publish.Output)}\n{Tail(publish.Error)}");
        var executable = Path.Combine(output, name);
        if (!File.Exists(executable))
            throw new InvalidDataException($"dotnet publish of {name} for witos-{architecture.Name} left no {executable}.");
        Console.WriteLine($"dotnet publish -r witos-{architecture.Name}: {name} ({new FileInfo(executable).Length} bytes, {executable}).");
        return executable;
    }

    // The shared framework's libraries for the architecture (R2.3a): the runtime pack's lib directory.
    private static string FrameworkDirectory(string root, string tree, KernelArchitecture architecture) =>
        Path.Combine(tree, "artifacts", "bin", $"microsoft.netcore.app.runtime.witos-{architecture.Name}", "Release", "runtimes",
            $"witos-{architecture.Name}", "lib", ReadLock(root).TargetFramework);

    internal static string ProgramDirectory(string root, KernelArchitecture architecture) =>
        Path.Combine(root, "artifacts", "runtime-witos", "ilc", architecture.Name);

    // A tool of the build that may run for minutes.
    private static async Task RunToolAsync(string executable, string[] arguments, string directory)
    {
        var result = await Processes.RunAsync(executable, arguments, directory, 1800);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed (exit {result.ExitCode}, timeout={result.TimedOut}).\n" +
                $"{Tail(result.Output)}\n{Tail(result.Error)}");
    }

    // The try_run measurement's first half: a configure of CoreCLR for witos whose initial cache forgets tryrun.cmake's
    // answers, so that CMake keeps every probe it compiled for the target and lists it in TryRunResults.cmake. The
    // configure stops for want of the answers and for no other reason; the probes are copied out and the build
    // directory, whose cache holds the unanswered checks, is removed before the build configures again.
    private static async Task<IReadOnlyList<string>> CollectProbesAsync(string root, string tree, KernelArchitecture architecture,
        IReadOnlyDictionary<string, string> environment)
    {
        var objects = ObjectDirectory(tree, architecture);
        if (Directory.Exists(objects))
            Directory.Delete(objects, recursive: true);
        var configure = await Processes.RunAsync(Path.Combine(tree, "src", "coreclr", "build-runtime.sh"),
            [$"-{architecture.Name}", "-release", "-cross", "-os", "witos", "-configureonly", "-cmakeargs", "-C " + Path.Combine(root, TRYRUN_MEASURE)],
            tree, 1800, environment);
        var results = Path.Combine(objects, "TryRunResults.cmake");
        if (configure.TimedOut || !File.Exists(results) || configure.Output.Contains("CMake Error at", StringComparison.Ordinal) ||
            configure.Error.Contains("CMake Error at", StringComparison.Ordinal))
            throw new InvalidOperationException($"The measuring configure of witos-{architecture.Name} failed otherwise than for want of " +
                $"try_run answers (exit {configure.ExitCode}, timeout={configure.TimedOut}).\n{Tail(configure.Output)}\n{Tail(configure.Error)}");
        var probes = ParseTryRunResults(await File.ReadAllTextAsync(results));
        if (probes.Count == 0)
            throw new InvalidDataException($"The configure of witos-{architecture.Name} ran no try_run probe.");
        var directory = ProbeDirectory(root, architecture);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        foreach (var (variable, executable) in probes)
            File.Copy(executable, Path.Combine(directory, variable));
        Directory.Delete(objects, recursive: true);
        Console.WriteLine($"The native configure of witos-{architecture.Name} asks {probes.Count} try_run questions: {directory}.");
        return probes.Select(probe => probe.Variable).ToList();
    }

    // tryrun.cmake's answers, as the build's cache holds them.
    private static async Task<IReadOnlyDictionary<string, string>> ReadAnswersAsync(string objects, IReadOnlyList<string> probes)
    {
        var cache = (await File.ReadAllLinesAsync(Path.Combine(objects, "CMakeCache.txt")))
            .Select(line => Regex.Match(line, @"^([A-Z0-9_]+_EXITCODE):[A-Z]+=(.*)$")).Where(match => match.Success)
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value.Trim(), StringComparer.Ordinal);
        var missing = probes.Where(probe => !cache.ContainsKey(probe)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"tryrun.cmake answers no {string.Join(", ", missing)} for witos.");
        return probes.ToDictionary(probe => probe, probe => cache[probe], StringComparer.Ordinal);
    }

    // The try_run measurement's second half: the release kernel boots the system layer's root task, whose /bin/init runs
    // every probe; the guest's answers come from the serial log.
    private static async Task<IReadOnlyDictionary<string, string>> MeasureProbesAsync(string root, KernelArchitecture architecture,
        IReadOnlyList<string> probes)
    {
        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_TRYRUN_SCENARIO, architecture: architecture);
        var name = $"{architecture.Name}-runtime-tryrun-256";
        var isa = architecture.Triple[..architecture.Triple.IndexOf('-')];
        await BootScenarioRunner.RunAsync(root, image, new BootRequest(name, 256, 300, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = [$"[TRYRUN] {probes.Count} probes on {isa}: {probes.Count} ran", architecture.RootTaskPassedLine]
        });
        var answers = ParseGuestAnswers(await File.ReadAllLinesAsync(Path.Combine(root, "artifacts", "logs", name + ".serial.log")));
        var missing = probes.Where(probe => !answers.ContainsKey(probe)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"witos-{architecture.Name} did not answer {string.Join(", ", missing)}.");
        return answers;
    }

    private static string ObjectDirectory(string tree, KernelArchitecture architecture) =>
        Path.Combine(tree, "artifacts", "obj", "coreclr", $"witos.{architecture.Name}.Release");

    private static string ProbeDirectory(string root, KernelArchitecture architecture) =>
        Path.Combine(root, "artifacts", "runtime-witos", "tryrun", architecture.Name);

    // Whether CoreLib's code loads a string: OperatingSystem.OSPlatformName, the one place CoreLib spells its platform
    // (RFC 0015 section 3), is a constant the trimmed CoreLib keeps only where IsOSPlatform loads it.
    private static bool HasUserString(string corelib, string value)
    {
        using var stream = File.OpenRead(corelib);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var size = metadata.GetHeapSize(HeapIndex.UserString);
        for (var handle = MetadataTokens.UserStringHandle(1); !handle.IsNil && MetadataTokens.GetHeapOffset(handle) < size;
             handle = metadata.GetNextHandle(handle))
        {
            if (metadata.GetUserString(handle) == value)
                return true;
        }
        return false;
    }

    // The patch set by area against the FreeBSD and Haiku ports, written to artifacts/runtime-witos/patch-set.json.
    private static async Task<string> MeasureAsync(string root, RuntimePin pin)
    {
        var areas = PATCHES.Keys.GroupBy(Area).ToDictionary(group => group.Key, group => group.Count());
        int Count(string area) => areas.GetValueOrDefault(area);
        var report = Path.Combine(root, "artifacts", "runtime-witos", "patch-set.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            files = PATCHES.Count,
            areas,
            budget = pin.Budget
        }, new JsonSerializerOptions { WriteIndented = true }));
        return $"Patch set: {PATCHES.Count} files ({string.Join(", ", areas.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} {pair.Value}"))}); " +
            $"CoreCLR {Count("coreclr")} of FreeBSD's {pin.Budget.FreeBsdCoreClr} and Haiku's {pin.Budget.HaikuCoreClr}, " +
            $"native libraries {Count("nativeLibraries")} of {pin.Budget.FreeBsdNativeLibraries} and {pin.Budget.HaikuNativeLibraries}.";
    }

    private static async Task<string> GitAsync(string directory, string[] arguments, int timeout = 120)
    {
        var result = await Processes.RunAsync("git", ["-c", "safe.directory=*", .. arguments], directory, timeout);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments[0]} failed in {directory}. {result.Output}\n{result.Error}");
        return result.Output;
    }

    private static string Tail(string text) => text.Length <= 6000 ? text : text[^6000..];

    #endregion
}

/// <summary>
/// The pinned dotnet/runtime of the Unix-form port.
/// </summary>
/// <param name="Repository">Repository URL.</param>
/// <param name="Tag">Release tag.</param>
/// <param name="Version">Runtime version.</param>
/// <param name="Commit">Commit of the tag.</param>
/// <param name="Sources">Each upstream path the patch set changes and the SHA-256 of its bytes there.</param>
/// <param name="Budget">The size of the FreeBSD and Haiku ports.</param>
internal sealed record RuntimePin(string Repository, string Tag, string Version, string Commit,
    (string Path, string Sha256)[] Sources, RuntimeBudget Budget)
{
    /// <summary>
    /// The release's target framework, such as net10.0.
    /// </summary>
    public string TargetFramework => $"net{Version[..Version.IndexOf('.')]}.0";

    /// <summary>
    /// The framework version an application of the release asks for, as the SDK writes it: the release's x.0.0.
    /// </summary>
    public string FrameworkReference => $"{Version[..Version.IndexOf('.')]}.0.0";

    /// <summary>
    /// The version the tree's own build gives its shared framework, as its testhost lays it out and its test applications
    /// ask for it: the release's version without a preview's label (R8.2), which a preview's applications roll forward to.
    /// </summary>
    public string ProductVersion => Version.Split('-')[0];
}

/// <summary>
/// The size of the FreeBSD and Haiku ports in files of CoreCLR and of the native libraries (RFC 0015 section 4).
/// </summary>
/// <param name="FreeBsdCoreClr">FreeBSD's CoreCLR files.</param>
/// <param name="FreeBsdNativeLibraries">FreeBSD's native library files.</param>
/// <param name="HaikuCoreClr">Haiku's CoreCLR files.</param>
/// <param name="HaikuNativeLibraries">Haiku's native library files.</param>
internal sealed record RuntimeBudget(int FreeBsdCoreClr, int FreeBsdNativeLibraries, int HaikuCoreClr, int HaikuNativeLibraries);

/// <summary>
/// The merged test groups of src/tests the guest runs and the tests of them it leaves out with their reasons (R7.2b).
/// </summary>
/// <param name="Groups">Merged runners the guest runs whole: project paths under src/tests without the extension.</param>
/// <param name="Excluded">Test names as the groups print them, and the reasons.</param>
/// <param name="Standalone">Merged runners, as Groups names them, whose process-isolated tests run one by one.</param>
internal sealed record CoreClrTestSelection(IReadOnlyList<string> Groups, IReadOnlyList<string> Standalone,
    IReadOnlyDictionary<string, string> Excluded);

/// <summary>
/// The library test projects the guest runs and the test methods it leaves out with their reasons (R7.3).
/// </summary>
/// <param name="Projects">Project paths under the runtime tree.</param>
/// <param name="Excluded">xunit method names or patterns, and the reasons.</param>
internal sealed record LibraryTestSelection(IReadOnlyList<string> Projects, IReadOnlyDictionary<string, string> Excluded);
