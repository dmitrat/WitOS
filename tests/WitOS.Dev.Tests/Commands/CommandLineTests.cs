using WitOS.Dev.Commands;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Commands;

/// <summary>
/// Command-line failures: a failed build invalidates the current acceptance and reports an error.
/// </summary>
[TestFixture]
public sealed class CommandLineTests
{
    #region Functions

    [Test]
    public async Task FailedBuildInvalidatesCurrentAcceptanceTest()
    {
        var root = TestEnvironment.Root;
        var scratch = TestEnvironment.Scratch();
        var isolated = Path.Combine(scratch, "failed-root");
        var dir = Path.Combine(isolated, "artifacts/x64/runtime-boot");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(isolated, "WitOS.slnx"), "<Solution />");
        var prior = Path.Combine(dir, "acceptance.json");
        File.WriteAllText(prior, "{\"prior\":true}");
        var cwd = Environment.CurrentDirectory;
        var stderr = Console.Error;
        using var errors = new StringWriter();
        int exit;
        try
        { Console.SetError(errors); Environment.CurrentDirectory = isolated; exit = await CommandLine.RunAsync(["runtime-boot-run"]); }
        finally { Environment.CurrentDirectory = cwd; Console.SetError(stderr); }
        Assert.That(errors.ToString().Contains("ERROR:"), Is.True, "Expected diagnostic missing");
        Assert.That(exit != 0, Is.True, "Missing source should fail build");
        Assert.That(!File.Exists(prior), Is.True, "Previous success still current after failure");
    }

    #endregion
}
