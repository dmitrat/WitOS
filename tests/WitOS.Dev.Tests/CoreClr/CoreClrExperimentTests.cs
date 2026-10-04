using System.Text.Json.Nodes;
using WitOS.Dev.CoreClr;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.CoreClr;

/// <summary>
/// CoreCLR source experiment: a mismatched source pin is rejected.
/// </summary>
[TestFixture]
public sealed class CoreClrExperimentTests
{
    #region Functions

    [Test]
    public async Task CoreClrProfilePinMismatchTest()
    {
        var root = TestEnvironment.Root;
        var scratch = TestEnvironment.Scratch();
        var isolated = Path.Combine(scratch, "coreclr-profile");
        Directory.CreateDirectory(Path.Combine(isolated, "experiments/NativeAotProbe"));
        Directory.CreateDirectory(Path.Combine(isolated, "experiments/CoreClrProbe"));
        File.Copy(Path.Combine(root, "experiments/NativeAotProbe/upstream.lock.json"), Path.Combine(isolated, "experiments/NativeAotProbe/upstream.lock.json"));
        var profile = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, "experiments/CoreClrProbe/profile.json")))!;
        profile["runtimeCommit"] = new string('0', 40);
        File.WriteAllText(Path.Combine(isolated, "experiments/CoreClrProbe/profile.json"), profile.ToJsonString());
        bool rejected = false;
        try
        { await CoreClrExperiment.RunAsync(isolated); }
        catch (InvalidDataException) { rejected = true; }
        Assert.That(rejected, Is.True, "CoreCLR mismatched source pin accepted");
    }

    #endregion
}
