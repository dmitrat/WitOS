using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WitOS.Apps;

/// <summary>
/// A folder feed of base applications (ADR 0027 decision 5, RFC 0010 §132; plan step C4.3): a directory on any volume —
/// another disk, the FAT volume, a USB disk later — that holds, for each application's version, its entry
/// &lt;id&gt;/&lt;version&gt;.json and its package beside it. The entry is the version's manifest (its catalog entry)
/// with the package's file and the SHA-256 of its bytes; the package is a zip of the version's files as the layer holds
/// them, less the manifest. The SHA-256 makes the package's integrity offline-verifiable; trust in whoever wrote the feed
/// waits for signatures (RFC 0010 §44–§112, M8).
/// </summary>
public static class AppFeed
{
    #region Constants

    /// <summary>
    /// An entry's extension.
    /// </summary>
    public const string ENTRY_EXTENSION = ".json";

    /// <summary>
    /// A package's extension.
    /// </summary>
    public const string PACKAGE_EXTENSION = ".zip";

    private static readonly Regex SHA256_TEXT = new("^[0-9a-f]{64}$");

    // A zip's entries carry this time, so that the same files make the same package.
    private static readonly DateTimeOffset PACKAGE_TIME = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Entries and the manifests the layer writes from them: indented, with line feeds on every system.
    private static readonly JsonSerializerOptions ENTRY_OPTIONS = new() { WriteIndented = true, NewLine = "\n" };

    #endregion

    #region Functions

    /// <summary>
    /// Reads a feed: every entry of every application's directory, each checked whole — its manifest, the identity and
    /// the version its directory and file name, a package beside it and a SHA-256 of the right form. An entry that fails
    /// a check is a problem of the reading, not an exception, so that the rest of the feed stays usable.
    /// </summary>
    /// <param name="directory">The feed's directory.</param>
    /// <returns>The entries, ordered by identity and version, and the problems.</returns>
    /// <exception cref="DirectoryNotFoundException">The feed's directory does not exist.</exception>
    public static FeedReading Read(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"No feed at {directory}.");
        var entries = new List<FeedEntry>();
        var problems = new List<string>();
        foreach (var application in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            foreach (var file in Directory.EnumerateFiles(application, "*" + ENTRY_EXTENSION).Order(StringComparer.Ordinal))
            {
                try
                {
                    entries.Add(ReadEntry(file));
                }
                catch (Exception error) when (error is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
                {
                    problems.Add($"{Path.GetFileName(application)}/{Path.GetFileName(file)}: {error.Message}");
                }
            }
        }
        return new FeedReading(
            [.. entries.OrderBy(entry => entry.Manifest.Id, StringComparer.Ordinal).ThenBy(entry => entry.Manifest.Version, AppVersion.ORDER)],
            problems);
    }

