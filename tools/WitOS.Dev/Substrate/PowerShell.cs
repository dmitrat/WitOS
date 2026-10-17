using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Substrate;

/// <summary>
/// Upstream PowerShell 7 on WitOS (RFC 0020, plan step C1.3): the release src/Substrate/powershell.lock.json pins by the
/// SHA-256 of its framework-dependent archive, IL alone, is downloaded once into .tools/downloads and extracted under
/// .tools/&lt;name&gt; by <c>setup</c>, which also checks out PowerShell-Native at the pinned commit; the pinned clang
/// builds libpsl-native from that source unchanged for each ISA against the sysroot, and the scenario lays the archive's
/// files with the library on the data volume, where <c>dotnet pwsh.dll</c> runs them unchanged. Nothing of PowerShell is
/// patched: it is an application (RFC 0020 section 59).
/// </summary>
internal static class PowerShell
{
    #region Constants

    /// <summary>
    /// The pin.
    /// </summary>
    public const string LOCK = "src/Substrate/powershell.lock.json";

    /// <summary>
    /// The native library's file name, which pwsh's P/Invokes name and CoreCLR finds beside pwsh.dll.
    /// </summary>
    public const string NATIVE_LIBRARY = "libpsl-native.so";

    // The library's sources within PowerShell-Native, and the configure results its pal_config.h.in asks for: WitOS
    // has sysconf and no sys/sysctl.h, as Linux with musl.
    private const string NATIVE_SOURCES = "src/libpsl-native/src";

    private const string PAL_CONFIG = "#pragma once\n#define HAVE_SYS_SYSCTL_H 0\n#define HAVE_SYSCONF 1\n";

