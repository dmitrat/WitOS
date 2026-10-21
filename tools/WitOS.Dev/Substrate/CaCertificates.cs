using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;

namespace WitOS.Dev.Substrate;

/// <summary>
/// The root certificates of the system layer (plan step W3.1): Mozilla's CA bundle as curl's project extracts it from
/// the NSS store, one PEM file src/Substrate/cacert.lock.json pins by its SHA-256, downloaded once into .tools/downloads
/// by <c>setup</c>. The .NET install lays it at /etc/ssl/certs/ca-certificates.crt: System.Security.Cryptography's root
/// store on Unix reads every file of /etc/ssl/certs when SSL_CERT_DIR and SSL_CERT_FILE name nothing, and OpenSSL's own
/// default directory lies under /etc/ssl, the configuration directory the system layer's build names. There are no
/// symbolic links in the directory (D7 is open): one bundle file serves, as the store provider parses every certificate
/// of a file.
/// </summary>
internal static class CaCertificates
{
    #region Constants

    /// <summary>
    /// The pin.
    /// </summary>
    public const string LOCK = "src/Substrate/cacert.lock.json";

    /// <summary>
    /// The license the pin names: the extract's, the same as Mozilla's certdata.txt it is made from, which curl's page
    /// states (the file's header names its source, not the license).
    /// </summary>
    public const string LICENSE = "MPL-2.0";

    // The header's lines that make the file the bundle: curl's title and Mozilla as the source of the data.
    private const string TITLE_LINE = "## Bundle of CA Root Certificates";
    private const string SOURCE_LINE = "## Certificate data from Mozilla as of:";

    /// <summary>
    /// The bundle's path in the install.
    /// </summary>
    public const string PACKAGE_PATH = "etc/ssl/certs/ca-certificates.crt";

    // The fewest certificates a bundle may hold: Mozilla's store has held more than a hundred for years, and a file with
    // fewer is not the bundle.
    private const int MINIMUM_CERTIFICATES = 100;

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the pin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    public static CaCertificatesPin ReadPin(string root)
    {
        var pin = JsonSerializer.Deserialize<CaCertificatesPin>(File.ReadAllText(Path.Combine(root, LOCK)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing CA bundle pin.");
        var date = Regex.Match(pin.Name, "^cacert-([0-9]{4}-[0-9]{2}-[0-9]{2})$");
        if (!date.Success || !Regex.IsMatch(pin.Sha256, "^[0-9a-f]{64}$") || pin.License != LICENSE ||
            pin.Url != $"https://curl.se/ca/{pin.Name}.pem")
            throw new InvalidDataException("Unsupported CA bundle pin.");
        return pin;
    }

    /// <summary>
    /// Downloads the pinned bundle when missing, verifies its SHA-256 and that it is the bundle: curl's title line, Mozilla
    /// named as the data's source and at least <see cref="MINIMUM_CERTIFICATES"/> certificates.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The bundle's path.</returns>
    public static async Task<string> PrepareAsync(string root)
    {
        var pin = ReadPin(root);
        var bundle = await Toolchain.RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", pin.Name + ".pem"), pin.Url, pin.Sha256);
        var text = await File.ReadAllTextAsync(bundle);
        if (!text.Contains(TITLE_LINE, StringComparison.Ordinal) || !text.Contains(SOURCE_LINE, StringComparison.Ordinal))
            throw new InvalidDataException($"{pin.Name} is not curl's bundle of Mozilla's root certificates: its header lacks \"{TITLE_LINE}\" or \"{SOURCE_LINE}\".");
        var certificates = Regex.Matches(text, "-----BEGIN CERTIFICATE-----").Count;
        if (certificates < MINIMUM_CERTIFICATES)
            throw new InvalidDataException($"{pin.Name} holds {certificates} certificates, fewer than a bundle's {MINIMUM_CERTIFICATES}.");
        return bundle;
    }

    /// <summary>
    /// Requires the bundle <c>setup</c> downloads.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The bundle's path.</returns>
    public static string Require(string root)
    {
        var pin = ReadPin(root);
        var bundle = Path.Combine(root, ".tools", "downloads", pin.Name + ".pem");
        if (!File.Exists(bundle))
            throw new InvalidOperationException($"Pinned {pin.Name} is missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
        return bundle;
    }

    /// <summary>
    /// The bundle in the system layer's package.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Package path and the file to place there.</returns>
    public static IReadOnlyList<(string Name, string Source)> PackageFiles(string root) => [(PACKAGE_PATH, Require(root))];

    #endregion
}

/// <summary>
/// The pinned CA bundle the .NET install carries as its root store.
/// </summary>
/// <param name="Name">The extract, cacert-&lt;date&gt;.</param>
/// <param name="Url">The PEM file of the extract.</param>
/// <param name="Sha256">The SHA-256 of the file.</param>
/// <param name="License">The license, which the file's header states.</param>
/// <param name="Purpose">Why the system layer carries it.</param>
internal sealed record CaCertificatesPin(string Name, string Url, string Sha256, string License, string Purpose);
