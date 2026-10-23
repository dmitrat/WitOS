using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// An ordinary ASP.NET Core application in the guest (plan step W3.2, Implementation Strategy §67): tests/Runtime.Witos/WebApp,
/// the web SDK's minimal API on Kestrel, published by the stock SDK without a RID and run through dotnet on the shared
/// frameworks of the install beside the device services, on four processors as an application under load (P2). The
/// runner requests it through QEMU's port forward once it listens — a text, JSON, an echoed POST and /stop — and requires
/// each answer; the application requests its own HTTPS endpoint and reports the check; the scenario requires its lines
/// and its exit with 0.
/// </summary>
internal static class RuntimeAspNetCore
{
    #region Constants

    /// <summary>
    /// The application's project.
    /// </summary>
    public const string PROJECT = "tests/Runtime.Witos/WebApp";

    /// <summary>
    /// The guest's port Kestrel serves HTTP on, which the runner reaches through the forward.
    /// </summary>
    public const ushort HTTP_PORT = 8080;

    /// <summary>
    /// The line the application prints once Kestrel listens, after which the runner requests.
    /// </summary>
    public const string LISTENING = "[ASPNET] listening on http://[::]:8080 and https://[::]:8443";

    /// <summary>
    /// The text the runner expects of /; the application's answer.
    /// </summary>
    public const string HELLO = "Hello from ASP.NET Core on WITOS";

    /// <summary>
    /// The text the runner posts to /echo and expects back.
    /// </summary>
    public const string ECHO = "WitOS echo through Kestrel";

    private const string APPLICATION = "webapp";

    private const string COMMAND = "/dotnet/dotnet /app/webapp/WebApp.dll";

    #endregion

    #region Functions

    /// <summary>
    /// Boots the application beside the services and the install earlier runs built (the runtime-aspnetcore command).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the boot passed.</returns>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var scenario = KernelImageBuilder.RUNTIME_ASPNETCORE_SCENARIO;
        RuntimeApps.ForgetLogs(root, architecture, scenario);
        var output = Path.Combine(root, "artifacts", architecture.Name, scenario);
        Directory.CreateDirectory(output);
        // The framework once for the pin (runtime-witos builds it before its first install; the command alone builds it too).
        if (OperatingSystem.IsLinux())
            await AspNetCoreFramework.BuildAsync(root);
        var package = await BuildPackageAsync(root, output, architecture);
        await RuntimeApps.BootAsync(root, architecture, scenario, RequiredLines(), 1500, package: package,
            processors: RuntimeProcessors.PROCESSORS, forward: HTTP_PORT, actions: Actions());
        Console.WriteLine($"ASP.NET Core on witos-{architecture.Name}: Kestrel answered the runner's requests through the forward and its own HTTPS request.");
    }

    /// <summary>
    /// The package: the device services, the .NET install with the ASP.NET Core shared framework and the application under
    /// /app/webapp.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture) =>
        RuntimeApps.BuildPackageAsync(root, output, architecture, [(APPLICATION, PROJECT)], [COMMAND]);

    /// <summary>
    /// The runner's requests once the application listens: the text of /, the JSON of /json, the echo of a POST and /stop,
    /// each answered with 200 and the expected text, or the boot ends.
    /// </summary>
    /// <returns>The actions.</returns>
    public static IReadOnlyList<BootAction> Actions() =>
    [
        BootAction.Http(LISTENING, "GET", "/", HELLO),
        BootAction.Http("", "GET", "/json", "\"framework\":\".NET 10."),
        BootAction.Http("", "POST", "/echo", ECHO),
        BootAction.Http("", "GET", "/stop", "stopping")
    ];

    /// <summary>
    /// The lines the scenario requires: the device manager's TCP probe expecting no connection of the runner (the package
    /// carries no mark, and the port is the application's), Kestrel listening, the application's own HTTPS request answered,
    /// the five requests served and the exit with 0.
    /// </summary>
    /// <returns>The lines.</returns>
    public static string[] RequiredLines() =>
    [
        "[DEVICES] network TCP probe: no connection of the runner expected", LISTENING, "[ASPNET] https self-check: ok",
        "[ASPNET] served 5 requests, exit 0", $"[APPS-INIT] {COMMAND} exited with 0"
    ];

    #endregion
}
