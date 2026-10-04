using WitOS.Dev.Host;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Host;

/// <summary>
/// Bounded output capture: the exact limit, overflow and oversized evidence files.
/// </summary>
[TestFixture]
public sealed class BoundedCaptureTests
{
    #region Functions

    [Test]
    public async Task BoundedCaptureTest()
    {
        var root = TestEnvironment.Root;
        var scratch = TestEnvironment.Scratch();
        var capture = new BoundedCapture();
        capture.Append(new string('x', BoundedCapture.LIMIT));
        Assert.That(!capture.Truncated, Is.True, "Exact capture limit rejected");
        capture.Append("y");
        Assert.That(capture.Truncated && capture.Snapshot().Length == BoundedCapture.LIMIT, Is.True, "Capture is unbounded");
        var file = Path.Combine(scratch, "oversized-serial.log");
        using (var output = File.Create(file))
            output.SetLength(BoundedCapture.LIMIT + 1L);
        bool rejected = false;
        try
        { await BoundedCapture.ReadFileAsync(file); }
        catch (InvalidDataException) { rejected = true; }
        Assert.That(rejected, Is.True, "Oversized serial file accepted");
        foreach (var stream in new[] { "stdout", "stderr" })
        {
            rejected = false;
            try
            { await Processes.RunAsync("dotnet", [TestEnvironment.ChildAssembly, "capture-limit", stream], root, 15); }
            catch (InvalidDataException) { rejected = true; }
            Assert.That(rejected, Is.True, "Truncated process output accepted: " + stream);
        }
    }

    #endregion
}
