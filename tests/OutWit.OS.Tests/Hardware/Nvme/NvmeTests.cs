using System;
using System.Buffers.Binary;
using System.Text;
using OutWit.OS.Hardware.Nvme;

namespace OutWit.OS.Tests.Hardware.Nvme;

/// <summary>
/// The NVMe pieces of plan step H3.2 on the host: a submission queue entry and a completion queue entry in NVM Express's
/// layout, the PRP entries of transfers of one, two and many pages, the first starting inside its page, the segments
/// the PRP rules refuse, and Identify Controller and Identify Namespace data as QEMU's controller answers them.
/// </summary>
[TestFixture]
public sealed class NvmeTests
{
    #region Tests

    [Test]
    public void SubmissionEntryFollowsTheLayoutTest()
    {
        var entry = new byte[NvmeCommands.SUBMISSION_BYTES];
        NvmeCommands.WriteSubmission(entry, NvmeCommands.READ, 0x1234, 1, 0x1000_0200, 0x2000_0000, [0x89ABCDEF, 0x01, 7]);
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(entry), Is.EqualTo(0x1234_0002U), "the opcode, PRPs, the identifier");
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(4)), Is.EqualTo(1U), "the namespace");
        Assert.That((BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(24)), BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(32))),
            Is.EqualTo((0x1000_0200UL, 0x2000_0000UL)), "PRP 1 and PRP 2");
        Assert.That((BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(40)), BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(44)),
            BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(48))), Is.EqualTo((0x89ABCDEFU, 1U, 7U)), "dwords 10 to 12: the block and the count less one");
        Assert.Throws<ArgumentOutOfRangeException>(() => NvmeCommands.WriteSubmission(entry, 0, 0, 0, 0, 0, new uint[7]));
    }

    [Test]
    public void CompletionCarriesItsPhaseAndStatusTest()
    {
        var entry = new byte[NvmeCommands.COMPLETION_BYTES];
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), 5U | 1U << 16); // the submission head, queue 1
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(12), 0x0042 | 1U << 16); // identifier 0x42, the phase, success
        var completion = NvmeCommands.ReadCompletion(entry);
        Assert.That((completion.SubmissionHead, completion.SubmissionQueue, completion.Identifier, completion.Phase, completion.Succeeded),
            Is.EqualTo(((ushort)5, (ushort)1, (ushort)0x42, true, true)));
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(12), 0x0042 | (2U << 8 | 0x81) << 17); // media error 0x81, phase clear
        completion = NvmeCommands.ReadCompletion(entry);
        Assert.That((completion.Phase, completion.Succeeded, completion.StatusType, completion.StatusCode), Is.EqualTo((false, false, 2, 0x81)));
    }

    [Test]
    public void PrpsCoverOneTwoAndManyPagesTest()
    {
        var list = new ulong[512];
        Assert.That(NvmeCommands.BuildPrps([(0x10_0200, 512)], list, 0x9000, out var prp1, out var prp2), Is.Zero);
        Assert.That((prp1, prp2), Is.EqualTo((0x10_0200UL, 0UL)), "a block inside one page: PRP 1 alone");
        Assert.That(NvmeCommands.BuildPrps([(0x10_0000, 8192)], list, 0x9000, out prp1, out prp2), Is.Zero);
        Assert.That((prp1, prp2), Is.EqualTo((0x10_0000UL, 0x10_1000UL)), "two pages: PRP 2 the second");
        Assert.That(NvmeCommands.BuildPrps([(0x10_0E00, 0x200), (0x20_0000, 0x3000), (0x30_0000, 0x200)], list, 0x9000, out prp1, out prp2), Is.EqualTo(4));
        Assert.That((prp1, prp2), Is.EqualTo((0x10_0E00UL, 0x9000UL)), "more pages: PRP 2 the list");
        Assert.That(list[..4], Is.EqualTo(new ulong[] { 0x20_0000, 0x20_1000, 0x20_2000, 0x30_0000 }), "every page after the first byte's");
        Assert.That(NvmeCommands.BuildPrps([(0x10_0200, 0x1E00)], list, 0x9000, out prp1, out prp2), Is.Zero);
        Assert.That((prp1, prp2), Is.EqualTo((0x10_0200UL, 0x10_1000UL)), "an offset first page reaching one more");
    }

    [Test]
    public void SegmentsThePrpRulesRefuseTest()
    {
        var list = new ulong[4];
        Assert.Throws<ArgumentException>(() => NvmeCommands.BuildPrps([], list, 0, out _, out _), "no segment");
        Assert.Throws<ArgumentException>(() => NvmeCommands.BuildPrps([(0x1000, 0x1000), (0x3200, 0x200)], list, 0, out _, out _), "a later segment inside a page");
        Assert.Throws<ArgumentException>(() => NvmeCommands.BuildPrps([(0x1000, 0x800), (0x3000, 0x200)], list, 0, out _, out _), "a segment ending inside a page before the last");
        Assert.Throws<ArgumentException>(() => NvmeCommands.BuildPrps([(0x1000, 0x6000)], list, 0, out _, out _), "more pages than the list holds");
    }

    [Test]
    public void QemuControllerAndNamespaceIdentifyThemselvesTest()
    {
        var controller = new byte[NvmeControllerIdentity.BYTES];
        BinaryPrimitives.WriteUInt16LittleEndian(controller, 0x1B36);
        Ascii(controller, 4, 20, "witos-data");
        Ascii(controller, 24, 40, "QEMU NVMe Ctrl");
        Ascii(controller, 64, 8, "11.1.0");
        controller[77] = 7;
        BinaryPrimitives.WriteUInt32LittleEndian(controller.AsSpan(516), 256);
        controller[525] = 0x07; // the volatile write cache, with QEMU's flush of every namespace
        var identity = NvmeControllerIdentity.Read(controller);
        Assert.That((identity.Vendor, identity.Serial, identity.Model, identity.Firmware, identity.MaxTransferPages, identity.Namespaces, identity.VolatileWriteCache),
            Is.EqualTo(((ushort)0x1B36, "witos-data", "QEMU NVMe Ctrl", "11.1.0", 7, 256U, true)));
        var space = new byte[NvmeControllerIdentity.BYTES];
        BinaryPrimitives.WriteUInt64LittleEndian(space, 65536);
        space[26] = 1;                                                            // LBA format 1 in use
        BinaryPrimitives.WriteUInt32LittleEndian(space.AsSpan(128), 9U << 16);   // format 0: 512 bytes
        BinaryPrimitives.WriteUInt32LittleEndian(space.AsSpan(132), 12U << 16);  // format 1: 4096 bytes
        var name = NvmeNamespaceIdentity.Read(space);
        Assert.That((name.Blocks, name.BlockBytes, name.MetadataBytes, name.WriteProtected, name.Bytes), Is.EqualTo((65536UL, 4096, 0, false, 65536L * 4096)));
        space[99] = 1;
        Assert.That(NvmeNamespaceIdentity.Read(space).WriteProtected, Is.True);
        BinaryPrimitives.WriteUInt64LittleEndian(space, 0);
        Assert.Throws<FormatException>(() => NvmeNamespaceIdentity.Read(space), "an inactive namespace");
        Assert.Throws<FormatException>(() => NvmeControllerIdentity.Read(new byte[512]));
    }

    #endregion

    #region Tools

    private static void Ascii(byte[] data, int offset, int length, string text) =>
        Encoding.ASCII.GetBytes(text.PadRight(length)).CopyTo(data, offset);

    #endregion
}
