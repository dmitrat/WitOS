using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The .NET compatibility suite on witos (plan step N1, Implementation Strategy §57-58, RFC 0015 §1): the ordinary
/// portable application tests/DotNetCompatibility, published by the stock SDK without a RID, run unchanged through
/// dotnet from the install of R5, beside the device services so that the data disk's volume holds its files. The host's
/// own runs, on Windows and Linux, are a host test of WitOS.Dev.Tests.
/// </summary>
internal static class RuntimeCompatibility
{
    #region Constants

    /// <summary>
    /// The suite's sources.
    /// </summary>
    public const string SUITE = "tests/DotNetCompatibility";

    /// <summary>
    /// The scenario's /bin/init, which runs the suite with /data/tmp as its temporary directory.
    /// </summary>
    public const string INIT = "tests/User/compatibility_init.c";

    #endregion

    #region Functions

    /// <summary>
    /// The suite's run on witos alone, on the services and the .NET install earlier runs built (the runtime-compatibility
    /// command).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the suite ran.</returns>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_COMPATIBILITY_SCENARIO, architecture: architecture);
        var dataDisk = Path.Combine(Path.GetDirectoryName(image)!, "data-disk.img");
        await RuntimeDataDisk.CreateAsync(dataDisk);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest($"{architecture.Name}-runtime-compatibility-512", 512, 1800,
            ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = RequiredLines(architecture),
            DataDisk = dataDisk
        });
        Console.WriteLine($"Compatibility on witos-{architecture.Name}: the suite ran unchanged through dotnet, every difference as recorded.");
    }

    /// <summary>
    /// The lines the runtime-compatibility scenario requires.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The required lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
    [
        $"[N1] .NET compatibility on witos ({(architecture == KernelArchitecture.X64 ? "X64" : "Arm64")}): ",
        " recorded differences, 0 unexpected",
        "[COMPATIBILITY-INIT] /dotnet/dotnet exited with 0",
        "[ROOT-TASK] /system/DeviceManager stopped",
        architecture.RootTaskPassedLine
    ];

    /// <summary>
    /// The package: the device services of runtime-devices, the .NET install of R5, the suite under /app and
    /// tests/User/compatibility_init.c as /bin/init.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var services = RuntimeDevices.ServiceFiles(root, architecture);
        await Sysroot.BuildAsync(root, architecture);
        var init = Path.Combine(output, "compatibility_init.o");
        await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, INIT), init, LibWitos.Options(root));
        var initProgram = await MuslLibc.LinkStartedProgramAsync(root, architecture, output, "compatibility_init", [init]);
        var app = await PublishAsync(root, output);
        IEnumerable<(string Name, string Source)> package =
        [
            .. services, ("bin/init", initProgram), .. await RuntimeWitos.InstallFilesAsync(root, output, architecture),
            .. Directory.GetFiles(app).Order(StringComparer.Ordinal).Select(file => ($"app/{Path.GetFileName(file)}", file))
        ];
        return [.. package.DistinctBy(file => file.Name)];
    }

    // The suite published by the stock SDK without a RID from a copy apart from the repository's build properties, as a
    // developer's machine builds it (R6); the published directory.
    private static async Task<string> PublishAsync(string root, string output)
    {
        var project = Path.Combine(output, "compatibility-project");
        if (Directory.Exists(project))
            Directory.Delete(project, recursive: true);
        var sources = Path.Combine(root, SUITE);
        foreach (var file in Directory.EnumerateFiles(sources, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sources, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] is "bin" or "obj")
                continue;
            var target = Path.Combine(project, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        await File.WriteAllTextAsync(Path.Combine(project, "Directory.Build.props"), "<Project />\n");
        await File.WriteAllTextAsync(Path.Combine(project, "Directory.Build.targets"), "<Project />\n");
        var app = Path.Combine(output, "compatibility-app");
        if (Directory.Exists(app))
            Directory.Delete(app, recursive: true);
        await Processes.RequireSuccessAsync("dotnet",
            ["publish", Path.Combine(project, "DotNetCompatibility.csproj"), "-c", "Release", "-o", app, "-nologo"], project);
        return app;
    }

    #endregion
}
