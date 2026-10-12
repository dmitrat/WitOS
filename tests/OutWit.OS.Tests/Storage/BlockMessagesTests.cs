using System;
using System.Buffers.Binary;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.Storage;

/// <summary>
/// The block protocol's messages (witos/block.h): their layout, the bytes they refuse, and the check a driver makes
/// before anything reaches the device.
/// </summary>
[TestFixture]
public sealed class BlockMessagesTests
{
    #region Constants

    private const int BLOCK = 512;
    private const long DEVICE = 1024 * BLOCK;
    private const int TRANSFER = 64 * 4096;
    private const long WINDOW = 64 * 4096;

    #endregion

    #region Fields

    private static readonly BlockReply WRITABLE = new(BlockMessages.INFO, 0, DEVICE, BLOCK, BlockMessages.FLUSHES, TRANSFER);
    private static readonly BlockReply READ_ONLY = WRITABLE with { Flags = BlockMessages.READ_ONLY };

    #endregion

    #region Tests

    [Test]
    public void RequestHasTheHeaderLayoutTest()
    {
        var bytes = BlockMessages.Encode(new BlockRequest(BlockMessages.WRITE, BlockMessages.DURABLE, 0x1122334455, 0x2000, 0x400));

        Assert.That(bytes, Has.Length.EqualTo(40));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes), Is.EqualTo(BlockMessages.VERSION));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)), Is.EqualTo(40));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)), Is.EqualTo(BlockMessages.WRITE));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)), Is.EqualTo(BlockMessages.DURABLE));
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16)), Is.EqualTo(0x1122334455));
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(24)), Is.EqualTo(0x2000));
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32)), Is.EqualTo(0x400));
    }

    [Test]
    public void ReplyHasTheHeaderLayoutTest()
    {
        var bytes = BlockMessages.Encode(new BlockReply(BlockMessages.INFO, 0, DEVICE, BLOCK, BlockMessages.READ_ONLY, TRANSFER));

        Assert.That(bytes, Has.Length.EqualTo(40));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)), Is.EqualTo(BlockMessages.INFO));
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)), Is.Zero);
        Assert.That(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)), Is.EqualTo(DEVICE));
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)), Is.EqualTo(BLOCK));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)), Is.EqualTo(BlockMessages.READ_ONLY));
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32)), Is.EqualTo(TRANSFER));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(36)), Is.Zero);
    }

    [Test]
    public void MessagesRoundTripTest()
    {
        var request = new BlockRequest(BlockMessages.READ, 0, 8 * BLOCK, 4 * BLOCK, 2 * BLOCK);
        var reply = new BlockReply(BlockMessages.READ, BlockMessages.DEVICE_ERROR);

        Assert.That(BlockMessages.DecodeRequest(BlockMessages.Encode(request)), Is.EqualTo(request));
        Assert.That(BlockMessages.DecodeReply(BlockMessages.Encode(reply)), Is.EqualTo(reply));
    }

    [Test]
    public void OtherBytesAreRefusedTest()
    {
        var bytes = BlockMessages.Encode(new BlockReply(BlockMessages.FLUSH, 0));
        var otherVersion = (byte[])bytes.Clone();
        otherVersion[0] = 2;
        var otherSize = (byte[])bytes.Clone();
        otherSize[4] = 32;
        var reserved = (byte[])bytes.Clone();
        reserved[36] = 1;

        Assert.Throws<FormatException>(() => BlockMessages.DecodeReply(otherVersion));
        Assert.Throws<FormatException>(() => BlockMessages.DecodeReply(otherSize));
        Assert.Throws<FormatException>(() => BlockMessages.DecodeReply(reserved));
        Assert.Throws<FormatException>(() => BlockMessages.DecodeRequest(bytes.AsSpan(0, 32)));
    }

    [TestCase(BlockMessages.READ, 0U, 0L, BLOCK, 0L, 0)]
    [TestCase(BlockMessages.READ, 0U, DEVICE - BLOCK, BLOCK, WINDOW - BLOCK, 0)]
    [TestCase(BlockMessages.WRITE, BlockMessages.DURABLE, 0L, TRANSFER, 0L, 0)]
    [TestCase(BlockMessages.READ, 0U, 1L, BLOCK, 0L, BlockMessages.INVALID)]
    [TestCase(BlockMessages.READ, 0U, 0L, BLOCK + 1, 0L, BlockMessages.INVALID)]
    [TestCase(BlockMessages.READ, 0U, 0L, BLOCK, 256L, BlockMessages.INVALID)]
    [TestCase(BlockMessages.READ, 0U, 0L, 0, 0L, BlockMessages.INVALID)]
    [TestCase(BlockMessages.READ, 0U, DEVICE, BLOCK, 0L, BlockMessages.INVALID)]
    [TestCase(BlockMessages.READ, 0U, 0L, BLOCK, WINDOW, BlockMessages.INVALID)]
    [TestCase(BlockMessages.READ, 0U, 0L, TRANSFER + BLOCK, 0L, BlockMessages.INVALID)]
    [TestCase(BlockMessages.READ, 0U, -BLOCK, BLOCK, 0L, BlockMessages.INVALID)]
    [TestCase(BlockMessages.READ, BlockMessages.DURABLE, 0L, BLOCK, 0L, BlockMessages.INVALID)]
    [TestCase(BlockMessages.WRITE, 2U, 0L, BLOCK, 0L, BlockMessages.INVALID)]
    [TestCase(BlockMessages.INFO, 0U, 0L, 0, 0L, 0)]
    [TestCase(BlockMessages.FLUSH, 0U, 0L, 0, 0L, 0)]
    [TestCase(BlockMessages.FLUSH, 0U, BLOCK, 0, 0L, BlockMessages.INVALID)]
    [TestCase(BlockMessages.ATTACH, 0U, 0L, 4096, 0L, 0)]
    [TestCase(BlockMessages.ATTACH, 0U, 0L, 4096 + BLOCK, 0L, BlockMessages.INVALID)]
    [TestCase(BlockMessages.ATTACH, 0U, 0L, 65 * 4096, 0L, BlockMessages.INVALID)]
    [TestCase(9U, 0U, 0L, 0, 0L, BlockMessages.UNKNOWN_OPERATION)]
    public void CheckTest(uint operation, uint flags, long offset, int length, long window, int expected)
    {
        Assert.That(BlockMessages.Check(new BlockRequest(operation, flags, offset, length, window), WRITABLE, WINDOW), Is.EqualTo(expected));
    }

    [Test]
    public void CheckRefusesWritesOfAReadOnlyDeviceTest()
    {
        Assert.That(BlockMessages.Check(new BlockRequest(BlockMessages.WRITE, 0, 0, BLOCK), READ_ONLY, WINDOW),
            Is.EqualTo(BlockMessages.READ_ONLY_DEVICE));
        Assert.That(BlockMessages.Check(new BlockRequest(BlockMessages.READ, 0, 0, BLOCK), READ_ONLY, WINDOW), Is.Zero);
    }

    [Test]
    public void CheckRefusesTransfersBeforeAttachTest()
    {
        Assert.That(BlockMessages.Check(new BlockRequest(BlockMessages.READ, 0, 0, BLOCK), WRITABLE, 0), Is.EqualTo(BlockMessages.INVALID));
    }

    #endregion
}
