using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The representative NuGet libraries' own test suites on witos (plan step N2.2, RFC 0015 section 9's gate N2): each
/// library's repository at the commit its pinned package was built from (build/runtime/nuget-suites.json), checked out
/// under .tools/nuget-suites; its test projects built unchanged on the Linux host by the SDK its global.json asks for;
/// each suite run on the host and then in the guest, through dotnet beside the device services (<see cref="RuntimeApps"/>),
/// by the runner its repository uses. The suites and xunit's console runner are files of the data disk's ext4 volume,
/// /data/suites, read through the namespace service, since together they outgrow the boot package. The guest has .NET 10 alone, so a suite built for an earlier .NET rolls forward to
/// it, on the host too. A suite passes when the guest ran as many tests as the host, passed as many and failed none; a
/// test leaves both runs only with a reason. Each suite belongs to a group that runs as one boot: together the suites run
/// for hours in the guest, so CI runs each group as a job of its own on the runtime the linux-host leg built.
/// </summary>
internal static class RuntimeNuGetSuites
{
    #region Constants

    /// <summary>
    /// The selection: the repositories, the suites, the SDKs and xunit's console runner.
    /// </summary>
    public const string SELECTION = "build/runtime/nuget-suites.json";

    /// <summary>
    /// The guest's wall-clock limit for every suite together.
    /// </summary>
    public const int TIMEOUT_SECONDS = 4 * 3600;

    /// <summary>
    /// The guest's memory: Roslyn's and NUnit's suites take 400 MB on Linux, beside the boot package, the services and the
    /// data volume's cache.
    /// </summary>
    public const int MEMORY_MIB = 2048;

    // Where the volume holds the suites, each in a directory of its name, and xunit's console runner; where the guest
    // finds them.
    private const string VOLUME_SUITES = "suites";
    private const string GUEST_SUITES = "/data/" + VOLUME_SUITES;
    private const string RUNNER_DIRECTORY = "xunit.runner.console";
    private const string XUNIT_CONSOLE = "xunit.console.dll";
    private const string GUEST_XUNIT_CONSOLE = GUEST_SUITES + "/" + RUNNER_DIRECTORY + "/" + XUNIT_CONSOLE;

    // The data disk's room beyond the suites: the journal, the tests' temporary files and the probes' (N2.2).
    private const long DISK_ROOM = 256L * 1024 * 1024;
    private const long MIB = 1024 * 1024;

    // The prefix the kernel gives a line of user output.
    private const string GUEST_PREFIX = "[USER] ";

    // apps_init.c's line for a change of directory, which starts a suite's output in the guest's log; the runners'
    // summaries and their lines for a failed test.
    private static readonly Regex SUITE_LINE = new(@"\[APPS-INIT\] in /data/suites/(\S+)$", RegexOptions.Compiled);
    private static readonly Regex XUNIT_SUMMARY = new(@"^\s*\S+\s+Total: (\d+), Errors: (\d+), Failed: (\d+), Skipped: (\d+)",
        RegexOptions.Compiled);
    private static readonly Regex XUNIT_FAILURE = new(@"^\s*(\S.*?) \[FAIL\]\s*$", RegexOptions.Compiled);
    private static readonly Regex NUNIT_SUMMARY = new(@"Test Count: (\d+), Passed: (\d+), Failed: (\d+), Warnings: (\d+), " +
        @"Inconclusive: (\d+), Skipped: (\d+)", RegexOptions.Compiled);
    // NUnit numbers the failures of a test that failed several times 12-1), 12-2) and so on.
    private static readonly Regex NUNIT_FAILURE = new(@"^\s*\d+(?:-\d+)?\) (?:Failed|Error) : (.+?)\s*$", RegexOptions.Compiled);

    private static readonly Regex COMMIT = new("^[0-9a-f]{40}$", RegexOptions.Compiled);
    private static readonly Regex SHA256 = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex NAME = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    #endregion

    #region Functions

