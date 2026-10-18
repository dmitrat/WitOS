using WitOS.Apps;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// A session's data disk with the layer of base applications (ADR 0027, plan step C4.1): made with every application of
/// the catalog, or brought up to the catalog in place — a new version beside the old, its launchers written, the old
/// kept — or switched to another installed version, the rollback. The disk's ext4 volume is what the guest mounts at
/// /data; the change goes through the host's e2fsprogs, so it needs a Linux host.
/// </summary>
internal static class AppsDisk
{
    #region Constants

    /// <summary>
    /// A session's data disk: the layer, the home directory and room for what the person writes.
    /// </summary>
    public const long DISK_BYTES = 1024L << 20;

    #endregion

    #region Functions

    /// <summary>
    /// The files of a session's data volume: the layer of every application of the catalog and the home directory.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="output">A directory of the host for the layer's staging.</param>
    /// <returns>The volume's paths and the host's files.</returns>
    public static async Task<List<(string Path, string Source)>> VolumeFilesAsync(string root, KernelArchitecture architecture, string output)
    {
        var files = new List<(string Path, string Source)>(await AppsLayer.VolumeFilesAsync(root, architecture, Path.Combine(output, "layer")));
        var keep = Path.Combine(output, "keep");
        await File.WriteAllTextAsync(keep, "");
        files.Add(("home/.keep", keep));
        return files;
    }

    /// <summary>
    /// Makes the disk with the layer when it is missing; brings an existing disk's layer up to the catalog, or switches
    /// an application to an installed version; then prints each application's versions and the one in use.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="disk">The disk's image.</param>
    /// <param name="use">An application and an installed version to use instead of bringing the layer up to the catalog.</param>
    /// <returns>When the disk holds the layer.</returns>
    /// <exception cref="FileNotFoundException">A version to use is asked of a disk that does not exist.</exception>
    public static async Task RunAsync(string root, KernelArchitecture architecture, string disk, (string Id, string Version)? use)
    {
        var path = Path.GetFullPath(disk);
        var scratch = Path.Combine(root, "artifacts", architecture.Name, "apps");
        var catalog = await AppCatalog.ReadAsync(root);
        if (!File.Exists(path))
        {
            if (use is not null)
                throw new FileNotFoundException($"No data disk at {path} to switch a version on.", path);
            await RuntimeDataDisk.CreateAsync(path, files: await VolumeFilesAsync(root, architecture, scratch), bytes: DISK_BYTES);
            Console.WriteLine($"Made the data disk {path} with the layer of {catalog.Count} base application(s):");
            foreach (var entry in catalog)
                Console.WriteLine($"  {entry.Id} {entry.Version}, in use; commands {string.Join(", ", entry.Commands.Select(command => AppsLayer.GuestCommand(command.Name)))}");
            return;
        }
        var apphost = await File.ReadAllBytesAsync(AppsLayer.ApphostPath(root, architecture));
        var launchers = Path.Combine(scratch, "launchers");
        var report = new List<string>();
        await RuntimeDataDisk.EditExt4Async(path, async volume =>
        {
            var store = new Ext4LayerStore(volume, Path.Combine(scratch, "debugfs"));
            if (use is { } chosen)
                await AppsLayer.UseAsync(store, chosen.Id, chosen.Version, apphost, launchers);
            else
            {
                foreach (var entry in catalog)
                {
                    var written = await AppsLayer.InstallAsync(store, entry, await AppsLayer.ApplicationFilesAsync(root, architecture, entry), apphost,
                        launchers);
                    report.Add($"{entry.Id} {entry.Version} {(written ? "installed" : "was there")}");
                }
            }
            foreach (var entry in catalog)
                report.Add($"  {entry.Id}: versions {string.Join(", ", await AppsLayer.VersionsAsync(store, entry.Id))}; in use {await AppsLayer.CurrentAsync(store, entry.Id)}");
        });
        Console.WriteLine($"The layer of {path}:");
        foreach (var line in report)
            Console.WriteLine(line);
    }

    /// <summary>
    /// Writes a folder feed of every application of the catalog (plan step C4.3): each version's package of the files the
    /// layer would hold and its entry, the manifest with the package's SHA-256, for the installer in the guest.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture, whose native parts the packages take.</param>
    /// <param name="feed">The feed's directory.</param>
    /// <returns>When the feed holds every application.</returns>
    public static async Task WriteFeedAsync(string root, KernelArchitecture architecture, string feed)
    {
        var directory = Path.GetFullPath(feed);
        foreach (var entry in await AppCatalog.ReadAsync(root))
        {
            var written = await AppFeed.WriteAsync(directory, AppsLayer.ManifestJson(entry), await AppsLayer.ApplicationFilesAsync(root, architecture, entry));
            Console.WriteLine($"{entry.Id} {entry.Version}: {Path.GetRelativePath(directory, written.Package)}, SHA-256 {written.Sha256}");
        }
        Console.WriteLine($"Wrote the feed {directory} for witos-{architecture.Name}.");
    }

    #endregion
}
