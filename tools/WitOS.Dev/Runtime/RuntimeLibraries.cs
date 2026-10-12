using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// Representative NuGet libraries on witos (plan step N2.1, Implementation Strategy §59): two ordinary test applications
/// from nuget.org packages run unchanged through dotnet beside the device services (<see cref="RuntimeApps"/>) — NUnit's
/// through NUnitLite, with MessagePack's and Math.NET's tests, and an xUnit v3 application with MemoryPack's and
/// Roslyn's. The host's own runs, on Windows and Linux, are a host test of WitOS.Dev.Tests.
/// </summary>
internal static class RuntimeLibraries
{
    #region Constants

    /// <summary>
    /// The NUnit application's sources.
    /// </summary>
    public const string NUNIT_APP = "tests/RepresentativeLibraries/NUnitApp";

    /// <summary>
    /// The xUnit application's sources.
    /// </summary>
    public const string XUNIT_APP = "tests/RepresentativeLibraries/XUnitApp";

    #endregion

    #region Functions

    /// <summary>
    /// The applications' run on witos alone, on the services and the .NET install earlier runs built (the
    /// runtime-libraries command).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the applications ran.</returns>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        await RuntimeApps.BootAsync(root, architecture, KernelImageBuilder.RUNTIME_LIBRARIES_SCENARIO, RequiredLines(), 3600);
        Console.WriteLine($"Libraries on witos-{architecture.Name}: NUnit's and xUnit's applications passed their tests of MessagePack, " +
            "Math.NET, MemoryPack and Roslyn unchanged through dotnet.");
    }

    /// <summary>
    /// The lines the runtime-libraries scenario requires beside those of every scenario of applications: each runner's
    /// summary with no failure and each application's exit with zero.
    /// </summary>
    /// <returns>The required lines.</returns>
    public static string[] RequiredLines() =>
    [
        "Overall result: Passed",
        ", Failed: 0, Warnings: 0",
        "=== TEST EXECUTION SUMMARY ===",
        "Errors: 0, Failed: 0, Skipped: 0",
        "[APPS-INIT] /dotnet/dotnet /app/nunit/NUnitApp.dll exited with 0",
        "[APPS-INIT] /dotnet/dotnet /app/xunit/XUnitApp.dll exited with 0"
    ];

    /// <summary>
    /// The package: the device services, the .NET install and both applications under /app.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture) =>
        RuntimeApps.BuildPackageAsync(root, output, architecture, [("nunit", NUNIT_APP), ("xunit", XUNIT_APP)],
            ["/dotnet/dotnet /app/nunit/NUnitApp.dll", "/dotnet/dotnet /app/xunit/XUnitApp.dll"]);

    #endregion
}
