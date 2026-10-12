using WitOS.Dev.Host;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// Representative NuGet libraries on the host (plan step N2.1, Implementation Strategy §59): the NUnit and xUnit
/// applications the guest runs, built with the solution and run through dotnet on Windows and on Linux.
/// </summary>
[TestFixture]
public sealed class RepresentativeLibrariesTests
{
    #region Tests

    [TestCase("NUnitApp", "Overall result: Passed")]
    [TestCase("XUnitApp", "Errors: 0, Failed: 0, Skipped: 0")]
    public async Task ApplicationPassesTest(string application, string summary)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var path = Path.Combine(TestEnvironment.Root, "tests", "RepresentativeLibraries", application, "bin", configuration, "net10.0", application + ".dll");
        if (!File.Exists(path))
            Assert.Ignore($"{application} is not built for {configuration}: build WitOS.slnx.");

        var result = await Processes.RunAsync("dotnet", [path], Path.GetDirectoryName(path)!, 600);

        Assert.That(result.ExitCode, Is.Zero, result.Output);
        Assert.That(result.Output, Does.Contain(summary));
    }

    #endregion
}