    private static readonly Regex COMMIT = new("^[0-9a-f]{40}$");

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the pin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    /// <exception cref="InvalidDataException">The pin is not a PowerShell release's musl fxdependent archive at its
    /// canonical URL with a PowerShell-Native commit.</exception>
    public static async Task<PowerShellPin> ReadPinAsync(string root)
    {
        var pin = JsonSerializer.Deserialize<PowerShellPin>(await File.ReadAllTextAsync(Path.Combine(root, LOCK)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing PowerShell pin.");
        if (!Regex.IsMatch(pin.Version, @"^7\.[0-9]+\.[0-9]+$") || pin.Name != $"powershell-{pin.Version}" ||
            pin.Archive != $"{pin.Name}-linux-x64-musl-noopt-fxdependent.tar.gz" ||
            pin.Url != $"https://github.com/PowerShell/PowerShell/releases/download/v{pin.Version}/{pin.Archive}" ||
            !Regex.IsMatch(pin.Sha256, "^[0-9a-f]{64}$") || pin.NativeRepository != "https://github.com/PowerShell/PowerShell-Native" ||
            !COMMIT.IsMatch(pin.NativeCommit))
            throw new InvalidDataException("Unsupported PowerShell pin.");
        return pin;
    }

    /// <summary>
    /// Downloads the pinned archive when missing, verifies its SHA-256 and extracts it once, and checks out
    /// PowerShell-Native at the pinned commit once (the setup command).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The extracted archive's directory.</returns>
    public static async Task<string> PrepareAsync(string root)
    {
        var pin = await ReadPinAsync(root);
        var archive = await Toolchain.RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", pin.Archive), pin.Url, pin.Sha256);
        var tree = ArchiveDirectory(root, pin);
        if (!File.Exists(Stamp(root, pin)) || !File.Exists(Path.Combine(tree, "pwsh.dll")))
        {
            if (Directory.Exists(tree))
                Directory.Delete(tree, recursive: true);
            Directory.CreateDirectory(tree);
            await Processes.RequireSuccessAsync(Toolchain.Tar(), ["-xzf", archive, "-C", tree], root);
            if (!File.Exists(Path.Combine(tree, "pwsh.dll")) || !File.Exists(Path.Combine(tree, "pwsh.runtimeconfig.json")))
                throw new InvalidDataException($"The {pin.Archive} archive holds no pwsh.dll with its runtimeconfig.");
            await File.WriteAllTextAsync(Stamp(root, pin), pin.Sha256 + "\n");
        }
        await PrepareNativeSourcesAsync(root, pin);
        return tree;
    }

    /// <summary>
    /// Requires what <c>setup</c> prepares.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    /// <exception cref="InvalidOperationException">setup has not run since the pin changed.</exception>
    public static async Task<PowerShellPin> RequireAsync(string root)
    {
        var pin = await ReadPinAsync(root);
        if (!File.Exists(Stamp(root, pin)) || !File.Exists(Path.Combine(ArchiveDirectory(root, pin), "pwsh.dll")) ||
            !Directory.Exists(Path.Combine(NativeSourceDirectory(root, pin), ".git")))
            throw new InvalidOperationException($"PowerShell {pin.Version} is not prepared. Run: dotnet run --project tools/WitOS.Dev -- setup");
        return pin;
    }

    /// <summary>
    /// Builds libpsl-native for the architecture from the pinned PowerShell-Native source, unchanged, with the pinned
    /// clang against the sysroot, once per pin and sysroot; the pal_config.h the source expects is written beside it.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The library's path.</returns>
    public static async Task<string> BuildNativeAsync(string root, KernelArchitecture architecture)
    {
        var pin = await RequireAsync(root);
        var sysroot = await Sysroot.BuildAsync(root, architecture);
        var sources = Path.Combine(NativeSourceDirectory(root, pin), NATIVE_SOURCES);
        var files = Directory.GetFiles(sources, "*.cpp").Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
            throw new InvalidDataException($"PowerShell-Native holds no sources in {sources}.");
        var output = Path.Combine(root, "artifacts", architecture.Name, "powershell");
        var library = Path.Combine(output, NATIVE_LIBRARY);
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            [pin.NativeCommit, PAL_CONFIG, .. Sysroot.DriverOptions(root, architecture), .. files.Select(Path.GetFileName)!]))));
        var stampPath = Path.Combine(output, "stamp.txt");
        if (File.Exists(library) && File.Exists(stampPath) && await File.ReadAllTextAsync(stampPath) == stamp)
            return library;
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "pal_config.h"), PAL_CONFIG);
        // PowerShell-Native's own options less its hardening flags, which need glibc's fortified headers: C++11, -Wall
        // -Werror, position-independent, one shared object with no undefined symbol.
        await Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            "--driver-mode=g++", .. Sysroot.DriverOptions(root, architecture), "-std=c++11", "-O2", "-Wall", "-Werror", "-fPIC", "-shared",
            $"-Wl,-soname,{NATIVE_LIBRARY}", "-Wl,--no-undefined", "-I" + sources, "-I" + output, .. files, "-o", library
        ], root);
        await File.WriteAllTextAsync(stampPath, stamp);
        _ = sysroot;
        return library;
    }

    /// <summary>
    /// What a data volume holds for PowerShell: every file of the archive under the directory and the library built
    /// for the architecture beside pwsh.dll.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="directory">The directory on the volume, without slashes.</param>
    /// <returns>The volume's paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Path, string Source)>> VolumeFilesAsync(string root, KernelArchitecture architecture, string directory)
    {
        var pin = await RequireAsync(root);
        var tree = ArchiveDirectory(root, pin);
        var files = Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => ($"{directory}/{Path.GetRelativePath(tree, file).Replace('\\', '/')}", file)).ToList();
        files.Add(($"{directory}/{NATIVE_LIBRARY}", await BuildNativeAsync(root, architecture)));
        // The session banner's profile (C1.4): $PSHOME/profile.ps1, which every interactive session runs; WitOS's, not
        // the archive's, and the one file beside the archive's.
        files.Add(($"{directory}/profile.ps1", Path.Combine(root, Runtime.ConsoleLogo.PROFILE)));
        return files;
    }

    // PowerShell-Native at the pinned commit, as libc-test is checked out: one commit fetched, detached, verified.
    private static async Task PrepareNativeSourcesAsync(string root, PowerShellPin pin)
    {
        var source = NativeSourceDirectory(root, pin);
        if (!Directory.Exists(Path.Combine(source, ".git")))
        {
            if (Directory.Exists(source) && Directory.EnumerateFileSystemEntries(source).Any())
                throw new InvalidOperationException("PowerShell-Native source directory exists without Git metadata; refusing to overwrite it.");
            Directory.CreateDirectory(source);
            await Git.RunAsync(source, ["init"]);
            await Git.RunAsync(source, ["remote", "add", "origin", pin.NativeRepository + ".git"]);
            await Git.RunAsync(source, ["fetch", "--depth=1", "origin", pin.NativeCommit], 600);
            await Git.RunAsync(source, ["checkout", "--detach", pin.NativeCommit], 600);
        }
        var revision = (await Git.RunAsync(source, ["rev-parse", "HEAD"])).Trim();
        if (revision != pin.NativeCommit)
            throw new InvalidDataException("Existing PowerShell-Native checkout differs from the pinned commit; it was not changed.");
        await Git.RequireCleanAsync(source);
        if (!Directory.Exists(Path.Combine(source, NATIVE_SOURCES)))
            throw new InvalidDataException($"PowerShell-Native at {pin.NativeCommit} holds no {NATIVE_SOURCES}.");
    }

    private static string ArchiveDirectory(string root, PowerShellPin pin) => Path.Combine(root, ".tools", pin.Name);

    private static string NativeSourceDirectory(string root, PowerShellPin pin) => Path.Combine(root, ".tools", "powershell-native", pin.NativeCommit);

    private static string Stamp(string root, PowerShellPin pin) => ArchiveDirectory(root, pin) + ".verified-" + pin.Sha256[..16];

    #endregion
}

/// <summary>
/// The pin of PowerShell: the release, its framework-dependent archive's URL and SHA-256, PowerShell-Native's
/// repository, commit and nearest tag, and why it is there.
/// </summary>
internal sealed record PowerShellPin(string Name, string Version, string Archive, string Url, string Sha256, string NativeRepository,
    string NativeCommit, string NativeTag, string Purpose);
