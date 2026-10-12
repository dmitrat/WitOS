using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The .NET compatibility suite on witos (plan step N1, Implementation Strategy §57-58, RFC 0015 §1): the ordinary
/// portable application tests/DotNetCompatibility run unchanged through dotnet beside the device services
/// (<see cref="RuntimeApps"/>), so that the data disk's volume holds its files. The host's own runs, on Windows and
/// Linux, are a host test of WitOS.Dev.Tests.
/// </summary>
internal static class RuntimeCompatibility
{
    #region Constants

    /// <summary>
    /// The suite's sources.
    /// </summary>
    public const string SUITE = "tests/DotNetCompatibility";

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
        await RuntimeApps.BootAsync(root, architecture, KernelImageBuilder.RUNTIME_COMPATIBILITY_SCENARIO, RequiredLines(architecture), 1800);
        Console.WriteLine($"Compatibility on witos-{architecture.Name}: the suite ran unchanged through dotnet, every difference as recorded.");
    }

    /// <summary>
    /// The lines the runtime-compatibility scenario requires beside those of every scenario of applications.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The required lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
    [
        $"[N1] .NET compatibility on witos ({(architecture == KernelArchitecture.X64 ? "X64" : "Arm64")}): ",
        " recorded differences, 0 unexpected",
        "[APPS-INIT] /dotnet/dotnet /app/compatibility/DotNetCompatibility.dll exited with 0"
    ];

    /// <summary>
    /// The package: the device services, the .NET install and the suite under /app/compatibility.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture) =>
        RuntimeApps.BuildPackageAsync(root, output, architecture, [("compatibility", SUITE)],
            ["/dotnet/dotnet /app/compatibility/DotNetCompatibility.dll"]);

    #endregion
}
