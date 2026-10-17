using System;
using OutWit.OS.Hardware;

namespace OutWit.OS.Display;

/// <summary>
/// The geometry of the firmware's framebuffer as its device descriptor carries it (witos/device.h, the firmware bus,
/// plan step C2.1): pixels of 32 bits in rows of Stride pixels, Width of them shown, Height rows, in one of two byte
/// orders. The descriptor's region 0 is the memory.
/// </summary>
/// <param name="Width">Pixels shown in a row.</param>
/// <param name="Height">Rows.</param>
/// <param name="Stride">Pixels a row takes in memory, at least the width.</param>
/// <param name="Format"><see cref="BGRX"/> or <see cref="RGBX"/>.</param>
public sealed record FramebufferGeometry(int Width, int Height, int Stride, uint Format)
{
    #region Constants

    /// <summary>
    /// The firmware bus's kind of a framebuffer (WIT_FIRMWARE_DEVICE_FRAMEBUFFER).
    /// </summary>
    public const uint KIND = 1;

    /// <summary>
    /// Blue, green, red, then an unused byte (WIT_PIXEL_BGRX): a pixel read as a little-endian word is 0x00RRGGBB.
    /// </summary>
    public const uint BGRX = 1;

    /// <summary>
    /// Red, green, blue, then an unused byte (WIT_PIXEL_RGBX): a pixel read as a little-endian word is 0x00BBGGRR.
    /// </summary>
    public const uint RGBX = 2;

    /// <summary>
    /// Bytes a pixel takes.
    /// </summary>
    public const int PIXEL_BYTES = 4;

    #endregion

    #region Functions

    /// <summary>
    /// Reads the geometry of a firmware framebuffer's descriptor.
    /// </summary>
    /// <param name="descriptor">A descriptor of the firmware bus whose kind is a framebuffer.</param>
    /// <returns>The geometry.</returns>
    /// <exception cref="FormatException">The descriptor is not a framebuffer's, or its words do not describe one.</exception>
    public static FramebufferGeometry FromDescriptor(DeviceDescriptor descriptor)
    {
        if (descriptor.Bus != DeviceDescriptor.BUS_FIRMWARE || descriptor.Identity.Count < 4 || descriptor.Identity[0] != KIND)
            throw new FormatException("The descriptor is not the firmware's framebuffer.");
        var geometry = new FramebufferGeometry((int)(descriptor.Identity[1] & 0xFFFF), (int)(descriptor.Identity[1] >> 16),
            (int)descriptor.Identity[2], descriptor.Identity[3]);
        if (geometry.Width == 0 || geometry.Height == 0 || geometry.Stride < geometry.Width || geometry.Stride > 0xFFFF ||
            geometry.Format is not (BGRX or RGBX) || descriptor.Regions.Count != 1 || !descriptor.Regions[0].IsMemory ||
            descriptor.Regions[0].Size < (ulong)geometry.Bytes)
            throw new FormatException($"The framebuffer's descriptor names {geometry.Width} by {geometry.Height}, stride {geometry.Stride}, " +
                $"format {geometry.Format} over {descriptor.Regions.Count} regions, which is no framebuffer.");
        return geometry;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The bytes the rows take: the stride times the height times a pixel's bytes.
    /// </summary>
    public long Bytes => (long)Stride * Height * PIXEL_BYTES;

    /// <summary>
    /// The format's name.
    /// </summary>
    public string FormatName => Format == BGRX ? "BGRX" : "RGBX";

    #endregion
}
