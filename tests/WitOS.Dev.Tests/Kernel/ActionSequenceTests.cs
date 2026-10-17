using WitOS.Dev.Kernel;

namespace WitOS.Dev.Tests.Kernel;

/// <summary>
/// The runner's actions in order (plan step C2.3): each after its line, found after the previous action's line in the
/// serial log or the transcript, so that a prompt shown again fires one action a time; an action without a line follows
/// the previous one at once.
/// </summary>
[TestFixture]
public sealed class ActionSequenceTests
{
    #region Tests

    [Test]
    public void ActionsFollowTheirLinesInOrderTest()
    {
        var actions = new ActionSequence(
        [
            new BootAction("PS>", BootActionKind.Key, "a"),
            new BootAction("PS>", BootActionKind.Key, "b"),
            new BootAction("PS>", BootActionKind.Screendump),
            new BootAction("", BootActionKind.Key, "c")
        ]);
        Assert.That(actions.Next("boot", ""), Is.Null);
        Assert.That(actions.Next("boot", "PS> ")?.Argument, Is.EqualTo("a"));
        Assert.That(actions.Next("boot", "PS> "), Is.Null, "the second action needs a second prompt");
        Assert.That(actions.Taken, Is.EqualTo(1));
        Assert.That(actions.Next("boot", "PS> x\r\nPS> ")?.Argument, Is.EqualTo("b"));
        Assert.That(actions.Next("boot PS>", "PS> x\r\nPS> ")?.Kind, Is.EqualTo(BootActionKind.Screendump), "a line of the serial log counts too");
        Assert.That(actions.Next("boot PS>", "PS> x\r\nPS> ")?.Argument, Is.EqualTo("c"), "an action without a line follows the previous one at once");
        Assert.That(actions.Done, Is.True);
        Assert.That(actions.Next("boot PS> PS>", "PS> PS> PS>"), Is.Null);
    }

    // The runner types a key once the guest's keyboard driver reported the previous one (C2.3): it counts the driver's
    // key lines and nothing else of the driver's.
    [Test]
    public void KeyReportsCountTheDriversKeyLinesTest()
    {
        const string log = "[USER] [VIRTIO-INPUT] driver of pci/00:06.0: ready" + "\n" +
            "[USER] [VIRTIO-INPUT] key 35 down" + "\n" + "[USER] [VIRTIO-INPUT] key 35 up" + "\n" + "[USER] [DEVICES] key 1" + "\n";
        Assert.That(BootScenarioRunner.KeyReports(log), Is.EqualTo(2));
        Assert.That(BootScenarioRunner.KeyReports(""), Is.Zero);
    }

    [Test]
    public void TheSameLineInBothStreamsFiresOnceTest()
    {
        var actions = new ActionSequence([new BootAction("ready", BootActionKind.Key, "a"), new BootAction("ready", BootActionKind.Key, "b")]);
        Assert.That(actions.Next("ready", "ready")?.Argument, Is.EqualTo("a"));
        Assert.That(actions.Next("ready", "ready")?.Argument, Is.EqualTo("b"), "the transcript's line serves the second action");
        Assert.That(actions.Next("ready", "ready"), Is.Null);
    }

    #endregion
}
