using WitOS.Dev.Host;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// The .NET compatibility suite on the host (plan step N1, Implementation Strategy §58): the same application the guest
/// runs, built with the solution and run through dotnet on Windows and on Linux, with no difference expectations.json does
/// not record.
/// </summary>
[TestFixture]
public sealed class DotNetCompatibilityTests
{
    #region Tests

    [Test]
    public async Task SuiteRunsAsRecordedTest()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var suite = Path.Combine(TestEnvironment.Root, "tests", "DotNetCompatibility", "bin", configuration, "net10.0", "DotNetCompatibility.dll");
        if (!File.Exists(suite))
            Assert.Ignore($"The suite is not built for {configuration}: build WitOS.slnx.");

        var result = await Processes.RunAsync("dotnet", [suite], Path.GetDirectoryName(suite)!, 600);

        Assert.That(result.ExitCode, Is.Zero, result.Output);
        Assert.That(result.Output, Does.Contain(" recorded differences, 0 unexpected"));
    }

    #endregion
}
