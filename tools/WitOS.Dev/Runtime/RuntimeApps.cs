using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// Ordinary .NET applications on witos beside the device services (plan steps N1, N2.1 and N2.2): each published by the stock
/// SDK without a RID from a copy apart from the repository's build properties, as a developer's machine builds it (R6),
/// placed under /app/&lt;name&gt; of a package that holds the device services and the .NET install of R5, and run through
/// dotnet by tests/User/apps_init.c from the commands of /etc/apps, with a fresh data disk whose volume holds their
/// files.
/// </summary>
internal static class RuntimeApps
{
    #region Constants

    /// <summary>
    /// The memory a scenario of applications boots with unless the command says otherwise.
    /// </summary>
    public const int MEMORY_MIB = 512;

    /// <summary>
    /// The scenarios' /bin/init, which runs the commands of /etc/apps with /data/tmp as their temporary directory.
    /// </summary>
    public const string INIT = "tests/User/apps_init.c";

    #endregion

    #region Functions

    /// <summary>
    /// The package: the device services, the .NET install, each application under /app/&lt;name&gt;, the commands in
    /// /etc/apps and tests/User/apps_init.c as /bin/init.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="applications">Each application's name and its project's directory under the repository.</param>
    /// <param name="commands">The commands /bin/init runs, one a line, and the directories it runs them in.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture, IReadOnlyList<(string Name, string Project)> applications, IReadOnlyList<string> commands)
    {
        var published = new List<(string Name, string Directory)>();
        foreach (var (name, project) in applications)
            published.Add((name, await PublishAsync(root, output, project, name)));
        return await PackageAsync(root, output, architecture, published, commands);
    }

