using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WitOS.Apps;

/// <summary>
/// The layer of base applications in a directory of the file system (ADR 0027 decisions 2, 3 and 5; plan step C4.3):
/// &lt;root&gt;/&lt;id&gt;/&lt;version&gt;/ with the files and witos-app.json, &lt;root&gt;/&lt;id&gt;/current naming the version in
/// use, and &lt;root&gt;/bin/&lt;command&gt; the launchers. An installation from a feed verifies the package's SHA-256,
/// extracts it into a hidden directory beside the versions, writes the manifest last and renames the directory into
/// place (RFC 0010 §124), then makes the version current; using an installed version rewrites the launchers and current,
/// each through a new file renamed over the old, so that a reader sees the old or the new and never half of one. No
/// installation database: the layer is its directories.
/// </summary>
public sealed class AppsDirectory
{
    #region Constants

    /// <summary>
    /// The manifest of a version.
    /// </summary>
    public const string MANIFEST = "witos-app.json";

    /// <summary>
    /// The file that names an application's version in use.
    /// </summary>
    public const string CURRENT = "current";

    /// <summary>
    /// The launchers' directory under the layer's root.
    /// </summary>
    public const string BIN = "bin";

    /// <summary>
    /// Where the guest's layer lies: the data volume's apps directory.
    /// </summary>
    public const string GUEST_ROOT = "/data/apps";

    private const string PARTIAL = ".partial";

    private const UnixFileMode EXECUTABLE = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
        UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    #endregion

    #region Constructors

    /// <summary>
    /// The layer at a directory.
    /// </summary>
    /// <param name="root">The layer's root, /data/apps in the guest.</param>
    public AppsDirectory(string root)
    {
        Root = root;
    }

    #endregion

    #region Functions

