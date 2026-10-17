using System;
using System.Buffers.Binary;
using OutWit.OS.Display;
using OutWit.OS.Hardware;

namespace OutWit.OS.Tests.Display;

/// <summary>
/// The device manager's start of the display service (witos/display.h, plan step C2.2): the framebuffer descriptor's
/// bytes round-trip, and the geometry reads from them.
/// </summary>
[TestFixture]
public sealed class DisplayStartTests
{
    #region Tests

    [Test]
    public void StartCarriesTheFramebufferTest()
    {
        var descriptor = Framebuffer(5);

        var bytes = DisplayStart.Encode(descriptor);
        var decoded = DisplayStart.Decode(bytes);

        Assert.That(bytes, Has.Length.EqualTo(DisplayStart.SIZE));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes), Is.EqualTo(DisplayStart.VERSION));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)), Is.EqualTo(DisplayStart.START));
        Assert.That(decoded.Index, Is.EqualTo(5));
        Assert.That(decoded.Bus, Is.EqualTo(DeviceDescriptor.BUS_FIRMWARE));
        Assert.That(decoded.Regions, Is.EqualTo(descriptor.Regions));
        var geometry = FramebufferGeometry.FromDescriptor(decoded);
        Assert.That((geometry.Width, geometry.Height, geometry.Stride, geometry.Format), Is.EqualTo((800, 600, 800, FramebufferGeometry.BGRX)));
    }

    [Test]
    public void OtherBytesAreRefusedTest()
    {
        var bytes = DisplayStart.Encode(Framebuffer(0));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), DriverStart.START + 1);
        Assert.Throws<FormatException>(() => DisplayStart.Decode(bytes));
        Assert.Throws<FormatException>(() => DisplayStart.Decode(DriverStart.Encode(Framebuffer(0))[..16]));
        Assert.Throws<ArgumentException>(() => DisplayStart.Encode(new DeviceDescriptor(0, DeviceDescriptor.BUS_FIRMWARE, 0, [1, 0, 0, 0], [], [])));
    }

    #endregion

    #region Tools

    // The firmware's framebuffer as the platform describes it (C2.1), read from a table so that it carries its bytes.
    private static DeviceDescriptor Framebuffer(int index)
    {
        var bytes = new byte[DeviceTable.DESCRIPTOR_SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, DeviceDescriptor.BUS_FIRMWARE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), FramebufferGeometry.KIND);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 800 | 600 << 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 800);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), FramebufferGeometry.BGRX);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 0x8DFD_1000);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), 0x30_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), DeviceRegion.MEMORY);
        return DeviceTable.ParseDescriptor(index, bytes);
    }

    #endregion
}
