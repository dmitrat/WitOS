using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// An interactive WitOS session on the host's console (plan step C1.4, RFC 0020 section 34): the device services, the
/// .NET install and the terminal boot with a data disk that holds PowerShell and, when asked, a directory the person
/// published on the host; /bin/init starts PowerShell on the terminal, which the tool relays to the console, and the
/// session ends with PowerShell's exit. Not a gate: the way to work with the system by hand.
/// </summary>
internal static class RuntimeShell
{
    #region Constants

    private const string SCENARIO = "shell";

    private const int TIMEOUT_SECONDS = 24 * 3600;

    private const long DISK_BYTES = 1024L << 20;

    #endregion

    #region Functions

    /// <summary>
    /// Boots the session and relays its terminal to the console until PowerShell exits.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="published">A directory published on the host, laid on the data volume under its name; none by default.</param>
    /// <param name="memoryMiB">The guest's memory.</param>
    /// <returns>When the session ended.</returns>
    /// <exception cref="DirectoryNotFoundException">The published directory does not exist.</exception>
    public static async Task RunAsync(string root, KernelArchitecture architecture, string? published, int memoryMiB)
    {
        var output = Path.Combine(root, "artifacts", architecture.Name, SCENARIO);
        Directory.CreateDirectory(output);
        var package = await RuntimePowerShell.PackageAsync(root, output, architecture, ["cd /data", RuntimePowerShell.PWSH]);
        var volume = new List<(string Path, string Source)>();
        volume.AddRange(await PowerShell.VolumeFilesAsync(root, architecture, RuntimePowerShell.VOLUME_DIRECTORY));
        var keep = Path.Combine(output, "keep");
        await File.WriteAllTextAsync(keep, "");
        volume.Add(("home/.keep", keep));
        string? where = null;
        if (published is not null)
        {
            var directory = Path.GetFullPath(published);
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"No published directory at {directory}.");
            var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            volume.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => ($"{name}/{Path.GetRelativePath(directory, file).Replace('\\', '/')}", file)));
            where = $"/data/{name}";
        }
        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_POWERSHELL_SCENARIO, outputDirectory: output,
            architecture: architecture, package: package);
        var dataDisk = Path.Combine(output, "data-disk.img");
        await RuntimeDataDisk.CreateAsync(dataDisk, files: volume, bytes: DISK_BYTES);
        Console.WriteLine($"WitOS on witos-{architecture.Name}: PowerShell opens on this console once the services are up; " +
            $"type exit to end the session{(where is null ? "" : $"; your files are under {where}")}.");
        await BootScenarioRunner.RunAsync(root, image, new BootRequest($"{architecture.Name}-{SCENARIO}-{memoryMiB}", memoryMiB, TIMEOUT_SECONDS,
            ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            DataDisk = dataDisk,
            InteractiveTerminal = true
        });
        Console.WriteLine("Session ended.");
    }

    #endregion
}
