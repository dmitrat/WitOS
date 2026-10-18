using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// An interactive WitOS session on the host's console (plan step C1.4, RFC 0020 section 34): the device services, the
/// .NET install and the terminal boot with a data disk that holds PowerShell and, when asked, a directory the person
/// published on the host; /bin/init starts PowerShell on the terminal, which the tool relays to the console, and the
/// session ends with PowerShell's exit. In a window (C2.4) the board has the display and the keyboard of the
/// runtime-display scenario: PowerShell draws on the display service's screen in QEMU's window, the window's keys reach
/// the keyboard driver, and the console shows the virtio console's mirror. The image and the data disk need the runtime
/// and e2fsprogs of a Linux host, so a session may be prepared there and booted where a screen is. Not a gate: the way
/// to work with the system by hand.
/// </summary>
internal static class RuntimeShell
{
    #region Constants

    private const string SCENARIO = "shell";

    private const int TIMEOUT_SECONDS = 24 * 3600;

    private const long DISK_BYTES = 1024L << 20;

    // The package's mark of a session, which the device manager's terminal probe looks for (TerminalProbe.SESSION_MARK).
    private const string SESSION_MARK = "etc/witos/session";

    #endregion

    #region Functions

    /// <summary>
    /// Boots the session and relays its terminal to the console until PowerShell exits, or prepares it.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="options">What the person asked for.</param>
    /// <returns>When the session ended, or when its image and disk were prepared.</returns>
    /// <exception cref="DirectoryNotFoundException">The published directory does not exist.</exception>
    /// <exception cref="FileNotFoundException">A prepared image or its data disk does not exist.</exception>
    public static async Task RunAsync(string root, KernelArchitecture architecture, ShellOptions options)
    {
        var output = Path.Combine(root, "artifacts", architecture.Name, SCENARIO);
        Directory.CreateDirectory(output);
        string image;
        string dataDisk;
        string? where = null;
        if (options.Image is not null)
        {
            // A session prepared on another host: the image and the disk as they are.
            image = Path.GetFullPath(options.Image);
            if (!File.Exists(image))
                throw new FileNotFoundException($"No prepared image at {image}.", image);
            dataDisk = Path.GetFullPath(options.Disk ?? throw new ArgumentException("A prepared image boots with its --disk."));
            if (!File.Exists(dataDisk))
                throw new FileNotFoundException($"No data disk at {dataDisk}.", dataDisk);
        }
        else
        {
            (image, dataDisk, where) = await BuildAsync(root, architecture, options, output);
            if (options.Prepare is not null)
            {
                Prepare(architecture, options, image, dataDisk);
                return;
            }
        }
        Console.WriteLine(options.Window
            ? $"WitOS on witos-{architecture.Name}: PowerShell opens in QEMU's window once the services are up, and this console " +
              $"mirrors it; type exit in the window to end the session{(where is null ? "" : $"; your files are under {where}")}."
            : $"WitOS on witos-{architecture.Name}: PowerShell opens on this console once the services are up; " +
              $"type exit to end the session{(where is null ? "" : $"; your files are under {where}")}.");
        await BootScenarioRunner.RunAsync(root, image, new BootRequest($"{architecture.Name}-{SCENARIO}-{options.MemoryMiB}",
            options.MemoryMiB, TIMEOUT_SECONDS, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            DataDisk = dataDisk,
            InteractiveTerminal = true,
            Display = options.Window,
            Keyboard = options.Window,
            Window = options.Window,
            Accelerated = options.Accelerated
        });
        Console.WriteLine("Session ended.");
    }

    // The session's image and data disk: PowerShell's package, its files and the published directory on the volume. A
    // kept disk boots as the previous session left it.
    private static async Task<(string Image, string DataDisk, string? Where)> BuildAsync(string root, KernelArchitecture architecture,
        ShellOptions options, string output)
    {
        var package = (await RuntimePowerShell.PackageAsync(root, output, architecture, ["cd /data", RuntimePowerShell.PWSH])).ToList();
        // A session a person works in (C2.7): the device manager skips the terminal probe, whose wait held /bin/init back.
        var session = Path.Combine(output, "session");
        await File.WriteAllTextAsync(session, "");
        package.Add((SESSION_MARK, session));
        // The installer of base applications (C4.3): witos-apps on PATH, for a feed the person puts on a volume.
        package.AddRange(await InstallerPackage.FilesAsync(root, architecture, output));
        var volume = await AppsDisk.VolumeFilesAsync(root, architecture, output);
        string? where = null;
        if (options.Published is not null)
        {
            var directory = Path.GetFullPath(options.Published);
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"No published directory at {directory}.");
            var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            volume.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => ($"{name}/{Path.GetRelativePath(directory, file).Replace('\\', '/')}", file)));
            where = $"/data/{name}";
        }
        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_POWERSHELL_SCENARIO, outputDirectory: output,
            architecture: architecture, package: package);
        var dataDisk = options.Disk is null ? Path.Combine(output, "data-disk.img") : Path.GetFullPath(options.Disk);
        if (options.Disk is not null && File.Exists(dataDisk))
            Console.WriteLine($"Booting the data disk {dataDisk} as the previous session left it{(where is null ? "" : "; the published directory is not laid on an existing disk")}.");
        else
            await RuntimeDataDisk.CreateAsync(dataDisk, files: volume, bytes: DISK_BYTES);
        return (image, dataDisk, where);
    }

    // The prepared session: the image and the disk side by side in the directory, and the command that boots them.
    private static void Prepare(KernelArchitecture architecture, ShellOptions options, string image, string dataDisk)
    {
        var directory = Path.GetFullPath(options.Prepare!);
        Directory.CreateDirectory(directory);
        var preparedImage = Path.Combine(directory, $"WitOS-{architecture.Name}-shell.img");
        var preparedDisk = Path.Combine(directory, $"data-{architecture.Name}.img");
        File.Copy(image, preparedImage, overwrite: true);
        if (!string.Equals(Path.GetFullPath(dataDisk), preparedDisk, StringComparison.Ordinal))
            File.Copy(dataDisk, preparedDisk, overwrite: true);
        Console.WriteLine($"Prepared the session for witos-{architecture.Name} in {directory}; on a host with a screen boot it with:");
        Console.WriteLine($"  dotnet run --project tools/WitOS.Dev --configuration Release -- shell --arch {architecture.Name} " +
            $"--image {Path.GetFileName(preparedImage)} --disk {Path.GetFileName(preparedDisk)}{(options.Window ? " --window" : "")}");
    }

    #endregion
}
