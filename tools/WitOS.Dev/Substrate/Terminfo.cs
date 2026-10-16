using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;

namespace WitOS.Dev.Substrate;

/// <summary>
/// The terminfo database in the .NET install (plan step C1.3): System.Console on Unix reads the TERM entry's
/// capabilities from /usr/share/terminfo, and PSReadLine needs them. The release src/Substrate/ncurses.lock.json pins
/// by the SHA-256 of its tarball gives misc/terminfo.src, extracted under .tools/&lt;name&gt; by <c>setup</c>; the Linux
/// host's tic compiles the entries the install carries, as a distribution's ncurses-base compiles them, and the process
/// environment names TERM. Never a system copy of terminfo.
/// </summary>
internal static class Terminfo
{
    #region Constants

    /// <summary>
    /// The pin.
    /// </summary>
    public const string LOCK = "src/Substrate/ncurses.lock.json";

    /// <summary>
    /// Where the entries are in the install: the database's default directory, which System.Console reads.
    /// </summary>
    public const string DIRECTORY = "usr/share/terminfo";

    /// <summary>
    /// The terminal the process environment names: what the runner's emulator and a desktop terminal are.
    /// </summary>
    public const string TERM = "xterm-256color";

    // The entries the install carries: TERM, the entry it builds on, and the plain one.
    private static readonly string[] ENTRIES = [TERM, "xterm", "vt100"];

    private const string SOURCE = "misc/terminfo.src";

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the pin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    public static async Task<NcursesPin> ReadPinAsync(string root)
    {
        var pin = JsonSerializer.Deserialize<NcursesPin>(await File.ReadAllTextAsync(Path.Combine(root, LOCK)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing ncurses pin.");
        if (!Regex.IsMatch(pin.Name, @"^ncurses-[0-9]+\.[0-9]+$") || !Regex.IsMatch(pin.Sha256, "^[0-9a-f]{64}$") ||
            pin.Url != $"https://ftp.gnu.org/gnu/ncurses/{pin.Name}.tar.gz")
            throw new InvalidDataException("Unsupported ncurses pin.");
        return pin;
    }

    /// <summary>
    /// Downloads the pinned tarball when missing, verifies its SHA-256 and extracts terminfo.src once.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The path of terminfo.src.</returns>
    public static async Task<string> PrepareAsync(string root)
    {
        var pin = await ReadPinAsync(root);
        var tarball = await Toolchain.RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", pin.Name + ".tar.gz"), pin.Url, pin.Sha256);
        var source = SourcePath(root, pin);
        if (File.Exists(Stamp(root, pin)) && File.Exists(source))
            return source;
        var tree = Path.Combine(root, ".tools", pin.Name);
        if (Directory.Exists(tree))
            Directory.Delete(tree, recursive: true);
        Directory.CreateDirectory(tree);
        await Processes.RequireSuccessAsync(Toolchain.Tar(), ["-xzf", tarball, "-C", tree, $"{pin.Name}/{SOURCE}"], root);
        if (!File.Exists(source))
            throw new InvalidDataException($"The {pin.Name} tarball holds no {SOURCE}.");
        await File.WriteAllTextAsync(Stamp(root, pin), pin.Sha256 + "\n");
        return source;
    }

    /// <summary>
    /// The entries for the install, compiled by the Linux host's tic once per pin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Package paths under <see cref="DIRECTORY"/> and the files to place there.</returns>
    /// <exception cref="InvalidOperationException">setup has not run, or this is not the Linux host.</exception>
    public static async Task<IReadOnlyList<(string Name, string Source)>> PackageFilesAsync(string root)
    {
        var pin = await ReadPinAsync(root);
        var source = SourcePath(root, pin);
        if (!File.Exists(Stamp(root, pin)) || !File.Exists(source))
            throw new InvalidOperationException($"{pin.Name} is not prepared. Run: dotnet run --project tools/WitOS.Dev -- setup");
        var output = Path.Combine(root, "artifacts", "terminfo", pin.Name);
        var stamp = Path.Combine(output, "stamp.txt");
        var expected = string.Join(',', ENTRIES) + " " + pin.Sha256 + "\n";
        if (!File.Exists(stamp) || await File.ReadAllTextAsync(stamp) != expected)
        {
            if (OperatingSystem.IsWindows())
                throw new InvalidOperationException("terminfo is compiled by the Linux host's tic: the .NET install is built there (runtime-witos).");
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
            Directory.CreateDirectory(output);
            // -x keeps the user-defined capabilities xterm-256color carries; -e names the entries, with what they build on.
            await Processes.RequireSuccessAsync("tic", ["-x", "-o", output, "-e", string.Join(',', ENTRIES), source], output);
            foreach (var entry in ENTRIES)
                if (!File.Exists(Path.Combine(output, entry[..1], entry)))
                    throw new InvalidDataException($"tic left no {entry} in {output}.");
            await File.WriteAllTextAsync(stamp, expected);
        }
        return [.. ENTRIES.Select(entry => ($"{DIRECTORY}/{entry[..1]}/{entry}", Path.Combine(output, entry[..1], entry)))];
    }

    private static string SourcePath(string root, NcursesPin pin) => Path.Combine(root, ".tools", pin.Name, pin.Name, SOURCE.Replace('/', Path.DirectorySeparatorChar));

    private static string Stamp(string root, NcursesPin pin) => Path.Combine(root, ".tools", pin.Name) + ".verified-" + pin.Sha256[..16];

    #endregion
}

/// <summary>
/// The pin of ncurses: the release's name, its tarball's URL and SHA-256, and why it is there.
/// </summary>
internal sealed record NcursesPin(string Name, string Url, string Sha256, string Purpose);
