using System;
using System.IO;
using System.Linq;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Sdhci;

namespace OutWit.OS.Tests.Hardware.Sdhci;

/// <summary>
/// The SD host (plan step H8.2) against a model of a standard SD host controller with a card: the SD specification's
/// identification of a high capacity card and of a version 1.x standard capacity one, the clocks and the bus, reads and
/// writes through ADMA2 over the client's segments, write protection, a failed block the next request survives, the
/// requests it refuses, and the controllers it refuses.
/// </summary>
[TestFixture]
public sealed class SdhciHostTests
{
    #region Constants

    private const int CARD_BYTES = 8 * 1024 * 1024;

    #endregion

    #region Tests

    [Test]
    public void HighCapacityCardIsIdentifiedTest()
    {
        var (host, model, _) = Start();
        Assert.That(model.Commands.Take(4), Is.EqualTo(new[] { 0, 8, 55, 141 }), "CMD0, CMD8, then ACMD41 until ready");
        Assert.That(model.Commands.Count(command => command == 141), Is.EqualTo(2), "the card was busy once");
        Assert.That(model.Commands.SkipWhile(command => command is 0 or 8 or 55 or 141), Is.EqualTo(new[] { 2, 3, 9, 7, 55, 106 }),
            "CID, address, CSD, select, the 4-bit bus; no CMD16 for a high capacity card");
        Assert.That(model.Ocr & 1u << 30, Is.Not.Zero, "ACMD41 asked for high capacity after CMD8's echo");
        Assert.That(model.Selected && model.BusWidth == 4);
        Assert.That(host.Capacity, Is.EqualTo(CARD_BYTES));
        Assert.That((host.Version, host.BaseClockHz, host.ClockHz), Is.EqualTo((2, 100_000_000L, 25_000_000L)));
        Assert.That(host.IsReadOnly || host.Flushes, Is.False);
        Assert.That(host.Description, Does.Contain($"SDHC/SDXC card \"{FakeSdhci.PRODUCT}\" serial {FakeSdhci.SERIAL:x8} of {CARD_BYTES} bytes"));
        Assert.That(host.Description, Does.Contain("4-bit bus at 25 MHz"));
        // Every register access was a 32-bit word, and the 4-bit bus was set in host control 1.
        Assert.That(model.Writes.Last(write => write.Offset == 0x28).Value & 2, Is.EqualTo(2));
    }

    [Test]
    public void StandardCapacityCardOfVersionOneTest()
    {
        var (host, model, memory) = Start(version1: true, options: new SdhciOptions(BusWidth: 1));
        Assert.That(model.Commands.Take(3), Is.EqualTo(new[] { 0, 8, 55 }), "CMD8 went unanswered");
        Assert.That(model.Ocr & 1u << 30, Is.Zero, "no high capacity asked of a version 1.x card");
        Assert.That(model.Commands, Does.Contain(16), "CMD16 sets 512-byte blocks");
        Assert.That(model.Commands, Does.Not.Contain(106), "a board wired for one line keeps it");
        Assert.That(host.Description, Does.StartWith("SDSC card"));
        // Byte addressing: the block at 4096 bytes.
        var buffer = memory.Buffer(512);
        model.Card[4096] = 0x5A;
        host.Read(4096, [(buffer.Physical, 512)]);
        Assert.That(buffer.Bytes[0], Is.EqualTo(0x5A));
    }

