using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.Terminal.Server;
using OutWit.OS.Terminals;

namespace OutWit.OS.Tests.Terminal;

/// <summary>
/// The terminal service's pseudo-terminals (plan step E4.1): the master types at the slave's discipline and reads its
/// output after output processing, the slave side hangs up when the master goes and the master sees the slave side's
/// end, the size and the signal characters reach the slave's sessions, a program's write waits for room, and the
/// numbers are the lowest free, as Linux's devpts gives them.
/// </summary>
[TestFixture]
public sealed class PseudoTerminalTests
{
    #region Tests

    [Test]
    public void MasterTypesALineAndReadsItsEchoTest()
    {
        var (terminal, master, slave) = Open();
        terminal.Type("hi\r"u8);
        Assert.That(terminal.Slave.Read(100, true, out var line), Is.Zero);
        Assert.That(Encoding.ASCII.GetString(line), Is.EqualTo("hi\n"), "ICRNL: the return ends the line");
        Assert.That(terminal.Read(100, true, out var echo), Is.Zero);
        Assert.That(Encoding.ASCII.GetString(echo), Is.EqualTo("hi\r\n"), "the echo through ONLCR");
        Assert.That(master.Events, Does.Contain(TerminalMessages.EVENT_INPUT));
        Assert.That(slave.Events, Does.Contain(TerminalMessages.EVENT_INPUT));
    }

    [Test]
    public void SlaveOutputReachesTheMasterThroughOutputProcessingTest()
    {
        var (terminal, master, _) = Open();
        Assert.That(terminal.Slave.Write("a\nb"u8), Is.Zero);
        Assert.That(terminal.Poll() & TerminalMessages.INPUT_READY, Is.Not.Zero);
        Assert.That(terminal.Read(2, true, out var first), Is.Zero);
        Assert.That(terminal.Read(100, true, out var rest), Is.Zero);
        Assert.That(Encoding.ASCII.GetString(first) + Encoding.ASCII.GetString(rest), Is.EqualTo("a\r\nb"));
        Assert.That(terminal.Read(100, true, out _), Is.EqualTo(TerminalMessages.AGAIN));
        Assert.That(master.Events, Does.Contain(TerminalMessages.EVENT_INPUT));
    }

    [Test]
    public void MasterWaitsUntilASlaveJoinedTest()
    {
        var pseudo = new PseudoTerminals();
        var terminal = pseudo.Open()!;
        terminal.Open(new Recorder());
        Assert.That(terminal.Read(100, true, out _), Is.EqualTo(TerminalMessages.AGAIN), "no slave joined yet: no hangup");
        Assert.That(terminal.Poll() & TerminalMessages.HANGUP, Is.Zero);
    }

    [Test]
    public void MasterSeesTheLastSlaveLeaveTest()
    {
        var (terminal, master, slave) = Open();
        terminal.Slave.Write("bye"u8);
        terminal.Slave.Remove(slave);
        Assert.That(master.Events, Does.Contain(TerminalMessages.EVENT_HANGUP));
        Assert.That(terminal.Read(100, true, out var left), Is.Zero, "what the slave wrote is read first");
        Assert.That(Encoding.ASCII.GetString(left), Is.EqualTo("bye"));
        Assert.That(terminal.Read(100, true, out _), Is.EqualTo(TerminalMessages.IO_ERROR), "then EIO, as Linux's master answers");
        Assert.That(terminal.Poll() & TerminalMessages.HANGUP, Is.Not.Zero);
        var again = new Recorder();
        Assert.That(terminal.Slave.Add(again), Is.True, "a slave may join again while the master lives");
        Assert.That(terminal.Read(100, true, out _), Is.EqualTo(TerminalMessages.AGAIN));
    }

