using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;

namespace WitOS.Dev.Substrate;

/// <summary>
/// ICU's public headers in the system layer's sysroot (plan step R1.2b): the unicode/ headers of ICU4C's common and i18n
/// libraries from the release src/Substrate/icu.lock.json pins by the SHA-256 of its source tarball, downloaded once into
/// .tools/downloads and extracted under .tools/&lt;name&gt; by <c>setup</c>. .NET's globalization shim compiles against
/// them as it does on Linux and loads ICU's libraries at run time; WitOS has no ICU library yet, so the shim reports its
/// absence and .NET runs in invariant mode (RFC 0015 section 8).
/// </summary>
internal static class IcuHeaders
{
    #region Constants

    /// <summary>
    /// The pin.
    /// </summary>
    public const string LOCK = "src/Substrate/icu.lock.json";

    // The libraries whose headers the sysroot carries: libicuuc's and libicui18n's, the two the shim loads.
    private static readonly string[] LIBRARIES = ["common", "i18n"];

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the pin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    public static async Task<IcuPin> ReadPinAsync(string root)
    {
        var pin = JsonSerializer.Deserialize<IcuPin>(await File.ReadAllTextAsync(Path.Combine(root, LOCK)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing ICU pin.");
        var release = Regex.Match(pin.Name, "^icu4c-([0-9]+)\\.([0-9]+)$");
        if (!release.Success || !Regex.IsMatch(pin.Sha256, "^[0-9a-f]{64}$") || pin.Url !=
            $"https://github.com/unicode-org/icu/releases/download/release-{release.Groups[1].Value}.{release.Groups[2].Value}/{pin.Name}-sources.tgz")
            throw new InvalidDataException("Unsupported ICU pin.");
        return pin;
    }

    /// <summary>
    /// The unicode/ directories of the pinned libraries, which the sysroot's usr/include/unicode joins.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="pin">The pin.</param>
    /// <returns>The directories.</returns>
    public static string[] HeaderDirectories(string root, IcuPin pin) =>
        LIBRARIES.Select(library => Path.Combine(Directory(root, pin), "icu", "source", library, "unicode")).ToArray();

    /// <summary>
    /// Downloads the pinned source tarball when missing, verifies its SHA-256 and extracts the headers once; a tree
    /// extracted from a tarball of another hash is replaced.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The directory holding the tree.</returns>
    public static async Task<string> PrepareAsync(string root)
    {
        var pin = await ReadPinAsync(root);
        var tarball = await Toolchain.RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", pin.Name + "-sources.tgz"),
            pin.Url, pin.Sha256);
        var tree = Directory(root, pin);
        var stamp = tree + ".verified-" + pin.Sha256;
        if (File.Exists(stamp) && System.IO.Directory.Exists(tree))
            return tree;
        if (System.IO.Directory.Exists(tree))
            System.IO.Directory.Delete(tree, recursive: true);
        System.IO.Directory.CreateDirectory(tree);
        await Processes.RequireSuccessAsync(Toolchain.Tar(),
            ["-xf", tarball, "-C", tree, .. LIBRARIES.Select(library => $"icu/source/{library}/unicode")], root);
        if (HeaderDirectories(root, pin).Any(directory => !System.IO.Directory.Exists(directory) ||
                !System.IO.Directory.EnumerateFiles(directory, "*.h").Any()))
            throw new InvalidDataException($"The {pin.Name} tarball does not hold the unicode/ headers of {string.Join(" and ", LIBRARIES)}.");
        await File.WriteAllTextAsync(stamp, pin.Sha256 + "\n");
        return tree;
    }

    /// <summary>
    /// Requires the headers <c>setup</c> extracts.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="pin">The pin.</param>
    public static void Require(string root, IcuPin pin)
    {
        if (!File.Exists(Directory(root, pin) + ".verified-" + pin.Sha256) || HeaderDirectories(root, pin).Any(directory => !System.IO.Directory.Exists(directory)))
            throw new InvalidOperationException($"Pinned {pin.Name} headers are missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
    }

    #endregion

    #region Tools

    private static string Directory(string root, IcuPin pin) => Path.Combine(root, ".tools", pin.Name);

    #endregion
}

/// <summary>
/// The pinned ICU4C release whose headers the sysroot carries.
/// </summary>
/// <param name="Name">The release, icu4c-&lt;major&gt;.&lt;minor&gt;.</param>
/// <param name="Url">The source tarball of the release.</param>
/// <param name="Sha256">The SHA-256 of the tarball.</param>
/// <param name="Purpose">Why the sysroot carries the headers.</param>
internal sealed record IcuPin(string Name, string Url, string Sha256, string Purpose);
