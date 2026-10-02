using System.Reflection;
using System.Text.RegularExpressions;
using WitOS.Dev.Commands;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Repository;

/// <summary>
/// CI workflows: required gates, trigger paths and commands that exist in the tool.
/// </summary>
[TestFixture]
public sealed class WorkflowTests
{
    #region Functions

    [Test]
    public void CoreClrHostCiPrerequisitesTest()
    {
        var root = TestEnvironment.Root;
        var ci = File.ReadAllText(Path.Combine(root, ".github/workflows/coreclr-host.yml"));
        var pr = ci.IndexOf("  pull_request:", StringComparison.Ordinal);
        var permissions = ci.IndexOf("permissions:", StringComparison.Ordinal);
        Assert.That(pr > 0 && permissions > pr, Is.True, "Hosting CI trigger sections missing");
        foreach (var section in new[] { ci[..pr], ci[pr..permissions] })
            Assert.That(section.Contains("experiments/CoreClrHost/**", StringComparison.Ordinal) && section.Contains("upstream.lock.json", StringComparison.Ordinal), Is.True, "Hosting/pin changes do not select CI");
        var source = ci.IndexOf("-- coreclr-source", StringComparison.Ordinal);
        Assert.That(source >= 0 && ci.IndexOf("-- coreclr-host", StringComparison.Ordinal) > source, Is.True, "Hosting reference lacks its source-built runtime prerequisite");
        Assert.That(ci.Contains("-- coreclr-host-files", StringComparison.Ordinal), Is.True, "Hosting PAL contract CI gate missing");
    }

    [Test]
    public void BootChangesSelectManagedCiTest()
    {
        var root = TestEnvironment.Root;
        var ci = File.ReadAllText(Path.Combine(root, ".github/workflows/nativeaot.yml"));
        var pr = ci.IndexOf("  pull_request:", StringComparison.Ordinal);
        var dispatch = ci.IndexOf("  workflow_dispatch:", StringComparison.Ordinal);
        Assert.That(pr > 0 && dispatch > pr, Is.True, "CI trigger sections missing");
        foreach (var section in new[] { ci[..pr], ci[pr..dispatch] })
            Assert.That(section.Contains("- 'src/Boot.Uefi/**'", StringComparison.Ordinal) && section.Contains("- 'tests/WitOS.Dev.Tests/**'", StringComparison.Ordinal) && section.Contains("- 'tests/WitOS.Dev.Tests.Child/**'", StringComparison.Ordinal) && section.Contains("- 'src/Runtime.CoreClr/**'", StringComparison.Ordinal), Is.True, "Boot/tool/CoreCLR paths missing from a trigger");
        foreach (var command in new[] { "runtime-source", "runtime-config", "runtime-boot-run", "-- test", "TestCategory=PeCoverage", "TestCategory=PeFuzz", "coreclr-functions", "coreclr-memory", "coreclr-storage" })
            Assert.That(ci.Contains(command, StringComparison.Ordinal), Is.True, "Required M3 CI gate missing: " + command);
    }

    // Every dev tool command a CI workflow invokes must exist in the command catalog.
    [Test]
    public void WorkflowCommandsExistTest()
    {
        var root = TestEnvironment.Root;
        var known = CommandLine.CommandNames.ToHashSet(StringComparer.Ordinal);
        var used = 0;
        foreach (var workflow in Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml"))
        {
            var pattern = @"--project tools/WitOS\.Dev .*?-- ([a-z0-9-]+)";
            foreach (Match match in Regex.Matches(File.ReadAllText(workflow), pattern))
            {
                ++used;
                Assert.That(known.Contains(match.Groups[1].Value), Is.True, $"{Path.GetFileName(workflow)} runs unknown command {match.Groups[1].Value}");
            }
        }
        Assert.That(used > 0, Is.True, "No dev tool command invocations were found in the workflows");
    }

    [Test]
    public void WorkflowTestCategoriesExistTest()
    {
        var declared = typeof(TestCategories).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        var tagged = typeof(WorkflowTests).Assembly.GetTypes().SelectMany(type => type.GetMethods())
            .SelectMany(method => method.GetCustomAttributes<CategoryAttribute>()).Select(category => category.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.That(tagged, Is.EquivalentTo(declared), "Every declared category must tag a test, and only declared ones");
        var used = 0;
        foreach (var workflow in Directory.EnumerateFiles(Path.Combine(TestEnvironment.Root, ".github", "workflows"), "*.yml"))
        {
            var pattern = @"dotnet test tests/WitOS\.Dev\.Tests .*?--filter TestCategory=(\w+)";
            foreach (Match match in Regex.Matches(File.ReadAllText(workflow), pattern))
            {
                ++used;
                Assert.That(declared, Does.Contain(match.Groups[1].Value),
                    $"{Path.GetFileName(workflow)} selects unknown test category {match.Groups[1].Value}");
            }
        }
        Assert.That(used, Is.GreaterThan(0), "No test category selections were found in the workflows");
    }

    #endregion
}