    [Test]
    public void ReadsAndWritesThroughTheTableTest()
    {
        var (host, model, memory) = Start();
        var first = memory.Buffer(40 * 1024);
        var second = memory.Buffer(8 * 1024);
        var pattern = Enumerable.Range(0, 48 * 1024).Select(i => (byte)(i * 13 + 7)).ToArray();
        pattern.AsSpan(0, 40 * 1024).CopyTo(first.Bytes);
        pattern.AsSpan(40 * 1024).CopyTo(second.Bytes);
        host.Write(1024 * 1024, [(first.Physical, 40 * 1024), (second.Physical, 8 * 1024)]);
        Assert.That(model.Card.AsSpan(1024 * 1024, 48 * 1024).ToArray(), Is.EqualTo(pattern));
        Assert.That(model.Commands.Last(), Is.EqualTo(25), "a multiple-block write");
        Assert.That(model.Descriptors, Is.EqualTo(3), "40 KiB in pieces of 32 and 8, then 8");
        Assert.That(model.AutoStops, Is.EqualTo(1), "Auto CMD12 ended it");
        Assert.That(model.Fences, Is.EqualTo(1), "the table reached the device before the command");

        var back = memory.Buffer(48 * 1024);
        host.Read(1024 * 1024, [(back.Physical, 48 * 1024)]);
        Assert.That(back.Bytes.ToArray(), Is.EqualTo(pattern));
        Assert.That(model.Commands.Last(), Is.EqualTo(18));

        var one = memory.Buffer(512);
        host.Read(1024 * 1024 + 512, [(one.Physical, 512)]);
        Assert.That(one.Bytes.ToArray(), Is.EqualTo(pattern[512..1024]));
        Assert.That(model.Commands.Last(), Is.EqualTo(17), "a single block");
        host.Write(0, [(one.Physical, 512)]);
        Assert.That(model.Commands.Last(), Is.EqualTo(24));
        host.Flush();
    }

    [Test]
    public void WriteProtectionTest()
    {
        var (host, _, memory) = Start(writable: false);
        Assert.That(host.IsReadOnly, Is.True);
        Assert.That(host.Description, Does.Contain("write-protected"));
        var buffer = memory.Buffer(512);
        Assert.Throws<IOException>(() => host.Write(0, [(buffer.Physical, 512)]));
        var (slot, _, _) = Start(writable: false, options: new SdhciOptions(IgnoreWriteProtect: true));
        Assert.That(slot.IsReadOnly, Is.False, "a microSD slot has no write-protect switch");
    }

    [Test]
    public void FailedBlockTest()
    {
        var (host, model, memory) = Start();
        var buffer = memory.Buffer(4096);
        model.FailedBlock = 10;
        var failure = Assert.Throws<IOException>(() => host.Read(8 * 512, [(buffer.Physical, 4096)]))!;
        Assert.That(failure.Message, Does.Contain("Reading 8 blocks at block 8 failed"));
        Assert.That(failure.Message, Does.Contain("0x0020"), "the data CRC error");
        model.FailedBlock = null;
        host.Read(8 * 512, [(buffer.Physical, 4096)]);
    }

    [Test]
    public void RefusedRequestsTest()
    {
        var (host, _, memory) = Start();
        var buffer = memory.Buffer(4096);
        Assert.Throws<ArgumentException>(() => host.Read(100, [(buffer.Physical, 512)]), "an offset within a block");
        Assert.Throws<ArgumentException>(() => host.Read(0, [(buffer.Physical, 100)]), "part of a block");
        Assert.Throws<ArgumentException>(() => host.Read(CARD_BYTES, [(buffer.Physical, 512)]), "beyond the card");
        Assert.Throws<ArgumentException>(() => host.Read(0, [(0x1_0000_0000UL, 512)]), "beyond 32-bit DMA");
        Assert.Throws<ArgumentException>(() => host.Read(0, [(buffer.Physical + 2, 512)]), "an address ADMA2 cannot take");
        Assert.Throws<ArgumentException>(() => host.Read(0, Enumerable.Repeat((buffer.Physical, 512), 65).ToArray()), "too many segments");
    }

