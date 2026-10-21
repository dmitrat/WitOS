using System.IO.Compression;
using System.Xml.Linq;
using WitOS.Dev.Host;

namespace WitOS.Dev.Runtime;

/// <summary>
/// A base application published as a .NET tool package (ADR 0027, plan step C2.5): the package of nuget.org the catalog
/// names, downloaded once into .tools/downloads and verified by its SHA-256, whose tools/&lt;framework&gt;/any directory is
/// the application as the SDK installs a portable tool; its DotnetToolSettings.xml must name each command of the catalog
/// with the same entry point, run by dotnet. A tool published per RID (.NET 10's layout) names its portable package,
/// &lt;id&gt;.any, in the catalog.
/// </summary>
internal static class AppToolPackage
{
    #region Constants

    private const string SETTINGS = "DotnetToolSettings.xml";

    #endregion

    #region Functions

    /// <summary>
    /// The application's files, by their paths in its directory.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="entry">The catalog entry, of the tool kind.</param>
    /// <returns>Paths in the application's directory and the host's files.</returns>
    /// <exception cref="InvalidDataException">The package holds no portable tool, or its commands differ from the catalog's.</exception>
    public static async Task<IReadOnlyList<(string Path, string Source)>> FilesAsync(string root, AppEntry entry)
    {
        var directory = await ExtractAsync(root, new AppPackage(entry.Source.Package!, entry.Version, entry.Source.Sha256!));
        var tool = Path.Combine(directory, "tools", "net" + entry.Framework.Split(' ')[1], "any");
        var settings = Path.Combine(tool, SETTINGS);
        if (!File.Exists(settings))
            throw new InvalidDataException($"{entry.Source.Package} {entry.Version} holds no portable tool at {Path.GetRelativePath(directory, settings)}.");
        RequireCommands(entry, XDocument.Load(settings));
        return [.. Directory.EnumerateFiles(tool, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => (Path.GetRelativePath(tool, path).Replace('\\', '/'), path))];
    }

    /// <summary>
    /// A NuGet package of nuget.org, downloaded once into .tools/downloads, verified by its SHA-256 and extracted once
    /// under .tools/apps.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="package">The package.</param>
    /// <returns>The directory of its files.</returns>
    public static async Task<string> ExtractAsync(string root, AppPackage package)
    {
        var id = package.Id.ToLowerInvariant();
        var file = $"{id}.{package.Version}.nupkg";
        var archive = await Toolchain.RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", file),
            $"https://api.nuget.org/v3-flatcontainer/{id}/{package.Version}/{file}", package.Sha256);
        var directory = Path.Combine(root, ".tools", "apps", $"{id}.{package.Version}");
        var stamp = directory + ".verified-" + package.Sha256[..16];
        if (!File.Exists(stamp))
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            ZipFile.ExtractToDirectory(archive, directory);
            await File.WriteAllTextAsync(stamp, package.Sha256 + "\n");
        }
        return directory;
    }

    /// <summary>
    /// A package's files as a restore leaves them in the global packages folder, by their paths from the folder:
    /// &lt;id&gt;/&lt;version&gt;/... in lower case, the package's own files, less its signature and its OPC parts.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="package">The package.</param>
    /// <returns>Paths in the global packages folder and the host's files.</returns>
    public static async Task<IReadOnlyList<(string Path, string Source)>> PackageFolderFilesAsync(string root, AppPackage package)
    {
        var directory = await ExtractAsync(root, package);
        var prefix = $"{package.Id.ToLowerInvariant()}/{package.Version}";
        return [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
            .Where(path => path is not (".signature.p7s" or "[Content_Types].xml") && !path.StartsWith("_rels/", StringComparison.Ordinal) &&
                !path.StartsWith("package/", StringComparison.Ordinal))
            .Select(path => ($"{prefix}/{path}", Path.Combine(directory, path)))];
    }

    /// <summary>
    /// Requires the tool's settings to name every command of the catalog with its entry point, run by dotnet.
    /// </summary>
    /// <param name="entry">The catalog entry.</param>
    /// <param name="settings">The package's DotnetToolSettings.xml.</param>
    /// <exception cref="InvalidDataException">A command is missing, has another entry point or another runner.</exception>
    public static void RequireCommands(AppEntry entry, XDocument settings)
    {
        var commands = settings.Descendants("Command").ToDictionary(command => (string?)command.Attribute("Name") ?? "",
            command => ((string?)command.Attribute("EntryPoint"), (string?)command.Attribute("Runner")), StringComparer.Ordinal);
        foreach (var command in entry.Commands)
        {
            if (!commands.TryGetValue(command.Name, out var tool) || tool.Item1 != command.Assembly || tool.Item2 != "dotnet")
                throw new InvalidDataException($"{entry.Id}: the tool's settings name no command {command.Name} of {command.Assembly} run by dotnet.");
        }
    }

    #endregion
}
