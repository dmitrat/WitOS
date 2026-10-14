using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;

namespace WitOS.Dev.Substrate;

/// <summary>
/// The IANA time zone database in the .NET install (plan step N4.1, RFC 0015 section 8). The release
/// src/Substrate/tzdata.lock.json pins by the SHA-256 of its tarball is downloaded once into .tools/downloads and
/// extracted under .tools/&lt;name&gt; by <c>setup</c>; the Linux host's zic compiles its sources into the TZif files
/// of /usr/share/zoneinfo, as tzdata's own Makefile does (TDATA: the continents, etcetera, factory and backward;
/// -b fat, so that every reader finds every transition; -L /dev/null, POSIX time without leap seconds), and the install
/// carries them with zone.tab, zone1970.tab and iso3166.tab, which TimeZoneInfo reads on Unix. Never a system copy of
/// zoneinfo.
/// </summary>
internal static class Tzdata
{
    #region Constants

    /// <summary>
    /// The pin.
    /// </summary>
    public const string LOCK = "src/Substrate/tzdata.lock.json";

    /// <summary>
    /// Where the TZif files are in the install: TimeZoneInfo's default directory on Unix.
    /// </summary>
    public const string DIRECTORY = "usr/share/zoneinfo";

    // The sources zic compiles, in tzdata's Makefile's order (TDATA = YDATA NDATA BACKWARD).
    private static readonly string[] SOURCES =
        ["africa", "antarctica", "asia", "australasia", "europe", "northamerica", "southamerica", "etcetera", "factory", "backward"];

    // The tables the install carries beside the TZif files.
    private static readonly string[] TABLES = ["zone.tab", "zone1970.tab", "iso3166.tab"];

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the pin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    public static async Task<TzdataPin> ReadPinAsync(string root)
    {
        var pin = JsonSerializer.Deserialize<TzdataPin>(await File.ReadAllTextAsync(Path.Combine(root, LOCK)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing tzdata pin.");
        if (!Regex.IsMatch(pin.Name, "^tzdata20[0-9]{2}[a-z]$") || !Regex.IsMatch(pin.Sha256, "^[0-9a-f]{64}$") ||
            pin.Url != $"https://data.iana.org/time-zones/releases/{pin.Name}.tar.gz")
            throw new InvalidDataException("Unsupported tzdata pin.");
        return pin;
    }

    /// <summary>
    /// Downloads the pinned tarball when missing, verifies its SHA-256 and extracts it once; a tree extracted from a
    /// tarball of another hash is replaced.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The directory holding the sources.</returns>
    public static async Task<string> PrepareAsync(string root)
    {
        var pin = await ReadPinAsync(root);
        var tarball = await Toolchain.RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", pin.Name + ".tar.gz"), pin.Url, pin.Sha256);
        var tree = SourceDirectory(root, pin);
        if (File.Exists(Stamp(root, pin)) && Directory.Exists(tree))
            return tree;
        if (Directory.Exists(tree))
            Directory.Delete(tree, recursive: true);
        foreach (var stale in Directory.GetFiles(Path.GetDirectoryName(tree)!, Path.GetFileName(tree) + ".verified-*"))
            File.Delete(stale);
        Directory.CreateDirectory(tree);
        await Processes.RequireSuccessAsync(Toolchain.Tar(), ["-xzf", tarball, "-C", tree], root);
        var missing = SOURCES.Concat(TABLES).Where(file => !File.Exists(Path.Combine(tree, file))).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"The {pin.Name} tarball does not hold {string.Join(", ", missing)}.");
        await File.WriteAllTextAsync(Stamp(root, pin), pin.Sha256 + "\n");
        return tree;
    }

    /// <summary>
    /// Requires the sources <c>setup</c> extracts.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="pin">The pin.</param>
    public static void Require(string root, TzdataPin pin)
    {
        if (!File.Exists(Stamp(root, pin)) || !Directory.Exists(SourceDirectory(root, pin)))
            throw new InvalidOperationException($"Pinned {pin.Name} is missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
    }

    /// <summary>
    /// The TZif files and tables of the install under usr/share/zoneinfo, compiled once for the pin by the Linux host's
    /// zic into artifacts/tzdata/&lt;name&gt;.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> PackageFilesAsync(string root)
    {
        var pin = await ReadPinAsync(root);
        Require(root, pin);
        var zoneinfo = await BuildAsync(root, pin);
        var files = new List<(string Name, string Source)>();
        foreach (var file in Directory.EnumerateFiles(zoneinfo, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            files.Add(($"{DIRECTORY}/{Path.GetRelativePath(zoneinfo, file).Replace('\\', '/')}", file));
        foreach (var table in TABLES)
            files.Add(($"{DIRECTORY}/{table}", Path.Combine(SourceDirectory(root, pin), table)));
        return files;
    }

    // zic's output for the pin, made once: the stamp names the pin's hash.
    private static async Task<string> BuildAsync(string root, TzdataPin pin)
    {
        var output = Path.Combine(root, "artifacts", "tzdata", pin.Name);
        var zoneinfo = Path.Combine(output, "zoneinfo");
        var stamp = Path.Combine(output, "stamp.txt");
        if (File.Exists(stamp) && await File.ReadAllTextAsync(stamp) == pin.Sha256 + "\n" && Directory.Exists(zoneinfo))
            return zoneinfo;
        if (OperatingSystem.IsWindows())
            throw new InvalidOperationException("tzdata is compiled by the Linux host's zic: the .NET install is built there (runtime-witos).");
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
        Directory.CreateDirectory(zoneinfo);
        var sources = SourceDirectory(root, pin);
        await Processes.RequireSuccessAsync("zic", ["-b", "fat", "-L", "/dev/null", "-d", zoneinfo, .. SOURCES.Select(source => Path.Combine(sources, source))],
            sources);
        if (!File.Exists(Path.Combine(zoneinfo, "Europe", "Berlin")) || !File.Exists(Path.Combine(zoneinfo, "UTC")))
            throw new InvalidDataException($"zic left no Europe/Berlin or UTC in {zoneinfo}.");
        await File.WriteAllTextAsync(stamp, pin.Sha256 + "\n");
        return zoneinfo;
    }

    private static string SourceDirectory(string root, TzdataPin pin) => Path.Combine(root, ".tools", pin.Name);

    private static string Stamp(string root, TzdataPin pin) => SourceDirectory(root, pin) + ".verified-" + pin.Sha256[..16];

    #endregion
}

/// <summary>
/// The pin of tzdata: the release's name, its tarball's URL and SHA-256, and why it is there.
/// </summary>
internal sealed record TzdataPin(string Name, string Url, string Sha256, string Purpose);
