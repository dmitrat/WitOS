using System;

namespace OutWit.OS.Display;

/// <summary>
/// The test pattern the device manager's display probe draws into the firmware's framebuffer (plan step C2.1), which the
/// host checks in QEMU's screendump by the same function: a black frame of <see cref="FRAME"/> pixels and, inside it,
/// four horizontal bands — red, green, blue, white — so that a wrong stride, byte order or geometry shows at once.
/// </summary>
public static class TestPattern
{
    #region Constants

    /// <summary>
    /// The frame's width in pixels.
    /// </summary>
    public const int FRAME = 16;

    /// <summary>
    /// The bands inside the frame.
    /// </summary>
    public const int BANDS = 4;

    #endregion

    #region Functions

    /// <summary>
    /// The colour of a pixel.
    /// </summary>
    /// <param name="x">The column, from the left.</param>
    /// <param name="y">The row, from the top.</param>
    /// <param name="width">The pixels shown in a row.</param>
    /// <param name="height">The rows.</param>
    /// <returns>Red, green and blue.</returns>
    public static (byte Red, byte Green, byte Blue) Pixel(int x, int y, int width, int height)
    {
        if (x < FRAME || y < FRAME || x >= width - FRAME || y >= height - FRAME)
            return (0, 0, 0);
        var inner = Math.Max(1, height - 2 * FRAME);
        return ((y - FRAME) * BANDS / inner) switch
        {
            0 => (255, 0, 0),
            1 => (0, 255, 0),
            2 => (0, 0, 255),
            _ => (255, 255, 255)
        };
    }

    /// <summary>
    /// The row at which a band starts, and the row of its middle, for a check that samples the bands.
    /// </summary>
    /// <param name="band">The band, 0 to 3.</param>
    /// <param name="height">The rows.</param>
    /// <returns>The row in the band's middle.</returns>
    public static int BandMiddle(int band, int height) => FRAME + (height - 2 * FRAME) * (2 * band + 1) / (2 * BANDS);

    /// <summary>
    /// A colour as the framebuffer's word of a format, read or written little-endian.
    /// </summary>
    /// <param name="color">Red, green and blue.</param>
    /// <param name="format"><see cref="FramebufferGeometry.BGRX"/> or <see cref="FramebufferGeometry.RGBX"/>.</param>
    /// <returns>The pixel's 32 bits.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is neither.</exception>
    public static uint Encode((byte Red, byte Green, byte Blue) color, uint format) => format switch
    {
        FramebufferGeometry.BGRX => (uint)color.Red << 16 | (uint)color.Green << 8 | color.Blue,
        FramebufferGeometry.RGBX => (uint)color.Blue << 16 | (uint)color.Green << 8 | color.Red,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a pixel format of the firmware's framebuffer.")
    };

    #endregion
}