    /// <summary>
    /// The applications the layer holds: the directories with at least one installed version, by identity.
    /// </summary>
    /// <returns>The identities.</returns>
    public IReadOnlyList<string> Applications() =>
        !Directory.Exists(Root)
            ? []
            : [.. Directory.EnumerateDirectories(Root).Select(Path.GetFileName).OfType<string>()
                .Where(name => name != BIN && !name.StartsWith('.') && Versions(name).Count > 0).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The installed versions of an application: its directories that hold a manifest, oldest first.
    /// </summary>
    /// <param name="id">The application's identity.</param>
    /// <returns>The versions.</returns>
    public IReadOnlyList<string> Versions(string id)
    {
        var directory = Path.Combine(Root, id);
        return !Directory.Exists(directory)
            ? []
            : [.. Directory.EnumerateDirectories(directory).Select(Path.GetFileName).OfType<string>()
                .Where(name => !name.StartsWith('.') && File.Exists(Path.Combine(directory, name, MANIFEST))).Order(AppVersion.ORDER)];
    }

    /// <summary>
    /// The version in use of an application, or null.
    /// </summary>
    /// <param name="id">The application's identity.</param>
    /// <returns>The version current names.</returns>
    public string? Current(string id)
    {
        var current = Path.Combine(Root, id, CURRENT);
        return File.Exists(current) ? File.ReadAllText(current).Trim() : null;
    }

    /// <summary>
    /// An installed version's manifest.
    /// </summary>
    /// <param name="id">The application's identity.</param>
    /// <param name="version">The version.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidOperationException">The layer has no such version.</exception>
    /// <exception cref="InvalidDataException">The manifest is not valid or names another version.</exception>
    public AppManifest Manifest(string id, string version)
    {
        var path = Path.Combine(Root, id, version, MANIFEST);
        if (!File.Exists(path))
            throw new InvalidOperationException($"The layer has no {id} {version}.");
        var manifest = AppManifest.Parse(File.ReadAllText(path));
        if (manifest.Id != id || manifest.Version != version)
            throw new InvalidDataException($"The manifest of {id} {version} names {manifest.Id} {manifest.Version}.");
        return manifest;
    }

    /// <summary>
    /// Installs a feed's version unless the layer has it, then makes it the version in use. The package's bytes must
    /// have the SHA-256 the entry names; a failed installation leaves the layer as it was.
    /// </summary>
    /// <param name="entry">The feed's entry.</param>
    /// <param name="apphost">The apphost's bytes, from which the launchers are written.</param>
    /// <param name="report">Told each step, for a screen or a log.</param>
    /// <param name="cancellationToken">Cancels the installation before the rename.</param>
    /// <returns>Whether the version was written; false when the layer had it.</returns>
    /// <exception cref="InvalidDataException">The package's SHA-256 differs from the entry's, an entry of the zip leaves the
    /// version's directory, or a command's assembly is missing.</exception>
    public async Task<bool> InstallAsync(FeedEntry entry, byte[] apphost, Action<string>? report = null, CancellationToken cancellationToken = default)
    {
        var manifest = entry.Manifest;
        var id = manifest.Id;
        var version = manifest.Version;
        var target = Path.Combine(Root, id, version);
        var written = !File.Exists(Path.Combine(target, MANIFEST));
        if (written)
        {
            report?.Invoke($"Verifying {id} {version}");
            var sha256 = await AppFeed.HashAsync(entry.Package, cancellationToken);
            if (sha256 != entry.Sha256)
                throw new InvalidDataException($"The package of {id} {version} has the SHA-256 {sha256}, its entry names {entry.Sha256}; nothing was installed.");
            var partial = Path.Combine(Root, id, "." + version + PARTIAL);
            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true);
            Directory.CreateDirectory(partial);
            try
            {
                await ExtractAsync(entry.Package, partial, id, version, report, cancellationToken);
                if (manifest.Commands.FirstOrDefault(command => !File.Exists(Path.Combine(partial, command.Assembly))) is { } missing)
                    throw new InvalidDataException($"The package of {id} {version} holds no {missing.Assembly} for {missing.Name}; nothing was installed.");
                // The manifest last, then the rename: a version is installed when its directory holds its manifest.
                await File.WriteAllTextAsync(Path.Combine(partial, MANIFEST), manifest.Json, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                Directory.Move(partial, target);
            }
            catch
            {
                if (Directory.Exists(partial))
                    Directory.Delete(partial, recursive: true);
                throw;
            }
        }
        Use(id, version, apphost);
        report?.Invoke(written ? $"Installed {id} {version}" : $"{id} {version} was installed; it is in use");
        return written;
    }

    /// <summary>
    /// Makes an installed version the one in use: its commands' launchers, then current. This is the update's last step
    /// and the rollback.
    /// </summary>
    /// <param name="id">The application's identity.</param>
    /// <param name="version">The installed version.</param>
    /// <param name="apphost">The apphost's bytes.</param>
    /// <exception cref="InvalidOperationException">The layer has no such version.</exception>
    public void Use(string id, string version, byte[] apphost)
    {
        var manifest = Manifest(id, version);
        var bin = Path.Combine(Root, BIN);
        Directory.CreateDirectory(bin);
        foreach (var command in manifest.Commands)
        {
            var launcher = Path.Combine(bin, command.Name);
            var fresh = Path.Combine(bin, "." + command.Name + PARTIAL);
            File.WriteAllBytes(fresh, AppLauncher.Write(apphost, AppLauncher.AppPath(id, version, command.Assembly)));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(fresh, EXECUTABLE);
            File.Move(fresh, launcher, overwrite: true);
        }
        var current = Path.Combine(Root, id, CURRENT);
        var next = Path.Combine(Root, id, "." + CURRENT + PARTIAL);
        File.WriteAllText(next, version + "\n");
        File.Move(next, current, overwrite: true);
        WriteSdkRoots();
    }

    /// <summary>
    /// The global.json in the directory above the layer (C2.5, /data/global.json in the guest): the roots of every
    /// application whose current version holds sdk/, in the applications' order, so that dotnet finds the SDK a base
    /// application brings; removed once no current version holds one.
    /// </summary>
    public void WriteSdkRoots()
    {
        var roots = new List<string>();
        foreach (var id in Applications())
        {
            if (Current(id) is { } version && Directory.Exists(Path.Combine(Root, id, version, SdkRoots.SDK)))
                roots.Add(SdkRoots.Root(Path.GetFileName(Root), id, version));
        }
        var above = Path.GetDirectoryName(Path.GetFullPath(Root))!;
        var file = Path.Combine(above, SdkRoots.FILE);
        if (roots.Count == 0)
        {
            File.Delete(file);
            return;
        }
        var fresh = Path.Combine(above, "." + SdkRoots.FILE + PARTIAL);
        File.WriteAllText(fresh, SdkRoots.GlobalJson(roots));
        File.Move(fresh, file, overwrite: true);
    }

    // Extracts a package's zip, each entry checked to stay within the directory and none of them a manifest; a mode the
    // zip records is applied where the system has modes.
    private static async Task ExtractAsync(string package, string directory, string id, string version, Action<string>? report,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(package);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var files = zip.Entries.Where(item => !item.FullName.EndsWith('/')).ToList();
        var done = 0;
        foreach (var item in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AppManifest.IsRelative(item.FullName) || item.FullName == MANIFEST)
                throw new InvalidDataException($"The package of {id} {version} holds {item.FullName}, which may not be written; nothing was installed.");
            var path = Path.Combine(directory, item.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var from = await item.OpenAsync(cancellationToken))
            await using (var to = File.Create(path))
                await from.CopyToAsync(to, cancellationToken);
            var mode = (item.ExternalAttributes >> 16) & 0x1FF;
            if (mode != 0 && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, (UnixFileMode)mode);
            done++;
            if (done % 8 == 0 || done == files.Count)
                report?.Invoke($"Extracting {id} {version}: {done} of {files.Count} files");
        }
    }

    #endregion

    #region Properties

    /// <summary>
    /// The layer's root.
    /// </summary>
    public string Root { get; }

    #endregion
}
