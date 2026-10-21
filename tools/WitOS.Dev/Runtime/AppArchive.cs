using System.Formats.Tar;
using System.IO.Compression;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// An application of the archive kind laid in part (plan step C2.5, ADR 0027): a release archive downloaded once by its
/// URL and verified by its SHA-256, extracted once under .tools/apps, of which the catalog's lay directories go into the
/// application's directory by their paths in the archive, less its leave paths. The .NET SDK is the first: its managed
/// directory and the reference pack from the SDK's own archive, without the Linux apphost template and the .NET
/// Framework test host, which are no .NET IL of this platform; PowerShell's archive goes whole through its own code.
/// </summary>
internal static class AppArchive
{
    #region Functions

    /// <summary>
    /// The application's files, by their paths in the archive.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="entry">The catalog entry, of the archive kind.</param>
    /// <param name="architecture">Target architecture, whose archive a per-RID publication takes.</param>
    /// <returns>Paths in the application's directory and the host's files.</returns>
    /// <exception cref="InvalidDataException">A lay directory holds nothing in the archive.</exception>
    public static async Task<IReadOnlyList<(string Path, string Source)>> FilesAsync(string root, AppEntry entry, KernelArchitecture architecture)
    {
        var directory = await ExtractAsync(root, entry, architecture);
        var paths = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/')).Order(StringComparer.Ordinal);
        return [.. Select(paths, entry.Source.Lay ?? [], entry.Source.Leave ?? []).Select(path => (path, Path.Combine(directory, path)))];
    }

    /// <summary>
    /// The archive's paths to lay: those under a lay directory, or all of them without one, less those under a leave
    /// directory; every lay directory must hold something.
    /// </summary>
    /// <param name="paths">The archive's files by their paths, with '/'.</param>
    /// <param name="lay">The directories to lay.</param>
    /// <param name="leave">The directories left out.</param>
    /// <returns>The paths laid, in the order given.</returns>
    /// <exception cref="InvalidDataException">A lay directory holds nothing.</exception>
    public static IReadOnlyList<string> Select(IEnumerable<string> paths, IReadOnlyList<string> lay, IReadOnlyList<string> leave)
    {
        static bool Under(string path, string directory) => path.StartsWith(directory + "/", StringComparison.Ordinal);
        var selected = paths.Where(path => (lay.Count == 0 || lay.Any(directory => Under(path, directory))) &&
            !leave.Any(directory => Under(path, directory))).ToList();
        foreach (var directory in lay)
        {
            if (!selected.Any(path => Under(path, directory)))
                throw new InvalidDataException($"The archive holds no files under {directory}.");
        }
        return selected;
    }

    /// <summary>
    /// The archive, downloaded once into .tools/downloads, verified by its SHA-256 and extracted once under .tools/apps.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="entry">The catalog entry, of the archive kind.</param>
    /// <returns>The directory of the archive's files.</returns>
    public static async Task<string> ExtractAsync(string root, AppEntry entry, KernelArchitecture architecture)
    {
        // The one archive, or the architecture's where the publication is per RID (C2.5), extracted apart.
        var source = entry.Source;
        var (url, name, sha256, directory) = source.Architectures is null
            ? (source.Url!, source.File!, source.Sha256!, Path.Combine(root, ".tools", "apps", $"{entry.Id}.{entry.Version}"))
            : source.Architectures.TryGetValue(architecture.Name, out var of)
                ? (of.Url, of.File, of.Sha256, Path.Combine(root, ".tools", "apps", $"{entry.Id}.{entry.Version}.{architecture.Name}"))
                : throw new InvalidOperationException($"{entry.Id}: no archive for {architecture.Name}; the layer of that architecture leaves it out.");
        var archive = await Toolchain.RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", name), url, sha256);
        var stamp = directory + ".verified-" + sha256[..16];
        if (!File.Exists(stamp))
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            Directory.CreateDirectory(directory);
            await using (var file = File.OpenRead(archive))
            await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                await TarFile.ExtractToDirectoryAsync(gzip, directory, overwriteFiles: false);
            }
            await File.WriteAllTextAsync(stamp, sha256 + "\n");
        }
        return directory;
    }

    #endregion
}