    [Test]
    public void ControllersRefusedTest()
    {
        var memory = new FakeMemory();
        Assert.Throws<IOException>(() => new SdhciHost(new FakeSdhci(new byte[CARD_BYTES], memory, inserted: false), new NoWait(),
            memory.Buffer(4096)).Start(), "no card");
        Assert.Throws<IOException>(() => new SdhciHost(new FakeSdhci(new byte[CARD_BYTES], memory, adma2: false), new NoWait(),
            memory.Buffer(4096)).Start(), "no ADMA2");
        Assert.Throws<IOException>(() => new SdhciHost(new FakeSdhci(new byte[CARD_BYTES], memory, baseMhz: 0), new NoWait(),
            memory.Buffer(4096)).Start(), "no base clock");
        var named = new SdhciHost(new FakeSdhci(new byte[CARD_BYTES], memory, baseMhz: 0), new NoWait(), memory.Buffer(4096),
            new SdhciOptions(BaseClockHz: 200_000_000));
        named.Start();
        Assert.That(named.ClockHz, Is.EqualTo(25_000_000), "the board's base clock");
        Assert.Throws<ArgumentException>(() => new SdhciHost(new FakeSdhci(new byte[CARD_BYTES], memory), new NoWait(), memory.Buffer(512)));
    }

    [Test]
    public void DividersTest()
    {
        Assert.That(SdhciHost.Divider(100_000_000, 400_000, 2), Is.EqualTo(125));
        Assert.That(SdhciHost.Divider(100_000_000, 25_000_000, 2), Is.EqualTo(2));
        Assert.That(SdhciHost.Divider(100_000_000, 100_000_000, 2), Is.Zero);
        Assert.That(SdhciHost.Divider(200_000_000, 400_000, 2), Is.EqualTo(250));
        Assert.That(SdhciHost.Divider(50_000_000, 400_000, 1), Is.EqualTo(0x40), "version 2.00's powers of two");
        Assert.That(SdhciHost.Frequency(50_000_000, 0x40, 1), Is.EqualTo(390_625));
        Assert.That(SdhciHost.Divider(50_000_000, 25_000_000, 1), Is.EqualTo(1));
    }

    [Test]
    public void CardRegistersTest()
    {
        Assert.That(SdCommand.Register(SdCommand.READ_MULTIPLE_BLOCK, SdResponse.R1, data: true), Is.EqualTo(18 << 8 | 0x3A));
        Assert.That(SdCommand.Register(SdCommand.SELECT_CARD, SdResponse.R1b, data: false), Is.EqualTo(7 << 8 | 0x1B));
        Assert.That(SdCommand.Register(SdCommand.ALL_SEND_CID, SdResponse.R2, data: false), Is.EqualTo(2 << 8 | 0x09));
        Assert.That(SdCommand.Register(SdCommand.SD_SEND_OP_COND, SdResponse.R3, data: false), Is.EqualTo(41 << 8 | 0x02));
        Assert.That(SdCommand.Register(SdCommand.GO_IDLE_STATE, SdResponse.None, data: false), Is.Zero);
        Assert.That(SdCommand.Register(SdCommand.STOP_TRANSMISSION, SdResponse.R1b, data: false) & 0xC0, Is.EqualTo(0xC0), "an abort");
        // A 32 GB card's CSD version 2.0: C_SIZE 60999, (60999 + 1) x 512 KiB.
        Assert.That(SdCommand.Capacity((UInt128)1 << 126 | (UInt128)60999 << 48), Is.EqualTo(61000L * 512 * 1024));
        Assert.Throws<FormatException>(() => SdCommand.Capacity((UInt128)2 << 126));
    }

    #endregion

    #region Tools

    private static (SdhciHost Host, FakeSdhci Model, FakeMemory Memory) Start(bool version1 = false, bool writable = true, SdhciOptions? options = null)
    {
        var memory = new FakeMemory();
        var model = new FakeSdhci(new byte[CARD_BYTES], memory, version1: version1, writable: writable);
        var host = new SdhciHost(model, new NoWait(), memory.Buffer(4096), options);
        host.Start();
        return (host, model, memory);
    }

    private sealed class NoWait : IInterruptWait
    {
        public void Wait(TimeSpan timeout)
        {
        }
    }

    #endregion
}