    /// <summary>
    /// The suites on witos, every one or a group's: built and run on the Linux host, then run in the guest (the
    /// runtime-nuget-suites command, on the runtime an earlier runtime-witos built).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="group">The group of suites to run; every suite when null.</param>
    /// <returns>When every suite passed in the guest as on the host.</returns>
    public static async Task RunAsync(string root, KernelArchitecture architecture, string? group = null)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The NuGet libraries' suites are built and run on the Linux host, as runtime-witos is.");
        var selection = SelectGroup(ReadSelection(root), group);
        var host = await BuildAndRunOnHostAsync(root, selection);
        var log = Path.Combine(root, "artifacts", "logs",
            $"{architecture.Name}-{KernelImageBuilder.RUNTIME_NUGET_SUITES_SCENARIO}-{MEMORY_MIB}.serial.log");
        if (File.Exists(log))
            File.Delete(log);
        var files = VolumeFiles(root, selection);
        var bytes = files.Sum(file => new FileInfo(file.Source).Length);
        var diskBytes = (bytes * 3 / 2 + DISK_ROOM + MIB - 1) / MIB * MIB;
        var output = Path.Combine(root, "artifacts", architecture.Name, KernelImageBuilder.RUNTIME_NUGET_SUITES_SCENARIO);
        Directory.CreateDirectory(output);
        var package = await BuildPackageAsync(root, output, architecture, selection);
        Exception? failure = null;
        try
        {
            // On as many processors as the runtime's own scenario (P2): the suites under load on several processors.
            await RuntimeApps.BootAsync(root, architecture, KernelImageBuilder.RUNTIME_NUGET_SUITES_SCENARIO, [], TIMEOUT_SECONDS, files,
                diskBytes, MEMORY_MIB, package, processors: RuntimeProcessors.PROCESSORS);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or TimeoutException)
        {
            failure = exception;
        }
        var guest = File.Exists(log) ? ReadGuestResults(selection, await File.ReadAllLinesAsync(log)) : new Dictionary<string, NuGetSuiteResult>();
        var (report, problems) = await ReportAsync(root, architecture, group, selection, host, guest);
        if (failure is not null || problems.Count > 0)
            throw new InvalidOperationException((failure is not null ? failure.Message + "\n" : "") + report +
                (problems.Count > 0 ? "\n" + string.Join("\n", problems) : ""), failure);
        Console.WriteLine(report);
    }

    /// <summary>
    /// The package of every suite: the device services, the .NET install, and in /etc/apps each suite's directory on the
    /// volume and its command.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture) =>
        BuildPackageAsync(root, output, architecture, ReadSelection(root));

    /// <summary>
    /// The package of the selection's suites.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="selection">The suites the guest runs.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture, NuGetSuiteSelection selection)
    {
        var commands = selection.Suites.SelectMany(suite => new[]
        {
            $"cd {GUEST_SUITES}/{suite.Name}", string.Join(' ', ["/dotnet/dotnet", .. DotnetArguments(suite, GUEST_XUNIT_CONSOLE)])
        }).ToList();
        return RuntimeApps.BuildPackageAsync(root, output, architecture, [], commands);
    }

    /// <summary>
    /// The files of the data disk's volume: each suite's build output under suites/&lt;name&gt; and xunit's console
    /// runner under suites/xunit.runner.console.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="selection">The selection.</param>
    /// <returns>Volume paths and the files to place there.</returns>
    public static IReadOnlyList<(string Path, string Source)> VolumeFiles(string root, NuGetSuiteSelection selection)
    {
        var files = new List<(string Path, string Source)>();
        foreach (var suite in selection.Suites)
        {
            var directory = SuiteOutput(root, selection, suite);
            if (!File.Exists(Path.Combine(directory, suite.Assembly)))
                throw new InvalidOperationException($"{suite.Name} is not built: {directory}.");
            files.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => ($"{VOLUME_SUITES}/{suite.Name}/{Path.GetRelativePath(directory, file).Replace('\\', '/')}", file)));
        }
        files.AddRange(Directory.EnumerateFiles(XunitConsoleDirectory(root, selection.XunitConsole)).Order(StringComparer.Ordinal)
            .Select(file => ($"{VOLUME_SUITES}/{RUNNER_DIRECTORY}/{Path.GetFileName(file)}", file)));
        return files;
    }

    /// <summary>
    /// The suites of a group with the repositories they need.
    /// </summary>
    /// <param name="selection">The selection.</param>
    /// <param name="group">The group; every suite when null.</param>
    /// <returns>The group's selection.</returns>
    /// <exception cref="ArgumentException">No suite is in the group.</exception>
    public static NuGetSuiteSelection SelectGroup(NuGetSuiteSelection selection, string? group)
    {
        if (group is null)
            return selection;
        var suites = selection.Suites.Where(suite => suite.Group == group).ToList();
        if (suites.Count == 0)
            throw new ArgumentException($"No suite of {SELECTION} is in group {group}; the groups are " +
                $"{string.Join(", ", selection.Suites.Select(suite => suite.Group).Distinct(StringComparer.Ordinal))}.");
        return selection with
        {
            Repositories = [.. selection.Repositories.Where(repository => suites.Any(suite => suite.Repository == repository.Name))],
            Suites = suites
        };
    }

    /// <summary>
    /// Reads and checks the selection.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The selection.</returns>
    public static NuGetSuiteSelection ReadSelection(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, SELECTION)));
        var element = document.RootElement;
        var sdks = element.GetProperty("sdks").EnumerateObject().ToDictionary(sdk => sdk.Name, sdk => sdk.Value.GetString() ?? "",
            StringComparer.Ordinal);
        var console = element.GetProperty("xunitConsole");
        var xunitConsole = new NuGetPin(console.GetProperty("version").GetString() ?? "", console.GetProperty("sha256").GetString() ?? "");
        var repositories = element.GetProperty("repositories").EnumerateArray().Select(repository => new NuGetSuiteRepository(
            repository.GetProperty("name").GetString() ?? "",
            repository.GetProperty("package").GetString() ?? "",
            repository.GetProperty("url").GetString() ?? "",
            repository.GetProperty("commit").GetString() ?? "",
            repository.GetProperty("sdk").GetString(),
            [.. repository.GetProperty("submodules").EnumerateArray().Select(submodule => submodule.GetString() ?? "")])).ToList();
        var suites = element.GetProperty("suites").EnumerateArray().Select(suite => new NuGetSuite(
            suite.GetProperty("name").GetString() ?? "",
            suite.GetProperty("repository").GetString() ?? "",
            suite.GetProperty("group").GetString() ?? "",
            suite.GetProperty("project").GetString() ?? "",
            suite.GetProperty("framework").GetString() ?? "",
            suite.GetProperty("output").GetString() ?? "",
            suite.GetProperty("assembly").GetString() ?? "",
            suite.GetProperty("runner").GetString() ?? "",
            suite.TryGetProperty("program", out var program) ? program.GetString() : null,
            suite.GetProperty("excluded").EnumerateObject().ToDictionary(test => test.Name, test => test.Value.GetString() ?? "",
                StringComparer.Ordinal),
            suite.TryGetProperty("arguments", out var arguments)
                ? [.. arguments.EnumerateArray().Select(argument => argument.GetString() ?? "")]
                : [])).ToList();
        var problems = new List<string>();
        if (sdks.Any(sdk => !SHA256.IsMatch(sdk.Value)) || !SHA256.IsMatch(xunitConsole.Sha256) || xunitConsole.Version.Length == 0)
            problems.Add("an SDK or xunit's console runner has no SHA-256");
        foreach (var repository in repositories)
        {
            if (!NAME.IsMatch(repository.Name) || !repository.Url.StartsWith("https://", StringComparison.Ordinal) ||
                !COMMIT.IsMatch(repository.Commit) || repository.Package.Length == 0)
                problems.Add($"repository {repository.Name}: a name, a package, an https URL and a commit");
            if (repository.Sdk is not null && !sdks.ContainsKey(repository.Sdk))
                problems.Add($"repository {repository.Name}: SDK {repository.Sdk} is not pinned");
        }
        if (repositories.DistinctBy(repository => repository.Name).Count() != repositories.Count)
            problems.Add("two repositories share a name");
        foreach (var suite in suites)
        {
            if (!NAME.IsMatch(suite.Name) || suite.Name == RUNNER_DIRECTORY)
                problems.Add($"suite {suite.Name}: the name is a directory of /suites");
            if (repositories.All(repository => repository.Name != suite.Repository))
                problems.Add($"suite {suite.Name}: no repository {suite.Repository}");
            if (!NAME.IsMatch(suite.Group))
                problems.Add($"suite {suite.Name}: no group");
            if (suite.Runner is not ("xunit2" or "xunit3" or "nunitlite") || (suite.Program is not null && suite.Runner != "nunitlite"))
                problems.Add($"suite {suite.Name}: runner {suite.Runner}");
            if (suite.Project.Length == 0 || suite.Framework.Length == 0 || suite.Output.Length == 0 ||
                !suite.Assembly.EndsWith(".dll", StringComparison.Ordinal) || Path.IsPathRooted(suite.Output))
                problems.Add($"suite {suite.Name}: a project, a framework, an output under the checkout and an assembly");
            if (suite.Arguments.Any(argument => argument.Length == 0 || argument.Contains(' ') || argument.Contains('"')))
                problems.Add($"suite {suite.Name}: an argument is empty or holds a space or a quote");
            foreach (var (test, reason) in suite.Excluded)
                if (!(test.StartsWith("class:", StringComparison.Ordinal) || test.StartsWith("method:", StringComparison.Ordinal)) ||
                    test.Contains(' ') || test.Contains('\'') || string.IsNullOrWhiteSpace(reason))
                    problems.Add($"suite {suite.Name}: exclusion {test} needs 'class:' or 'method:', a name without spaces or quotes, and a reason");
        }
        if (suites.Count == 0 || suites.DistinctBy(suite => suite.Name).Count() != suites.Count)
            problems.Add("no suite, or two suites share a name");
        if (problems.Count > 0)
            throw new InvalidDataException($"{SELECTION}: {string.Join("; ", problems)}.");
        return new NuGetSuiteSelection(sdks, xunitConsole, repositories, suites);
    }

    /// <summary>
    /// What dotnet runs for a suite, from the suite's directory: rolled forward to the latest .NET there is, the guest's
    /// one and the host's .NET 10 even beside the earlier ones a host may have, the runner its repository uses, and the
    /// exclusions in the runner's own options.
    /// </summary>
    /// <param name="suite">The suite.</param>
    /// <param name="xunitConsole">xunit's console runner, for an xunit v2 suite.</param>
    /// <returns>dotnet's arguments.</returns>
    public static IReadOnlyList<string> DotnetArguments(NuGetSuite suite, string xunitConsole)
    {
        var excluded = suite.Excluded.Keys.Order(StringComparer.Ordinal).ToList();
        IReadOnlyList<string> xunitExclusions = [.. excluded.SelectMany(test => test.StartsWith("class:", StringComparison.Ordinal)
            ? new[] { "-noclass", test["class:".Length..] }
            : ["-nomethod", test["method:".Length..]])];
        IReadOnlyList<string> nunitExclusions = excluded.Count == 0 ? [] :
            ["--where=" + string.Join("&&", excluded.Select(test => test.StartsWith("class:", StringComparison.Ordinal)
                ? $"class!='{test["class:".Length..]}'"
                : $"test!='{test["method:".Length..]}'"))];
        IReadOnlyList<string> runner = suite.Runner switch
        {
            "xunit2" => [xunitConsole, suite.Assembly, "-nologo", "-nocolor", .. xunitExclusions],
            "xunit3" => [suite.Assembly, "-nologo", "-nocolor", .. xunitExclusions],
            _ => [.. suite.Program is null ? [suite.Assembly] : new[] { suite.Program, suite.Assembly }, "--noresult", "--nocolor",
                .. nunitExclusions]
        };
        return ["exec", "--roll-forward", "LatestMajor", .. runner, .. suite.Arguments];
    }

    /// <summary>
    /// What one run of a suite reported: its runner's summary and the tests it named as failed.
    /// </summary>
    /// <param name="runner">The suite's runner.</param>
    /// <param name="lines">The run's output.</param>
    /// <returns>The result, or null without a summary.</returns>
    public static NuGetSuiteResult? ReadResult(string runner, IEnumerable<string> lines)
    {
        NuGetSuiteResult? result = null;
        var failures = new List<string>();
        foreach (var line in lines)
        {
            if (runner == "nunitlite")
            {
                var summary = NUNIT_SUMMARY.Match(line);
                if (summary.Success)
                {
                    // A test that passed with a warning (Assert.Warn: NUnit's own speed checks under emulation) passed.
                    var total = int.Parse(summary.Groups[1].Value);
                    var passed = int.Parse(summary.Groups[2].Value) + int.Parse(summary.Groups[4].Value);
                    var failed = int.Parse(summary.Groups[3].Value);
                    result = new NuGetSuiteResult(total, passed, failed, total - passed - failed, failures);
                    continue;
                }
                var failure = NUNIT_FAILURE.Match(line);
                if (failure.Success)
                    failures.Add(failure.Groups[1].Value);
            }
            else
            {
                var summary = XUNIT_SUMMARY.Match(line);
                if (summary.Success)
                {
                    var total = int.Parse(summary.Groups[1].Value);
                    var failed = int.Parse(summary.Groups[2].Value) + int.Parse(summary.Groups[3].Value);
                    var skipped = int.Parse(summary.Groups[4].Value);
                    result = new NuGetSuiteResult(total, total - failed - skipped, failed, skipped, failures);
                    continue;
                }
                var failure = XUNIT_FAILURE.Match(line);
                if (failure.Success)
                    failures.Add(failure.Groups[1].Value);
            }
        }
        return result;
    }

    /// <summary>
    /// The guest's results: its log split at apps_init.c's line for each suite's directory.
    /// </summary>
    /// <param name="selection">The selection.</param>
    /// <param name="log">The guest's serial log.</param>
    /// <returns>Each suite's result that had a summary.</returns>
    public static IReadOnlyDictionary<string, NuGetSuiteResult> ReadGuestResults(NuGetSuiteSelection selection, IEnumerable<string> log)
    {
        var outputs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var raw in log)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(GUEST_PREFIX, StringComparison.Ordinal))
                line = line[GUEST_PREFIX.Length..];
            var marker = SUITE_LINE.Match(line);
            if (marker.Success)
            {
                current = [];
                outputs[marker.Groups[1].Value] = current;
                continue;
            }
            current?.Add(line);
        }
        var results = new Dictionary<string, NuGetSuiteResult>(StringComparer.Ordinal);
        foreach (var suite in selection.Suites)
            if (outputs.TryGetValue(suite.Name, out var lines) && ReadResult(suite.Runner, lines) is { } result)
                results[suite.Name] = result;
        return results;
    }

    // Every repository checked out and every suite built, then run on the host: each suite's result.
    private static async Task<IReadOnlyDictionary<string, NuGetSuiteResult>> BuildAndRunOnHostAsync(string root, NuGetSuiteSelection selection)
    {
        var logs = Path.Combine(root, "artifacts", "nuget-suites");
        Directory.CreateDirectory(logs);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            // The checkout is shallow: Nerdbank.GitVersioning, which MessagePack's and xunit's builds use, reads no
            // history then and takes the version its version.json names.
            ["NBGV_GitEngine"] = "Disabled"
        };
        // On a runner a suite's tests write GitHub's file commands to files of the suite's logs, never to the runner's own:
        // a line of the test frameworks' suites in GITHUB_ENV failed a job after every suite had passed (W2.2, #167).
        foreach (var name in (string[])["GITHUB_ENV", "GITHUB_OUTPUT", "GITHUB_PATH", "GITHUB_STATE", "GITHUB_STEP_SUMMARY"])
        {
            if (Environment.GetEnvironmentVariable(name) is not null)
                environment[name] = Path.Combine(logs, $"host-{name.ToLowerInvariant()}.txt");
        }
        foreach (var repository in selection.Repositories)
        {
            var source = await PrepareSourcesAsync(root, repository);
            var dotnet = "dotnet";
            var buildEnvironment = environment;
            if (repository.Sdk is not null)
            {
                dotnet = await RequireSdkAsync(root, repository.Sdk, selection.Sdks[repository.Sdk]);
                buildEnvironment = new Dictionary<string, string>(environment, StringComparer.Ordinal) { ["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)! };
            }
            foreach (var suite in selection.Suites.Where(suite => suite.Repository == repository.Name))
            {
                // The pin decides the suite's dependencies, not the advisories published up to the day of the restore
                // (R8.1).
                var build = await Processes.RunAsync(dotnet,
                    ["build", suite.Project, "-c", "Release", "-f", suite.Framework, "-p:NuGetAudit=false", "-nologo"],
                    source, 3600, buildEnvironment);
                await File.WriteAllTextAsync(Path.Combine(logs, $"{suite.Name}-build.log"), build.Output + build.Error);
                if (build.TimedOut || build.ExitCode != 0)
                    throw new InvalidOperationException($"The build of {suite.Name} failed (exit {build.ExitCode}, timeout={build.TimedOut}); " +
                        $"see {Path.Combine(logs, $"{suite.Name}-build.log")}.");
            }
        }
        var xunitConsole = Path.Combine(await RequireXunitConsoleAsync(root, selection.XunitConsole), XUNIT_CONSOLE);
        var results = new Dictionary<string, NuGetSuiteResult>(StringComparer.Ordinal);
        foreach (var suite in selection.Suites)
        {
            var run = await Processes.RunAsync("dotnet", DotnetArguments(suite, xunitConsole), SuiteOutput(root, selection, suite), 3600, environment);
            var log = Path.Combine(logs, $"{suite.Name}-host.log");
            await File.WriteAllTextAsync(log, run.Output + run.Error);
            var result = ReadResult(suite.Runner, (run.Output + "\n" + run.Error).Split('\n').Select(line => line.TrimEnd('\r')));
            if (run.TimedOut || result is null || result.Failed > 0 || run.ExitCode != 0)
                throw new InvalidOperationException($"{suite.Name} failed on the host (exit {run.ExitCode}, timeout={run.TimedOut}" +
                    $"{(result is null ? ", no summary" : $", {result.Failed} failed: {string.Join("; ", result.Failures.Take(20))}")}); see {log}. " +
                    "A test that fails on the host leaves the selection with its reason.");
            Console.WriteLine($"{suite.Name} on the host: {result.Total} tests, {result.Passed} passed, {result.Skipped} not run.");
            results[suite.Name] = result;
        }
        return results;
    }

    // The comparison of the guest's results with the host's, written beside runtime-witos's other reports, a group's
    // apart: the report's line and what fails it.
    private static async Task<(string Report, IReadOnlyList<string> Problems)> ReportAsync(string root, KernelArchitecture architecture,
        string? group, NuGetSuiteSelection selection, IReadOnlyDictionary<string, NuGetSuiteResult> host,
        IReadOnlyDictionary<string, NuGetSuiteResult> guest)
    {
        var problems = new List<string>();
        foreach (var suite in selection.Suites)
        {
            if (!guest.TryGetValue(suite.Name, out var result))
            {
                problems.Add($"{suite.Name}: no summary in the guest");
                continue;
            }
            var reference = host[suite.Name];
            if (result.Failed > 0)
                problems.Add($"{suite.Name}: {result.Failed} failed in the guest: {string.Join("; ", result.Failures.Take(40))}");
            if (result.Total != reference.Total || result.Passed != reference.Passed)
                problems.Add($"{suite.Name}: the guest ran {result.Total} and passed {result.Passed}, the host ran {reference.Total} and passed {reference.Passed}");
        }
        var path = Path.Combine(root, "artifacts", "runtime-witos",
            group is null ? $"nuget-suites-{architecture.Name}.json" : $"nuget-suites-{architecture.Name}-{group}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            suites = selection.Suites.ToDictionary(suite => suite.Name, suite => new
            {
                package = selection.Repositories.Single(repository => repository.Name == suite.Repository).Package,
                host = host[suite.Name],
                guest = guest.GetValueOrDefault(suite.Name),
                excluded = suite.Excluded.Count
            }),
            problems
        }, new JsonSerializerOptions { WriteIndented = true }));
        var passed = guest.Values.Sum(result => result.Passed);
        var report = $"The NuGet libraries' suites{(group is null ? "" : $" of group {group}")} on witos-{architecture.Name}: " +
            $"{guest.Count} of {selection.Suites.Count} suites ran, " +
            $"{passed} tests passed as on the host, {guest.Values.Sum(result => result.Skipped)} not run, " +
            $"{selection.Suites.Sum(suite => suite.Excluded.Count)} exclusions ({path}).";
        return (report, problems);
    }

    // A repository's checkout of its pinned commit, with the submodules the selection names.
    private static async Task<string> PrepareSourcesAsync(string root, NuGetSuiteRepository repository)
    {
        var source = Path.Combine(root, ".tools", "nuget-suites", $"{repository.Name}-{repository.Commit[..12]}");
        if (!Directory.Exists(Path.Combine(source, ".git")))
        {
            if (Directory.Exists(source))
                Directory.Delete(source, recursive: true);
            Directory.CreateDirectory(source);
            Console.WriteLine($"Fetching {repository.Name} at {repository.Commit[..12]}...");
            await Git.RunAsync(source, ["init", "-q"]);
            await Git.RunAsync(source, ["remote", "add", "origin", repository.Url + ".git"]);
            await Git.RunAsync(source, ["fetch", "--depth=1", "origin", repository.Commit], 1800);
            await Git.RunAsync(source, ["checkout", "-q", "--detach", repository.Commit], 600);
            foreach (var submodule in repository.Submodules)
                await Git.RunAsync(source, ["submodule", "update", "--init", "--depth=1", "--", submodule], 1800);
        }
        var revision = (await Git.RunAsync(source, ["rev-parse", "HEAD"])).Trim();
        if (revision != repository.Commit)
            throw new InvalidDataException($"{source} is at {revision}, not the pinned {repository.Commit}; it was not changed.");
        return source;
    }

    // A pinned SDK for linux-x64, extracted once under .tools from its verified archive: its dotnet.
    private static async Task<string> RequireSdkAsync(string root, string version, string sha256)
    {
        var directory = Path.Combine(root, ".tools", $"dotnet-sdk-{version}");
        var dotnet = Path.Combine(directory, "dotnet");
        if (File.Exists(dotnet) && Directory.Exists(Path.Combine(directory, "sdk", version)))
            return dotnet;
        var archive = await Toolchain.RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", $"dotnet-sdk-{version}-linux-x64.tar.gz"),
            $"https://builds.dotnet.microsoft.com/dotnet/Sdk/{version}/dotnet-sdk-{version}-linux-x64.tar.gz", sha256);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        await Processes.RequireSuccessAsync(Toolchain.Tar(), ["-xzf", archive, "-C", directory], root);
        return dotnet;
    }

    // xunit's console runner for .NET from its verified package: the directory of xunit.console.dll.
    private static async Task<string> RequireXunitConsoleAsync(string root, NuGetPin pin)
    {
        var directory = XunitConsoleDirectory(root, pin);
        if (File.Exists(Path.Combine(directory, XUNIT_CONSOLE)))
            return directory;
        var package = await Toolchain.RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", $"xunit.runner.console.{pin.Version}.nupkg"),
            $"https://api.nuget.org/v3-flatcontainer/xunit.runner.console/{pin.Version}/xunit.runner.console.{pin.Version}.nupkg", pin.Sha256);
        ZipFile.ExtractToDirectory(package, Path.Combine(root, ".tools", $"xunit.runner.console-{pin.Version}"), overwriteFiles: true);
        return directory;
    }

    private static string XunitConsoleDirectory(string root, NuGetPin pin) =>
        Path.Combine(root, ".tools", $"xunit.runner.console-{pin.Version}", "tools", "net6.0");

    private static string SuiteOutput(string root, NuGetSuiteSelection selection, NuGetSuite suite)
    {
        var repository = selection.Repositories.Single(repository => repository.Name == suite.Repository);
        return Path.Combine(root, ".tools", "nuget-suites", $"{repository.Name}-{repository.Commit[..12]}", suite.Output);
    }

    #endregion
}

