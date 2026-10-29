using System;
using System.Buffers.Binary;
using System.Text;
using OutWit.OS.Namespaces;

namespace OutWit.OS.Tests.Namespace;

/// <summary>
/// The namespace protocol's messages (witos/namespace.h): the layouts the C structures have, round trips with a stat's
/// times to the nanosecond, a listing's entries, the disk messages, and the bytes they refuse.
/// </summary>
[TestFixture]
public sealed class NamespaceMessagesTests
{
    #region Tests

    [Test]
    public void RequestHasTheHeaderLayoutTest()
    {
        var bytes = NamespaceMessages.Encode(new NamespaceRequest(NamespaceMessages.OPEN, NamespaceMessages.CREATE, 7, 9, 11, 0x1A4));

        Assert.That(bytes, Has.Length.EqualTo(48));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)), Is.EqualTo(NamespaceMessages.OPEN));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)), Is.EqualTo(NamespaceMessages.CREATE));
        Assert.That(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)), Is.EqualTo(7));
        Assert.That(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)), Is.EqualTo(9));
        Assert.That(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32)), Is.EqualTo(11));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)), Is.EqualTo(0x1A4));
        Assert.That(NamespaceMessages.DecodeRequest(bytes), Is.EqualTo(new NamespaceRequest(NamespaceMessages.OPEN, NamespaceMessages.CREATE, 7, 9, 11, 0x1A4)));
    }

    [Test]
    public void ReplyCarriesTheStatTest()
    {
        var modified = new DateTimeOffset(2026, 10, 12, 3, 4, 5, TimeSpan.Zero).AddTicks(1234567);
        var stat = new NamespaceStat(NamespaceMessages.KIND_FILE, 0x1A4, 3100, 42, 1, 2, 1000, 1001, 4096, 8,
            DateTimeOffset.UnixEpoch.AddSeconds(-1).AddTicks(5), modified, modified);
        var bytes = NamespaceMessages.Encode(new NamespaceReply(NamespaceMessages.FSTAT, 0, 3, 4, 5, stat));

        var reply = NamespaceMessages.DecodeReply(bytes);

        Assert.That(bytes, Has.Length.EqualTo(136));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)), Is.EqualTo(NamespaceMessages.KIND_FILE));
        Assert.That(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(40 + 8)), Is.EqualTo(3100));
        Assert.That(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(40 + 56)), Is.EqualTo(-1)); // seconds floored before 1970
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40 + 80)), Is.EqualTo(500));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40 + 84)), Is.EqualTo(123456700));
        Assert.That(reply, Is.EqualTo(new NamespaceReply(NamespaceMessages.FSTAT, 0, 3, 4, 5, stat)));
        Assert.That(NamespaceMessages.DecodeReply(NamespaceMessages.Encode(new NamespaceReply(NamespaceMessages.CLOSE, 9))).Stat, Is.Null);
    }

    [Test]
    public void EntriesArePaddedTest()
    {
        var window = new byte[64];

        var first = NamespaceMessages.WriteEntry(window, 77, NamespaceMessages.KIND_DIRECTORY, "abc");
        var full = NamespaceMessages.WriteEntry(window.AsSpan(first), 1, NamespaceMessages.KIND_FILE, new string('x', 60));

        Assert.That(first, Is.EqualTo(24));
        Assert.That(BinaryPrimitives.ReadInt64LittleEndian(window), Is.EqualTo(77));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(window.AsSpan(12)), Is.EqualTo(24));
        Assert.That(Encoding.UTF8.GetString(window, NamespaceMessages.ENTRY_SIZE, BinaryPrimitives.ReadUInt16LittleEndian(window.AsSpan(14))), Is.EqualTo("abc"));
        Assert.That(full, Is.Zero);
    }

    // WitNamespaceVolume (E1): 256 bytes, the numbers first, then the texts at 40, 72, 136, 152 and 192; a label longer
    // than its field cut at a character.
    [Test]
    public void VolumeHasTheHeaderLayoutTest()
    {
        var volume = new NamespaceVolume(NamespaceMessages.VOLUME_SPACE | NamespaceMessages.VOLUME_WEAK_IDENTITY, NamespaceMessages.ORIGIN_REMOVABLE,
            1L << 33, 12345, 4096, 7, "USB-DISK", "/media/USB-DISK", "FAT32", "ABCD-1234", "USB DISK");
        var bytes = new byte[NamespaceVolume.SIZE];
        volume.Write(bytes);

        Assert.That(NamespaceVolume.SIZE, Is.EqualTo(256));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)), Is.EqualTo(256));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)), Is.EqualTo(NamespaceMessages.ORIGIN_REMOVABLE));
        Assert.That(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)), Is.EqualTo(1L << 33));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(36)), Is.EqualTo(7));
        foreach (var (at, text) in new[] { (40, "USB-DISK"), (72, "/media/USB-DISK"), (136, "FAT32"), (152, "ABCD-1234"), (192, "USB DISK") })
            Assert.That(Encoding.UTF8.GetString(bytes, at, text.Length), Is.EqualTo(text));
        Assert.That(NamespaceVolume.Read(bytes), Is.EqualTo(volume));

        var longLabel = volume with { Label = new string('é', 40) };
        longLabel.Write(bytes);
        Assert.That(NamespaceVolume.Read(bytes).Label, Is.EqualTo(new string('é', 31)), "63 bytes hold 31 characters of two bytes");
        Assert.Throws<System.ArgumentException>(() => (volume with { Alias = new string('a', 32) }).Write(bytes));
        Assert.Throws<FormatException>(() => NamespaceVolume.Read(new byte[NamespaceVolume.SIZE]));
    }

    [Test]
    public void DisksRoundTripTest()
    {
        var bytes = NamespaceMessages.EncodeDisk(NamespaceMessages.DISK_READ_ONLY, "pci/00:02.0/1af4:1001");

        Assert.That(bytes, Has.Length.EqualTo(80));
        Assert.That(NamespaceMessages.DecodeDisk(bytes), Is.EqualTo((NamespaceMessages.DISK_READ_ONLY, "pci/00:02.0/1af4:1001")));
        Assert.That(NamespaceMessages.DecodeDisk(NamespaceMessages.EncodeDisk(NamespaceMessages.DISK_END, "")).Flags, Is.EqualTo(NamespaceMessages.DISK_END));
    }

    [Test]
    public void OtherBytesAreRefusedTest()
    {
        var bytes = NamespaceMessages.Encode(new NamespaceRequest(NamespaceMessages.MOUNTS));
        bytes[0] = 2;

        Assert.Throws<FormatException>(() => NamespaceMessages.DecodeRequest(bytes));
        Assert.Throws<FormatException>(() => NamespaceMessages.DecodeReply(new byte[48]));
        Assert.Throws<FormatException>(() => NamespaceMessages.DecodeDisk(new byte[16]));
    }

    #endregion
}
