using WitOS.Apps;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The installer of base applications in the boot package (plan step C4.3, ADR 0027 decision 5): WitOS's own portable
/// application, published by the stock SDK without a RID, under /system/Installer with the witos apphost beside it, from
/// which it writes the layer's launchers, and its own launcher /usr/bin/witos-apps, on PATH. It belongs to the system,
/// updated with the image, and manages the layer of the data volume. Where the architecture's framework is ReadyToRun
/// (R9), its assemblies are too, and crossgen2 lies beside it under /system/Crossgen2, with which it compiles a version's
/// native code on the device (R9.2).
/// </summary>
internal static class InstallerPackage
{
    #region Constants

    /// <summary>
    /// The installer's command.
    /// </summary>
    public const string COMMAND = "/usr/bin/witos-apps";

    /// <summary>
    /// The installer's directory in the boot package.
    /// </summary>
    public const string DIRECTORY = "system/Installer";

    private const string PROJECT = "src/Apps/Installer/Installer.csproj";

    private const string ASSEMBLY = "witos-apps.dll";

    private const int PUBLISH_SECONDS = 1800;

    #endregion

    #region Functions

    /// <summary>
    /// The installer's files in the boot package: its publish and the apphost under /system/Installer, its launcher in
    /// /usr/bin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture, whose apphost the package takes.</param>
    /// <param name="output">A directory of the host for the publish and the launcher.</param>
    /// <returns>Package paths and the files to place there.</returns>
    /// <exception cref="InvalidOperationException">The publish failed.</exception>
    public static async Task<IReadOnlyList<(string Name, string Source)>> FilesAsync(string root, KernelArchitecture architecture, string output)
    {
        var published = Path.Combine(output, "installer");
        if (Directory.Exists(published))
            Directory.Delete(published, recursive: true);
        var publish = await Processes.RunAsync("dotnet",
            ["publish", Path.Combine(root, PROJECT), "-c", "Release", "-o", published, "-p:UseAppHost=false", "-nologo"], root, PUBLISH_SECONDS);
        if (publish.TimedOut || publish.ExitCode != 0)
            throw new InvalidOperationException($"The publish of the installer failed (exit {publish.ExitCode}): {publish.Output} {publish.Error}");
        var apphostPath = AppsLayer.ApphostPath(root, architecture);
        var apphost = await File.ReadAllBytesAsync(apphostPath);
        var launcher = Path.Combine(output, "witos-apps");
        await File.WriteAllBytesAsync(launcher, AppLauncher.Write(apphost, $"../../{DIRECTORY}/{ASSEMBLY}"));
        IReadOnlyList<(string Path, string Source)> installerFiles = [.. Directory.EnumerateFiles(published, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => (Path.GetRelativePath(published, path).Replace('\\', '/'), path))];
        // R9.2: the installer's assemblies as ReadyToRun images where the architecture's framework is, as the system's
        // framework is (R9), and crossgen2, with which it compiles the layer's versions on the device.
        var native = NativeCodeBuild.For(root, architecture);
        if (native is not null)
            installerFiles = await native.CompileAsync("installer", "witos-apps", installerFiles);
        var files = installerFiles.Select(file => ($"{DIRECTORY}/{file.Path}", file.Source)).ToList();
        files.Add(($"{DIRECTORY}/apphost", apphostPath));
        files.Add((COMMAND.TrimStart('/'), launcher));
        files.AddRange(await Crossgen2Package.FilesAsync(root, architecture, Path.Combine(output, "crossgen2-launcher")));
        return files;
    }

    #endregion
}
