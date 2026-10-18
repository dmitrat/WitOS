using System.Text;
using System.Text.Json;
using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The layer of base applications on the data volume (ADR 0027, plan step C4.1): an application is the directory
/// apps/&lt;id&gt;/&lt;version&gt;/ with its files as the source publishes them and witos-app.json, its catalog entry;
/// apps/&lt;id&gt;/current names the version in use (a file, since the volume has no links until D7); a command is
/// apps/bin/&lt;command&gt;, upstream's witos apphost written for the version's assembly as the SDK writes a .NET tool's shim
/// (the assembly's path, relative to the launcher, in the apphost's placeholder). An installation puts a new version
/// beside the old and makes it current; using another installed version rewrites the launchers, which is the rollback.
/// No installation database exists: the layer is its directories and the launchers follow from the manifests.
/// </summary>
internal static class AppsLayer
{
    #region Constants

    /// <summary>
    /// The layer's directory on the volume.
    /// </summary>
    public const string ROOT = "apps";

    /// <summary>
    /// The launchers' directory on the volume, which /etc/environment puts on PATH.
    /// </summary>
    public const string BIN = ROOT + "/bin";

    /// <summary>
    /// The manifest of a version: its catalog entry.
    /// </summary>
    public const string MANIFEST = "witos-app.json";

    /// <summary>
    /// The file that names an application's version in use.
    /// </summary>
    public const string CURRENT = "current";

    /// <summary>
    /// Where the guest mounts the volume that holds the layer.
    /// </summary>
    public const string MOUNT = "/data";

    // The apphost's placeholder for its application's path: the SHA-256 of "foobar" in UTF-8, which the SDK's HostWriter
    // replaces, followed by zeros to the length of the host's buffer.
    private const string PLACEHOLDER = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

    private const int MAX_APP_PATH_BYTES = 1024;

    // The user's global packages folder on the volume: HOME is /data/home (N4.1).
    private const string PACKAGES = "home/.nuget/packages";

    private static readonly JsonSerializerOptions MANIFEST_OPTIONS = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    #endregion

    #region Functions

    /// <summary>
    /// The directory of an application's version on the volume.
    /// </summary>
    public static string VersionDirectory(string id, string version) => $"{ROOT}/{id}/{version}";

    /// <summary>
    /// A command's launcher as the guest names it: /data/apps/bin/&lt;command&gt;.
    /// </summary>
    public static string GuestCommand(string command) => $"{MOUNT}/{BIN}/{command}";

    /// <summary>
    /// A launcher: the apphost with the application's path in its placeholder, as the SDK writes an apphost.
    /// </summary>
    /// <param name="apphost">The apphost's bytes.</param>
    /// <param name="appPath">The assembly's path, relative to the launcher's directory or absolute.</param>
    /// <returns>The launcher's bytes.</returns>
    /// <exception cref="InvalidDataException">The apphost holds no placeholder, or the path does not fit it.</exception>
    public static byte[] Launcher(ReadOnlySpan<byte> apphost, string appPath)
    {
        var placeholder = Encoding.UTF8.GetBytes(PLACEHOLDER);
        var at = apphost.IndexOf(placeholder);
        if (at < 0 || apphost[(at + 1)..].IndexOf(placeholder) >= 0)
            throw new InvalidDataException("The apphost holds no single application path placeholder.");
        var path = Encoding.UTF8.GetBytes(appPath);
        if (path.Length == 0 || path.Length >= MAX_APP_PATH_BYTES || path.Contains((byte)0) || at + MAX_APP_PATH_BYTES > apphost.Length)
            throw new InvalidDataException($"The application path {appPath} does not fit the apphost's placeholder.");
        var launcher = apphost.ToArray();
        path.CopyTo(launcher, at);
        Array.Clear(launcher, at + path.Length, placeholder.Length - Math.Min(path.Length, placeholder.Length));
        return launcher;
    }

    /// <summary>
    /// Installs an application's version unless the layer has it already, then makes it the version in use.
    /// </summary>
    /// <param name="store">The layer's store.</param>
    /// <param name="entry">The application's catalog entry.</param>
    /// <param name="files">Its files, by their paths in the application's directory.</param>
    /// <param name="apphost">The apphost's bytes.</param>
    /// <param name="scratch">A directory of the host for the launchers written.</param>
    /// <param name="packages">The NuGet packages it finds in the global packages folder, by their paths from the folder.</param>
    /// <returns>Whether the version was written; false when the layer had it.</returns>
    public static async Task<bool> InstallAsync(ILayerStore store, AppEntry entry, IReadOnlyList<(string Path, string Source)> files, byte[] apphost,
        string scratch, IReadOnlyList<(string Path, string Source)>? packages = null)
    {
        var directory = VersionDirectory(entry.Id, entry.Version);
        var written = !await store.ExistsAsync($"{directory}/{MANIFEST}");
        if (written)
        {
            foreach (var (path, source) in files)
                await store.WriteFileAsync($"{directory}/{path}", source);
            // The packages it finds in the user's global packages folder (C2.5), each version written once.
            foreach (var package in (packages ?? []).GroupBy(file => file.Path[..file.Path.IndexOf('/', file.Path.IndexOf('/') + 1)]))
            {
                if (await store.ExistsAsync($"{PACKAGES}/{package.Key}"))
                    continue;
                foreach (var (path, source) in package)
                    await store.WriteFileAsync($"{PACKAGES}/{path}", source);
            }
            // The manifest last: a version is installed when its manifest is there.
            await store.WriteTextAsync($"{directory}/{MANIFEST}", JsonSerializer.Serialize(entry, MANIFEST_OPTIONS) + "\n");
        }
        await UseAsync(store, entry.Id, entry.Version, apphost, scratch);
        return written;
    }

