using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OutWit.OS.Hardware.Usb;

namespace OutWit.OS.Tests.Hardware.Usb;

/// <summary>
/// USB mass storage on the host (plan step E3): Bulk-Only Transport's wrappers, its recovery from a stalled data phase,
/// a stalled status, a phase error and a wrapper out of order, and a SCSI disk over it — opened as Linux's sd opens a
/// drive (INQUIRY, a unit attention, READ CAPACITY 10 and 16, MODE SENSE's write protect), read and written through
/// physical segments, flushed — against a drive the test simulates as QEMU's usb-storage answers.
/// </summary>
[TestFixture]
public sealed class UsbMassStorageTests
{
    #region Constants

    private const int BLOCK = 512;

    #endregion

    #region Tests

    [Test]
    public void OpensADriveTest()
    {
        var drive = new SimulatedDrive(blocks: 8192) { UnitAttentions = 2 };
        var disk = ScsiDisk.Open(new BulkOnlyTransport(drive), _ => { }, 16, 65536, 120 * 1024);

        Assert.That((disk.Capacity, disk.BlockSize, disk.IsReadOnly, disk.IsRemovable), Is.EqualTo((8192L * BLOCK, BLOCK, false, true)));
        Assert.That((disk.Vendor, disk.Product, disk.Revision), Is.EqualTo(("QEMU", "QEMU HARDDISK", "2.5+")));
        Assert.That(drive.Commands.Take(4), Is.EqualTo(new byte[] { 0x12, 0x00, 0x03, 0x00 }), "INQUIRY, then TEST UNIT READY past its attention");
        Assert.That(drive.Commands.Count(command => command == 0x25), Is.EqualTo(1), "READ CAPACITY (10)");
    }

    [Test]
    public void ReadsAndWritesThroughSegmentsTest()
    {
        var drive = new SimulatedDrive(blocks: 8192);
        var disk = ScsiDisk.Open(new BulkOnlyTransport(drive), _ => { }, 16, 65536, 120 * 1024);
        var data = new byte[3 * BLOCK];
        new Random(3).NextBytes(data);
        drive.Memory[0x1000] = data[..BLOCK];
        drive.Memory[0x9000] = data[BLOCK..];
        disk.Write(10 * BLOCK, [(0x1000UL, BLOCK), (0x9000UL, 2 * BLOCK)]);
        Assert.That(drive.Medium.AsSpan(10 * BLOCK, 3 * BLOCK).SequenceEqual(data), Is.True);
        Assert.That(drive.Commands.Last(), Is.EqualTo(0x2A), "WRITE (10)");

        drive.Memory[0x2000] = new byte[3 * BLOCK];
        disk.Read(10 * BLOCK, [(0x2000UL, 3 * BLOCK)]);
        Assert.That(drive.Memory[0x2000], Is.EqualTo(data));
        disk.Flush();
        Assert.That(drive.Commands.Last(), Is.EqualTo(0x35), "SYNCHRONIZE CACHE");
        Assert.Throws<ArgumentException>(() => disk.Read(1, [(0x2000UL, BLOCK)]), "not of the blocks");
    }

    // Beyond 2^32 blocks READ CAPACITY (10) answers its maximum, and the disk asks (16) and reads by READ (16).
    [Test]
    public void ReachesBeyondTwoTerabytesTest()
    {
        var drive = new SimulatedDrive(blocks: (1L << 33) + 7);
        var disk = ScsiDisk.Open(new BulkOnlyTransport(drive), _ => { }, 16, 65536, 120 * 1024);
        Assert.That(disk.Capacity, Is.EqualTo(((1L << 33) + 7) * BLOCK));
        Assert.That(drive.Commands, Does.Contain((byte)0x9E));
        drive.Memory[0x3000] = new byte[BLOCK];
        disk.Read(((1L << 33) + 2) * BLOCK, [(0x3000UL, BLOCK)]);
        Assert.That(drive.Commands.Last(), Is.EqualTo(0x88), "READ (16)");
    }

    [Test]
    public void ReportsWriteProtectionAndFailuresTest()
    {
        var drive = new SimulatedDrive(blocks: 1024) { WriteProtected = true };
        var disk = ScsiDisk.Open(new BulkOnlyTransport(drive), _ => { }, 16, 65536, 120 * 1024);
        Assert.That(disk.IsReadOnly, Is.True);
        Assert.Throws<IOException>(() => disk.Write(0, [(0x1000UL, BLOCK)]));

        // A medium error comes back as REQUEST SENSE names it.
        var failing = new SimulatedDrive(blocks: 1024) { FailingBlock = 5 };
        var other = ScsiDisk.Open(new BulkOnlyTransport(failing), _ => { }, 16, 65536, 120 * 1024);
        failing.Memory[0x1000] = new byte[BLOCK];
        var error = Assert.Throws<IOException>(() => other.Read(5 * BLOCK, [(0x1000UL, BLOCK)]));
        Assert.That(error!.Message, Does.Contain("READ of 1 blocks at 5").And.Contain("medium error").And.Contain("0x11"));

        var empty = new SimulatedDrive(blocks: 1024) { NoMedium = true };
        Assert.That(Assert.Throws<IOException>(() => ScsiDisk.Open(new BulkOnlyTransport(empty), _ => { }, 16, 65536, 4096))!.Message,
            Does.Contain("no medium"));
    }

