using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
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

    // A check's line: its id and its verdict; the summary line's "subject" holds spaces, so it is no id.
    private static readonly Regex CHECK_LINE = new(@"\[N1\] (?<id>[A-Za-z0-9_]+\.[A-Za-z0-9_]+): (?<verdict>pass|fail|not applicable)\b");

    private static readonly JsonSerializerOptions JSON = new() { WriteIndented = true };

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
        RuntimeApps.ForgetLogs(root, architecture, KernelImageBuilder.RUNTIME_COMPATIBILITY_SCENARIO);
        // The runner's authority of this boot (W3.1): its certificate in the guest's root store, its HTTPS server behind
        // the adapter, so that the suite's Network.HttpsRunner check trusts the server through the store.
        using var authority = new RunnerAuthority(IPAddress.Parse(NetworkPeer.ECHO_ADDRESS));
        var output = Path.Combine(root, "artifacts", architecture.Name, KernelImageBuilder.RUNTIME_COMPATIBILITY_SCENARIO);
        Directory.CreateDirectory(output);
        var package = await RuntimeApps.WithRunnerAuthorityAsync(await BuildPackageAsync(root, output, architecture), output, authority);
        // On several processors (P2), as an application meets a machine.
        await RuntimeApps.BootAsync(root, architecture, KernelImageBuilder.RUNTIME_COMPATIBILITY_SCENARIO, RequiredLines(architecture), 1800,
            package: package, terminal: Script(), processors: RuntimeProcessors.PROCESSORS, https: authority);
        // N4.4: the record of this run, which runtime-published compares a publish of another host against. The suite
        // writes to the terminal (N4.3), so its lines are in the transcript, not in the serial log.
        var checks = ReadChecks(await RuntimeApps.GuestLinesAsync(root, architecture, KernelImageBuilder.RUNTIME_COMPATIBILITY_SCENARIO));
        if (checks.Count == 0)
            throw new InvalidOperationException("The suite's checks were not found in the guest's serial log or terminal transcript.");
        var sdk = (await Processes.RunAsync("dotnet", ["--version"], root)).Output.Trim();
        var record = new CompatibilityRecord(architecture.Name, RuntimePublished.HostName(), sdk, checks);
        var path = RecordPath(root, architecture);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, JSON));
        Console.WriteLine($"Compatibility on witos-{architecture.Name}: the suite ran unchanged through dotnet, every difference as recorded " +
                          $"({checks.Count} checks recorded in {path}).");
    }

    /// <summary>
    /// The verdict of each check in a guest's log: pass, fail or not applicable, by the check's id.
    /// </summary>
    /// <param name="lines">The log's lines.</param>
    /// <returns>The verdicts; the last line of a check that printed twice.</returns>
    public static IReadOnlyDictionary<string, string> ReadChecks(IEnumerable<string> lines)
    {
        var checks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var match = CHECK_LINE.Match(line);
            if (match.Success)
                checks[match.Groups["id"].Value] = match.Groups["verdict"].Value;
        }
        return checks;
    }

    /// <summary>
    /// The record of the suite's last run on an architecture.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The record's path.</returns>
    public static string RecordPath(string root, KernelArchitecture architecture) =>
        Path.Combine(root, "artifacts", "runtime-witos", $"compatibility-{architecture.Name}.json");

    /// <summary>
    /// Reads the record of the suite's last run on an architecture.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The record, or null when no run wrote one.</returns>
    public static async Task<CompatibilityRecord?> ReadRecordAsync(string root, KernelArchitecture architecture)
    {
        var path = RecordPath(root, architecture);
        return File.Exists(path) ? JsonSerializer.Deserialize<CompatibilityRecord>(await File.ReadAllTextAsync(path)) : null;
    }

    /// <summary>
    /// The lines the runtime-compatibility scenario requires beside those of every scenario of applications.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The required lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
    [
        $"[N1] .NET compatibility on witos ({architecture.OsArchitecture}): ",
        " recorded differences, 0 unexpected, 0 not applicable",
        "[N1] Terminal.ReadLine: pass",
        "[N1] Terminal.ReadKey: pass",
        "[N1] Terminal.KeyAvailable: pass",
        "[N1] Terminal.CancelKeyPress: pass",
        "[N1] Terminal.PosixSignal: pass",
        "[APPS-INIT] /dotnet/dotnet /app/compatibility/DotNetCompatibility.dll exited with 0"
    ];

    /// <summary>
    /// What the runner types into the terminal (N4.3b): what the suite's Terminal checks ask for, each once its prompt
    /// appeared; the terminal probe's line comes first (<see cref="RuntimeTerminal"/>).
    /// </summary>
    /// <returns>The script.</returns>
    public static IReadOnlyList<Host.TerminalInput> Script() =>
    [
        new Host.TerminalInput("[TERMINAL] type a line", RuntimeTerminal.PROBE_LINE + "\n"),
        new Host.TerminalInput(TerminalPrompts.LINE, TerminalPrompts.LINE_TYPED + "\n"),
        new Host.TerminalInput(TerminalPrompts.KEY, "k"),
        new Host.TerminalInput(TerminalPrompts.KEY_AVAILABLE, "y\n"),
        new Host.TerminalInput(TerminalPrompts.CANCEL, "\x03"),
        new Host.TerminalInput(TerminalPrompts.SIGNAL, "\x03")
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

    #region Nested Types

    /// <summary>
    /// The record of the suite's run on witos (N4.4): the architecture, the host that published the suite and its SDK,
    /// and each check's verdict.
    /// </summary>
    public sealed record CompatibilityRecord(string Architecture, string PublishedOn, string Sdk, IReadOnlyDictionary<string, string> Checks);

    /// <summary>
    /// The prompts the suite's Terminal checks print (tests/DotNetCompatibility/Terminal/TerminalChecks.cs), which the
    /// runner answers; a host test requires the suite's source to hold the same strings.
    /// </summary>
    public static class TerminalPrompts
    {
        public const string LINE = "[N1] type a line";

        public const string LINE_TYPED = "hello n1";

        public const string KEY = "[N1] press a key";

        public const string KEY_AVAILABLE = "[N1] type y";

        public const string CANCEL = "[N1] press Ctrl-C";

        public const string SIGNAL = "[N1] press Ctrl-C again";
    }

    #endregion
}
