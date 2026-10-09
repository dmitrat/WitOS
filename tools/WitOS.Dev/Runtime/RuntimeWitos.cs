using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.NativeAot;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The Unix form of upstream .NET for TargetOS=witos (plan phase R, RFC 0015) on a Linux host: the pinned dotnet/runtime
/// checkout, a work tree with the witos patch set applied, the runtime's own build script and the measure of the patch
/// set against the FreeBSD and Haiku ports. Step R1.1 builds System.Private.CoreLib for witos-x64 and witos-arm64 and
/// checks the platform's name in it.
/// </summary>
internal static class RuntimeWitos
{
    #region Constants

    /// <summary>
    /// The pin: the commit, the bytes of every upstream file the patch set changes and the ports' budget.
    /// </summary>
    public const string LOCK = "build/runtime/runtime.lock.json";

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
            "src.libraries.Microsoft.NETCore.Platforms.src.PortableRuntimeIdentifierGraph.json"
    };

    #endregion

    #region Functions

    /// <summary>
    /// Applies the patch set to the pinned tree and builds System.Private.CoreLib for TargetOS=witos (plan step R1.1).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">x64 or arm64.</param>
    public static async Task BuildCoreLibAsync(string root, KernelArchitecture architecture)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("dotnet/runtime builds for TargetOS=witos on a Linux host (plan steps T2.1a, R1.1).");
        var pin = ReadLock(root);
        var checkout = await PrepareCheckoutAsync(root, pin);
        var tree = await PrepareTreeAsync(root, checkout, pin);
        var build = await Processes.RunAsync(Path.Combine(tree, "build.sh"),
            ["clr.corelib", "-os", "witos", "-arch", architecture.Name, "-c", "Release"], tree, 3600);
        if (build.TimedOut || build.ExitCode != 0)
            throw new InvalidOperationException($"build.sh failed (exit {build.ExitCode}, timeout={build.TimedOut}).\n" +
                $"{Tail(build.Output)}\n{Tail(build.Error)}");
        var corelib = Path.Combine(tree, "artifacts", "bin", "coreclr", $"witos.{architecture.Name}.Release", "IL",
            "System.Private.CoreLib.dll");
        if (!HasUserString(corelib, "WITOS"))
            throw new InvalidDataException($"{corelib} does not name its platform WITOS.");
        Console.WriteLine($"System.Private.CoreLib for witos-{architecture.Name}: OperatingSystem names WITOS ({corelib}).");
        Console.WriteLine(await MeasureAsync(root, pin));
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
