using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The ASP.NET Core shared framework in the .NET install (plan step W3.2, Implementation Strategy §67, W0 decision 9):
/// Microsoft.AspNetCore.App built from the dotnet/aspnetcore commit build/runtime/aspnetcore.lock.json pins, the release
/// that pairs with the runtime's, by the repository's own build on the Linux host with ReadyToRun compilation off, so that
/// every assembly is IL alone and the one framework serves every ISA under CoreCLR's JIT. Upstream publishes the framework
/// as runtime packs per RID, whose assemblies carry the RID's ReadyToRun code over their IL: CoreCLR refuses such an image
/// on another machine (COR_E_BADIMAGEFORMAT, with no fall back to the IL), and no pack exists for riscv64. The install lays
/// the framework under /dotnet/shared/Microsoft.AspNetCore.App/&lt;version&gt;, where hostfxr resolves an application's
/// framework reference, with the runtimeconfig the build writes and a deps.json the tool writes as it writes
/// Microsoft.NETCore.App's (R5).
/// </summary>
internal static class AspNetCoreFramework
{
    #region Constants

    /// <summary>
    /// The pin.
    /// </summary>
    public const string LOCK = "build/runtime/aspnetcore.lock.json";

    /// <summary>
    /// The framework's name, its directory under /dotnet/shared.
    /// </summary>
    public const string FRAMEWORK = "Microsoft.AspNetCore.App";

    /// <summary>
    /// The license the pin names, which the repository's LICENSE.txt states.
    /// </summary>
    public const string LICENSE = "MIT";

    // The repository's build of the runtime pack alone, without the native, JavaScript and Java parts, with ReadyToRun off
    // (CrossgenOutput), the release's stable version (DotNetFinalVersionKind), one RID for the pack's path, which the IL
    // does not depend on, and without NuGet's audit, which reads the advisories of the day of the restore and must not decide
    // whether the pin builds (R8.1).
    private const string PROJECT = "src/Framework/App.Runtime/src/Microsoft.AspNetCore.App.Runtime.sfxproj";

    private const string RID = "linux-x64";

    private static readonly string[] BUILD_OPTIONS =
    [
        "-c", "Release", "--no-build-nodejs", "--no-build-java", "--no-build-native",
        "/p:CrossgenOutput=false", "/p:DotNetFinalVersionKind=release", $"/p:TargetRuntimeIdentifier={RID}", "/p:NuGetAudit=false"
    ];

    // The fewest assemblies the framework holds: the release has 141, and a build that produced fewer is not the framework.
    private const int MINIMUM_ASSEMBLIES = 100;