    /// <summary>
    /// Makes an installed version the one in use: its name in current and its commands' launchers.
    /// </summary>
    /// <param name="store">The layer's store.</param>
    /// <param name="id">The application's identity.</param>
    /// <param name="version">The installed version.</param>
    /// <param name="apphost">The apphost's bytes.</param>
    /// <param name="scratch">A directory of the host for the launchers written.</param>
    /// <exception cref="InvalidOperationException">The layer has no such version.</exception>
    public static async Task UseAsync(ILayerStore store, string id, string version, byte[] apphost, string scratch)
    {
        var manifest = await store.ReadTextAsync($"{VersionDirectory(id, version)}/{MANIFEST}")
            ?? throw new InvalidOperationException($"The layer has no {id} {version}.");
        var entry = AppCatalog.Parse(manifest, id);
        Directory.CreateDirectory(scratch);
        foreach (var command in entry.Commands)
        {
            var launcher = Path.Combine(scratch, $"{id}-{version}-{command.Name}");
            await File.WriteAllBytesAsync(launcher, Launcher(apphost, $"../{id}/{version}/{command.Assembly}"));
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(launcher, (UnixFileMode)Convert.ToInt32("755", 8));
            await store.WriteFileAsync($"{BIN}/{command.Name}", launcher);
        }
        await store.WriteTextAsync($"{ROOT}/{id}/{CURRENT}", version + "\n");
        await store.FlushAsync();
    }

    /// <summary>
    /// The installed versions of an application: the directories that hold a manifest.
    /// </summary>
    public static async Task<IReadOnlyList<string>> VersionsAsync(ILayerStore store, string id)
    {
        var versions = new List<string>();
        foreach (var name in await store.ListAsync($"{ROOT}/{id}"))
        {
            if (name != CURRENT && await store.ExistsAsync($"{VersionDirectory(id, name)}/{MANIFEST}"))
                versions.Add(name);
        }
        return versions;
    }

    /// <summary>
    /// The version in use of an application, or null.
    /// </summary>
    public static async Task<string?> CurrentAsync(ILayerStore store, string id) => (await store.ReadTextAsync($"{ROOT}/{id}/{CURRENT}"))?.Trim();

    /// <summary>
    /// The files of a new data disk's layer: every application of the catalog installed in a staging directory, by their
    /// paths on the volume.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture, whose apphost and native parts the layer takes.</param>
    /// <param name="staging">A directory of the host for the layer, made anew.</param>
    /// <returns>The volume's paths and the host's files.</returns>
    public static async Task<IReadOnlyList<(string Path, string Source)>> VolumeFilesAsync(string root, KernelArchitecture architecture, string staging)
    {
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        var layer = Path.Combine(staging, "volume");
        var store = new DirectoryLayerStore(layer);
        var apphost = await File.ReadAllBytesAsync(ApphostPath(root, architecture));
        foreach (var entry in await AppCatalog.ReadAsync(root))
            await InstallAsync(store, entry, await ApplicationFilesAsync(root, architecture, entry), apphost, Path.Combine(staging, "launchers"),
                await PackageFilesAsync(root, entry));
        return [.. Directory.EnumerateFiles(layer, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => (Path.GetRelativePath(layer, file).Replace('\\', '/'), file))];
    }

    /// <summary>
    /// An application's files, by their paths in its directory: what its source publishes and its native part.
    /// </summary>
    /// <exception cref="NotSupportedException">A kind of source no base application uses yet.</exception>
    public static async Task<IReadOnlyList<(string Path, string Source)>> ApplicationFilesAsync(string root, KernelArchitecture architecture, AppEntry entry) =>
        entry switch
        {
            { Id: PowerShell.CATALOG_ID } => await PowerShell.ApplicationFilesAsync(root, architecture),
            { Source.Kind: AppCatalog.TOOL } => await AppToolPackage.FilesAsync(root, entry),
            _ => throw new NotSupportedException($"{entry.Id}: the layer lays no {entry.Source.Kind} source yet; its step brings it.")
        };

    /// <summary>
    /// The NuGet packages an application finds in the global packages folder, by their paths from the folder.
    /// </summary>
    public static async Task<IReadOnlyList<(string Path, string Source)>> PackageFilesAsync(string root, AppEntry entry)
    {
        var files = new List<(string Path, string Source)>();
        foreach (var package in entry.Packages ?? [])
            files.AddRange(await AppToolPackage.PackageFolderFilesAsync(root, package));
        return files;
    }

    /// <summary>
    /// The witos apphost of an architecture, which host.native built in the runtime's tree (R5).
    /// </summary>
    /// <exception cref="InvalidOperationException">The tree holds no apphost.</exception>
    public static string ApphostPath(string root, KernelArchitecture architecture)
    {
        var apphost = Path.Combine(root, "artifacts", "runtime-witos", "src", "artifacts", "bin", $"witos-{architecture.Name}.Release", "corehost", "apphost");
        return File.Exists(apphost)
            ? apphost
            : throw new InvalidOperationException($"No witos apphost at {apphost}. Run: dotnet run --project tools/WitOS.Dev -- runtime-witos --arch {architecture.Name}");
    }

    #endregion
}
