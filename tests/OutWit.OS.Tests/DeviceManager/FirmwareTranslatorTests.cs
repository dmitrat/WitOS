using System;
using OutWit.OS.DeviceManager.Uhi;
using OutWit.OS.Display;
using OutWit.OS.Hardware;

namespace OutWit.OS.Tests.DeviceManager;

/// <summary>
/// The device manager's translation of the firmware bus's descriptors (plan step C2.1): the framebuffer the boot
/// contract described, with its geometry read from the identity words as witos/device.h lays them out.
/// </summary>
[TestFixture]
public sealed class FirmwareTranslatorTests
{
    #region Tests

    [Test]
    public void FramebufferIsAFrameBufferOfTheFirmwareTest()
    {
        var record = PciTranslator.Translate(Framebuffer(800, 600, 800, FramebufferGeometry.BGRX, 3145728));
        Assert.That(record.Class, Is.EqualTo("FrameBuffer"));
        Assert.That(record.Provider, Is.EqualTo("Firmware"));
        Assert.That(record.Id, Is.EqualTo("firmware/framebuffer"));
        Assert.That(record.DiscoveryLine,
            Is.EqualTo("Device discovered: Class: FrameBuffer, Provider: Firmware, DeviceId: firmware/framebuffer, Regions: 1, Lines: 0"));
    }

    [Test]
    public void GeometryReadsTheIdentityWordsTest()
    {
        var geometry = FramebufferGeometry.FromDescriptor(Framebuffer(1280, 800, 1280, FramebufferGeometry.BGRX, 4096000));
        Assert.That((geometry.Width, geometry.Height, geometry.Stride, geometry.Format), Is.EqualTo((1280, 800, 1280, FramebufferGeometry.BGRX)));
        Assert.That(geometry.Bytes, Is.EqualTo(4096000));
        Assert.That(geometry.FormatName, Is.EqualTo("BGRX"));
        Assert.That(FramebufferGeometry.FromDescriptor(Framebuffer(640, 480, 1024, FramebufferGeometry.RGBX, 2097152)).FormatName, Is.EqualTo("RGBX"));
    }

    [Test]
    public void GeometryRefusesWhatIsNoFramebufferTest()
    {
        Assert.That(() => FramebufferGeometry.FromDescriptor(Framebuffer(800, 600, 799, FramebufferGeometry.BGRX, 3145728)),
            Throws.TypeOf<FormatException>(), "A stride below the width");
        Assert.That(() => FramebufferGeometry.FromDescriptor(Framebuffer(800, 600, 800, 7, 3145728)),
            Throws.TypeOf<FormatException>(), "An unknown format");
        Assert.That(() => FramebufferGeometry.FromDescriptor(Framebuffer(800, 600, 800, FramebufferGeometry.BGRX, 4096)),
            Throws.TypeOf<FormatException>(), "A region too small for the rows");
        Assert.That(() => FramebufferGeometry.FromDescriptor(new DeviceDescriptor(0, DeviceDescriptor.BUS_PCI, 0x100, [0x1AF4, 0, 0, 0], [], [])),
            Throws.TypeOf<FormatException>(), "A PCI function");
    }

    [Test]
    public void OtherFirmwareKindsAreNamedByKindTest()
    {
        var record = PciTranslator.Translate(new DeviceDescriptor(3, DeviceDescriptor.BUS_FIRMWARE, 0, [7, 0, 0, 0], [], []));
        Assert.That(record.Class, Is.EqualTo("FirmwareDevice7"));
        Assert.That(record.Id, Is.EqualTo("firmware/7"));
        Assert.That(record.Provider, Is.EqualTo("Firmware"));
    }

    [Test]
    public void TestPatternHasAFrameAndFourBandsTest()
    {
        Assert.That(TestPattern.Pixel(0, 0, 800, 600), Is.EqualTo(((byte)0, (byte)0, (byte)0)), "The frame is black");
        Assert.That(TestPattern.Pixel(799, 599, 800, 600), Is.EqualTo(((byte)0, (byte)0, (byte)0)));
        Assert.That(TestPattern.Pixel(400, TestPattern.BandMiddle(0, 600), 800, 600), Is.EqualTo(((byte)255, (byte)0, (byte)0)), "Red");
        Assert.That(TestPattern.Pixel(400, TestPattern.BandMiddle(1, 600), 800, 600), Is.EqualTo(((byte)0, (byte)255, (byte)0)), "Green");
        Assert.That(TestPattern.Pixel(400, TestPattern.BandMiddle(2, 600), 800, 600), Is.EqualTo(((byte)0, (byte)0, (byte)255)), "Blue");
        Assert.That(TestPattern.Pixel(400, TestPattern.BandMiddle(3, 600), 800, 600), Is.EqualTo(((byte)255, (byte)255, (byte)255)), "White");
        Assert.That(TestPattern.Encode((255, 0, 0), FramebufferGeometry.BGRX), Is.EqualTo(0x00FF0000U), "Red in the third byte of BGRX");
        Assert.That(TestPattern.Encode((255, 0, 0), FramebufferGeometry.RGBX), Is.EqualTo(0x000000FFU), "Red in the first byte of RGBX");
        Assert.That(TestPattern.Encode((0, 0, 255), FramebufferGeometry.BGRX), Is.EqualTo(0x000000FFU));
    }

    #endregion

    #region Tools

    private static DeviceDescriptor Framebuffer(uint width, uint height, uint stride, uint format, ulong bytes) =>
        new(5, DeviceDescriptor.BUS_FIRMWARE, 0, [FramebufferGeometry.KIND, width | height << 16, stride, format],
            [new DeviceRegion(0x0E4CF000, bytes, DeviceRegion.MEMORY)], []);

    #endregion
}
