using System;
using System.Buffers.Binary;
using OutWit.OS.Communication;

namespace OutWit.OS.Tests.Communication;

/// <summary>
/// A creator's request that its service stop (witos/manager.h's WitServiceStop, plan step D6.1).
/// </summary>
[TestFixture]
public sealed class ServiceStopTests
{
    #region Tests

    [Test]
    public void EncodeIsServiceStopTest()
    {
        var stop = ServiceStop.Encode(0x0123456789ABCDEF);

        Assert.That(stop, Has.Length.EqualTo(24));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(stop), Is.EqualTo(3u));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(stop.AsSpan(4)), Is.EqualTo(24u));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(stop.AsSpan(8)), Is.EqualTo(2u));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(stop.AsSpan(12)), Is.Zero);
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(stop.AsSpan(16)), Is.EqualTo(0x0123456789ABCDEFul));
    }

    [Test]
    public void TryDecodeReadsDeadlineTest()
    {
        Assert.That(ServiceStop.TryDecode(ServiceStop.Encode(ulong.MaxValue), out var deadline), Is.True);
        Assert.That(deadline, Is.EqualTo(ulong.MaxValue));
    }

    [Test]
    public void TryDecodeRefusesOtherBytesTest()
    {
        Assert.That(ServiceStop.TryDecode(ServiceStatus.Encode(2), out _), Is.False);
        var stop = ServiceStop.Encode(0);
        BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(12), 1);
        Assert.That(ServiceStop.TryDecode(stop, out _), Is.False);
        stop = ServiceStop.Encode(0);
        BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(8), 1);
        Assert.That(ServiceStop.TryDecode(stop, out _), Is.False);
    }

    #endregion
}