    [Test]
    public void MasterCloseHangsUpTheSlaveTest()
    {
        var ended = new List<int>();
        var pseudo = new PseudoTerminals();
        var terminal = pseudo.Open()!;
        var slave = new Recorder();
        terminal.Open(new Recorder());
        Assert.That(terminal.Slave.Add(slave), Is.True);
        var reader = Task.Run(() => (terminal.Slave.Read(100, false, out var bytes), bytes.Length));
        Thread.Sleep(50);
        terminal.Close();
        Assert.That(reader.Wait(5000), Is.True, "a read that waits ends with the hangup");
        Assert.That(reader.Result, Is.EqualTo((0, 0)), "the end of the file");
        Assert.That(slave.Events, Does.Contain(TerminalMessages.EVENT_HANGUP));
        Assert.That(terminal.Slave.Write("x"u8), Is.EqualTo(TerminalMessages.IO_ERROR));
        Assert.That(terminal.Slave.Poll() & TerminalMessages.HANGUP, Is.Not.Zero);
        Assert.That(terminal.Slave.Add(new Recorder()), Is.False, "no slave joins a hung-up terminal");
        Assert.That(pseudo.Find(terminal.Number), Is.SameAs(terminal), "a slave session still holds it");
        terminal.Slave.Remove(slave);
        Assert.That(pseudo.Find(terminal.Number), Is.Null, "its master and its last slave session went");
    }

    [Test]
    public void ResizeReachesTheSlaveOnceItChangesTest()
    {
        var (terminal, _, slave) = Open();
        terminal.Slave.Resize(132, 43);
        terminal.Slave.Resize(132, 43);
        Assert.That((terminal.Slave.Columns, terminal.Slave.Rows), Is.EqualTo(((ushort)132, (ushort)43)));
        Assert.That(slave.Events.FindAll(kind => kind == TerminalMessages.EVENT_SIZE), Has.Count.EqualTo(1));
    }

    [Test]
    public void InterruptCharacterReachesTheSlaveTest()
    {
        var (terminal, _, slave) = Open();
        terminal.Type([3]); // Ctrl-C under ISIG
        Assert.That(slave.Events, Does.Contain(TerminalMessages.EVENT_INTERRUPT));
    }

    [Test]
    public void ProgramWaitsForRoomAndEchoDoesNotTest()
    {
        var (terminal, _, _) = Open();
        Assert.That(terminal.Slave.Write(new byte[PseudoTerminal.OUTPUT_LIMIT]), Is.Zero);
        var writer = Task.Run(() => terminal.Slave.Write("more"u8));
        Assert.That(writer.Wait(200), Is.False, "the queue is full: the program's write waits");
        terminal.Type("e"u8); // the echo goes in all the same: the master's own write produced it
        Assert.That(terminal.Read(PseudoTerminal.OUTPUT_LIMIT, true, out var first), Is.Zero);
        Assert.That(first, Has.Length.EqualTo(PseudoTerminal.OUTPUT_LIMIT));
        Assert.That(writer.Wait(5000), Is.True, "the master read: the write goes on");
        Assert.That(terminal.Read(100, true, out var rest), Is.Zero);
        Assert.That(Encoding.ASCII.GetString(rest), Is.EqualTo("emore"));
    }

    [Test]
    public void NumbersAreTheLowestFreeTest()
    {
        var pseudo = new PseudoTerminals();
        var terminals = new List<PseudoTerminal>();
        for (var n = 0; n < TerminalMessages.PSEUDO_MAXIMUM; n++)
            terminals.Add(pseudo.Open()!);
        Assert.That(terminals.ConvertAll(terminal => terminal.Number), Is.EqualTo(Enumerable.Range(0, TerminalMessages.PSEUDO_MAXIMUM)));
        Assert.That(pseudo.Open(), Is.Null, "every number is taken");
        terminals[3].Close();
        Assert.That(pseudo.Open()!.Number, Is.EqualTo(3), "a master that went with no slave frees its number");
        Assert.That(pseudo.Stop(), Has.Length.EqualTo(TerminalMessages.PSEUDO_MAXIMUM));
        Assert.That(pseudo.Open(), Is.Null, "none opens once the service stops");
    }

    #endregion

    #region Tools

    private static (PseudoTerminal Terminal, Recorder Master, Recorder Slave) Open()
    {
        var terminal = new PseudoTerminals().Open()!;
        var master = new Recorder();
        var slave = new Recorder();
        terminal.Open(master);
        Assert.That(terminal.Slave.Add(slave), Is.True);
        return (terminal, master, slave);
    }

    // A session that keeps what it was told.
    private sealed class Recorder : ITerminalSession
    {
        private readonly object m_gate = new();
        private readonly List<uint> m_events = [];

        public void Notify(uint kind)
        {
            lock (m_gate)
                m_events.Add(kind);
        }

        public List<uint> Events
        {
            get
            {
                lock (m_gate)
                    return [.. m_events];
            }
        }
    }

    #endregion
}