/// <summary>
/// The NuGet libraries' suites the guest runs (N2.2).
/// </summary>
/// <param name="Sdks">Pinned SDKs by version, with the SHA-256 of each one's linux-x64 archive.</param>
/// <param name="XunitConsole">xunit's console runner package.</param>
/// <param name="Repositories">The libraries' repositories.</param>
/// <param name="Suites">The test projects.</param>
internal sealed record NuGetSuiteSelection(IReadOnlyDictionary<string, string> Sdks, NuGetPin XunitConsole,
    IReadOnlyList<NuGetSuiteRepository> Repositories, IReadOnlyList<NuGetSuite> Suites);

/// <summary>
/// A pinned download.
/// </summary>
/// <param name="Version">Its version.</param>
/// <param name="Sha256">The SHA-256 of its bytes.</param>
internal sealed record NuGetPin(string Version, string Sha256);

/// <summary>
/// A library's repository at the commit its package was built from.
/// </summary>
/// <param name="Name">Its name in the selection and its checkout's.</param>
/// <param name="Package">The package and version N2.1 pins.</param>
/// <param name="Url">The repository.</param>
/// <param name="Commit">The commit.</param>
/// <param name="Sdk">The pinned SDK its global.json asks for, or null for the host's.</param>
/// <param name="Submodules">Submodule paths a suite needs.</param>
internal sealed record NuGetSuiteRepository(string Name, string Package, string Url, string Commit, string? Sdk,
    IReadOnlyList<string> Submodules);

