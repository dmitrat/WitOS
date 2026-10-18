using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The runtime on several processors (plan step P2): an ordinary application, tests/Runtime.Witos/Processors, published
/// by the stock SDK without a RID once as it is and once with ServerGarbageCollection, runs unchanged through dotnet
/// beside the device services (<see cref="RuntimeApps"/>) on a board of <see cref="PROCESSORS"/> processors: the processor
/// count, the processors its threads run on, the collector's heaps, the thread pool and exact results of work on every
/// processor at once, under the workstation and the server collector.
/// </summary>
internal static class RuntimeProcessors
{
    #region Constants

    /// <summary>
    /// The application's sources.
    /// </summary>
    public const string PROCESSORS_APP = "tests/Runtime.Witos/Processors";

    /// <summary>
    /// The processors the scenario's board gives the guest, and the scenarios of applications under load (N1, N2).
    /// </summary>
    public const int PROCESSORS = 4;

    /// <summary>
    /// The application's checks.
    /// </summary>
    public const int CHECKS = 11;

    // The commands in the guest: the application under each collector, told how many processors to expect.
    private static readonly string[] COMMANDS =
    [
        $"/dotnet/dotnet /app/processors/Processors.dll {PROCESSORS}",
        $"/dotnet/dotnet /app/processors-server/Processors.dll {PROCESSORS}"
    ];

    #endregion

    #region Functions

    /// <summary>
    /// The application's runs on witos, on the services and the .NET install earlier runs built (the runtime-processors
    /// command).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the application ran.</returns>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        await RuntimeApps.BootAsync(root, architecture, KernelImageBuilder.RUNTIME_PROCESSORS_SCENARIO, RequiredLines(), 1800,
            processors: PROCESSORS);
        Console.WriteLine($"Processors on witos-{architecture.Name}: an ordinary application passed its {CHECKS} checks on {PROCESSORS} " +
            "processors under the workstation and the server collector.");
    }

    /// <summary>
    /// The lines the runtime-processors scenario requires beside those of every scenario of applications: the board's
    /// processors, each run's summary and each run's exit with zero.
    /// </summary>
    /// <returns>The required lines.</returns>
    public static string[] RequiredLines() =>
    [
        $"Processors present/online: {PROCESSORS}/{PROCESSORS}",
        $"[P2] {PROCESSORS} processors, workstation GC: {CHECKS} of {CHECKS} checks passed",
        $"[P2] {PROCESSORS} processors, server GC: {CHECKS} of {CHECKS} checks passed",
        // apps_init reports a command by its program and first argument alone.
        "[APPS-INIT] /dotnet/dotnet /app/processors/Processors.dll exited with 0",
        "[APPS-INIT] /dotnet/dotnet /app/processors-server/Processors.dll exited with 0"
    ];

    /// <summary>
    /// The package: the device services, the .NET install and both publishes of the application under /app.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var workstation = await RuntimeApps.PublishAsync(root, output, PROCESSORS_APP, "processors");
        var server = await RuntimeApps.PublishAsync(root, output, PROCESSORS_APP, "processors-server", ["ServerGarbageCollection=true"]);
        return await RuntimeApps.PackageAsync(root, output, architecture, [("processors", workstation), ("processors-server", server)], COMMANDS);
    }

    #endregion
}
