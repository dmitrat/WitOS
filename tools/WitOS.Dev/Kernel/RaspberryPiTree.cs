using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;

namespace WitOS.Dev.Kernel;

/// <summary>
/// The Raspberry Pi 4's device tree for QEMU's raspi4b (plan step H7.2): the Raspberry Pi firmware's
/// bcm2711-rpi-4-b.dtb, pinned in build/boards/raspi4b.lock.json by the SHA-256 of pftf/RPi4's release archive, which
/// <c>setup</c> downloads once into .tools/downloads and from which it extracts the tree alone. QEMU takes the tree with
/// <c>-dtb</c>; the runner adds the entropy seed QEMU does not (<see cref="DeviceTreeBlob.WithChosenProperty"/>).
/// </summary>
internal static class RaspberryPiTree
{
    #region Constants

    /// <summary>
    /// The pin.
    /// </summary>
    public const string LOCK = "build/boards/raspi4b.lock.json";

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the pin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    /// <exception cref="InvalidDataException">The pin is not one this tool serves.</exception>
    public static RaspberryPiTreePin ReadPin(string root)
    {
        var pin = JsonSerializer.Deserialize<RaspberryPiTreePin>(File.ReadAllText(Path.Combine(root, LOCK)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing Raspberry Pi tree pin.");
        var version = Regex.Match(pin.Name, @"^RPi4_UEFI_Firmware_(v\d+\.\d+)$");
        if (!version.Success || !Regex.IsMatch(pin.Sha256, "^[0-9a-f]{64}$") || !Regex.IsMatch(pin.Tree, @"^bcm2711-[a-z0-9-]+\.dtb$") ||
            pin.Url != $"https://github.com/pftf/RPi4/releases/download/{version.Groups[1].Value}/{pin.Name}.zip")
            throw new InvalidDataException("Unsupported Raspberry Pi tree pin.");
        return pin;
    }

    /// <summary>
    /// Downloads the pinned archive when missing, verifies its SHA-256 and extracts the board's tree once.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The tree.</returns>
    public static async Task<string> PrepareAsync(string root)
    {
        var pin = ReadPin(root);
        var archive = await Toolchain.RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", pin.Name + ".zip"), pin.Url, pin.Sha256);
        var tree = TreePath(root, pin);
        if (File.Exists(Stamp(root, pin)) && File.Exists(tree))
            return tree;
        Directory.CreateDirectory(Path.GetDirectoryName(tree)!);
        using (var zip = ZipFile.OpenRead(archive))
        {
            var entry = zip.GetEntry(pin.Tree) ?? throw new InvalidDataException($"The {pin.Name} archive does not hold {pin.Tree}.");
            entry.ExtractToFile(tree, overwrite: true);
        }
        DeviceTreeBlob.Require(await File.ReadAllBytesAsync(tree));
        await File.WriteAllTextAsync(Stamp(root, pin), pin.Sha256 + "\n");
        return tree;
    }

    /// <summary>
    /// The tree <c>setup</c> extracted.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The file.</returns>
    /// <exception cref="InvalidOperationException">setup did not run.</exception>
    public static string Require(string root)
    {
        var pin = ReadPin(root);
        var tree = TreePath(root, pin);
        if (!File.Exists(Stamp(root, pin)) || !File.Exists(tree))
            throw new InvalidOperationException($"Pinned {pin.Name} is missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
        return tree;
    }

    private static string TreePath(string root, RaspberryPiTreePin pin) => Path.Combine(root, ".tools", pin.Name, pin.Tree);

    private static string Stamp(string root, RaspberryPiTreePin pin) => Path.Combine(root, ".tools", pin.Name + ".verified-" + pin.Sha256[..16]);

    #endregion
}

/// <summary>
/// The Raspberry Pi tree's pin (build/boards/raspi4b.lock.json).
/// </summary>
/// <param name="Name">The archive, RPi4_UEFI_Firmware_v&lt;version&gt;.</param>
/// <param name="Url">The archive's URL.</param>
/// <param name="Sha256">The archive's SHA-256.</param>
/// <param name="Tree">The board's tree within the archive.</param>
/// <param name="Purpose">Why the pin exists.</param>
internal sealed record RaspberryPiTreePin(string Name, string Url, string Sha256, string Tree, string Purpose);
