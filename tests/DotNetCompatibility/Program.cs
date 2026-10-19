// The .NET compatibility suite (plan step N1): every area's checks, each with a time limit, compared with the
// differences expectations.json records for the platform it runs on. A check that fails without a recorded difference,
// or passes where one is recorded, is unexpected; the run exits with 0 when nothing is.
using System.Runtime.InteropServices;
using System.Text.Json;
using DotNetCompatibility;

// The suite as the child of its own process checks (Platform, N4.2): "--echo CODE" copies its input to its output,
// reports on its error stream and exits with CODE; "--sleep" waits to be killed.
if (args is ["--echo", var code])
{
    var input = await Console.In.ReadToEndAsync();
    await Console.Out.WriteAsync(input);
    await Console.Out.FlushAsync();
    await Console.Error.WriteAsync($"echoed {input.Length} characters in {Environment.CurrentDirectory} with " +
        $"DOTNET_COMPATIBILITY_CHILD={Environment.GetEnvironmentVariable("DOTNET_COMPATIBILITY_CHILD")}\n");
    await Console.Error.FlushAsync();
    return int.Parse(code);
}
if (args is ["--sleep"])
{
    Thread.Sleep(Timeout.Infinite);
    return 0;
}
// Arguments the suite does not know are refused, never a run of the suite: a child started with them would otherwise run
// every check again and start children of its own.
if (args is not ([] or ["--list"]))
{
    await Console.Error.WriteLineAsync($"unknown arguments: {string.Join(' ', args)}");
    return 2;
}

var platform = PlatformChecks.Key;
var recorded = Expectations(platform);
IEnumerable<Check>[] areas =
[
    PlatformChecks.All(), ConsoleChecks.All(), TaskChecks.All(), ThreadChecks.All(), TimerChecks.All(), ExceptionChecks.All(),
    ReflectionChecks.All(), FileChecks.All(), AssemblyLoadingChecks.All(), GcChecks.All(), SynchronizationChecks.All(),
    GlobalizationChecks.All(), TimeZoneChecks.All(), TerminalChecks.All(), NetworkChecks.All()
];
var checks = areas.SelectMany(area => area).ToList();
// The compatibility matrix (Strategy §145, N4.5) lists the checks from the suite itself: "--list" prints each check's id
// and runs none.
if (args is ["--list"])
{
    foreach (var check in checks)
        Console.WriteLine(check.Id);
    return 0;
}
int differences = 0, unexpected = 0, notApplicable = 0;
foreach (var check in checks)
{
    CheckResult result;
    try
    {
        result = await check.Run().WaitAsync(TimeSpan.FromSeconds(60));
    }
    catch (TimeoutException)
    {
        result = new CheckResult(false, "no result within 60 s");
    }
    catch (Exception exception)
    {
        result = new CheckResult(false, $"threw {exception.GetType().Name}: {exception.Message}");
    }
    if (!result.Applicable)
    {
        // A check that cannot run here (the terminal's without a terminal, N4.3b) is neither a pass nor a failure.
        notApplicable++;
        Console.WriteLine($"[N1] {check.Id}: not applicable ({result.Observed})");
        continue;
    }
    var reason = recorded.GetValueOrDefault(check.Id);
    var expected = reason is null ? result.Passed : !result.Passed;
    differences += reason is not null && expected ? 1 : 0;
    unexpected += expected ? 0 : 1;
    Console.WriteLine($"[N1] {check.Id}: {(result.Passed ? "pass" : "fail")} ({result.Observed})" +
        (reason is null ? "" : $"; recorded difference: {reason}") + (expected ? "" : "; UNEXPECTED"));
}
FileChecks.Clean();
Console.WriteLine($"[N1] .NET compatibility on {platform} ({RuntimeInformation.ProcessArchitecture}): {checks.Count} checks, " +
    $"{differences} recorded differences, {unexpected} unexpected, {notApplicable} not applicable");
return unexpected == 0 ? 0 : 1;

// The checks expected to fail on the platform, each with the reason the difference is intended.
static Dictionary<string, string> Expectations(string platform)
{
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "expectations.json")));
    return document.RootElement.TryGetProperty(platform, out var differences)
        ? differences.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString() ?? "")
        : [];
}