    // BOT 6.7: a stalled data phase is cleared and the status read; a stalled status is read once more; a phase error or a
    // wrapper of another tag is answered by the reset recovery before the command fails.
    [Test]
    public void RecoversAsBulkOnlyAsksTest()
    {
        var drive = new SimulatedDrive(blocks: 1024);
        var transport = new BulkOnlyTransport(drive);
        var disk = ScsiDisk.Open(transport, _ => { }, 16, 65536, 120 * 1024);
        drive.Memory[0x1000] = new byte[BLOCK];

        drive.StallNextData = true;
        drive.StallNextStatus = true;
        disk.Read(0, [(0x1000UL, BLOCK)]);
        Assert.That(drive.Halts, Is.EqualTo(new[] { true, true }), "the IN endpoint's halt cleared after the data and after the status");

        drive.Halts.Clear();
        drive.PhaseErrorNext = true;
        Assert.Throws<IOException>(() => disk.Read(0, [(0x1000UL, BLOCK)]));
        Assert.That((drive.Resets, drive.Halts.Count), Is.EqualTo((1, 2)), "the reset, then both halts");

        drive.WrongTagNext = true;
        Assert.Throws<IOException>(() => disk.Read(0, [(0x1000UL, BLOCK)]));
        Assert.That(drive.Resets, Is.EqualTo(2));
        disk.Read(0, [(0x1000UL, BLOCK)]);
    }

