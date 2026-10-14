using WitOS.Dev.Runtime;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// The compatibility matrix (plan step N4.5): the committed document is what the suite's checks, expectations.json and
/// the list beyond the suite render, the list's statuses are the matrix's, and the rendering reads the recorded
/// differences into each area's status.
/// </summary>
[TestFixture]
public sealed class CompatibilityMatrixTests
{
    #region Tests

    // The document is current: compatibility-matrix would write the same text.
    [Test]
    public async Task DocumentIsCurrentTest()
    {
        if (!File.Exists(CompatibilityMatrix.SuitePath(TestEnvironment.Root)))
            Assert.Ignore("The suite is not built for this configuration: build WitOS.slnx.");
        var generated = await CompatibilityMatrix.GenerateAsync(TestEnvironment.Root);
        var committed = await File.ReadAllTextAsync(Path.Combine(TestEnvironment.Root, CompatibilityMatrix.DOCUMENT));
        Assert.That(committed.Replace("\r\n", "\n"), Is.EqualTo(generated),
            $"{CompatibilityMatrix.DOCUMENT} is not what compatibility-matrix renders: run the command and commit the document.");
    }

    // Every capability beyond the suite has a known status, a step and a fact.
    [Test]
    public async Task BeyondTheSuiteIsCompleteTest()
    {
        var beyond = await CompatibilityMatrix.ReadBeyondAsync(TestEnvironment.Root);
        Assert.That(beyond.Capabilities, Has.Count.GreaterThanOrEqualTo(10));
        Assert.That(beyond.Capabilities.Select(capability => capability.Name), Is.Unique);
        Assert.That(beyond.Hardware.Select(hardware => hardware.Platform), Does.Contain("QEMU x64 (q35)").And.Contain("QEMU ARM64 (virt)"));
    }

    // An area with a recorded difference is partial on that platform alone, naming the check.
    [Test]
    public void RecordedDifferencesMakeAnAreaPartialTest()
    {
        var expectations = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["windows"] = new Dictionary<string, string>(),
            ["linux"] = new Dictionary<string, string>(),
            ["witos"] = new Dictionary<string, string> { ["FileIO.Watcher"] = "no notifications" }
        };
        var beyond = new CompatibilityMatrix.Beyond([new CompatibilityMatrix.Capability("Networking", "Planned", "M7", "absent")],
            [new CompatibilityMatrix.Hardware("QEMU x64 (q35)", "Primary", "every gate")]);
        var text = CompatibilityMatrix.Render(["FileIO.Read", "FileIO.Watcher", "Tasks.WhenAll"], expectations, beyond);
        Assert.That(text, Does.Contain("| FileIO | 2 | Supported | Supported | Partial: Watcher |"));
        Assert.That(text, Does.Contain("| Tasks | 1 | Supported | Supported | Supported |"));
        Assert.That(text, Does.Contain("| `FileIO.Watcher` | WitOS (x64, ARM64) | no notifications |"));
        Assert.That(text, Does.Contain("| Networking | Planned | M7 | absent |"));
        Assert.That(text, Does.Contain("3 checks in 2 areas."));
    }

    #endregion
}
