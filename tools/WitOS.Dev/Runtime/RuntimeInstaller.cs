using WitOS.Apps;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The installer scenario (plan step C4.3, ADR 0027 decision 5): the data volume's layer holds Arlecchino.Commander's
/// release v2026.9.1, built from its source, and a folder feed on the FAT volume offers the catalog's 2026.9.19. On the
/// display's console, driven through the keyboard by QMP, the installer /usr/bin/witos-apps updates the commander from
/// the feed beside the old version, rolls back to it through its version picker and quits; its use command then puts the
/// update in use again, its list command reports the layer, and the updated commander starts through its rewritten
/// launcher. The host requires the screendump after the rollback equal to the frame it renders from the transcript and,
/// on the clean volume, current, the launcher and every file of the new version as the feed's package held them, the old
/// version kept.
/// </summary>
internal static class RuntimeInstaller
{
    #region Constants

    /// <summary>
    /// The feed's directory in the guest, on the data disk's FAT volume.
    /// </summary>
    public const string FEED = "/fat/feed";

    /// <summary>
    /// The application the scenario updates.
    /// </summary>
    public const string APPLICATION = "Arlecchino.Commander";

    /// <summary>
    /// The commander's release the layer starts with: v2026.9.1, whose Arlecchino submodule (the library's v2026.9.2) its
    /// repository still holds.
    /// </summary>
    public const string OLD_VERSION = "2026.9.1";

    private const string OLD_COMMIT = "09110d7b02a9e79678d22717f1007eaa4b26f3cf";

    private const string ARLC = "/data/apps/bin/arlc";

    private const int MEMORY_MIB = 1024;

    private const long DISK_BYTES = 1536L << 20; // the .NET SDK takes 400 MiB of the layer (C2.5); the disk is written from one array, so below 2 GiB

    // Two hours (R9.2): the guest's crossgen2 compiles the update's 34 assemblies under TCG, which on CI's x64 runner took most of
    // the hour the scenario had before, the screen's part after it still to come.
    private const int TIMEOUT_SECONDS = 7200;

    // The commander's command line, drawn once its panels are up.
    private const string COMMANDER_READY = "type a command here";

    #endregion

    #region Functions

    /// <summary>
    /// Boots the scenario on the services and the install earlier runs built.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the boot passed and the volume holds the update.</returns>
    /// <exception cref="InvalidOperationException">The screen differs from the frame the host rendered, or the volume holds
    /// another layer than the installer wrote.</exception>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var scenario = KernelImageBuilder.RUNTIME_INSTALLER_SCENARIO;
        var output = Path.Combine(root, "artifacts", architecture.Name, scenario);
        Directory.CreateDirectory(output);
        var latest = await AppCatalog.ReadAsync(root, APPLICATION);
        var package = new List<(string Name, string Source)>(await RuntimeApps.PackageAsync(root, output, architecture, [], Commands(architecture, latest.Version)));
        package.AddRange(await InstallerPackage.FilesAsync(root, architecture, output));
        var image = await KernelImageBuilder.BuildAsync(root, scenario, outputDirectory: output, architecture: architecture, package: package);

        var old = latest with { Version = OLD_VERSION, Source = latest.Source with { Commit = OLD_COMMIT } };
        var oldFiles = await AppSourceBuild.FilesAsync(root, old);
        var latestFiles = await AppSourceBuild.FilesAsync(root, latest);
        var apphost = await File.ReadAllBytesAsync(AppsLayer.ApphostPath(root, architecture));
        var dataDisk = Path.Combine(output, "data-disk.img");
        await RuntimeDataDisk.CreateAsync(dataDisk, files: await VolumeFilesAsync(output, old, oldFiles, apphost, NativeCodeBuild.For(root, architecture)), bytes: DISK_BYTES,
            fatFiles: await FeedFilesAsync(output, latest, latestFiles));

