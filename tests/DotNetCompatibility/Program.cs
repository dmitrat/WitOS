// The .NET compatibility suite (plan step N1): every area's checks, each with a time limit, compared with the
// differences expectations.json records for the platform it runs on. A check that fails without a recorded difference,
// or passes where one is recorded, is unexpected; the run exits with 0 when nothing is.
using System.Runtime.InteropServices;
using System.Text.Json;
using DotNetCompatibility;

var platform = PlatformChecks.Key;
var recorded = Expectations(platform);
IEnumerable<Check>[] areas =
[
    PlatformChecks.All(), ConsoleChecks.All(), TaskChecks.All(), ThreadChecks.All(), TimerChecks.All(), ExceptionChecks.All(),
    ReflectionChecks.All(), FileChecks.All(), AssemblyLoadingChecks.All(), GcChecks.All(), SynchronizationChecks.All()
];
var checks = areas.SelectMany(area => area).ToList();
int differences = 0, unexpected = 0;
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
    var reason = recorded.GetValueOrDefault(check.Id);
    var expected = reason is null ? result.Passed : !result.Passed;
    differences += reason is not null && expected ? 1 : 0;
    unexpected += expected ? 0 : 1;
    Console.WriteLine($"[N1] {check.Id}: {(result.Passed ? "pass" : "fail")} ({result.Observed})" +
        (reason is null ? "" : $"; recorded difference: {reason}") + (expected ? "" : "; UNEXPECTED"));
}
FileChecks.Clean();
Console.WriteLine($"[N1] .NET compatibility on {platform} ({RuntimeInformation.ProcessArchitecture}): {checks.Count} checks, " +
    $"{differences} recorded differences, {unexpected} unexpected");
return unexpected == 0 ? 0 : 1;

// The checks expected to fail on the platform, each with the reason the difference is intended.
static Dictionary<string, string> Expectations(string platform)
{
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "expectations.json")));
    return document.RootElement.TryGetProperty(platform, out var differences)
        ? differences.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString() ?? "")
        : [];
}
