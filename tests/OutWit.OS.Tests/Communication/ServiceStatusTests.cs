using System;
using System.Buffers.Binary;
using OutWit.OS.Communication;

namespace OutWit.OS.Tests.Communication;

/// <summary>
/// A service's first answer to its creator (witos/manager.h's WitServiceStatus).
/// </summary>
[TestFixture]
public sealed class ServiceStatusTests
{
    #region Tests

    [Test]
    public void EncodeIsServiceStatusTest()
    {
        var status = ServiceStatus.Encode(71);

        Assert.That(status, Has.Length.EqualTo(16));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(status), Is.EqualTo(4u));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(4)), Is.EqualTo(16u));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(8)), Is.EqualTo(71u));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(12)), Is.Zero);
    }

    [Test]
    public void DecodeReadsErrorTest()
    {
        Assert.That(ServiceStatus.Decode(ServiceStatus.Encode(0)), Is.Zero);
        Assert.That(ServiceStatus.Decode(ServiceStatus.Encode(16)), Is.EqualTo(16));
    }

    [Test]
    public void DecodeRefusesOtherBytesTest()
    {
        var status = ServiceStatus.Encode(0);
        BinaryPrimitives.WriteUInt32LittleEndian(status, 2);
        Assert.That(ServiceStatus.Decode(status), Is.EqualTo(ServiceStatus.PROTOCOL_ERROR));
        Assert.That(ServiceStatus.Decode(new byte[8]), Is.EqualTo(ServiceStatus.PROTOCOL_ERROR));
    }

    #endregion
}