/// <summary>
/// A test project of a library and how its repository runs it.
/// </summary>
/// <param name="Name">Its name, and its directory under /suites.</param>
/// <param name="Repository">Its repository's name.</param>
/// <param name="Group">The group it runs in, one boot of the guest.</param>
/// <param name="Project">The project under the checkout.</param>
/// <param name="Framework">The framework it is built for.</param>
/// <param name="Output">Its build output under the checkout.</param>
/// <param name="Assembly">The test assembly.</param>
/// <param name="Runner">xunit2, xunit3 or nunitlite.</param>
/// <param name="Program">The program that runs the assembly, for a NUnit suite that is no program itself.</param>
/// <param name="Excluded">'class:' or 'method:' and a full name, and the reasons.</param>
/// <param name="Arguments">The runner's other options, after the selection's own.</param>
internal sealed record NuGetSuite(string Name, string Repository, string Group, string Project, string Framework, string Output,
    string Assembly, string Runner, string? Program, IReadOnlyDictionary<string, string> Excluded, IReadOnlyList<string> Arguments);

/// <summary>
/// What a run of a suite reported.
/// </summary>
/// <param name="Total">The tests it counted.</param>
/// <param name="Passed">Those that passed.</param>
/// <param name="Failed">Those that failed, with xunit's errors.</param>
/// <param name="Skipped">Those that neither passed nor failed: skipped, ignored, inconclusive or explicit.</param>
/// <param name="Failures">The failed tests the runner named.</param>
internal sealed record NuGetSuiteResult(int Total, int Passed, int Failed, int Skipped, IReadOnlyList<string> Failures);
