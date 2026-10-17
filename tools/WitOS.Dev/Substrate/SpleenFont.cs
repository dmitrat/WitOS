using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;

namespace WitOS.Dev.Substrate;

/// <summary>
/// The bitmap font of the display's console (plan step C2.2): Spleen's 8 by 16 BDF, pinned in src/Substrate/spleen.lock.json
/// by the SHA-256 of its release tarball, downloaded once into .tools/downloads and extracted under .tools/&lt;name&gt; by
/// <c>setup</c>, which requires the font and its LICENSE (BSD 2-Clause, the license the pin names) in the tarball. The
/// boot package carries the BDF and the license under /system/fonts; the display service reads the BDF at its start, and
/// the host reads the same file to render the frame it expects of the display.
/// </summary>
internal static class SpleenFont
{
    #region Constants

    /// <summary>
    /// The pin.
    /// </summary>
    public const string LOCK = "src/Substrate/spleen.lock.json";

    /// <summary>
    /// The font the display draws with, within the release.
    /// </summary>
    public const string BDF = "spleen-8x16.bdf";

    /// <summary>
    /// The release's license file.
    /// </summary>
    public const string LICENSE_FILE = "LICENSE";

    /// <summary>
    /// The license the pin must name: the SPDX identifier Spleen's files carry.
    /// </summary>
    public const string LICENSE = "BSD-2-Clause";

    /// <summary>
    /// Where the boot package carries the font (witos/display.h names the BDF there).
    /// </summary>
    public const string PACKAGE_DIRECTORY = "system/fonts";

    private const string SPDX_LINE = "SPDX-License-Identifier: BSD-2-Clause";

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the pin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    /// <exception cref="InvalidDataException">The pin is not one this tool serves.</exception>
    public static SpleenPin ReadPin(string root)
    {
        var pin = JsonSerializer.Deserialize<SpleenPin>(File.ReadAllText(Path.Combine(root, LOCK)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing Spleen pin.");
        var version = Regex.Match(pin.Name, @"^spleen-(\d+\.\d+\.\d+)$");
        if (!version.Success || !Regex.IsMatch(pin.Sha256, "^[0-9a-f]{64}$") || pin.License != LICENSE ||
            pin.Url != $"https://github.com/fcambus/spleen/releases/download/{version.Groups[1].Value}/{pin.Name}.tar.gz")
            throw new InvalidDataException("Unsupported Spleen pin.");
        return pin;
    }

    /// <summary>
    /// Downloads the pinned tarball when missing, verifies its SHA-256 and extracts it once; the font and its license
    /// must be in it, and the BDF must state the pin's license.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The directory holding the release.</returns>
    public static async Task<string> PrepareAsync(string root)
    {
        var pin = ReadPin(root);
        var tarball = await Toolchain.RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", pin.Name + ".tar.gz"), pin.Url, pin.Sha256);
        var tree = SourceDirectory(root, pin);
        if (File.Exists(Stamp(root, pin)) && Directory.Exists(tree))
            return tree;
        if (Directory.Exists(tree))
            Directory.Delete(tree, recursive: true);
        foreach (var stale in Directory.GetFiles(Path.GetDirectoryName(tree)!, Path.GetFileName(tree) + ".verified-*"))
            File.Delete(stale);
        Directory.CreateDirectory(tree);
        await Processes.RequireSuccessAsync(Toolchain.Tar(), ["-xzf", tarball, "-C", tree, "--strip-components=1"], root);
        if (!File.Exists(Path.Combine(tree, BDF)) || !File.Exists(Path.Combine(tree, LICENSE_FILE)))
            throw new InvalidDataException($"The {pin.Name} tarball does not hold {BDF} and {LICENSE_FILE}.");
        if (!(await File.ReadAllTextAsync(Path.Combine(tree, BDF))).Contains(SPDX_LINE, StringComparison.Ordinal))
            throw new InvalidDataException($"{BDF} of {pin.Name} does not state the license the pin names ({LICENSE}).");
        await File.WriteAllTextAsync(Stamp(root, pin), pin.Sha256 + "\n");
        return tree;
    }

    /// <summary>
    /// Requires the release <c>setup</c> extracts.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    /// <exception cref="InvalidOperationException">setup did not run.</exception>
    public static SpleenPin Require(string root)
    {
        var pin = ReadPin(root);
        if (!File.Exists(Stamp(root, pin)) || !Directory.Exists(SourceDirectory(root, pin)))
            throw new InvalidOperationException($"Pinned {pin.Name} is missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
        return pin;
    }

    /// <summary>
    /// The font's BDF, as <c>setup</c> extracted it.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The file.</returns>
    public static string BdfPath(string root) => Path.Combine(SourceDirectory(root, Require(root)), BDF);

    /// <summary>
    /// The files of the boot package: the BDF and the license under system/fonts.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static IReadOnlyList<(string Name, string Source)> PackageFiles(string root)
    {
        var tree = SourceDirectory(root, Require(root));
        return [($"{PACKAGE_DIRECTORY}/{BDF}", Path.Combine(tree, BDF)), ($"{PACKAGE_DIRECTORY}/spleen-{LICENSE_FILE}", Path.Combine(tree, LICENSE_FILE))];
    }

    private static string SourceDirectory(string root, SpleenPin pin) => Path.Combine(root, ".tools", pin.Name);

    private static string Stamp(string root, SpleenPin pin) => SourceDirectory(root, pin) + ".verified-" + pin.Sha256[..16];

    #endregion
}

/// <summary>
/// The Spleen pin (src/Substrate/spleen.lock.json).
/// </summary>
/// <param name="Name">The release, spleen-&lt;version&gt;.</param>
/// <param name="Url">The tarball's URL.</param>
/// <param name="Sha256">The tarball's SHA-256.</param>
/// <param name="License">The license the files state: BSD-2-Clause.</param>
/// <param name="Purpose">Why the pin exists.</param>
internal sealed record SpleenPin(string Name, string Url, string Sha256, string License, string Purpose);