    /// <summary>
    /// Writes a version into a feed: the package of its files and the entry, the manifest with the package's SHA-256.
    /// </summary>
    /// <param name="directory">The feed's directory.</param>
    /// <param name="manifestJson">The version's manifest.</param>
    /// <param name="files">The version's files, by their paths in its directory, and the files' sources.</param>
    /// <param name="cancellationToken">Cancels the writing.</param>
    /// <returns>The entry written.</returns>
    /// <exception cref="InvalidDataException">The manifest is not valid, a path leaves the version's directory, or the files
    /// lack a command's assembly.</exception>
    public static async Task<FeedEntry> WriteAsync(string directory, string manifestJson, IReadOnlyList<(string Path, string Source)> files,
        CancellationToken cancellationToken = default)
    {
        var manifest = AppManifest.Parse(manifestJson);
        foreach (var (path, _) in files)
        {
            if (!AppManifest.IsRelative(path) || path == AppsDirectory.MANIFEST)
                throw new InvalidDataException($"The package of {manifest.Id} {manifest.Version} cannot hold {path}.");
        }
        if (manifest.Commands.FirstOrDefault(command => files.All(file => file.Path != command.Assembly)) is { } missing)
            throw new InvalidDataException($"The files of {manifest.Id} {manifest.Version} lack {missing.Assembly} for {missing.Name}.");
        var application = Path.Combine(directory, manifest.Id);
        Directory.CreateDirectory(application);
        var package = Path.Combine(application, manifest.Version + PACKAGE_EXTENSION);
        await using (var stream = File.Create(package))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (path, source) in files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                var item = zip.CreateEntry(path, CompressionLevel.Optimal);
                item.LastWriteTime = PACKAGE_TIME;
                await using var to = await item.OpenAsync(cancellationToken);
                await using var from = File.OpenRead(source);
                await from.CopyToAsync(to, cancellationToken);
            }
        }
        var sha256 = await HashAsync(package, cancellationToken);
        using var document = JsonDocument.Parse(manifestJson);
        var entry = new Dictionary<string, object>
        {
            ["manifest"] = document.RootElement,
            ["package"] = new Dictionary<string, string> { ["file"] = Path.GetFileName(package), ["sha256"] = sha256 }
        };
        var entryPath = Path.Combine(application, manifest.Version + ENTRY_EXTENSION);
        await File.WriteAllTextAsync(entryPath, JsonSerializer.Serialize(entry, ENTRY_OPTIONS) + "\n", cancellationToken);
        // As a reader of the feed will see it.
        return ReadEntry(entryPath);
    }

    /// <summary>
    /// The SHA-256 of a file's bytes, in lower-case hexadecimal.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the reading.</param>
    /// <returns>The hash.</returns>
    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static FeedEntry ReadEntry(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("manifest", out var manifestElement) ||
            manifestElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("the entry holds no manifest.");
        // The manifest the layer will hold: the entry's, written anew on its own.
        var manifest = AppManifest.Parse(JsonSerializer.Serialize(manifestElement, ENTRY_OPTIONS) + "\n");
        var application = Path.GetFileName(Path.GetDirectoryName(file)!);
        if (manifest.Id != application || manifest.Version != Path.GetFileNameWithoutExtension(file))
            throw new InvalidDataException($"the entry of {manifest.Id} {manifest.Version} lies at another application's or version's name.");
        if (!root.TryGetProperty("package", out var package) || package.ValueKind != JsonValueKind.Object ||
            !package.TryGetProperty("file", out var name) || name.ValueKind != JsonValueKind.String ||
            !package.TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"the entry of {manifest.Id} {manifest.Version} names no package and SHA-256.");
        var fileName = name.GetString()!;
        var sha256 = hash.GetString()!;
        if (!AppManifest.IsRelative(fileName) || fileName.Contains('/') || !fileName.EndsWith(PACKAGE_EXTENSION, StringComparison.Ordinal))
            throw new InvalidDataException($"the package of {manifest.Id} {manifest.Version} is no zip beside its entry.");
        if (!SHA256_TEXT.IsMatch(sha256))
            throw new InvalidDataException($"the SHA-256 of {manifest.Id} {manifest.Version} is not 64 lower-case hexadecimal digits.");
        var packagePath = Path.Combine(Path.GetDirectoryName(file)!, fileName);
        if (!File.Exists(packagePath))
            throw new InvalidDataException($"the package {fileName} of {manifest.Id} {manifest.Version} is missing.");
        return new FeedEntry(manifest, packagePath, sha256, file);
    }

    #endregion
}

/// <summary>
/// A version a feed offers.
/// </summary>
/// <param name="Manifest">Its manifest.</param>
/// <param name="Package">The package's path.</param>
/// <param name="Sha256">The SHA-256 the entry names for the package's bytes.</param>
/// <param name="Path">The entry's path.</param>
public sealed record FeedEntry(AppManifest Manifest, string Package, string Sha256, string Path);

/// <summary>
/// What a feed holds: the entries that passed their checks and the problems of the rest.
/// </summary>
/// <param name="Entries">The entries, by identity and version.</param>
/// <param name="Problems">Why the others were left out.</param>
public sealed record FeedReading(IReadOnlyList<FeedEntry> Entries, IReadOnlyList<string> Problems);