    /// <summary>
    /// The package of applications published already (N4.4: on another host): the device services, the .NET install,
    /// each application's files under /app/<name> and the commands /bin/init runs.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="published">Each application's name and the directory its publish wrote.</param>
    /// <param name="commands">The commands /bin/init runs, one a line, and the directories it runs them in.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> PackageAsync(string root, string output,
        KernelArchitecture architecture, IReadOnlyList<(string Name, string Directory)> published, IReadOnlyList<string> commands)
    {
        var services = RuntimeDevices.ServiceFiles(root, architecture);
        await Sysroot.BuildAsync(root, architecture);
        var init = Path.Combine(output, "apps_init.o");
        await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, INIT), init, LibWitos.Options(root));
        var initProgram = await MuslLibc.LinkStartedProgramAsync(root, architecture, output, "apps_init", [init]);
        var list = Path.Combine(output, "apps");
        await File.WriteAllTextAsync(list, string.Concat(commands.Select(command => command + "\n")));
        var package = new List<(string Name, string Source)>
        {
            ("bin/init", initProgram), ("etc/apps", list)
        };
        package.AddRange(services);
        package.AddRange(await RuntimeWitos.InstallFilesAsync(root, output, architecture));
        foreach (var (name, directory) in published)
            package.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => ($"app/{name}/{Path.GetRelativePath(directory, file).Replace('\\', '/')}", file)));
        return [.. package.DistinctBy(file => file.Name)];
    }

    /// <summary>
    /// The serial log of a scenario of applications booted by <see cref="BootAsync"/> with the default memory.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="scenario">The scenario.</param>
    /// <returns>The log's path.</returns>
    public static string SerialLog(string root, KernelArchitecture architecture, string scenario) =>
        Path.Combine(root, "artifacts", "logs", $"{architecture.Name}-{scenario}-{MEMORY_MIB}.serial.log");

    /// <summary>
    /// Boots a scenario of applications with a fresh data disk.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="scenario">The scenario.</param>
    /// <param name="requiredLines">The lines it requires.</param>
    /// <param name="timeoutSeconds">Its wall-clock limit.</param>
    /// <param name="volumeFiles">Files the data disk's ext4 volume, /data, holds at their paths (N2.2); none by default.</param>
    /// <param name="diskBytes">The data disk's size.</param>
    /// <param name="memoryMiB">The guest's memory.</param>
    /// <param name="package">The boot package when the command built it (N2.2: a group of suites); the scenario's own
    /// when null.</param>
    /// <returns>When the boot passed.</returns>
    public static async Task BootAsync(string root, KernelArchitecture architecture, string scenario, string[] requiredLines,
        int timeoutSeconds, IReadOnlyList<(string Path, string Source)>? volumeFiles = null, long diskBytes = RuntimeDataDisk.BYTES,
        int memoryMiB = MEMORY_MIB, IReadOnlyList<(string Name, string Source)>? package = null, IReadOnlyList<TerminalInput>? terminal = null)
    {
        var image = await KernelImageBuilder.BuildAsync(root, scenario, architecture: architecture, package: package);
        var dataDisk = Path.Combine(Path.GetDirectoryName(image)!, "data-disk.img");
        await RuntimeDataDisk.CreateAsync(dataDisk, files: volumeFiles, bytes: diskBytes);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest($"{architecture.Name}-{scenario}-{memoryMiB}", memoryMiB, timeoutSeconds,
            ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = [.. requiredLines, "[APPS-INIT] ", " commands, 0 failed", "[ROOT-TASK] /system/DeviceManager stopped",
                architecture.RootTaskPassedLine],
            DataDisk = dataDisk,
            Terminal = terminal
        });
    }

    /// <summary>
    /// Publishes an application with the stock SDK and no RID from a copy of its project apart from the repository's
    /// build properties, as a developer's machine publishes it.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">The directory the copy and the publish go under.</param>
    /// <param name="project">The project's directory under the repository.</param>
    /// <param name="name">The application's name under <paramref name="output"/>.</param>
    /// <returns>The published directory.</returns>
    internal static async Task<string> PublishAsync(string root, string output, string project, string name)
    {
        var copy = await CopyProjectAsync(root, output, project, name);
        var published = Path.Combine(output, $"{name}-app");
        if (Directory.Exists(published))
            Directory.Delete(published, recursive: true);
        await Processes.RequireSuccessAsync("dotnet",
            ["publish", Directory.GetFiles(copy, "*.csproj").Single(), "-c", "Release", "-o", published, "-nologo"], copy);
        return published;
    }

    /// <summary>
    /// Builds one target framework of an application's project with the stock SDK, from a copy apart from the
    /// repository's build properties (N3.3: the side-by-side application for .NET 8 and for .NET 10, each against that
    /// release's reference pack).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="project">The project's directory under the repository.</param>
    /// <param name="name">The application's name under the artifacts.</param>
    /// <param name="framework">The target framework to build.</param>
    /// <returns>The build's output directory.</returns>
    internal static async Task<string> BuildAsync(string root, string output, string project, string name, string framework)
    {
        var copy = await CopyProjectAsync(root, output, project, $"{name}-{framework}");
        var built = Path.Combine(output, $"{name}-{framework}-build");
        if (Directory.Exists(built))
            Directory.Delete(built, recursive: true);
        await Processes.RequireSuccessAsync("dotnet",
            ["build", Directory.GetFiles(copy, "*.csproj").Single(), "-c", "Release", "-f", framework, "-o", built, "-nologo"], copy);
        return built;
    }

    // A copy of a project's sources under the artifacts with empty build properties and targets, so that the stock SDK
    // builds it as it would anywhere.
    private static async Task<string> CopyProjectAsync(string root, string output, string project, string name)
    {
        var copy = Path.Combine(output, $"{name}-project");
        if (Directory.Exists(copy))
            Directory.Delete(copy, recursive: true);
        var sources = Path.Combine(root, project);
        foreach (var file in Directory.EnumerateFiles(sources, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sources, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] is "bin" or "obj")
                continue;
            var target = Path.Combine(copy, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        await File.WriteAllTextAsync(Path.Combine(copy, "Directory.Build.props"), "<Project />\n");
        await File.WriteAllTextAsync(Path.Combine(copy, "Directory.Build.targets"), "<Project />\n");
        return copy;
    }

    #endregion
}
