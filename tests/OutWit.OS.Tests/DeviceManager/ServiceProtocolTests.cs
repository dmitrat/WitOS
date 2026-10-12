using System;
using System.Buffers.Binary;
using OutWit.OS.DeviceManager;

namespace OutWit.OS.Tests.DeviceManager;

/// <summary>
/// The device manager's messages with the root task (witos/device_manager.h, witos/manager.h).
/// </summary>
[TestFixture]
public sealed class ServiceProtocolTests
{
    #region Tests

    [Test]
    public void StartIsRecognizedTest()
    {
        Assert.That(ServiceProtocol.IsStart(Start(1, 16, 1, 0)), Is.True);
        Assert.That(ServiceProtocol.IsStart(Start(2, 16, 1, 0)), Is.False);
        Assert.That(ServiceProtocol.IsStart(Start(1, 24, 1, 0)), Is.False);
        Assert.That(ServiceProtocol.IsStart(Start(1, 16, 2, 0)), Is.False);
        Assert.That(ServiceProtocol.IsStart(Start(1, 16, 1, 7)), Is.False);
        Assert.That(ServiceProtocol.IsStart(new byte[12]), Is.False);
    }

    [Test]
    public void StatusIsServiceStatusTest()
    {
        var status = ServiceProtocol.Status(71);

        Assert.That(status, Has.Length.EqualTo(16));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(status), Is.EqualTo(ServiceProtocol.MANAGER_VERSION));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(4)), Is.EqualTo(16u));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(8)), Is.EqualTo(71u));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(12)), Is.Zero);
    }

    #endregion

    #region Tools

    private static byte[] Start(uint version, uint size, uint operation, uint reserved)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), operation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), reserved);
        return bytes;
    }

    #endregion
}
