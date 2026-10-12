using System.Text.RegularExpressions;
using WitOS.Dev.Runtime;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// The representative NuGet libraries' own test suites (plan step N2.2): the selection names pinned repositories and
/// runners and gives every exclusion its reason; its groups are CI's jobs; each runner gets the exclusions in its own
/// options; and the runners' summaries and failed tests are read from a host run and from the guest's log.
/// </summary>
[TestFixture]
public sealed class RuntimeNuGetSuitesTests
{
    #region Functions

    // The selection reads, covers the six libraries' repositories, and runs every suite by a known runner.
    [Test]
    public void SelectionIsCompleteTest()
    {
        var selection = RuntimeNuGetSuites.ReadSelection(TestEnvironment.Root);
        Assert.That(selection.Repositories.Select(repository => repository.Name),
            Is.EquivalentTo(new[] { "MessagePack", "MemoryPack", "MathNet", "NUnit", "xUnit", "Roslyn" }));
        Assert.That(selection.Suites, Has.All.Matches<NuGetSuite>(suite => suite.Runner is "xunit2" or "xunit3" or "nunitlite"));
        Assert.That(selection.Suites.SelectMany(suite => suite.Excluded.Values), Has.All.Not.Empty);
    }

    // A group takes its suites and the repositories they need, and CI's job of the suites runs every group on each ISA.
    [Test]
    public void GroupsAreCiJobsTest()
    {
        var selection = RuntimeNuGetSuites.ReadSelection(TestEnvironment.Root);
        var roslyn = RuntimeNuGetSuites.SelectGroup(selection, "roslyn");
        Assert.That(roslyn.Suites.Select(suite => suite.Name), Is.EqualTo(new[] { "Microsoft.CodeAnalysis.UnitTests" }));
        Assert.That(roslyn.Repositories.Select(repository => repository.Name), Is.EqualTo(new[] { "Roslyn" }));
        Assert.That(RuntimeNuGetSuites.SelectGroup(selection, null), Is.SameAs(selection));
        Assert.Throws<ArgumentException>(() => RuntimeNuGetSuites.SelectGroup(selection, "none"));
        var workflow = File.ReadAllText(Path.Combine(TestEnvironment.Root, ".github", "workflows", "kernel.yml"));
        var matrix = Regex.Match(workflow, @"^\s+group: \[([^\]]+)\]", RegexOptions.Multiline);
        Assert.That(matrix.Success, Is.True, "kernel.yml has no matrix of the suites' groups");
        Assert.That(matrix.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries),
            Is.EquivalentTo(selection.Suites.Select(suite => suite.Group).Distinct()));
    }

    // xunit's runners take -noclass and -nomethod; NUnitLite one --where of its test selection language.
    [Test]
    public void ExclusionsUseTheRunnersOptionsTest()
    {
        var excluded = new Dictionary<string, string> { ["class:A.B"] = "a reason", ["method:A.C.D"] = "a reason" };
        var xunit2 = new NuGetSuite("S", "R", "G", "p.csproj", "net8.0", "bin", "S.dll", "xunit2", null, excluded, []);
        Assert.That(RuntimeNuGetSuites.DotnetArguments(xunit2, "/r/xunit.console.dll"), Is.EqualTo(new[]
        {
            "exec", "--roll-forward", "Major", "/r/xunit.console.dll", "S.dll", "-nologo", "-nocolor", "-noclass", "A.B",
            "-nomethod", "A.C.D"
        }));
        var xunit3 = xunit2 with { Runner = "xunit3", Excluded = new Dictionary<string, string>() };
        Assert.That(RuntimeNuGetSuites.DotnetArguments(xunit3, "/r/xunit.console.dll"),
            Is.EqualTo(new[] { "exec", "--roll-forward", "Major", "S.dll", "-nologo", "-nocolor" }));
        var nunit = xunit2 with { Runner = "nunitlite", Program = "nunitlite-runner.dll", Arguments = ["--labels=Before"] };
        Assert.That(RuntimeNuGetSuites.DotnetArguments(nunit, "/r/xunit.console.dll"), Is.EqualTo(new[]
        {
            "exec", "--roll-forward", "Major", "nunitlite-runner.dll", "S.dll", "--noresult", "--nocolor",
            "--where=class!='A.B'&&test!='A.C.D'", "--labels=Before"
        }));
    }

    // xunit's summary counts errors as failures; NUnit's counts a warning as a pass and everything that neither passed nor
    // failed as not run.
    [Test]
    public void SummariesAndFailuresAreReadTest()
    {
        var xunit = RuntimeNuGetSuites.ReadResult("xunit2",
        [
            "    MemoryPack.Tests.GeneratorDiagnosticsTest.MEMPACK004_MultipleCtorWithoutAttribute [FAIL]",
            "      Expected diagnostics[0].Id to be \"MEMPACK004\"",
            "=== TEST EXECUTION SUMMARY ===",
            "   MemoryPack.Tests  Total: 157, Errors: 1, Failed: 33, Skipped: 2, Time: 3.892s"
        ]);
        Assert.That(xunit, Is.Not.Null);
        Assert.That((xunit!.Total, xunit.Passed, xunit.Failed, xunit.Skipped), Is.EqualTo((157, 121, 34, 2)));
        Assert.That(xunit.Failures, Is.EqualTo(new[] { "MemoryPack.Tests.GeneratorDiagnosticsTest.MEMPACK004_MultipleCtorWithoutAttribute" }));
        var nunit = RuntimeNuGetSuites.ReadResult("nunitlite",
        [
            "Errors, Failures and Warnings",
            "1) Failed : NUnit.Framework.Tests.A.B",
            "2) Error : NUnit.Framework.Tests.A.C",
            "Tests Not Run",
            "1) Explicit : NUnit.Framework.Tests.A.D",
            "  Overall result: Failed",
            "  Test Count: 22226, Passed: 20868, Failed: 2, Warnings: 3, Inconclusive: 1308, Skipped: 45"
        ]);
        Assert.That(nunit, Is.Not.Null);
        Assert.That((nunit!.Total, nunit.Passed, nunit.Failed, nunit.Skipped), Is.EqualTo((22226, 20871, 2, 1353)));
        Assert.That(nunit.Failures, Is.EqualTo(new[] { "NUnit.Framework.Tests.A.B", "NUnit.Framework.Tests.A.C" }));
        Assert.That(RuntimeNuGetSuites.ReadResult("xunit3", ["no summary"]), Is.Null);
    }

    // The guest's log is split at apps_init's line for each suite's directory, under the kernel's prefix.
    [Test]
    public void GuestLogIsSplitBySuiteTest()
    {
        var selection = RuntimeNuGetSuites.ReadSelection(TestEnvironment.Root);
        var results = RuntimeNuGetSuites.ReadGuestResults(selection,
        [
            "[USER] [APPS-INIT] in /data/suites/MessagePack.Tests",
            "[USER]    MessagePack.Tests  Total: 1088, Errors: 0, Failed: 0, Skipped: 6, Time: 300.1s",
            "[USER] [APPS-INIT] /dotnet/dotnet exec exited with 0 after 310 s",
            "[USER] [APPS-INIT] in /data/suites/MathNet.Numerics.Tests",
            "[USER]   Test Count: 10, Passed: 10, Failed: 0, Warnings: 0, Inconclusive: 0, Skipped: 0",
            "[USER] [APPS-INIT] in /data/suites/nunitlite.tests"
        ]);
        Assert.That(results.Keys, Is.EquivalentTo(new[] { "MessagePack.Tests", "MathNet.Numerics.Tests" }));
        Assert.That(results["MessagePack.Tests"].Passed, Is.EqualTo(1082));
        Assert.That(results["MathNet.Numerics.Tests"].Total, Is.EqualTo(10));
    }

    #endregion
}