    [Test]
    public void WritesTheCommandWrapperTest()
    {
        var drive = new SimulatedDrive(blocks: 1024);
        var transport = new BulkOnlyTransport(drive, lun: 0);
        drive.Memory[0x1000] = new byte[2 * BLOCK];
        transport.Execute([0x28, 0, 0, 0, 0, 3, 0, 0, 2, 0], deviceToHost: true, [(0x1000UL, 2 * BLOCK)]);
        var wrapper = drive.LastWrapper;
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wrapper), Is.EqualTo(0x43425355U), "USBC");
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wrapper.AsSpan(8)), Is.EqualTo((uint)(2 * BLOCK)), "the data length");
        Assert.That((wrapper[12], wrapper[13], wrapper[14], wrapper[15]), Is.EqualTo(((byte)0x80, (byte)0, (byte)10, (byte)0x28)));
        Assert.That(wrapper, Has.Length.EqualTo(BulkOnlyTransport.COMMAND_BYTES));
    }

    #endregion

    #region Types

    // A drive as QEMU's usb-storage answers over Bulk-Only Transport, its medium a sparse array of blocks, the host's
    // physical memory a map of buffers by address.
    private sealed class SimulatedDrive(long blocks) : IBulkOnlyPipes
    {
        private readonly Dictionary<long, byte[]> m_blocks = [];
        private byte[]? m_command;
        private uint m_tag;
        private int m_status;
        private byte[] m_answer = [];
        private (int Key, byte Asc, byte Ascq) m_sense;

        public Dictionary<ulong, byte[]> Memory { get; } = [];

        public List<byte> Commands { get; } = [];

        public List<bool> Halts { get; } = [];

        public byte[] LastWrapper { get; private set; } = [];

        public int Resets { get; private set; }

        public int UnitAttentions { get; set; }

        public bool WriteProtected { get; init; }

        public bool NoMedium { get; init; }

        public long FailingBlock { get; init; } = -1;

        public bool StallNextData { get; set; }

        public bool StallNextStatus { get; set; }

        public bool PhaseErrorNext { get; set; }

        public bool WrongTagNext { get; set; }

        // The medium's bytes, the blocks written so far and zeros elsewhere, for the first 16 MiB.
        public byte[] Medium
        {
            get
            {
                var bytes = new byte[(int)Math.Min(blocks, 32768) * BLOCK];
                foreach (var (index, block) in m_blocks.Where(item => item.Key < 32768))
                    block.CopyTo(bytes, (int)index * BLOCK);
                return bytes;
            }
        }

        public BulkResult SendBytes(ReadOnlySpan<byte> bytes, out int transferred)
        {
            transferred = bytes.Length;
            LastWrapper = bytes.ToArray();
            m_tag = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
            m_command = bytes.Slice(15, bytes[14]).ToArray();
            Commands.Add(m_command[0]);
            Execute();
            return BulkResult.Done;
        }

        public BulkResult ReceiveBytes(Span<byte> bytes, out int received)
        {
            if (m_command is not null && bytes.Length != BulkOnlyTransport.STATUS_BYTES)
            {
                // A small data phase: the answer the command made.
                received = Math.Min(bytes.Length, m_answer.Length);
                m_answer.AsSpan(0, received).CopyTo(bytes);
                m_command = null;
                return BulkResult.Done;
            }
            m_command = null;
            if (StallNextStatus)
            {
                StallNextStatus = false;
                received = 0;
                return BulkResult.Stalled;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x53425355);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], WrongTagNext ? m_tag + 1 : m_tag);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], 0);
            bytes[12] = PhaseErrorNext ? (byte)2 : (byte)m_status;
            (WrongTagNext, PhaseErrorNext) = (false, false);
            received = BulkOnlyTransport.STATUS_BYTES;
            return BulkResult.Done;
        }

        public BulkResult Transfer(bool deviceToHost, ReadOnlySpan<(ulong Address, int Length)> segments, out int transferred)
        {
            transferred = 0;
            m_command = null;
            if (StallNextData)
            {
                StallNextData = false;
                return BulkResult.Stalled;
            }
            if (m_status != 0)
                return BulkResult.Stalled; // the command failed before its data, as a drive stalls then
            var (first, count) = m_transfer;
            var at = 0;
            var bytes = new byte[count * BLOCK];
            if (!deviceToHost)
            {
                foreach (var (address, length) in segments)
                {
                    Memory[address].AsSpan(0, length).CopyTo(bytes.AsSpan(at));
                    at += length;
                }
                for (var i = 0; i < count; i++)
                    m_blocks[first + i] = bytes.AsSpan(i * BLOCK, BLOCK).ToArray();
            }
            else
            {
                for (var i = 0; i < count; i++)
                    (m_blocks.TryGetValue(first + i, out var block) ? block : new byte[BLOCK]).CopyTo(bytes, i * BLOCK);
                foreach (var (address, length) in segments)
                {
                    bytes.AsSpan(at, length).CopyTo(Memory[address]);
                    at += length;
                }
            }
            transferred = count * BLOCK;
            return BulkResult.Done;
        }

        public void ClearHalt(bool deviceToHost) => Halts.Add(deviceToHost);

        public void Reset() => Resets++;

        private (long First, int Count) m_transfer;

        // The command's outcome and its answer, as a direct-access drive gives them.
        private void Execute()
        {
            var command = m_command!;
            m_status = 0;
            m_answer = [];
            switch (command[0])
            {
                case 0x12: // INQUIRY
                    m_answer = new byte[36];
                    m_answer[1] = 0x80;
                    m_answer[4] = 31;
                    "QEMU    QEMU HARDDISK   2.5+"u8.ToArray().CopyTo(m_answer, 8);
                    break;
                case 0x00: // TEST UNIT READY
                    if (NoMedium)
                        Check(2, 0x3A, 0);
                    else if (UnitAttentions > 0)
                    {
                        UnitAttentions--;
                        Check(6, 0x29, 0);
                    }
                    break;
                case 0x03: // REQUEST SENSE
                    m_answer = new byte[18];
                    m_answer[0] = 0x70;
                    m_answer[2] = (byte)m_sense.Key;
                    m_answer[7] = 10;
                    m_answer[12] = m_sense.Asc;
                    m_answer[13] = m_sense.Ascq;
                    m_sense = default;
                    break;
                case 0x25: // READ CAPACITY (10)
                    m_answer = new byte[8];
                    BinaryPrimitives.WriteUInt32BigEndian(m_answer, blocks - 1 > uint.MaxValue ? uint.MaxValue : (uint)(blocks - 1));
                    BinaryPrimitives.WriteUInt32BigEndian(m_answer.AsSpan(4), BLOCK);
                    break;
                case 0x9E: // READ CAPACITY (16)
                    m_answer = new byte[32];
                    BinaryPrimitives.WriteUInt64BigEndian(m_answer, (ulong)(blocks - 1));
                    BinaryPrimitives.WriteUInt32BigEndian(m_answer.AsSpan(8), BLOCK);
                    break;
                case 0x1A: // MODE SENSE (6)
                    m_answer = new byte[4];
                    m_answer[0] = 3;
                    m_answer[2] = WriteProtected ? (byte)0x80 : (byte)0;
                    break;
                case 0x28 or 0x2A: // READ (10), WRITE (10)
                    m_transfer = (BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(2)), BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(7)));
                    Fails(command[0] == 0x2A);
                    break;
                case 0x88 or 0x8A: // READ (16), WRITE (16)
                    m_transfer = ((long)BinaryPrimitives.ReadUInt64BigEndian(command.AsSpan(2)), (int)BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(10)));
                    Fails(command[0] == 0x8A);
                    break;
                case 0x35: // SYNCHRONIZE CACHE (10)
                    break;
                default:
                    Check(5, 0x20, 0);
                    break;
            }
        }

        private void Fails(bool write)
        {
            if (write && WriteProtected)
                Check(7, 0x27, 0);
            else if (FailingBlock >= m_transfer.First && FailingBlock < m_transfer.First + m_transfer.Count)
                Check(3, 0x11, 0);
        }

        private void Check(int key, byte asc, byte ascq)
        {
            m_status = 1;
            m_sense = (key, asc, ascq);
        }
    }

    #endregion
}
