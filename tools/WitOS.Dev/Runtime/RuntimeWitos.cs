using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.NativeAot;
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
    /// The runs of the acceptance: four cycles of its eight probes (R2.2).
    /// </summary>
    public const int ACCEPTANCE_RUNS = 32;

    /// <summary>
    /// The switches the SDK passes ILC for a NativeAOT publish (R2.1).
    /// </summary>
    public const string PUBLISH_SWITCHES = "build/runtime/nativeaot-publish.json";

    // The symbol ILC gives the program's Main: the assembly, the type and the method.
    private const string PLATFORM_MAIN = "Platform_Program__Main";

    #endregion

    #region Fields

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
        ["src/coreclr/tools/Common/CommandLineHelpers.cs"] = "src.coreclr.tools.Common.CommandLineHelpers.cs"
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

        // The class libraries above CoreLib (R2.3a): upstream's shared framework for witos, which takes the libraries' Unix
        // flavors, and the reference pack programs compile against.
        await RunBuildAsync(tree, ["libs.sfx", "-os", "witos", "-arch", architecture.Name, "-c", "Release"], environment);
        var framework = FrameworkDirectory(tree, architecture);
        var references = Path.Combine(tree, "artifacts", "bin", "microsoft.netcore.app.ref", "ref", "net10.0");
        if (!File.Exists(Path.Combine(framework, "System.Console.dll")) || !File.Exists(Path.Combine(references, "System.Runtime.dll")))
            throw new InvalidDataException($"The shared framework for witos-{architecture.Name} left no System.Console or reference pack ({framework}).");
        Console.WriteLine($"The shared framework for witos-{architecture.Name}: {Directory.GetFiles(framework, "*.dll").Length} libraries ({framework}).");

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

        // The M3 acceptance (R2.2), compiled against the reference pack as an SDK build compiles a program, and by ILC with
        // the shared framework's libraries (R2.3a): its output goes through System.Console, its native pages and the
        // thread's id through the libc as a direct P/Invoke.
        var sources = Directory.GetFiles(Path.Combine(root, ACCEPTANCE_PROGRAM), "*.cs").Order(StringComparer.Ordinal).ToArray();
        var acceptance = await CompileProgramAsync(root, tree, ilc, aotsdk, architecture, "Acceptance", sources,
            Directory.GetFiles(references, "*.dll").Order(StringComparer.Ordinal).ToArray(), [Path.Combine(framework, "*.dll")],
            ["--directpinvoke:libc"]);
        var acceptanceExecutable = await LinkProgramAsync(root, tree, acceptance, aotsdk, architecture, ACCEPTANCE_EXECUTABLE);
        image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_ACCEPTANCE_SCENARIO, architecture: architecture);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest($"{architecture.Name}-runtime-acceptance-256", 256, 900, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = [architecture.AcceptancePassedLine, architecture.RootTaskPassedLine]
        });
        Console.WriteLine($"NativeAOT on witos-{architecture.Name}: the M3 acceptance passed its {ACCEPTANCE_RUNS} runs in the guest ({acceptanceExecutable}).");
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
    private static async Task RunBuildAsync(string tree, string[] arguments, IReadOnlyDictionary<string, string>? environment)
    {
        var build = await Processes.RunAsync(Path.Combine(tree, "build.sh"), arguments, tree, 3600, environment);
        if (build.TimedOut || build.ExitCode != 0)
            throw new InvalidOperationException($"build.sh {arguments[0]} failed (exit {build.ExitCode}, timeout={build.TimedOut}).\n" +
                $"{Tail(build.Output)}\n{Tail(build.Error)}");
    }

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
        var native = Path.Combine(tree, "artifacts", "bin", "native", $"net10.0-witos-Release-{architecture.Name}");
        string[] runtime = architecture == KernelArchitecture.X64
            ? ["libbootstrapper.o", "libRuntime.WorkstationGC.a", "libeventpipe-disabled.a", "libRuntime.VxsortDisabled.a",
                "libstandalonegc-disabled.a", "libaotminipal.a", "libstdc++compat.a"]
            : ["libbootstrapper.o", "libRuntime.WorkstationGC.a", "libeventpipe-disabled.a", "libstandalonegc-disabled.a",
                "libaotminipal.a", "libstdc++compat.a"];
        await RunToolAsync(Toolchain.Clang(root),
        [
            "--driver-mode=g++", .. Sysroot.DriverOptions(root, architecture), program, Path.Combine(native, "libSystem.Native.a"),
            .. runtime.Select(library => Path.Combine(sdk, library)), "-lpthread", "-ldl", "-lm", "-o", executable
        ], tree);
        return executable;
    }

    // The shared framework's libraries for the architecture (R2.3a): the runtime pack's lib directory.
    private static string FrameworkDirectory(string tree, KernelArchitecture architecture) =>
        Path.Combine(tree, "artifacts", "bin", $"microsoft.netcore.app.runtime.witos-{architecture.Name}", "Release", "runtimes",
            $"witos-{architecture.Name}", "lib", "net10.0");

    private static string ProgramDirectory(string root, KernelArchitecture architecture) =>
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
    (string Path, string Sha256)[] Sources, RuntimeBudget Budget);

/// <summary>
/// The size of the FreeBSD and Haiku ports in files of CoreCLR and of the native libraries (RFC 0015 section 4).
/// </summary>
/// <param name="FreeBsdCoreClr">FreeBSD's CoreCLR files.</param>
/// <param name="FreeBsdNativeLibraries">FreeBSD's native library files.</param>
/// <param name="HaikuCoreClr">Haiku's CoreCLR files.</param>
/// <param name="HaikuNativeLibraries">Haiku's native library files.</param>
internal sealed record RuntimeBudget(int FreeBsdCoreClr, int FreeBsdNativeLibraries, int HaikuCoreClr, int HaikuNativeLibraries);