    private const int BUILD_SECONDS = 5400;

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the pin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    public static AspNetCorePin ReadPin(string root)
    {
        var pin = JsonSerializer.Deserialize<AspNetCorePin>(File.ReadAllText(Path.Combine(root, LOCK)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing ASP.NET Core pin.");
        if (pin.Repository != "https://github.com/dotnet/aspnetcore" || !Regex.IsMatch(pin.Version, "^[0-9]+\\.[0-9]+\\.[0-9]+$") ||
            pin.Tag != $"v{pin.Version}" || pin.Branch != $"release/{pin.Version[..pin.Version.LastIndexOf('.')]}" ||
            !Regex.IsMatch(pin.Commit, "^[0-9a-f]{40}$") || pin.License != LICENSE ||
            pin.Submodules.Any(submodule => !Regex.IsMatch(submodule.Value, "^[0-9a-f]{40}$") || submodule.Key.Contains("..")))
            throw new InvalidDataException("Unsupported ASP.NET Core pin.");
        return pin;
    }

    /// <summary>
    /// Builds the framework once for the pin and the options on the Linux host (plan step W3.2): the pinned commit fetched
    /// shallow with its submodule under .tools/upstream, the repository's own build of the runtime pack, and the pack's
    /// assemblies, runtimeconfig and versions file extracted under artifacts/aspnetcore/&lt;version&gt;; every assembly must
    /// be IL alone, without ReadyToRun code.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The framework's directory under artifacts.</returns>
    /// <exception cref="PlatformNotSupportedException">The host is not Linux.</exception>
    /// <exception cref="InvalidDataException">The build left no pack, or one that is not the framework.</exception>
    public static async Task<string> BuildAsync(string root)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("dotnet/aspnetcore's shared framework builds on a Linux host (plan step W3.2).");
        var pin = ReadPin(root);
        var directory = Directory_(root, pin);
        var stamp = Path.Combine(directory, "stamp.txt");
        var expected = $"{pin.Commit}\n{string.Join(' ', BUILD_OPTIONS)}\n";
        if (File.Exists(stamp) && await File.ReadAllTextAsync(stamp) == expected && Directory.Exists(Path.Combine(directory, "lib")))
            return directory;
        var checkout = await PrepareCheckoutAsync(root, pin);
        Console.WriteLine($"Building {FRAMEWORK} {pin.Version} from dotnet/aspnetcore {pin.Commit[..12]} (IL alone)...");
        var build = await Processes.RunAsync(Path.Combine(checkout, "eng", "build.sh"),
            [.. BUILD_OPTIONS, "--projects", Path.Combine(checkout, PROJECT)], checkout, BUILD_SECONDS,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1" });
        if (build.TimedOut || build.ExitCode != 0)
            throw new InvalidOperationException($"dotnet/aspnetcore's build of {FRAMEWORK} failed (exit {build.ExitCode}, timeout={build.TimedOut}).\n" +
                $"{Tail(build.Output)}\n{Tail(build.Error)}");
        await Git.RequireCleanAsync(checkout);
        var package = Path.Combine(checkout, "artifacts", "packages", "Release", "Shipping", $"{FRAMEWORK}.Runtime.{RID}.{pin.Version}.nupkg");
        if (!File.Exists(package))
            throw new InvalidDataException($"dotnet/aspnetcore's build left no {package}.");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        var lib = Path.Combine(directory, "lib");
        Directory.CreateDirectory(lib);
        var prefix = $"runtimes/{RID}/lib/net{pin.Version[..pin.Version.IndexOf('.')]}.0/";
        using (var archive = ZipFile.OpenRead(package))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && !entry.FullName[prefix.Length..].Contains('/'))
                    entry.ExtractToFile(Path.Combine(lib, entry.FullName[prefix.Length..]), overwrite: false);
                else if (entry.FullName == $"{FRAMEWORK}.versions.txt")
                    entry.ExtractToFile(Path.Combine(directory, "versions.txt"), overwrite: false);
            }
        }
        var assemblies = Directory.GetFiles(lib, "*.dll");
        if (assemblies.Length < MINIMUM_ASSEMBLIES || !File.Exists(Path.Combine(directory, "versions.txt")) ||
            !File.Exists(Path.Combine(lib, $"{FRAMEWORK}.runtimeconfig.json")))
            throw new InvalidDataException($"{package} holds {assemblies.Length} assemblies, or no versions file or runtimeconfig; it is not the framework.");
        foreach (var assembly in assemblies)
        {
            if (!IsIlOnly(assembly))
                throw new InvalidDataException($"{assembly} carries ReadyToRun code or is not IL alone; the build was to leave it out.");
        }
        await File.WriteAllTextAsync(stamp, expected);
        Console.WriteLine($"{FRAMEWORK} {pin.Version}: {assemblies.Length} assemblies, IL alone ({lib}).");
        return directory;
    }

    /// <summary>
    /// The framework in the install (W3.2): its assemblies, the runtimeconfig the build wrote and the deps.json the tool
    /// writes for the architecture, under dotnet/shared/Microsoft.AspNetCore.App/&lt;version&gt;; the build must be the pin's
    /// and its runtimeconfig must name the install's Microsoft.NETCore.App.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario, where the deps.json is written.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    /// <exception cref="InvalidOperationException">The framework was not built.</exception>
    /// <exception cref="InvalidDataException">The build is not the pin's or names another runtime.</exception>
    public static async Task<IReadOnlyList<(string Name, string Source)>> FrameworkFilesAsync(string root, string output, KernelArchitecture architecture)
    {
        var pin = ReadPin(root);
        var directory = Directory_(root, pin);
        var lib = Path.Combine(directory, "lib");
        if (!Directory.Exists(lib) || !File.Exists(Path.Combine(directory, "versions.txt")))
            throw new InvalidOperationException($"{FRAMEWORK} {pin.Version} is not built under {directory}. Run: dotnet run --project tools/WitOS.Dev -- runtime-witos");
        var versions = (await File.ReadAllLinesAsync(Path.Combine(directory, "versions.txt"))).Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        if (versions.Count != 2 || versions[0] != pin.Commit || versions[1] != pin.Version)
            throw new InvalidDataException($"{directory} holds a build of {string.Join(' ', versions)}, not of {pin.Commit[..12]} {pin.Version}.");
        var runtimeconfig = Path.Combine(lib, $"{FRAMEWORK}.runtimeconfig.json");
        var runtime = RuntimeWitos.ReadLock(root);
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(runtimeconfig)))
        {
            var framework = document.RootElement.GetProperty("runtimeOptions").GetProperty("framework");
            if (framework.GetProperty("name").GetString() != "Microsoft.NETCore.App" || framework.GetProperty("version").GetString() != runtime.ProductVersion)
                throw new InvalidDataException($"{runtimeconfig} names {framework}, not the install's Microsoft.NETCore.App {runtime.ProductVersion}.");
        }
        var files = Directory.GetFiles(lib, "*.dll").Order(StringComparer.Ordinal).Select(file => (Name: Path.GetFileName(file), Source: file)).ToList();
        var depsDirectory = Path.Combine(output, "aspnetcore");
        Directory.CreateDirectory(depsDirectory);
        var deps = Path.Combine(depsDirectory, $"{FRAMEWORK}.deps.json");
        await File.WriteAllTextAsync(deps, RuntimeWitos.FrameworkDeps(FRAMEWORK, architecture, pin.Version, files));
        var prefix = $"dotnet/shared/{FRAMEWORK}/{pin.Version}/";
        return
        [
            .. files.Select(file => (prefix + file.Name, file.Source)),
            (prefix + $"{FRAMEWORK}.runtimeconfig.json", runtimeconfig), (prefix + $"{FRAMEWORK}.deps.json", deps)
        ];
    }

    /// <summary>
    /// Whether an assembly is IL alone: the CLI header's ILONLY flag set and no managed native header, which a ReadyToRun
    /// image has.
    /// </summary>
    /// <param name="path">The assembly.</param>
    /// <returns>True for IL alone.</returns>
    public static bool IsIlOnly(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new PEReader(stream);
        var header = reader.PEHeaders.CorHeader;
        return header is not null && header.Flags.HasFlag(CorFlags.ILOnly) && header.ManagedNativeHeaderDirectory.Size == 0;
    }

    #endregion

    #region Tools

    // The pinned checkout under .tools/upstream: the commit alone, fetched shallow, with the submodule the build needs, as
    // the runtime's checkout is prepared; the build writes into the checkout's ignored artifacts and .dotnet directories,
    // and nothing else of it changes.
    private static async Task<string> PrepareCheckoutAsync(string root, AspNetCorePin pin)
    {
        var checkout = Path.Combine(root, ".tools", "upstream", $"aspnetcore-{pin.Version}");
        if (!Directory.Exists(Path.Combine(checkout, ".git")))
        {
            if (Directory.Exists(checkout) && Directory.EnumerateFileSystemEntries(checkout).Any())
                throw new InvalidOperationException($"{checkout} exists without Git metadata; refusing to overwrite it.");
            Directory.CreateDirectory(checkout);
            Console.WriteLine($"Fetching dotnet/aspnetcore {pin.Tag} ({pin.Commit[..12]})...");
            await Git.RunAsync(checkout, ["init", "-q"]);
            await Git.RunAsync(checkout, ["remote", "add", "origin", pin.Repository + ".git"]);
            await Git.RunAsync(checkout, ["fetch", "-q", "--depth=1", "origin", pin.Commit], 1800);
            await Git.RunAsync(checkout, ["checkout", "-q", "--detach", pin.Commit], 600);
            foreach (var submodule in pin.Submodules.Keys)
                await Git.RunAsync(checkout, ["submodule", "update", "-q", "--init", "--depth=1", "--", submodule], 1800);
        }
        var revision = (await Git.RunAsync(checkout, ["rev-parse", "HEAD"])).Trim();
        var remote = (await Git.RunAsync(checkout, ["remote", "get-url", "origin"])).Trim().TrimEnd('/');
        if (revision != pin.Commit || (remote != pin.Repository && remote != pin.Repository + ".git"))
            throw new InvalidDataException($"{checkout} is not {pin.Repository} at {pin.Commit}; it was not changed.");
        foreach (var (submodule, commit) in pin.Submodules)
        {
            var head = (await Git.RunAsync(Path.Combine(checkout, submodule), ["rev-parse", "HEAD"])).Trim();
            if (head != commit)
                throw new InvalidDataException($"{checkout}'s {submodule} is at {head}, not at {commit}.");
        }
        await Git.RequireCleanAsync(checkout);
        return checkout;
    }

    private static string Directory_(string root, AspNetCorePin pin) => Path.Combine(root, "artifacts", "aspnetcore", pin.Version);

    private static string Tail(string text)
    {
        var lines = text.Split('\n');
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - 40)));
    }

    #endregion
}

/// <summary>
/// The pinned dotnet/aspnetcore commit the shared framework is built from.
/// </summary>
/// <param name="Repository">Repository URL.</param>
/// <param name="Branch">The release branch the tag belongs to, such as release/10.0.</param>
/// <param name="Tag">Release tag.</param>
/// <param name="Version">The framework's version, the tag without its v.</param>
/// <param name="Commit">Commit of the tag.</param>
/// <param name="Submodules">The submodules the build needs, each by its path and the commit the tag names.</param>
/// <param name="License">The license, which the repository's LICENSE.txt states.</param>
/// <param name="Purpose">Why the install carries it.</param>
internal sealed record AspNetCorePin(string Repository, string Branch, string Tag, string Version, string Commit,
    IReadOnlyDictionary<string, string> Submodules, string License, string Purpose);