        var name = $"{architecture.Name}-{scenario}-{MEMORY_MIB}";
        var logs = Path.Combine(root, "artifacts", "logs");
        var screen = BootScenarioRunner.ScreenPath(logs, name);
        foreach (var stale in new[] { screen, BootScenarioRunner.TranscriptPath(screen) }.Where(File.Exists))
            File.Delete(stale);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest(name, MEMORY_MIB, TIMEOUT_SECONDS, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = RequiredLines(architecture, latest.Version),
            DataDisk = dataDisk,
            Display = true,
            Keyboard = true,
            Terminal = [], // the virtio console mirrors the display: its transcript is what the display drew
            Actions = Actions(latest.Version)
        });

        var screenshot = Screenshot.Read(screen);
        var written = RuntimeDisplay.TerminalBytes(await File.ReadAllTextAsync(BootScenarioRunner.TranscriptPath(screen)));
        var expected = RuntimeDisplay.Render(root, screenshot.Width, screenshot.Height, written);
        screenshot.RequireFrame(expected.Frame);
        if (!expected.Screen.Text.Contains(Used(OLD_VERSION), StringComparison.Ordinal))
            throw new InvalidOperationException($"The screen at the screendump does not say '{Used(OLD_VERSION)}': {expected.Screen.Text}");
        await RequireLayerAsync(dataDisk, architecture, latest, latestFiles, oldFiles, apphost, NativeCodeBuild.For(root, architecture));
        Console.WriteLine($"Installer on witos-{architecture.Name}: witos-apps updated {APPLICATION} {OLD_VERSION} to {latest.Version} from the feed on " +
            $"{FEED} beside the old version and rolled back on the screen through the keyboard, put the update in use again by its use command, " +
            $"and the commander started through its rewritten launcher; the screen equals the frame the host rendered, and the clean volume " +
            $"holds the {latestFiles.Count} files of the new version, its launcher and current, with the old version kept" +
            (architecture.ReadyToRunFramework ? "; the guest's crossgen2 compiled the update's native code, which equals the host's byte for byte, and its launcher names it." : "."));
    }

    /// <summary>
    /// What /bin/init runs: the installer's screen on the feed, the update put in use again by the use command, its native
    /// code compiled on the device where the architecture has native code (R9.2), the list, and the commander through its
    /// launcher.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="version">The feed's version.</param>
    /// <returns>The lines of /etc/apps.</returns>
    public static string[] Commands(KernelArchitecture architecture, string version) =>
    [
        "cd /data",
        $"{InstallerPackage.COMMAND} --feed {FEED}",
        $"{InstallerPackage.COMMAND} use {APPLICATION} {version}",
        .. architecture.ReadyToRunFramework ? [$"{InstallerPackage.COMMAND} compile {APPLICATION}"] : Array.Empty<string>(),
        $"{InstallerPackage.COMMAND} list --feed {FEED}",
        ARLC
    ];

    /// <summary>
    /// What the runner does once each line appeared: the probe's line; I once the installer shows the feed's update; U once
    /// it reports the installation, Up to the old version in the picker, which starts on the version in use, and Enter, the
    /// rollback; the screendump; F10; and F10 to the commander once its panels are up. The update goes back in use through
    /// the use command, not the screen: the screen's renderer writes only the cells that change, so a second switch's line
    /// after the first's reaches the transcript as the one digit that differs.
    /// </summary>
    /// <param name="version">The feed's version.</param>
    /// <returns>The actions.</returns>
    public static IReadOnlyList<BootAction> Actions(string version) =>
    [
        new("[DEVICES] terminal probe: ", BootActionKind.Key, KeyTyping.Keys(RuntimeDisplay.PROBE_TEXT + "\r")),
        new($"{version} update", BootActionKind.Key, "i"),
        new($"Installed {APPLICATION} {version}", BootActionKind.Key, "u"),
        new(Picker(), BootActionKind.Key, "up"),
        new("", BootActionKind.Key, "ret"),
        new(Used(OLD_VERSION), BootActionKind.Screendump),
        new("", BootActionKind.Key, "f10"),
        new(COMMANDER_READY, BootActionKind.Key, "f10")
    ];

    /// <summary>
    /// The lines the scenario requires in the serial log or the terminal's transcript: the services' start, the
    /// installation and the rollback on the screen, the installer's exit, the update in use again by the use command, the
    /// list, the commander's exit through its launcher and the services' stop.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="version">The feed's version.</param>
    /// <returns>The lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture, string version) =>
    [
        "[DEVICES] display service /system/Display on firmware/framebuffer with the keyboard pci/",
        $"Installed {APPLICATION} {version}",
        Used(OLD_VERSION),
        $"[APPS-INIT] {InstallerPackage.COMMAND} --feed exited with 0",
        Used(version),
        $"[APPS-INIT] {InstallerPackage.COMMAND} use exited with 0",
        .. architecture.ReadyToRunFramework
            ? [$"Compiled {APPLICATION} {version} for {architecture.Name}: ", $"[APPS-INIT] {InstallerPackage.COMMAND} compile exited with 0"]
            : Array.Empty<string>(),
        $"{APPLICATION}: installed {OLD_VERSION}, {version}; in use {version}; feed {version} (installed)",
        $"[APPS-INIT] {InstallerPackage.COMMAND} list exited with 0",
        $"[APPS-INIT] {ARLC}  exited with 0",
        $"[APPS-INIT] {Commands(architecture, version).Length - 1} commands, 0 failed",
        "[DEVICES] device manager: stopped",
        architecture.RootTaskPassedLine
    ];

    // The data volume: the layer with the old version, as the host's tool lays it with its native code where the
    // architecture has it (R9.2), and the home directory.
    private static async Task<IReadOnlyList<(string Path, string Source)>> VolumeFilesAsync(string output, AppEntry old,
        IReadOnlyList<(string Path, string Source)> files, byte[] apphost, NativeCodeBuild? native)
    {
        var staging = Path.Combine(output, "layer");
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        var layer = Path.Combine(staging, "volume");
        await AppsLayer.InstallAsync(new DirectoryLayerStore(layer), old, files, apphost, Path.Combine(staging, "launchers"), native: native);
        var keep = Path.Combine(staging, "keep");
        await File.WriteAllTextAsync(keep, "");
        return [.. Directory.EnumerateFiles(layer, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => (Path.GetRelativePath(layer, file).Replace('\\', '/'), file)), ("home/.keep", keep)];
    }

    // The FAT volume's feed: the catalog's version's package and entry under /feed.
    private static async Task<IReadOnlyList<(string Path, string Source)>> FeedFilesAsync(string output, AppEntry latest,
        IReadOnlyList<(string Path, string Source)> files)
    {
        var feed = Path.Combine(output, "feed");
        if (Directory.Exists(feed))
            Directory.Delete(feed, recursive: true);
        await AppFeed.WriteAsync(feed, AppsLayer.ManifestJson(latest), files);
        return [.. Directory.EnumerateFiles(feed, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => ("feed/" + Path.GetRelativePath(feed, file).Replace('\\', '/'), file))];
    }

    // The volume after the boot: current names the update, the launcher starts it, every file of the update is the
    // publish's, and the old version is kept.
    // Where the architecture has native code (R9.2), the update's native directory, which the guest's crossgen2 wrote,
    // equals the one the host compiles from the same files byte for byte, its marker included, and the launcher names it.
    private static async Task RequireLayerAsync(string dataDisk, KernelArchitecture architecture, AppEntry latest,
        IReadOnlyList<(string Path, string Source)> latestFiles, IReadOnlyList<(string Path, string Source)> oldFiles, byte[] apphost, NativeCodeBuild? native)
    {
        var assembly = latest.Commands.Single().Assembly;
        var nativeName = native is null ? null : NativeCode.DirectoryName(architecture.Name);
        var directory = $"/{AppsLayer.ROOT}/{APPLICATION}";
        var paths = new List<string> { $"{directory}/{AppsLayer.CURRENT}", $"/{AppsLayer.BIN}/{latest.Commands.Single().Name}", $"{directory}/{OLD_VERSION}/{assembly}" };
        paths.AddRange(latestFiles.Select(file => $"{directory}/{latest.Version}/{file.Path}"));
        var contents = await RuntimeDataDisk.ReadExt4Async(dataDisk, paths);
        if (System.Text.Encoding.UTF8.GetString(contents[0]).Trim() != latest.Version)
            throw new InvalidOperationException($"The volume's {paths[0]} does not name {latest.Version}.");
        if (!contents[1].AsSpan().SequenceEqual(AppLauncher.Write(apphost, AppLauncher.AppPath(APPLICATION, latest.Version, assembly, nativeName))))
            throw new InvalidOperationException($"The volume's {paths[1]} is not the launcher of {APPLICATION} {latest.Version}.");
        var oldAssembly = await File.ReadAllBytesAsync(oldFiles.Single(file => file.Path == assembly).Source);
        if (!contents[2].AsSpan().SequenceEqual(oldAssembly))
            throw new InvalidOperationException($"The volume's {paths[2]} is not the old version's {assembly}.");
        for (var i = 0; i < latestFiles.Count; i++)
        {
            var publish = await File.ReadAllBytesAsync(latestFiles[i].Source);
            if (!contents[3 + i].AsSpan().SequenceEqual(publish))
                throw new InvalidOperationException($"The volume's {paths[3 + i]} differs from the publish's.");
        }
        if (native is null)
            return;
        var expected = await native.FilesAsync(latest, latestFiles);
        var compiled = await RuntimeDataDisk.ReadExt4Async(dataDisk, [.. expected.Select(file => $"{directory}/{latest.Version}/{nativeName}/{file.Path}")]);
        for (var i = 0; i < expected.Count; i++)
        {
            var host = await File.ReadAllBytesAsync(expected[i].Source);
            if (!compiled[i].AsSpan().SequenceEqual(host))
                throw new InvalidOperationException($"The guest's {nativeName}/{expected[i].Path} of {APPLICATION} {latest.Version} differs from the host's.");
        }
    }

    private static string Picker() => $"Use which version of {APPLICATION}?";

    private static string Used(string version) => $"{APPLICATION} uses {version}";

    #endregion
}
