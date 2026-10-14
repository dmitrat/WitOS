using WitOS.Dev.Host;
using WitOS.Dev.Runtime;
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

    // The runner types what the suite's Terminal checks ask for (N4.3b): the prompts the tool answers must be the ones
    // the suite prints, and the line the tool types the one the suite expects.
    [Test]
    public void TerminalPromptsAreTheSuitesTest()
    {
        var source = File.ReadAllText(Path.Combine(TestEnvironment.Root, "tests", "DotNetCompatibility", "Terminal", "TerminalChecks.cs"));
        foreach (var prompt in new[]
                 {
                     RuntimeCompatibility.TerminalPrompts.LINE, RuntimeCompatibility.TerminalPrompts.LINE_TYPED,
                     RuntimeCompatibility.TerminalPrompts.KEY, RuntimeCompatibility.TerminalPrompts.KEY_AVAILABLE,
                     RuntimeCompatibility.TerminalPrompts.CANCEL, RuntimeCompatibility.TerminalPrompts.SIGNAL
                 })
            Assert.That(source, Does.Contain($"\"{prompt}\""), $"the suite does not print or expect \"{prompt}\"");
    }

    #endregion
}
